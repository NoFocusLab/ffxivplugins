using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SettingsSync.Windows;

/// <summary>Files changed on this PC and on GitHub since they last agreed: you pick which copy wins.</summary>
public sealed class ConflictWindow : Window
{
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private readonly Plugin plugin;
    private readonly HashSet<string> ticked = new();

    /// <summary>Closed with the X: don't pop up again until the next sync finds conflicts or you open it.</summary>
    public bool Dismissed { get; set; }
    private int lastCount;

    public ConflictWindow(Plugin plugin) : base("Settings Sync - changed on both PCs###SettingsSyncConflicts")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 300), MaximumSize = new Vector2(1200, 1000) };
    }

    public override void OnClose() => Dismissed = true;

    public override void Draw()
    {
        var s = plugin.Syncer;
        var list = s.Conflicts;
        if (list.Count != lastCount) { lastCount = list.Count; ticked.Clear(); }
        if (list.Count == 0) { ImGui.TextUnformatted("Nothing to decide."); return; }

        ImGui.TextWrapped("These files changed on this PC and on GitHub since the last sync. Pick which copy to keep. " +
                          "The other copy is not lost: it stays in GitHub history (History tab).");
        if (!s.State.FirstSyncDone)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), "New PC: you almost certainly want GitHub's copy (from your set-up PC).");
        var from = list[0].RemoteFrom;
        ImGui.TextColored(Dim, $"GitHub's copy is from: {(string.IsNullOrEmpty(from) ? "unknown" : from)}");

        ImGui.BeginDisabled(s.Busy);
        if (ImGui.Button("Keep GitHub's copy of ALL")) _ = Task.Run(() => s.Resolve(list, false, SyncMode.Running, plugin.Life));
        ImGui.SameLine();
        if (ImGui.Button("Keep this PC's copy of ALL")) _ = Task.Run(() => s.Resolve(list, true, SyncMode.Running, plugin.Life));
        var chosen = list.Where(c => ticked.Contains(c.Path)).ToList();
        ImGui.BeginDisabled(chosen.Count == 0);
        if (ImGui.Button($"Ticked ({chosen.Count}): keep GitHub's")) _ = Task.Run(() => s.Resolve(chosen, false, SyncMode.Running, plugin.Life));
        ImGui.SameLine();
        if (ImGui.Button($"Ticked ({chosen.Count}): keep this PC's")) _ = Task.Run(() => s.Resolve(chosen, true, SyncMode.Running, plugin.Life));
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.TextColored(Dim, "GitHub's copies are applied the next time the game starts.");

        if (!ImGui.BeginTable("##conflicts", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(0, 0))) return;
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Changed here", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableHeadersRow();
        foreach (var c in list)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var on = ticked.Contains(c.Path);
            if (ImGui.Checkbox($"##{c.Path}", ref on)) { if (on) ticked.Add(c.Path); else ticked.Remove(c.Path); }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.Path);
            ImGui.TableNextColumn();
            ImGui.TextColored(Dim, $"{c.LocalTime:g}");
        }
        ImGui.EndTable();
    }
}
