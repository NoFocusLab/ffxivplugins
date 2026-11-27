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
        var up = p.Party.Upcoming(120, p.Mit.Downtime());
        ImGui.TextDisabled(up.Count == 0
            ? "Party buffs: none expected (no buff jobs in party, or not seen yet)"
            : "Party buffs: " + string.Join(", ", up.OrderBy(u => u.From).Select(u => $"{u.Name} in {u.From:0}s ({p.Party.Basis.GetValueOrDefault(u.Name, "?")})")));
        var terr = p.Mit.FightTerritory();
        if (terr != 0) ImGui.TextDisabled($"Learned burst schedule for this fight: {p.Party.Learner.PullsFor(terr)} pull(s) recorded (used once 2+ agree).");
        if (p.StatsNow() is { } st)
        {
            ImGui.TextDisabled($"Your stats (level {st.Level}): Skill Speed {st.Sks}, crit {st.CritRate:P1} x{st.CritMult:0.000}, " +
                               $"direct hit {st.DhRate:P1} -> a guaranteed crit+DH is worth x{st.CritDhFactor:0.00} an average hit" +
                               (C.CritDhFromStats ? "" : " (not used: fixed value in Settings)"));
            ImGui.TextDisabled("GCD length and opener step timing come from your actual recast (skill/spell speed and haste included).");
        }
        var dt = p.Mit.Downtime();
        var tlt = p.Mit.TimelineTime();
        ImGui.TextDisabled(tlt < 0 ? "Fight timeline: none (Mit Planner not running one)"
            : $"Fight timeline {tlt:0}s; downtime: " + (dt.Count == 0 ? "none in the next 90 s" : string.Join(", ", dt.Select(w => $"{w.From:0}-{w.Until:0}s"))));
        if (p.LastState is { } gs && gs.Enemies.Count > 0)
        {
            var aoe = p.Job.Actions.Where(a => a.IsAoe && a.Def.Potency > 0)
                .Select(a => $"{a.Name} {(gs.HitTtks.TryGetValue(a.Id, out var h) ? h.Length : 0)}{(gs.WouldPull.Contains(a.Id) ? " (would pull)" : "")}");
            ImGui.TextDisabled($"Enemies fighting: {gs.Enemies.Count} (" +
                               string.Join(", ", gs.Enemies.Take(6).Select(e => e.Ttk >= 999 ? "TTK ?" : $"TTK {e.Ttk:0}s")) + ")");
            ImGui.TextDisabled("AoE would hit: " + string.Join(", ", aoe));
        }
        if (Facing.GameAutoFaceOn() == false)
        {
            ImGui.TextColored(new Vector4(1f, 0.62f, 0.04f, 1f), "Your \"Face target when using an action\" setting is off: actions on a target behind you can fail.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Turn it on")) Facing.TurnGameAutoFaceOn();
        }
        if (p.RsrUnloads > 0)
            ImGui.TextDisabled($"RotationSolver unloaded while Live ({p.RsrUnloads}x); it's loaded again when you leave Live.");
        if (p.RsrForcedOff > 0)
            ImGui.TextDisabled($"RotationSolver switched itself on {p.RsrForcedOff}x while Live and was turned off again (its auto-on/countdown settings).");
        if (p.LastState is { } ms && p.Job is { } mj && (mj.Def.MpValue > 0 || mj.Actions.Any(a => a.MpCost > 0)))
            ImGui.TextDisabled($"MP: {ms.Mp:N0}/{ms.MaxMp:N0}" +
                               (mj.RaiseMpCost > 0 ? (C.TogglesFor(mj.Def.Job).KeepRaiseMp ? $", keeping {mj.RaiseMpCost:N0} for {mj.RaiseName}" : $", not keeping MP for {mj.RaiseName}") : ""));
        if (p.LastState?.Position is { } pi)
            ImGui.TextDisabled(pi.Omni ? "Positionals: target has none (all land)"
                : $"Positionals: you're at the {pi.Side}" + (C.AutoMove ? $"; rear {Eta(pi.RearEta)}, flank {Eta(pi.FlankEta)}" : "; auto-move off (True North covers)") +
                  (pi.Note.Length > 0 ? $" ({pi.Note})" : "") + "");
        if (C.AutoMove)
            ImGui.TextDisabled($"Auto-move: {(p.Mover.Status.Length > 0 ? p.Mover.Status : "idle")}" + (p.Mover.Reason.Length > 0 ? $" ({p.Mover.Reason})" : "") +
                               (p.Mover.Bmr.Loaded ? "" : " - BossMod not loaded: mechanics and danger zones unknown"));
        if (p.LastState is { } ds && ds.DangerNote.Length > 0)
            ImGui.TextDisabled(ds.DangerNote + (ds.UnsafeDash.Count > 0
                ? " - held: " + string.Join(", ", p.Job.Actions.Where(a => ds.UnsafeDash.Contains(a.Id)).Select(a => $"{a.Name} ({ds.UnsafeWhy.GetValueOrDefault(a.Id, "?")})"))
                : ""));
        DrawDecision("Next GCD", p.LastGcd);
        DrawDecision("Last weave check", p.LastWeave);
        if (p.Log.Current is { } f)
            ImGui.TextDisabled($"This fight: {f.Decisions} decisions, {f.Mismatches} differed, est. {f.Lost:N0} potency lost" +
                               (f.SafeModePresses > 0 ? $", {f.SafeModePresses} safe-mode presses" : ""));
    }

    private static string Eta(double s) => double.IsInfinity(s) ? "can't" : $"{s:0.0}s away";

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

        if (!ImGui.BeginTable("##actions", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 0))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableSetupColumn("Potency", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Combo", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Area", ImGuiTableColumnFlags.WidthFixed, 130);
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
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Def.Potency > 0 ? (a.PotencySource == "data" && job.Level < 100 ? "data (Lv100!)" : a.PotencySource) : "");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(a.Shape switch
            {
                Shape.CircleSelf => $"circle {a.Radius:0}y (you)",
                Shape.CircleTarget => $"circle {a.Radius:0}y (target)",
                Shape.Cone => $"cone {a.Radius:0}y {a.Angle:0}°",
                Shape.Line => $"line {a.Radius:0}x{a.Width:0}y",
                _ => "",
            } + (a.Falloff > 0 ? $", -{a.Falloff:P0}" : ""));
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
        ImGui.TextDisabled("Engage, burst, AoE, travel reserve and potion switches are on the job bar (/pilot bar). Ability icons are in Cooldown Grid's Offense group; clicking one there queues it here.");
        var b = C.ShowBars;
        if (ImGui.Checkbox("Show the job bar", ref b)) { C.ShowBars = b; changed = true; }
        var qs = C.QueueSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("A queued action (Cooldown Grid click) waits for its slot (s)", ref qs, 1, 15, "%.0f")) { C.QueueSeconds = qs; changed = true; }

        b = C.NoPullWithAoe;
        if (ImGui.Checkbox("Never use an AoE that would hit an enemy that isn't fighting yet", ref b)) { C.NoPullWithAoe = b; changed = true; }

        ImGui.Separator();
        DrawMoveRules(ref changed);
        DrawPartners(ref changed);

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
        b = C.UnloadRsr;
        if (ImGui.Checkbox("Unload RotationSolver while a job is Live", ref b)) { C.UnloadRsr = b; changed = true; plugin.Pilot.ModeChanged(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("RSR is unloaded (Dalamud's temporary disable) while a job is Live, so it can't switch itself on\n" +
                                                   "for a countdown or when attacked. Loaded again when you leave Live, when Combat Pilot closes,\n" +
                                                   "or when the game restarts. Your saved plugin list isn't changed (Settings Sync won't copy it).");
        if (!C.UnloadRsr)
        {
            b = C.TurnOffRsr;
            if (ImGui.Checkbox("Keep RotationSolver off while a job is Live", ref b)) { C.TurnOffRsr = b; changed = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turned off when you go Live, and again whenever it switches itself back on: checked every frame.");
        }
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
        b = C.CritDhFromStats;
        if (ImGui.Checkbox("Value guaranteed crit+DH from my Critical Hit / Direct Hit stats", ref b)) { C.CritDhFromStats = b; changed = true; }
        var f = C.CritDhFactor;
        if (C.CritDhFromStats) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Fixed value instead (x average hit)", ref f, 1.2f, 2.2f, "%.2f")) { C.CritDhFactor = f; changed = true; }
        if (C.CritDhFromStats) ImGui.EndDisabled();
        f = C.QueueWindow;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Queue the next GCD this early (s)", ref f, 0.1f, 0.6f, "%.2f")) { C.QueueWindow = f; changed = true; }
        if (changed) C.Save();
    }


    private void DrawPartners(ref bool changed)
    {
        var job = plugin.Pilot.Job;
        var ally = job?.Actions.Where(a => a.Def.AllyTarget).ToList();
        if (job == null || ally == null || ally.Count == 0) return;
        ImGui.Separator();
        ImGui.TextUnformatted($"Partners for {job.Def.Job}: who your ally-targeted actions go on");
        foreach (var a in ally)
        {
            var r = C.PartnerFor(a.Name);
            ImGui.PushID("partner" + a.Name);
            ImGui.TextUnformatted(a.Name);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.BeginCombo("##mode", r.Mode == CombatPilot.Engine.PartnerMode.Name ? "a named player" : "best job in party"))
            {
                if (ImGui.Selectable("best job in party", r.Mode == CombatPilot.Engine.PartnerMode.JobOrder)) { r.Mode = CombatPilot.Engine.PartnerMode.JobOrder; changed = true; }
                if (ImGui.Selectable("a named player", r.Mode == CombatPilot.Engine.PartnerMode.Name)) { r.Mode = CombatPilot.Engine.PartnerMode.Name; changed = true; }
                ImGui.EndCombo();
            }
            if (r.Mode == CombatPilot.Engine.PartnerMode.Name)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(160);
                var name = r.Name;
                if (ImGui.InputText("Name##pn", ref name, 40)) { r.Name = name; changed = true; }
                ImGui.SameLine();
                if (ImGui.SmallButton("Use my target") && Plugin.TargetManager.Target is { } t) { r.Name = t.Name.TextValue; changed = true; }
            }
            var order = string.Join(", ", r.JobOrder);
            ImGui.SetNextItemWidth(420);
            if (ImGui.InputText("Job order (best first)##po", ref order, 200))
            {
                r.JobOrder = order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).ToList();
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Default")) { r.JobOrder = new(CombatPilot.Engine.Partners.DefaultOrder); changed = true; }
            ImGui.TextDisabled(r.Mode == CombatPilot.Engine.PartnerMode.Name
                ? "Named player first; if they're dead or out of range, the job order decides."
                : "Highest in the list who's alive and within 30 y; tanks, then healers, after any unlisted job.");
            ImGui.PopID();
        }
    }

    private void DrawMoveRules(ref bool changed)
    {
        var job = plugin.Pilot.Job;
        if (job == null) { ImGui.TextDisabled("Movement rules: switch to a job with a job file."); return; }
        var r = C.TogglesFor(job.Def.Job).Move;
        ImGui.TextUnformatted($"Movement rules for {job.Def.Job} (auto-move, bar toggle):");
        ImGui.TextDisabled("Mechanics come first: set BossMod's AI to handle mechanics only (no follow distance). Then these.");
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("Stay near##anchor", r.Anchor switch { AnchorKind.Target => "just the target", AnchorKind.Name => "a party member (name)", _ => "a party member (role)" }))
        {
            if (ImGui.Selectable("a party member (role)", r.Anchor == AnchorKind.Role)) { r.Anchor = AnchorKind.Role; changed = true; }
            if (ImGui.Selectable("a party member (name)", r.Anchor == AnchorKind.Name)) { r.Anchor = AnchorKind.Name; changed = true; }
            if (ImGui.Selectable("just the target", r.Anchor == AnchorKind.Target)) { r.Anchor = AnchorKind.Target; changed = true; }
            ImGui.EndCombo();
        }
        if (r.Anchor == AnchorKind.Role)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo("##role", r.AnchorRole.ToString()))
            {
                foreach (var ar in Enum.GetValues<AnchorRole>())
                    if (ImGui.Selectable(ar.ToString(), r.AnchorRole == ar)) { r.AnchorRole = ar; changed = true; }
                ImGui.EndCombo();
            }
        }
        else if (r.Anchor == AnchorKind.Name)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            var n = r.AnchorName;
            if (ImGui.InputTextWithHint("##aname", "Character name", ref n, 40)) { r.AnchorName = n; changed = true; }
        }
        float f;
        if (r.Anchor != AnchorKind.Target)
        {
            f = r.AnchorRange; ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("X: within this of them (y, 0 = any)", ref f, 0, 40, "%.0f")) { r.AnchorRange = f; changed = true; }
        }
        f = r.TargetRange; ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Y: within this of the target (y)", ref f, 1, 25, "%.0f")) { r.TargetRange = f; changed = true; }
        f = r.PackRange; ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Z: within this of the pack centre for AoE (y, 0 = from the AoE's size)", ref f, 0, 10, "%.0f")) { r.PackRange = f; changed = true; }
        f = r.AoeGain * 100; ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Move in for AoE only if it's this much better (%)", ref f, 0, 100, "%.0f")) { r.AoeGain = f / 100; changed = true; }
        if (ImGui.SmallButton("Reset to role defaults"))
        { Mover.Defaults(r, Plugin.ObjectTable.LocalPlayer?.ClassJob.ValueNullable?.Role ?? 0); changed = true; }
    }

    private static readonly VirtualKey[] KillKeys =
    {
        VirtualKey.END, VirtualKey.HOME, VirtualKey.PAUSE, VirtualKey.SCROLL, VirtualKey.INSERT, VirtualKey.DELETE,
        VirtualKey.F9, VirtualKey.F10, VirtualKey.F11, VirtualKey.F12, VirtualKey.P,
    };

    private string KeyName() =>
        (C.KillCtrl ? "Ctrl+" : "") + (C.KillShift ? "Shift+" : "") + (C.KillAlt ? "Alt+" : "") + C.KillKey;
}
