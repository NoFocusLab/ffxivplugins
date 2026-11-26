using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SettingsSync;

/// <summary>When a sync runs, which decides what it may do.</summary>
public enum SyncMode
{
    /// <summary>Game starting, other plugins not loaded yet: pulled files are written straight into place.</summary>
    Startup,
    /// <summary>Game running: pushes go out, pulled files wait for the next start (plugins would overwrite them).</summary>
    Running,
    /// <summary>Game closing: push only.</summary>
    Closing,
}

/// <summary>Which parts a sync covers.</summary>
[Flags]
public enum SyncGroups
{
    None = 0,
    /// <summary>The pluginConfigs folder.</summary>
    Plugins = 1,
    /// <summary>Dalamud's repo list, plugin on/off states, theme... (see <see cref="DalamudSetup"/>).</summary>
    Dalamud = 2,
    All = Plugins | Dalamud,
}

/// <summary>A plugin that's on GitHub (installed on another PC) but not installed here.</summary>
public sealed record MissingPlugin(string InternalName, string Name, bool OnElsewhere);

/// <summary>A file both this PC and GitHub changed since they last agreed.</summary>
public sealed record Conflict(string Path, string LocalSha, string RemoteSha, DateTime LocalTime, string RemoteFrom);

/// <summary>
/// Two-way sync of pluginConfigs with the GitHub repository. Each file is compared three ways:
/// this PC now, GitHub now, and what both had at the last sync (LocalState.BaseShas). Whoever
/// changed it wins; if both did, it becomes a Conflict for you to decide. Nothing is ever deleted
/// automatically on either side.
/// </summary>
public sealed class Syncer : IDisposable
{
    public const string Prefix = "pluginConfigs/";
    private const string MetaPath = ".sync/last.json";

    private readonly Configuration cfg;
    private readonly string root;      // ...\XIVLauncher\pluginConfigs
    private readonly string ownDir;    // ...\pluginConfigs\SettingsSync
    private readonly string pendingDir;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly GitHub gh = new();

    public LocalState State { get; }
    public string Status { get; private set; } = "Idle";
    public bool Busy => gate.CurrentCount == 0;
    public List<Conflict> Conflicts { get; private set; } = new();
    /// <summary>Goes up each time a sync finds conflicts, so a dismissed conflict window can pop up again.</summary>
    public int ConflictGeneration { get; private set; }
    public List<CommitInfo> History { get; private set; } = new();
    /// <summary>Files only this PC has, not uploaded because the first sync isn't confirmed yet.</summary>
    public int HeldBack { get; private set; }

    /// <summary>Ends the new-PC lock: from now on this PC pushes like any other.</summary>
    public void ConfirmFirstSync()
    {
        State.FirstSyncDone = true;
        State.AllowedPushes.Clear();
        State.Log("First sync", $"Confirmed: {cfg.MachineName} now uploads its changes too");
        SaveState();
    }
    public string? Token { get; private set; }
    public bool SignedIn => !string.IsNullOrEmpty(Token);
    public DateTime LastAttempt { get; private set; } = DateTime.MinValue;

    public DalamudSetup Dalamud { get; }
    public List<MissingPlugin> Missing { get; private set; } = new();
    /// <summary>Custom repo URLs on GitHub's copy that this PC doesn't have yet (Dalamud only loads repos at start).</summary>
    public List<string> MissingRepos { get; private set; } = new();

    public Syncer(Configuration cfg, string pluginConfigsRoot, string ownDir)
    {
        this.cfg = cfg;
        Dalamud = new DalamudSetup(Directory.GetParent(pluginConfigsRoot.TrimEnd('\\', '/'))!.FullName, ownDir);
        root = pluginConfigsRoot;
        this.ownDir = ownDir;
        pendingDir = Path.Combine(ownDir, "pending");
        State = LocalState.Load(ownDir);
        Token = Secrets.LoadToken(ownDir);
        RefreshCounts();
    }

    public void Dispose()
    {
        gh.Dispose();
        gate.Dispose();
    }

    public bool Configured => SignedIn && cfg.Repository.Contains('/');

    // ------------------------------------------------------------------ sign-in

    public DeviceCode? PendingSignIn { get; private set; }
    private CancellationTokenSource? signInCts;

