using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SettingsSync.Windows;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f);

    private readonly Plugin plugin;
    private string excludesText = "";
    private bool excludesLoaded;
    private string? previewText;

    // History tab
    private bool historyLoaded;
    private readonly ConcurrentDictionary<string, List<string>> pluginsBySnapshot = new();
    private readonly Dictionary<string, int> selectedPlugin = new();
    private string? confirmKey;
    private string historyPc = "";   // "" = all PCs

    public MainWindow(Plugin plugin) : base("Settings Sync###SettingsSyncMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 380), MaximumSize = new Vector2(1200, 1000) };
    }

    private Configuration C => plugin.Configuration;
    private Syncer S => plugin.Syncer;

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##ssynctabs")) return;
        if (ImGui.BeginTabItem("Sync")) { DrawSync(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("History")) { DrawHistory(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ------------------------------------------------------------------ Sync tab

    private void DrawSync()
    {
        if (!S.Configured)
        {
            ImGui.TextColored(Warn, S.SignedIn ? "Set the repository on the Settings tab." : "Not signed in yet: open the Settings tab to set up.");
            ImGui.Separator();
        }

        ImGui.TextUnformatted($"This PC: {C.MachineName}    Repository: {(C.Repository.Length > 0 ? C.Repository : "(not set)")}");
        ImGui.TextUnformatted($"Last push: {When(S.State.LastPush)}");
        ImGui.TextUnformatted($"Last pull: {When(S.State.LastPull)}{(string.IsNullOrEmpty(S.State.LastPullFrom) ? "" : $"  (from {S.State.LastPullFrom})")}");
        ImGui.TextColored(S.Busy ? Warn : Dim, S.Status);
        if (S.Configured && !S.State.FirstSyncDone) DrawFirstSync();
        if (plugin.StartupNote.Length > 0) ImGui.TextColored(Good, plugin.StartupNote);
        if (S.PendingCount > 0)
            ImGui.TextColored(Warn, $"{S.PendingCount} pulled file(s) waiting: restart the game to apply them.");
        if (S.PendingDalamud > 0)
            ImGui.TextColored(Warn, $"{S.PendingDalamud} Dalamud setting(s) waiting: applied after you close the game.");
        if (S.Conflicts.Count > 0)
        {
            ImGui.TextColored(Warn, $"{S.Conflicts.Count} file(s) changed on both PCs.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Decide...")) plugin.OpenConflicts();
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(S.Busy || !S.Configured);
        if (ImGui.Button("Sync everything now", new Vector2(180, 0))) plugin.SyncNow();
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.TextUnformatted("Plugin settings");
        ImGui.SameLine();
        ImGui.BeginDisabled(S.Busy || !S.Configured);
        if (ImGui.SmallButton("Sync now##plugins")) plugin.SyncNow(groups: SyncGroups.Plugins);
        ImGui.SameLine();
        if (ImGui.SmallButton("Push only")) plugin.SyncNow(pushOnly: true, groups: SyncGroups.Plugins);
        ImGui.SameLine();
        if (ImGui.SmallButton("Pull only")) plugin.SyncNow(pullOnly: true, groups: SyncGroups.Plugins);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pulled files are applied the next time the game starts\n(running plugins would overwrite them otherwise).");
        var changed = false;
        changed |= Box("Automatic sync", C.AutoSync, v => C.AutoSync = v,
            "Off = nothing happens unless you press a button (use this when testing with two PCs open).");
        ImGui.BeginDisabled(!C.AutoSync);
        ImGui.Indent();
        changed |= Box("Pull when the game starts", C.SyncOnOpen, v => C.SyncOnOpen = v,
            "Runs before your other plugins load, so they start with the newest settings.");
        changed |= Box("Push when you log out or close the game", C.SyncOnClose, v => C.SyncOnClose = v, null);
        var mins = C.PushWhilePlayingMinutes;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Push changes while playing, every N minutes (0 = off)", ref mins, 5, 15))
        { C.PushWhilePlayingMinutes = Math.Clamp(mins, 0, 240); changed = true; }
        ImGui.Unindent();
        ImGui.EndDisabled();

        ImGui.Separator();
        DrawDalamud(ref changed);

        ImGui.Separator();
        ImGui.TextUnformatted("Recent activity");
        if (ImGui.BeginChild("##activity", new Vector2(0, 0), true))
        {
            var activity = S.State.ActivitySnapshot();
            foreach (var a in activity)
            {
                ImGui.TextColored(a.Kind == "Error" || a.Kind == "Conflict" ? Warn : Dim, $"{a.When:g}  {a.Kind,-8}");
                ImGui.SameLine();
                ImGui.TextWrapped(a.Detail);
            }
            if (activity.Count == 0) ImGui.TextColored(Dim, "(nothing yet)");
        }
        ImGui.EndChild();

        if (changed) C.Save();
    }

    private void DrawFirstSync()
    {
        ImGui.Separator();
        ImGui.TextColored(Warn, "New PC: first sync.");
        ImGui.TextWrapped("Nothing from this PC is uploaded yet, so a half-set-up PC can't overwrite your others. " +
                          "Press Sync everything now, pick GitHub's copy in the \"changed on both PCs\" window " +
                          "(unless you really want this PC's), restart the game, and check things look right. Then confirm.");
        if (S.HeldBack > 0)
            ImGui.TextColored(Dim, $"{S.HeldBack} file(s) only this PC has are waiting; they go up when you confirm.");
        ImGui.BeginDisabled(S.Busy || S.Conflicts.Count > 0);
        if (Confirm("firstsync", "This PC is set up: start uploading its changes")) S.ConfirmFirstSync();
        ImGui.EndDisabled();
        if (S.Conflicts.Count > 0) { ImGui.SameLine(); ImGui.TextColored(Dim, "(decide the conflicts first)"); }
        ImGui.Separator();
    }

    private void DrawDalamud(ref bool changed)
    {
        ImGui.TextUnformatted("Dalamud setup");
        ImGui.SameLine();
        ImGui.BeginDisabled(S.Busy || !S.Configured);
        if (ImGui.SmallButton("Sync now##dalamud")) plugin.SyncNow(groups: SyncGroups.Dalamud);
        ImGui.EndDisabled();
        ImGui.TextColored(Dim, "Custom repos, plugin on/off, favourites/hidden/pinned, theme, server info bar, update preferences.");
        changed |= Box("Sync Dalamud setup automatically", C.DalamudAuto, v => C.DalamudAuto = v,
            "When the game is up, on logout/close, and with the periodic push.\n" +
            "On/off switches apply right away; repo list, theme etc. apply after you close the game.");

        var repos = S.MissingRepos;
        if (repos.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Warn, $"Custom repos on the other PC but not here ({repos.Count}):");
            ImGui.TextColored(Dim, "They're added for you after the game closes. To install plugins now, add them yourself:");
            if (ImGui.SmallButton("Open Dalamud settings")) plugin.OpenDalamudSettings();
            ImGui.SameLine();
            ImGui.TextColored(Dim, "Experimental > Custom Plugin Repositories: paste, +, then Save and Close.");
            foreach (var url in repos.ToList())
            {
                ImGui.PushID("repo" + url);
                if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(url);
                ImGui.SameLine();
                ImGui.TextUnformatted(url);
                ImGui.PopID();
            }
        }

        var missing = S.Missing;
        if (missing.Count == 0) return;
        ImGui.Spacing();
        ImGui.TextColored(Warn, $"Installed on another PC but not here ({missing.Count}):");
        ImGui.TextColored(Dim, "Install opens the plugin installer searched to it. Not found? Add the repos above first.");
        foreach (var m in missing.ToList())
        {
            ImGui.PushID(m.InternalName);
            if (ImGui.SmallButton("Install")) plugin.OpenInstaller(m.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton("Ignore")) { C.IgnoredMissing.Add(m.InternalName); changed = true; S.Missing.Remove(m); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Don't list it on this PC (undo on the Settings tab).");
            ImGui.SameLine();
            ImGui.TextUnformatted(m.Name);
            if (!m.OnElsewhere) { ImGui.SameLine(); ImGui.TextColored(Dim, "(off on the other PC)"); }
            ImGui.PopID();
        }
    }

    private static string When(DateTime? t) => t == null ? "never" : $"{t.Value:g} ({Ago(t.Value)})";

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }

    private static bool Box(string label, bool v, Action<bool> set, string? tip)
    {
        var x = v;
        var hit = ImGui.Checkbox(label, ref x);
        if (tip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        if (hit) set(x);
        return hit;
    }

    // ------------------------------------------------------------------ History tab

    private void DrawHistory()
    {
        if (!S.Configured) { ImGui.TextColored(Warn, "Set up sign-in and the repository first (Settings tab)."); return; }
        if (!historyLoaded) { historyLoaded = true; _ = Task.Run(() => S.RefreshHistory(plugin.Life)); }

        ImGui.TextWrapped("The last 5 snapshots on GitHub, or the last 5 pushes from one PC. A snapshot is everything " +
                          "as it stood right after that push. Restoring makes a new snapshot equal to the old one " +
                          "(the newer ones stay in history, so a restore can be undone) and pulls it here; restart the game to apply.");
        if (ImGui.Button("Refresh")) { pluginsBySnapshot.Clear(); _ = Task.Run(() => S.RefreshHistory(plugin.Life)); }
        ImGui.SameLine();
        var all = S.History;
        var pcs = all.Select(c => c.PushedBy).Where(n => n != null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("Pushed from", historyPc.Length == 0 ? "All PCs" : historyPc))
        {
            if (ImGui.Selectable("All PCs", historyPc.Length == 0)) historyPc = "";
            foreach (var pc in pcs)
                if (ImGui.Selectable(pc + (pc.Equals(C.MachineName, StringComparison.OrdinalIgnoreCase) ? " (this PC)" : ""), pc == historyPc))
                    historyPc = pc;
            ImGui.EndCombo();
        }
        ImGui.Separator();

        var history = (historyPc.Length == 0 ? all : all.Where(c => string.Equals(c.PushedBy, historyPc, StringComparison.OrdinalIgnoreCase)))
            .Take(5).ToList();
        if (history.Count == 0) { ImGui.TextColored(Dim, "(no snapshots yet)"); return; }
        foreach (var c in history)
        {
            ImGui.PushID(c.Sha);
            ImGui.TextUnformatted($"{c.When:g}");
            ImGui.SameLine();
            ImGui.TextColored(Dim, c.Message.Split('\n')[0]);

            ImGui.BeginDisabled(S.Busy);
            if (Confirm($"all{c.Sha}", "Restore everything"))
                _ = Task.Run(() => S.Restore(c, null, plugin.Life));

            ImGui.SameLine();
            if (!pluginsBySnapshot.TryGetValue(c.Sha, out var names))
            {
                if (ImGui.SmallButton("Restore one plugin..."))
                {
                    pluginsBySnapshot[c.Sha] = new List<string>();
                    _ = Task.Run(async () =>
                    {
                        try { pluginsBySnapshot[c.Sha] = await S.PluginsIn(c.Sha, plugin.Life); }
                        catch (Exception e) { Plugin.Log.Warning(e, "Settings Sync: snapshot list"); pluginsBySnapshot.TryRemove(c.Sha, out _); }
                    });
                }
            }
            else if (names.Count == 0) ImGui.TextColored(Dim, "loading...");
            else
            {
                var sel = selectedPlugin.GetValueOrDefault(c.Sha);
                sel = Math.Clamp(sel, 0, names.Count - 1);
                ImGui.SetNextItemWidth(200);
                if (ImGui.BeginCombo("##plugin", names[sel]))
                {
                    for (var i = 0; i < names.Count; i++)
                        if (ImGui.Selectable(names[i], i == sel)) sel = i;
                    ImGui.EndCombo();
                }
                selectedPlugin[c.Sha] = sel;
                ImGui.SameLine();
                var name = names[sel];
                if (Confirm($"one{c.Sha}{name}", $"Restore {name}"))
                    _ = Task.Run(() => S.Restore(c, name, plugin.Life));
            }
            ImGui.EndDisabled();
            ImGui.Separator();
            ImGui.PopID();
        }
    }

    /// <summary>Two-click button: first click arms it, second click within the same item confirms.</summary>
    private bool Confirm(string key, string label)
    {
        if (confirmKey == key)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.25f, 0.2f, 1f));
            var go = ImGui.SmallButton($"Click again: {label}##{key}");
            ImGui.PopStyleColor();
            if (go) { confirmKey = null; return true; }
            return false;
        }
        if (ImGui.SmallButton($"{label}##{key}")) confirmKey = key;
        return false;
    }

    // ------------------------------------------------------------------ Settings tab

    private void DrawSettings()
    {
        var changed = false;

        ImGui.TextUnformatted("GitHub");
        var repo = C.Repository;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("Repository (owner/name)", "you/ffxiv-settings-sync", ref repo, 100)) { C.Repository = repo.Trim(); changed = true; }
        var id = C.ClientId;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint("GitHub App Client ID", "Iv23...", ref id, 60)) { C.ClientId = id.Trim(); changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("github.com > Settings > Developer settings > GitHub Apps > your app.\nNot a secret.");
        var branch = C.Branch;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputText("Branch", ref branch, 60)) { C.Branch = branch.Trim().Length > 0 ? branch.Trim() : "main"; changed = true; }

        if (S.SignedIn)
        {
            ImGui.TextColored(Good, "Signed in on this PC.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Sign out")) S.SignOut();
        }
        else if (S.PendingSignIn is { } code)
        {
            ImGui.TextUnformatted("1. Open");
            ImGui.SameLine();
            if (ImGui.SmallButton(code.Url)) Dalamud.Utility.Util.OpenLink(code.Url);
            ImGui.TextUnformatted("2. Log in to GitHub and enter this code:");
            ImGui.SameLine();
            ImGui.TextColored(Good, code.Code);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(code.Code);
            ImGui.TextColored(Dim, "Waiting for GitHub...");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel")) S.CancelSignIn();
        }
        else
        {
            ImGui.BeginDisabled(C.ClientId.Length == 0);
            if (ImGui.Button("Sign in with GitHub")) S.StartSignIn();
            ImGui.EndDisabled();
            if (C.ClientId.Length == 0) { ImGui.SameLine(); ImGui.TextColored(Dim, "(enter the Client ID first)"); }
        }
        ImGui.TextColored(Dim, S.Status);

        ImGui.Separator();
        ImGui.TextUnformatted("This PC");
        var machine = C.MachineName;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Name shown in history", ref machine, 40)) { C.MachineName = machine.Trim(); changed = true; }
        var wait = C.StartupWaitSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Max wait at game start (seconds)", ref wait, 1, 5)) { C.StartupWaitSeconds = Math.Clamp(wait, 2, 30); changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("If GitHub is slow, the game carries on after this long and syncs once it's up.");

        ImGui.Separator();
        ImGui.TextUnformatted("Screen");
        ImGui.TextColored(Dim, "Windows whose title bar is off the screen (e.g. a position synced from a bigger screen) are moved to the middle.");
        if (ImGui.SmallButton("Check windows now")) plugin.Rescue.RequestCheck();
        ImGui.SameLine();
        changed |= Box("Check automatically, every", C.RescueOffscreenWindows, v => C.RescueOffscreenWindows = v,
            "Off = only the button (plus one check shortly after the game starts).");
        ImGui.SameLine();
        ImGui.BeginDisabled(!C.RescueOffscreenWindows);
        var secs = C.RescueIntervalSeconds;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("seconds##rescue", ref secs, 5, 15)) { C.RescueIntervalSeconds = Math.Clamp(secs, 5, 600); changed = true; }
        ImGui.EndDisabled();
        if (plugin.Rescue.Stubborn.Count > 0)
            ImGui.TextColored(Warn, "Keep moving back off screen (their plugin sets the position; reset it in that plugin): " +
                                    string.Join(", ", plugin.Rescue.Stubborn.Select(WindowRescue.Label)));

        ImGui.Separator();
        ImGui.TextUnformatted("What syncs");
        var mb = C.MaxFileMb;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Skip files bigger than (MB)", ref mb, 1, 5)) { C.MaxFileMb = Math.Clamp(mb, 1, 50); changed = true; }

        if (!excludesLoaded) { excludesText = string.Join("\n", C.Excludes); excludesLoaded = true; }
        ImGui.TextColored(Dim, "Never sync (one per line, relative to pluginConfigs; * = any name, ** = any folders):");
        if (ImGui.InputTextMultiline("##excludes", ref excludesText, 8000, new Vector2(-1, 160)))
        {
            C.Excludes = excludesText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            changed = true;
        }
        if (ImGui.SmallButton("Reset to defaults"))
        {
            C.Excludes = new List<string>(Configuration.DefaultExcludes);
            excludesText = string.Join("\n", C.Excludes);
            changed = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Preview what would sync"))
        {
            previewText = "Scanning...";
            _ = Task.Run(() =>
            {
                var files = S.ScanLocal();
                var top = files.Values.GroupBy(f => Syncer.PluginOf(f.Rel))
                    .Select(g => (g.Key, Size: g.Sum(f => f.Size))).OrderByDescending(g => g.Size).Take(8)
                    .Select(g => $"{g.Key} {g.Size / 1024.0:0} KB");
                previewText = $"{files.Count} files, {files.Values.Sum(f => f.Size) / 1048576.0:0.0} MB. Biggest: {string.Join(", ", top)}";
            });
        }
        if (previewText != null) ImGui.TextWrapped(previewText);
        ImGui.TextColored(Dim, "Always skipped: Settings Sync's own settings and sign-in.");

        if (C.IgnoredMissing.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Plugins from other PCs not listed as missing here:");
            foreach (var n in C.IgnoredMissing.ToList())
            {
                ImGui.PushID("ign" + n);
                if (ImGui.SmallButton("List again")) { C.IgnoredMissing.Remove(n); changed = true; }
                ImGui.SameLine();
                ImGui.TextUnformatted(n);
                ImGui.PopID();
            }
        }

        if (changed) C.Save();
    }
}
