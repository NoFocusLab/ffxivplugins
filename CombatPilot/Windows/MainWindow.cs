using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatPilot.Engine;
using CombatPilot.Jobs;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;

namespace CombatPilot.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private Configuration C => plugin.Configuration;
    private bool selectReports;
    private FightSummary? openFight;
    private List<DecisionRecord> openDecisions = new();

    private static readonly Vector4 Ok = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.7f, 0.3f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.45f, 1f);

    public MainWindow(Plugin plugin) : base("Combat Pilot###CombatPilotMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 380), MaximumSize = new Vector2(2000, 2000) };
    }

    public void ShowReports()
    {
        IsOpen = true;
        selectReports = true;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##tabs")) return;
        if (ImGui.BeginTabItem("Pilot")) { DrawPilot(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Job data")) { DrawJobData(); ImGui.EndTabItem(); }
        var flags = selectReports ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (ImGui.BeginTabItem("Reports", flags)) { DrawReports(); ImGui.EndTabItem(); }
        selectReports = false;
        if (ImGui.BeginTabItem("Settings")) { DrawSettings(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ pilot

    private void DrawPilot()
    {
        var p = plugin.Pilot;
        ImGui.TextWrapped("Damage rotation for each job, planned live from what's happening now (a few GCDs ahead, " +
                          "re-planned every GCD). Mitigation stays with Mit Planner. Live presses the buttons and turns " +
                          "RotationSolver off for that job; Listen lets RSR fly and logs where Combat Pilot would differ.");
        ImGui.Separator();

        ImGui.TextUnformatted("Mode per job:");
        if (ImGui.BeginTable("##modes", 2, ImGuiTableFlags.SizingFixedFit))
        {
            foreach (var job in JobLibrary.All)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{job.Job}  (data: patch {job.Patch})");
                ImGui.TableNextColumn();
                var mode = C.ModeFor(job.Job);
                foreach (var m in Enum.GetValues<PilotMode>())
                {
                    if (ImGui.RadioButton($"{m}##{job.Job}", mode == m))
                    {
                        C.Modes[job.Job] = m;
                        C.Save();
                        plugin.Pilot.ModeChanged();
                    }
                    ImGui.SameLine();
                }
                ImGui.NewLine();
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Jobs without a job file stay with RSR. Kill switch: " + KeyName() + " or /pilot pause.");
        ImGui.Separator();

        if (p.Job == null) { ImGui.TextUnformatted("No job file for your current job."); return; }
        ImGui.TextColored(p.Paused || p.SafeMode ? Warn : Ok, $"{p.Job.Def.Job} (level {p.Job.Level}): {p.Status}");
        if (p.Mode == PilotMode.Live)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(p.Paused ? "Resume" : "Pause")) p.SetPaused(!p.Paused, "button");
        }
        var up = p.Party.Upcoming(120);
        ImGui.TextDisabled(up.Count == 0
            ? "Party buffs: none expected (no buff jobs in party, or not seen yet)"
            : "Party buffs: " + string.Join(", ", up.OrderBy(u => u.From).Select(u => $"{u.Name} in {u.From:0}s")));
        DrawDecision("Next GCD", p.LastGcd);
        DrawDecision("Last weave check", p.LastWeave);
        if (p.Log.Current is { } f)
            ImGui.TextDisabled($"This fight: {f.Decisions} decisions, {f.Mismatches} differed, est. {f.Lost:N0} potency lost" +
                               (f.SafeModePresses > 0 ? $", {f.SafeModePresses} safe-mode presses" : ""));
    }

    private static void DrawDecision(string title, Decision? d)
    {
        if (d == null) return;
        ImGui.TextUnformatted($"{title}: {d.Label}");
        if (d.Error != null) { ImGui.SameLine(); ImGui.TextColored(Bad, $"({d.Error})"); }
        if (d.Path.Count > 0) ImGui.TextDisabled("  plan: " + string.Join(" > ", d.Path.Take(12)));
        if (d.RootScores.Count > 1)
        {
            var best = d.RootScores.Values.Max();
            ImGui.TextDisabled("  vs: " + string.Join(", ", d.RootScores.OrderByDescending(k => k.Value).Skip(1).Take(4)
                .Select(k => $"{k.Key} -{best - k.Value:N0}")));
        }
    }

    // ================================================================ job data

    private void DrawJobData()
    {
        var job = plugin.Pilot.Job;
        if (job == null) { ImGui.TextUnformatted("Switch to a job with a job file (WAR) to see its data."); return; }
        ImGui.TextWrapped($"{job.Def.Job} job file, patch {job.Def.Patch}. {job.Def.Source}");
        ImGui.TextWrapped("Potency source \"game\" = read from the tooltip; \"data\" = the job file's number (tooltip unclear). " +
                          "A tooltip that differs from the file usually means a patch changed it: update the file.");
        if (job.Unresolved.Count > 0)
            ImGui.TextColored(Bad, "Not found in game data: " + string.Join(", ", job.Unresolved));
        if (ImGui.Button("Re-read")) plugin.Pilot.Rebind();

        if (!ImGui.BeginTable("##actions", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 0))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Potency", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Combo", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Check");
        ImGui.TableHeadersRow();
        foreach (var a in job.Actions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Level.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.IsGcd ? "GCD" : "oGCD");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Potency > 0 ? a.Potency.ToString("0") : "");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.ComboPotency > 0 ? a.ComboPotency.ToString("0") : "");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Def.Potency > 0 ? a.PotencySource : "");
            ImGui.TableNextColumn();
            if (a.TooltipPotency is { } t && Math.Abs(t - a.Def.Potency) > 0.5)
                ImGui.TextColored(Warn, $"file says {a.Def.Potency:0}");
        }
        ImGui.EndTable();
    }

    // ================================================================ reports

    private void DrawReports()
    {
        var log = plugin.Pilot.Log;
        ImGui.TextWrapped("Each fight: every decision point, what Combat Pilot wanted, what was pressed (by it or by RSR), " +
                          "and the estimated potency difference. Files are kept in the plugin's config folder.");
        ImGui.TextDisabled(log.Folder);
        if (log.History.Count == 0) { ImGui.TextUnformatted("No fights logged yet."); return; }

        if (ImGui.BeginTable("##fights", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 180)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("When");
            ImGui.TableSetupColumn("Where");
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Length", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Differed", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Est. lost", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();
            foreach (var f in log.History)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{f.Start:MM-dd HH:mm}##{f.File}", openFight == f, ImGuiSelectableFlags.SpanAllColumns))
                {
                    openFight = f;
                    openDecisions = log.Load(f);
                }
                ImGui.TableNextColumn(); ImGui.TextUnformatted(f.Place);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(f.Job);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(f.Mode);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{f.Seconds / 60:0}:{f.Seconds % 60:00}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{f.Mismatches}/{f.Decisions}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(f.Lost.ToString("N0"));
            }
            ImGui.EndTable();
        }

        if (openFight == null) return;
        if (openFight.Notes.Count > 0) ImGui.TextColored(Warn, "Notes: " + string.Join("; ", openFight.Notes));
        ImGui.TextDisabled("Biggest differences first:");
        if (!ImGui.BeginTable("##decisions", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 0))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Wanted");
        ImGui.TableSetupColumn("Pressed");
        ImGui.TableSetupColumn("Lost", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Plan");
        ImGui.TableHeadersRow();
        foreach (var r in openDecisions.Where(r => r.Wanted != r.Actual).OrderByDescending(r => r.Lost ?? 0))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{r.FightTime:0.0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Wanted);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Actual);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(r.Lost is { } l ? l.ToString("0") : "?");
            ImGui.TableNextColumn(); ImGui.TextDisabled(r.Plan);
        }
        ImGui.EndTable();
    }

    // ================================================================ settings

    private void DrawSettings()
    {
        var changed = false;
        ImGui.TextDisabled("Engage, burst, AoE, travel reserve and potion toggles are on the job bar (/pilot bar).");
        var b = C.ShowBars;
        if (ImGui.Checkbox("Show the job bar", ref b)) { C.ShowBars = b; changed = true; }
        var size = C.BarIconSize;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Bar icon size", ref size, 24, 72, "%.0f")) { C.BarIconSize = size; changed = true; }
        var qs = C.QueueSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("A clicked action waits for its slot (s)", ref qs, 1, 15, "%.0f")) { C.QueueSeconds = qs; changed = true; }

        ImGui.Separator();
        ImGui.TextUnformatted("Potions (turned on per job on the bar):");
        if (ImGui.RadioButton("Highest one in my bags", C.Potion == PotionChoice.Highest)) { C.Potion = PotionChoice.Highest; changed = true; plugin.Pilot.PotionSettingsChanged(); }
        if (ImGui.RadioButton("This one:", C.Potion == PotionChoice.Specific)) { C.Potion = PotionChoice.Specific; changed = true; plugin.Pilot.PotionSettingsChanged(); }
        ImGui.SameLine();
        var owned = plugin.Pilot.Job == null ? new List<OwnedPotion>() : Potions.Owned(plugin.Pilot.Job.Def.MainStat);
        var current = owned.FirstOrDefault(o => o.ItemId == C.PotionItemId);
        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo("##potion", current?.Label ?? (C.PotionItemId == 0 ? "(pick a potion)" : $"item #{C.PotionItemId} (none in bags)")))
        {
            foreach (var o in owned)
                if (ImGui.Selectable(o.Label, o.ItemId == C.PotionItemId))
                { C.PotionItemId = o.ItemId; C.Potion = PotionChoice.Specific; changed = true; plugin.Pilot.PotionSettingsChanged(); }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled("Now: " + plugin.Pilot.PotionStatus + ". [Estimate] valued at +6% damage for 30 s; placed by the planner (lines up with party buffs it can see coming).");

        ImGui.Separator();
        b = C.TurnOffRsr;
        if (ImGui.Checkbox("Turn RotationSolver off when a job is Live", ref b)) { C.TurnOffRsr = b; changed = true; }
        b = C.HandBackToRsr;
        if (ImGui.Checkbox("Turn RotationSolver back on for jobs that aren't Live", ref b)) { C.HandBackToRsr = b; changed = true; }
        b = C.ShowOverlay;
        if (ImGui.Checkbox("Show the small status panel", ref b)) plugin.SetOverlay(b);

        ImGui.Separator();
        ImGui.TextUnformatted("Kill switch (pause/resume Live):");
        b = C.KillCtrl; if (ImGui.Checkbox("Ctrl", ref b)) { C.KillCtrl = b; changed = true; }
        ImGui.SameLine(); b = C.KillShift; if (ImGui.Checkbox("Shift", ref b)) { C.KillShift = b; changed = true; }
        ImGui.SameLine(); b = C.KillAlt; if (ImGui.Checkbox("Alt", ref b)) { C.KillAlt = b; changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("Key", C.KillKey.ToString()))
        {
            foreach (var k in KillKeys)
                if (ImGui.Selectable(k.ToString(), k == C.KillKey)) { C.KillKey = k; changed = true; }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Planner:");
        var i = C.HorizonGcds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("GCDs to look ahead", ref i, 2, 12)) { C.HorizonGcds = i; changed = true; }
        i = C.BeamWidth;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Sequences kept per step", ref i, 8, 256)) { C.BeamWidth = i; changed = true; }
        var f = C.CritDhFactor;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Guaranteed crit+DH value (x average hit)", ref f, 1.2f, 2.2f, "%.2f")) { C.CritDhFactor = f; changed = true; }
        f = C.QueueWindow;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Queue the next GCD this early (s)", ref f, 0.1f, 0.6f, "%.2f")) { C.QueueWindow = f; changed = true; }
        if (changed) C.Save();
    }


    private static readonly VirtualKey[] KillKeys =
    {
        VirtualKey.END, VirtualKey.HOME, VirtualKey.PAUSE, VirtualKey.SCROLL, VirtualKey.INSERT, VirtualKey.DELETE,
        VirtualKey.F9, VirtualKey.F10, VirtualKey.F11, VirtualKey.F12, VirtualKey.P,
    };

    private string KeyName() =>
        (C.KillCtrl ? "Ctrl+" : "") + (C.KillShift ? "Shift+" : "") + (C.KillAlt ? "Alt+" : "") + C.KillKey;
}
