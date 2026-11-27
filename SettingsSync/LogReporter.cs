using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SettingsSync;

/// <summary>
/// Sends Dalamud logs to the settings repository's "logs" branch, so a crash on one PC can be looked at
/// from another PC or a phone. Each report is a folder logs/&lt;PC&gt;/&lt;date_time&gt;_&lt;crash|manual&gt;/ with
/// plain-text files (readable in GitHub's web/mobile view):
/// README.md (summary), crash.log (Dalamud's crash report), dalamud.log (last ~2 MB before the crash),
/// troubleshooting.json (plugin list and versions). Crash dumps (.dmp, ~150 MB) are never sent.
/// Reports older than 30 days are removed with each new one.
/// </summary>
public sealed class LogReporter
{
    public const string Branch = "logs";
    private const int TailBytes = 2 * 1024 * 1024;
    private const int KeepDays = 30;
    private const int MaxLine = 1000;

    private readonly string dir;   // ...\XIVLauncher
    private readonly SemaphoreSlim gate = new(1, 1);

    public LogReporter(string xivLauncherDir) => dir = xivLauncherDir;

    public string Status { get; private set; } = "";
    public bool Busy => gate.CurrentCount == 0;

    /// <summary>Crash logs (newest first) from the last 7 days that haven't been sent yet.</summary>
    public List<(string File, DateTime When)> NewCrashes(ICollection<string> alreadySent) =>
        Directory.EnumerateFiles(dir, "dalamud_appcrash_*.log")
            .Select(f => (File: f, When: CrashTime(Path.GetFileName(f))))
            .Where(c => c.When > DateTime.Now.AddDays(-7) && !alreadySent.Contains(Path.GetFileName(c.File)))
            .OrderByDescending(c => c.When).Take(3).ToList();

    /// <summary>"dalamud_appcrash_20261117_071230_730_70904.log" -> 2026-11-17 07:12:30 (local time).</summary>
    public static DateTime CrashTime(string name)
    {
        var parts = name.Split('_');
        return parts.Length >= 4 && DateTime.TryParseExact(parts[2] + parts[3], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var t) ? t : DateTime.MinValue;
    }