    public void StartSignIn()
    {
        signInCts?.Cancel();
        signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(16));
        var ct = signInCts.Token;
        var clientId = cfg.ClientId.Trim();
        Task.Run(async () =>
        {
            try
            {
                Status = "Asking GitHub for a sign-in code...";
                var code = await gh.StartDeviceFlow(clientId, ct);
                PendingSignIn = code;
                Status = $"Enter code {code.Code} at {code.Url}";
                var token = await gh.FinishDeviceFlow(clientId, code, ct);
                Secrets.SaveToken(ownDir, token);
                Token = token;
                Status = "Signed in.";
                State.Log("Sign-in", $"Signed in on {cfg.MachineName}");
                SaveState();
            }
            catch (OperationCanceledException) { Status = "Sign-in cancelled."; }
            catch (Exception e) { Status = "Sign-in failed: " + e.Message; Plugin.Log.Warning(e, "Settings Sync sign-in"); }
            finally { PendingSignIn = null; }
        }, ct);
    }

    public void CancelSignIn() => signInCts?.Cancel();

    public void SignOut()
    {
        Secrets.SaveToken(ownDir, null);
        Token = null;
        Status = "Signed out.";
    }

    // ------------------------------------------------------------------ local files

    private Regex? excludeRx;
    private string excludeKey = "";

    public bool IsExcluded(string rel)
    {
        if (rel.Equals("SettingsSync.json", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("SettingsSync/", StringComparison.OrdinalIgnoreCase)) return true;
        var key = string.Join("\n", cfg.Excludes);
        if (excludeRx == null || key != excludeKey)
        {
            excludeKey = key;
            var parts = cfg.Excludes.Select(p => p.Trim()).Where(p => p.Length > 0).Select(GlobToRegex).ToList();
            excludeRx = parts.Count == 0 ? null : new Regex("^(?:" + string.Join("|", parts) + ")$", RegexOptions.IgnoreCase);
        }
        return excludeRx?.IsMatch(rel) ?? false;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '[')
            {
                var end = glob.IndexOf(']', i + 1);
                if (end < 0) { sb.Append(@"\["); continue; }
                sb.Append('[').Append(glob[(i + 1)..end].Replace(@"\", @"\\")).Append(']');
                i = end;
            }
            else if (c == '\\') sb.Append('/');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>A file here: on disk (Full), or a virtual Dalamud-setup file (Data).</summary>
    public sealed record LocalFile(string Rel, string Full, string Sha, long Size, DateTime Modified, byte[]? Data = null);

    private static byte[] Bytes(LocalFile f) => f.Data ?? ReadShared(f.Full);

    /// <summary>State keys: pluginConfigs files by their path inside pluginConfigs; Dalamud setup by repo path.</summary>
    private static string RepoPath(string key) => key.StartsWith(DalamudSetup.Prefix, StringComparison.Ordinal) ? key : Prefix + key;

    private static SyncGroups GroupOf(string key) =>
        key.StartsWith(DalamudSetup.Prefix, StringComparison.Ordinal) ? SyncGroups.Dalamud : SyncGroups.Plugins;

    public Dictionary<string, LocalFile> ScanLocal()
    {
        var result = new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
        var maxBytes = Math.Max(1, cfg.MaxFileMb) * 1024L * 1024L;
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (IsExcluded(rel)) continue;
            try
            {
                var info = new FileInfo(full);
                if (info.Length > maxBytes) continue;
                var bytes = ReadShared(full);
                result[rel] = new LocalFile(rel, full, GitSha(bytes), bytes.Length, info.LastWriteTime);
            }
            catch (Exception e) { Plugin.Log.Debug($"Settings Sync: skipped {rel}: {e.Message}"); }
        }
        return result;
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The SHA git uses for a file's contents, so local files compare directly with GitHub's.</summary>
    public static string GitSha(byte[] data)
    {
        var header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        var all = new byte[header.Length + data.Length];
        header.CopyTo(all, 0);
        data.CopyTo(all, header.Length);
        return Convert.ToHexString(SHA1.HashData(all)).ToLowerInvariant();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The file as text if it is valid UTF-8 without NUL bytes (git then stores the exact same bytes), else null.</summary>
    private static string? AsText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            return StrictUtf8.GetByteCount(text) == bytes.Length ? text : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private void WriteLocal(string rel, byte[] data)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".ssync";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, full, true);
    }

    private void SaveState()
    {
        try { State.Save(ownDir); } catch (Exception e) { Plugin.Log.Warning(e, "Settings Sync: couldn't save state"); }
    }

    // ------------------------------------------------------------------ pending (pulled while running)

    public int PendingCount => PendingPluginFiles;

    /// <summary>Startup only: puts files pulled during the last session into place, before other plugins load.</summary>
    public int ApplyPending()
    {
        var applied = 0;
        foreach (var (rel, sha) in State.PendingShas.Where(p => GroupOf(p.Key) == SyncGroups.Plugins).ToList())
        {
            try
            {
                var src = PendingFile(rel);
                if (File.Exists(src)) { WriteLocal(rel, File.ReadAllBytes(src)); State.BaseShas[rel] = sha; applied++; }
            }
            catch (Exception e) { Plugin.Log.Warning(e, $"Settings Sync: couldn't apply pulled {rel}"); }
        }
        if (State.PendingShas.Keys.Any(k => GroupOf(k) == SyncGroups.Plugins))
        {
            foreach (var k in State.PendingShas.Keys.Where(k => GroupOf(k) == SyncGroups.Plugins).ToList())
            {
                State.PendingShas.Remove(k);
                try { File.Delete(PendingFile(k)); } catch { /* next time */ }
            }
            if (applied > 0) State.Log("Applied", $"{applied} file(s) pulled last session");
            SaveState();
        }
        RefreshCounts();
        return applied;
    }

    private string PendingFile(string key) => Path.Combine(pendingDir, key.Replace('/', Path.DirectorySeparatorChar));

    // Counts for the window, refreshed by the sync thread: the window must never enumerate State itself
    // while a sync is changing it.
    public int PendingPluginFiles { get; private set; }
    public int PendingDalamud { get; private set; }

    private void RefreshCounts()
    {
        PendingPluginFiles = State.PendingShas.Keys.Count(k => GroupOf(k) == SyncGroups.Plugins);
        PendingDalamud = State.PendingShas.Keys.Count(k => GroupOf(k) == SyncGroups.Dalamud);
    }

    /// <summary>Pulled Dalamud settings not applied yet (key -> contents), for the apply-on-exit helper.</summary>
    public Dictionary<string, byte[]> PendingDalamudConfig()
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var k in State.PendingShas.Keys.Where(k => k.StartsWith(DalamudSetup.ConfigPrefix, StringComparison.Ordinal)))
            if (File.Exists(PendingFile(k))) result[k] = File.ReadAllBytes(PendingFile(k));
        return result;
    }

    private void Stage(string rel, string sha, byte[] data)
    {
        var dst = PendingFile(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, data);
        State.PendingShas[rel] = sha;
    }

    // ------------------------------------------------------------------ sync

    /// <summary>Runs a sync unless one is already running. Never throws; the outcome is in Status / Activity.</summary>
    public async Task Run(SyncMode mode, CancellationToken ct, bool pushOnly = false, bool pullOnly = false, SyncGroups groups = SyncGroups.All)
    {
        if (!Configured) { Status = SignedIn ? "Set the repository first (Settings tab)." : "Not signed in (Settings tab)."; return; }
        if (!await gate.WaitAsync(0, ct)) return;
        LastAttempt = DateTime.Now;
        try
        {
            gh.Token = Token!;
            gh.Repo = cfg.Repository.Trim();
            // A push can lose a race with the other PC; one retry re-reads GitHub and recompares.
            for (var attempt = 0; attempt < 2; attempt++)
                if (await SyncOnce(mode, groups, pushOnly, pullOnly, ct)) break;
        }
        catch (OperationCanceledException) { Status = "Sync stopped (took too long)."; }
        catch (Exception e)
        {
            Status = "Sync failed: " + e.Message;
            State.Log("Error", e.Message);
            Plugin.Log.Warning(e, "Settings Sync");
            if (e is GitHubException { Status: System.Net.HttpStatusCode.Unauthorized }) SignOut();
        }
        finally
        {
            RefreshCounts();
            SaveState();
            gate.Release();
        }
    }

    /// <returns>False if the push lost a race and the sync should run again.</returns>
    private async Task<bool> SyncOnce(SyncMode mode, SyncGroups groups, bool pushOnly, bool pullOnly, CancellationToken ct)
    {
        Status = "Checking GitHub...";
        var head = await gh.GetHead(cfg.Branch, ct);
        if (head == null)
        {
            Status = "Empty repository: creating the first commit...";
            await gh.InitEmptyRepo(cfg.Branch, ct);
            head = await gh.GetHead(cfg.Branch, ct);
        }
        var remote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (head != null)
            foreach (var f in head.Files)
            {
                string key;
                if (f.Path.StartsWith(Prefix, StringComparison.Ordinal)) key = f.Path[Prefix.Length..];
                else if (f.Path.StartsWith(DalamudSetup.Prefix, StringComparison.Ordinal)) key = f.Path;
                else continue;
                if ((GroupOf(key) & groups) != 0 && !IsExcluded(key)) remote[key] = f.Sha;
            }
        var remoteFrom = head == null ? "" : await LastPusher(head, ct);

        Status = "Comparing files...";
        var local = (groups & SyncGroups.Plugins) != 0 ? ScanLocal() : new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
        var dalamudOn = (groups & SyncGroups.Dalamud) != 0 && Dalamud.Available;
        if (dalamudOn)
        {
            foreach (var (path, data) in Dalamud.Read(IsExcluded, includePlugins: mode != SyncMode.Closing))
                local[path] = new LocalFile(path, "", GitSha(data), data.Length, DateTime.Now, data);
            // Dalamud settings applied by the exit helper since the last run: now on disk, so settled.
            foreach (var (k, sha) in State.PendingShas.Where(p => GroupOf(p.Key) == SyncGroups.Dalamud).ToList())
                if (local.TryGetValue(k, out var lf0) && lf0.Sha == sha)
                {
                    State.BaseShas[k] = sha;
                    State.PendingShas.Remove(k);
                    try { File.Delete(PendingFile(k)); } catch { /* harmless */ }
                }
        }
        if (dalamudOn && mode == SyncMode.Closing)
            remote.Keys.Where(k => k.StartsWith(DalamudSetup.PluginPrefix, StringComparison.Ordinal)).ToList().ForEach(k => remote.Remove(k));
        else if (!dalamudOn && (groups & SyncGroups.Dalamud) != 0) remote.Keys.Where(k => GroupOf(k) == SyncGroups.Dalamud).ToList().ForEach(k => remote.Remove(k));
        var pulls = new List<string>();
        var pushes = new List<string>();
        var conflicts = new List<Conflict>();

        foreach (var rel in local.Keys.Union(remote.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var l = local.TryGetValue(rel, out var lf) ? lf.Sha : null;
            var r = remote.GetValueOrDefault(rel);
            var b = State.BaseShas.GetValueOrDefault(rel);
            if (l == r) { if (l != null) State.BaseShas[rel] = l; continue; }
            // Another PC's plugin that isn't installed here: nothing to push or compare, it's listed as missing.
            if (l == null && rel.StartsWith(DalamudSetup.PluginPrefix, StringComparison.Ordinal) && State.BaseShas.GetValueOrDefault(rel) == r) continue;
            if (r == null) { if (b != l) pushes.Add(rel); continue; }          // new or changed here; if b == l GitHub dropped it: leave it
            if (l == null) { if (b != r) pulls.Add(rel); continue; }           // new on GitHub; if b == r deleted here: leave it
            if (l == b) pulls.Add(rel);
            else if (r == b) pushes.Add(rel);
            else conflicts.Add(new Conflict(rel, l, r, lf!.Modified, remoteFrom));
        }

        if (pushOnly) pulls.Clear();
        var heldBack = 0;
        // The very first PC (nothing on GitHub yet) has nothing to protect: no lock.
        if (!State.FirstSyncDone && (head?.Files ?? new List<RemoteFile>()).All(f => !f.Path.StartsWith(Prefix, StringComparison.Ordinal) &&
                                                        !f.Path.StartsWith(DalamudSetup.Prefix, StringComparison.Ordinal)))
            State.FirstSyncDone = true;
        if (!State.FirstSyncDone)
        {
            // New PC: only what you explicitly chose to keep from this PC goes up until you confirm.
            heldBack = pushes.RemoveAll(p => !State.AllowedPushes.Contains(p));
        }
        if (pullOnly) pushes.Clear();
        if (mode == SyncMode.Closing) pulls.Clear();
        // Already waiting for next start: don't download it again.
        pulls.RemoveAll(p => State.PendingShas.TryGetValue(p, out var s) && s == remote[p]);

        // ---- pulls
        if (pulls.Count > 0)
        {
            Status = $"Downloading {pulls.Count} file(s)...";
            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (pulls.Count > 25)
            {
                var byPath = pulls.ToDictionary(RepoPath, p => p, StringComparer.Ordinal);
                var got = await gh.GetFilesFromArchive(head!.CommitSha, byPath.Keys.ToHashSet(StringComparer.Ordinal), ct);
                foreach (var (path, bytes) in got) data[byPath[path]] = bytes;
            }
            foreach (var rel in pulls.Where(r => !data.ContainsKey(r) || GitSha(data[r]) != remote[r]))
                data[rel] = await gh.GetBlob(remote[rel], ct);
            ct.ThrowIfCancellationRequested();   // at startup: never write after the game moved on
            foreach (var rel in pulls)
            {
                if (rel.StartsWith(DalamudSetup.PluginPrefix, StringComparison.Ordinal))
                {
                    // On/off state: switch it live if installed here; otherwise it shows in the missing list.
                    var name = rel[DalamudSetup.PluginPrefix.Length..];
                    var (display, on) = DalamudSetup.ParseState(data[rel], name);
                    State.PluginInfo[name] = new PluginInfo { Name = display, On = on };
                    if (local.ContainsKey(rel)) Dalamud.SwitchPlugin(name, on);
                    State.BaseShas[rel] = remote[rel];
                }
                else if (GroupOf(rel) == SyncGroups.Dalamud) Stage(rel, remote[rel], data[rel]);   // applied after the game closes
                else if (mode == SyncMode.Startup) { WriteLocal(rel, data[rel]); State.BaseShas[rel] = remote[rel]; }
                else Stage(rel, remote[rel], data[rel]);
            }
            State.LastPull = DateTime.Now;
            State.LastPullFrom = remoteFrom;
            var waitsForRestart = mode != SyncMode.Startup && pulls.Any(p => GroupOf(p) == SyncGroups.Plugins);
            var waitsForClose = pulls.Any(p => p.StartsWith(DalamudSetup.ConfigPrefix, StringComparison.Ordinal));
            State.Log("Pull", $"{pulls.Count} file(s) from {Show(remoteFrom)}" +
                              (waitsForRestart ? " (settings apply on next start)" : "") +
                              (waitsForClose ? " (Dalamud settings apply after the game closes)" : "") + $": {Summary(pulls)}");
        }

        // ---- pushes
        if (pushes.Count > 0)
        {
            Status = $"Uploading {pushes.Count} file(s)...";
            var entries = new List<KeyValuePair<string, string?>>();
            var texts = new List<KeyValuePair<string, string>>();
            foreach (var rel in pushes)
            {
                var bytes = Bytes(local[rel]);   // re-read: the file may have changed since the scan
                local[rel] = local[rel] with { Sha = GitSha(bytes) };
                if (AsText(bytes) is { } text) texts.Add(new(RepoPath(rel), text));
                else entries.Add(new(RepoPath(rel), await gh.CreateBlob(bytes, ct)));   // binary: its own upload
            }
            var meta = JsonSerializer.Serialize(new { machine = cfg.MachineName, time = DateTimeOffset.Now, files = pushes.Count });
            texts.Add(new(MetaPath, meta));
            var tree = await gh.CreateTreeWithText(head?.TreeSha, texts, entries, ct);
            // Make sure GitHub stored every text file byte-for-byte; re-upload any that differ as blobs.
            var stored = (await gh.GetTreeFiles(tree, ct)).ToDictionary(f => f.Path, f => f.Sha, StringComparer.Ordinal);
            var fixes = new List<KeyValuePair<string, string?>>();
            foreach (var rel in pushes)
                if (stored.GetValueOrDefault(RepoPath(rel)) != local[rel].Sha)
                    fixes.Add(new(RepoPath(rel), await gh.CreateBlob(Bytes(local[rel]), ct)));
            if (fixes.Count > 0)
            {
                Plugin.Log.Warning($"Settings Sync: {fixes.Count} file(s) re-uploaded as exact copies");
                tree = await gh.CreateTree(tree, fixes, ct);
            }
            var msg = $"Push from {cfg.MachineName}: {pushes.Count} file(s) - {Summary(pushes)}";
            var commit = await gh.CreateCommit(msg, tree, head?.CommitSha, ct);
            if (!await gh.MoveBranch(cfg.Branch, commit, head == null, ct))
            {
                Status = "The other PC pushed at the same moment; comparing again...";
                return false;
            }
            foreach (var rel in pushes) { State.BaseShas[rel] = local[rel].Sha; State.AllowedPushes.Remove(rel); }
            State.LastCommit = commit;
            State.LastPush = DateTime.Now;
            State.Log("Push", $"{pushes.Count} file(s): {Summary(pushes)}");
        }
        else if (head != null) State.LastCommit = head.CommitSha;

        if (dalamudOn && mode != SyncMode.Closing)
        {
            UpdateMissing(remote, local);
            await UpdateMissingRepos(remote, local, ct);
        }
        // Keep conflicts from groups this run didn't look at.
        conflicts.AddRange(Conflicts.Where(c => (GroupOf(c.Path) & groups) == 0));
        Conflicts = conflicts;
        if (conflicts.Count > 0) ConflictGeneration++;
        if (conflicts.Count > 0) State.Log("Conflict", $"{conflicts.Count} file(s) changed on both PCs: {Summary(conflicts.Select(c => c.Path))}");

        Status = (pulls.Count, pushes.Count, conflicts.Count) switch
        {
            (0, 0, 0) => $"In sync ({DateTime.Now:t}).",
            _ => $"Done ({DateTime.Now:t}): {pushes.Count} pushed, {pulls.Count} pulled" +
                 (pulls.Count > 0 && mode != SyncMode.Startup ? " (restart the game to apply)" : "") +
                 (conflicts.Count > 0 ? $", {conflicts.Count} need your choice" : "") + ".",
        };
        if (heldBack > 0) Status += $" {heldBack} file(s) from this PC held back until you confirm the first sync.";
        HeldBack = heldBack;
        return true;
    }

    private void UpdateMissing(Dictionary<string, string> remote, Dictionary<string, LocalFile> local)
    {
        Missing = remote.Keys
            .Where(k => k.StartsWith(DalamudSetup.PluginPrefix, StringComparison.Ordinal) && !local.ContainsKey(k))
            .Select(k => k[DalamudSetup.PluginPrefix.Length..])
            .Where(n => !cfg.IgnoredMissing.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Select(n => State.PluginInfo.TryGetValue(n, out var i) ? new MissingPlugin(n, i.Name, i.On) : new MissingPlugin(n, n, true))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const string RepoKey = DalamudSetup.ConfigPrefix + "ThirdRepoList.json";

    private async Task UpdateMissingRepos(Dictionary<string, string> remote, Dictionary<string, LocalFile> local, CancellationToken ct)
    {
        try
        {
            if (!remote.TryGetValue(RepoKey, out var sha) || (local.TryGetValue(RepoKey, out var lf) && lf.Sha == sha)) { MissingRepos = new(); return; }
            var mine = local.TryGetValue(RepoKey, out var l) ? DalamudSetup.RepoUrls(l.Data!) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MissingRepos = DalamudSetup.RepoUrls(await gh.GetBlob(sha, ct)).Where(u => !mine.Contains(u)).OrderBy(u => u).ToList();
        }
        catch (Exception e) { Plugin.Log.Debug($"Settings Sync: repo list compare failed: {e.Message}"); }
    }

    private async Task<string> LastPusher(RemoteHead head, CancellationToken ct)
    {
        var meta = head.Files.FirstOrDefault(f => f.Path == MetaPath);
        if (meta == null) return "";
        try
        {
            var j = JsonDocument.Parse(await gh.GetBlob(meta.Sha, ct)).RootElement;
            var when = j.TryGetProperty("time", out var t) ? t.GetDateTimeOffset().LocalDateTime.ToString("g") : "";
            return $"{j.GetProperty("machine").GetString()} {when}".Trim();
        }
        catch { return ""; }
    }

    private static string Show(string from) => string.IsNullOrEmpty(from) ? "GitHub" : from;

    /// <summary>"Artisan, MaterialStash (+3 more)": plugin names from file paths.</summary>
    public static string Summary(IEnumerable<string> paths)
    {
        var names = paths.Select(PluginOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" (+{names.Count - 5} more)" : "");
    }

    public static string PluginOf(string rel)
    {
        if (rel.StartsWith(DalamudSetup.ConfigPrefix, StringComparison.Ordinal)) return "Dalamud " + System.IO.Path.GetFileNameWithoutExtension(rel);
        if (rel.StartsWith(DalamudSetup.PluginPrefix, StringComparison.Ordinal)) return rel[DalamudSetup.PluginPrefix.Length..] + " (on/off)";
        var slash = rel.IndexOf('/');
        var first = slash >= 0 ? rel[..slash] : rel;
        return first.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? first[..^5] : first;
    }

    // ------------------------------------------------------------------ conflicts

    /// <summary>Settles conflicts, then syncs: keepLocal = this PC's copy goes to GitHub, else GitHub's copy comes here.</summary>
    public Task Resolve(IEnumerable<Conflict> chosen, bool keepLocal, SyncMode mode, CancellationToken ct)
    {
        var list = chosen.ToList();
        // Pretend the losing side never changed it; the normal comparison then moves the winner across.
        foreach (var c in list)
        {
            State.BaseShas[c.Path] = keepLocal ? c.RemoteSha : c.LocalSha;
            if (keepLocal) State.AllowedPushes.Add(c.Path);   // an explicit choice goes up even on a new PC
        }
        State.Log("Conflict", $"Kept {(keepLocal ? "this PC's" : "GitHub's")} copy of {Summary(list.Select(c => c.Path))}");
        Conflicts = Conflicts.Except(list).ToList();
        return Run(mode, ct);
    }

    // ------------------------------------------------------------------ history / restore

    public async Task RefreshHistory(CancellationToken ct)
    {
        if (!Configured) return;
        try
        {
            gh.Token = Token!;
            gh.Repo = cfg.Repository.Trim();
            History = await gh.RecentCommits(cfg.Branch, 100, ct);
        }
        catch (Exception e) { Status = "History failed: " + e.Message; }
    }

    /// <summary>Plugin names present in a snapshot (for the per-plugin restore list).</summary>
    public async Task<List<string>> PluginsIn(string commitSha, CancellationToken ct)
    {
        gh.Token = Token!;
        gh.Repo = cfg.Repository.Trim();
        var files = await gh.GetTreeFiles(await gh.GetCommitTree(commitSha, ct), ct);
        return files.Where(f => f.Path.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(f => PluginOf(f.Path[Prefix.Length..])).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes a new snapshot on GitHub equal to an old one (all plugins, or just one), then pulls it.
    /// The newer snapshots stay in history, so a restore can itself be undone.
    /// </summary>
    public async Task Restore(CommitInfo snapshot, string? plugin, CancellationToken ct)
    {
        if (!Configured || !await gate.WaitAsync(0, ct)) return;
        var what = plugin ?? "all plugins";
        try
        {
            gh.Token = Token!;
            gh.Repo = cfg.Repository.Trim();
            Status = $"Restoring {what}...";
            var head = await gh.GetHead(cfg.Branch, ct) ?? throw new InvalidOperationException("The repository is empty.");
            bool Mine(string path) => path.StartsWith(Prefix, StringComparison.Ordinal) &&
                                      (plugin == null || PluginOf(path[Prefix.Length..]).Equals(plugin, StringComparison.OrdinalIgnoreCase));
            var old = (await gh.GetTreeFiles(await gh.GetCommitTree(snapshot.Sha, ct), ct)).Where(f => Mine(f.Path)).ToList();
            var now = head.Files.Where(f => Mine(f.Path)).ToList();
            var entries = old.Select(f => new KeyValuePair<string, string?>(f.Path, f.Sha))
                .Concat(now.Where(n => old.All(o => o.Path != n.Path)).Select(n => new KeyValuePair<string, string?>(n.Path, null)))
                .ToList();
            if (entries.Count == 0) { Status = "Nothing to restore."; return; }
            var tree = await gh.CreateTree(head.TreeSha, entries, ct);
            var msg = $"Restore from {cfg.MachineName}: {what} as of {snapshot.When:g}";
            var commit = await gh.CreateCommit(msg, tree, head.CommitSha, ct);
            if (!await gh.MoveBranch(cfg.Branch, commit, false, ct)) { Status = "The other PC pushed at the same moment; try again."; return; }
            // Treat this PC's copies of the restored files as unchanged, so the sync pulls the restored versions.
            var local = ScanLocal();
            foreach (var f in old)
            {
                var rel = f.Path[Prefix.Length..];
                if (local.TryGetValue(rel, out var lf)) State.BaseShas[rel] = lf.Sha;
            }
            State.Log("Restore", $"{what} as of {snapshot.When:g}");
        }
        catch (Exception e) { Status = "Restore failed: " + e.Message; Plugin.Log.Warning(e, "Settings Sync restore"); return; }
        finally { SaveState(); gate.Release(); }
        await Run(SyncMode.Running, ct, pullOnly: true);
        await RefreshHistory(ct);
    }
}