    /// <summary>Uploads one report. crashFile null = a manual "send logs now" of the current session.</summary>
    public async Task<bool> Send(GitHub gh, string machine, string deviceType, string? crashFile, string note, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return false;
        try
        {
            var when = crashFile != null ? CrashTime(Path.GetFileName(crashFile)) : DateTime.Now;
            var kind = crashFile != null ? "crash" : "manual";
            var folder = $"logs/{Safe(machine)}/{when:yyyy-MM-dd_HH-mm-ss}_{kind}";
            Status = $"Sending {kind} report...";

            var files = new List<KeyValuePair<string, string>>();
            string? firstLine = null;
            if (crashFile != null)
            {
                var crash = ReadText(crashFile, int.MaxValue);
                firstLine = crash.Split('\n').FirstOrDefault()?.Trim();
                files.Add(new($"{folder}/crash.log", crash));
            }
            var (logName, log) = LogAround(crashFile != null ? when : DateTime.MaxValue);
            if (log.Length > 0) files.Add(new($"{folder}/dalamud.log", log));
            var ts = Path.Combine(dir, "dalamud.troubleshooting.json");
            if (File.Exists(ts)) files.Add(new($"{folder}/troubleshooting.json", ReadText(ts, int.MaxValue)));

            var readme = new StringBuilder()
                .AppendLine($"# {(crashFile != null ? "Crash" : "Logs")} on {machine}: {when:yyyy-MM-dd HH:mm:ss}")
                .AppendLine()
                .AppendLine($"- PC: {machine}{(deviceType.Length > 0 ? $" ({deviceType})" : "")}")
                .AppendLine($"- Sent: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (firstLine != null) readme.AppendLine($"- Crash: `{firstLine}`");
            if (note.Trim().Length > 0) readme.AppendLine($"- Note: {note.Trim()}");
            readme.AppendLine()
                .AppendLine("| File | What |").AppendLine("|---|---|");
            if (crashFile != null) readme.AppendLine("| crash.log | Dalamud's crash report: error, call stack, loaded plugins |");
            if (log.Length > 0) readme.AppendLine($"| dalamud.log | last ~2 MB of {logName}{(crashFile != null ? " up to the crash" : "")} |");
            readme.AppendLine("| troubleshooting.json | plugin list, versions, Dalamud and game version |");
            files.Insert(0, new($"{folder}/README.md", readme.ToString()));

            var head = await gh.GetHead(Branch, ct);
            // Old reports out, in the same commit.
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            var deletes = (head?.Files ?? new List<RemoteFile>())
                .Where(f => ReportTime(f.Path) is { } t && t < cutoff)
                .Select(f => new KeyValuePair<string, string?>(f.Path, null)).ToList();
            var tree = await gh.CreateTreeWithText(head?.TreeSha, files, deletes, ct);
            var commit = await gh.CreateCommit($"{(crashFile != null ? "Crash" : "Logs")} from {machine} at {when:g}", tree, head?.CommitSha, ct);
            if (!await gh.MoveBranch(Branch, commit, head == null, ct)) { Status = "Another PC sent a report at the same moment; try again."; return false; }
            Status = $"Sent {kind} report ({when:g}){(deletes.Count > 0 ? $", removed {deletes.Count} old file(s)" : "")}.";
            return true;
        }
        catch (Exception e)
        {
            Status = "Sending logs failed: " + e.Message;
            Plugin.Log.Warning(e, "Settings Sync: log report");
            return false;
        }
        finally { gate.Release(); }
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

    /// <summary>logs/PC/2026-11-17_07-12-30_crash/... -> 2026-11-17 07:12:30.</summary>
    private static DateTime? ReportTime(string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 3 || parts[0] != "logs" || parts[2].Length < 19) return null;
        return DateTime.TryParseExact(parts[2][..19], "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
    }

    /// <summary>
    /// The last ~2 MB of the session log that covers <paramref name="until"/>, ending just after it.
    /// Dalamud starts a new dalamud.log each session (the previous one becomes dalamud.old.log), so a
    /// crash found at the next start is in the old file.
    /// </summary>
    private (string Name, string Text) LogAround(DateTime until)
    {
        foreach (var name in new[] { "dalamud.log", "dalamud.old.log" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var text = ReadText(path, int.MaxValue);
            var start = LineTime(text, 0);
            if (until != DateTime.MaxValue && start != null && start > until) continue;   // this session began after the crash
            var end = text.Length;
            if (until != DateTime.MaxValue)
            {
                var stop = until.AddSeconds(10);
                for (var i = text.IndexOf('\n'); i >= 0 && i < text.Length - 1; i = text.IndexOf('\n', i + 1))
                    if (LineTime(text, i + 1) is { } t && t > stop) { end = i + 1; break; }
            }
            // Shorten giant lines (repo listings, base64 blobs) so the 2 MB reaches further back in time.
            var window = text[Math.Max(0, end - 6 * TailBytes)..end];
            var lines = window.Split('\n').Skip(end > 6 * TailBytes ? 1 : 0)
                .Select(l => l.Length > MaxLine ? l[..MaxLine] + $" ...[{l.Length - MaxLine} more chars cut]" : l);
            var slim = string.Join('\n', lines);
            var from = Math.Max(0, slim.Length - TailBytes);
            if (from > 0) { var nl = slim.IndexOf('\n', from); if (nl >= 0) from = nl + 1; }
            return (name, slim[from..]);
        }
        return ("", "");
    }

    /// <summary>"2026-11-26 16:22:17.478 -05:00 [INF] ..." at position i -> that time.</summary>
    private static DateTime? LineTime(string text, int i) =>
        i + 19 <= text.Length && DateTime.TryParseExact(text.AsSpan(i, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var t) ? t : null;

    private static string ReadText(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, new UTF8Encoding(false, false));
        var s = sr.ReadToEnd();
        return s.Length > max ? s[^max..] : s;
    }
}
