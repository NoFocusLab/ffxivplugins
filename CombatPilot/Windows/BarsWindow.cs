using System;
using System.Linq;
using System.Numerics;
using CombatPilot.Engine;
using CombatPilot.Jobs;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatPilot.Windows;

/// <summary>
/// The job's control bar, loaded for whichever job you're on: mode, engage, burst, AoE, travel
/// reserve, raise MP, opener, auto-move and potions, as iOS-style switches and segmented controls.
/// The job's ability icons and timers are in Cooldown Grid (Offense group); clicking one there
/// queues it here in Live (next slot it fits, ahead of the plan).
/// </summary>
public sealed unsafe class BarsWindow : Window
{
    private readonly Plugin plugin;
    private static readonly Vector4 HoldText = new(1f, 0.62f, 0.04f, 1f);

    public BarsWindow(Plugin plugin) : base("Combat Pilot bar###CombatPilotBars")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoScrollbar;
    }

    public override bool DrawConditions() => plugin.Configuration.ShowBars && plugin.Pilot.Job != null;

    public override void PreDraw() => WindowName = $"Combat Pilot: {plugin.Pilot.Job?.Def.Job}###CombatPilotBars";

    public override void Draw()
    {
        var p = plugin.Pilot;
        var job = p.Job!;
        var c = plugin.Configuration;
        var t = c.TogglesFor(job.Def.Job);
        var changed = false;
        const float gap = 14;
        var vertical = c.BarVertical;
        Controls.LabelWidth = vertical ? 110 * ImGuiHelpers.GlobalScale : 0;
        // Between controls: a new line when vertical, side by side otherwise.
        void Sep() { if (!vertical) ImGui.SameLine(0, gap); }

        // ---- header: options, layout
        if (ImGui.SmallButton("Options")) plugin.OpenSettings();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open Combat Pilot's settings.");
        ImGui.SameLine();
        if (ImGui.SmallButton(vertical ? "Rows" : "Stack")) { c.BarVertical = !vertical; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(vertical ? "Lay the switches out side by side." : "Stack the switches vertically.");

        // ---- mode, pause, engage
        var mode = c.ModeFor(job.Def.Job);
        var mi = Controls.Segmented("mode", "Mode", new[] { "Off", "Listen", "Live" }, mode switch { PilotMode.Live => 2, PilotMode.Listen => 1, _ => 0 },
            "Off: nothing. Listen: RSR flies, Combat Pilot logs where it would differ. Live: Combat Pilot presses.", onIndex: 2);
        if (mi >= 0) plugin.SetMode(mi switch { 2 => PilotMode.Live, 1 => PilotMode.Listen, _ => PilotMode.Off });
        if (mode == PilotMode.Live)
        {
            Sep();
            if (Controls.Switch("pause", "Running", !p.Paused, "Kill switch (also Ctrl+Shift+End): off pauses Live.", warnWhenOff: true))
                p.SetPaused(!p.Paused, "bar");
        }
        Sep();
        var ei = Controls.Segmented("engage", "Engage", new[] { "Wait", "Fighting", "Any" }, (int)c.Engage,
            "Starting a fight: wait for combat / attack targets already fighting / attack any target (pulls, dummies).");
        if (ei >= 0) { c.Engage = (EngageMode)ei; changed = true; }

        // ---- row 2: rotation switches
        var first = true;
        void Next() { if (!first) Sep(); first = false; }

        if (job.Def.Burst.Count > 0)
        {
            Next();
            if (Controls.Switch("burst", "Burst", !t.HoldBurst,
                    "On: burst used when it's best. Off (held): " + string.Join(", ", job.Def.Burst) + " wait until you switch back (or queue one).",
                    warnWhenOff: true))
            { t.HoldBurst = !t.HoldBurst; changed = true; }
        }

        Next();
        var ai = Controls.Segmented("aoe", "AoE", new[] { "Off", "Auto", "Force" }, t.Aoe switch { AoeMode.SingleTarget => 0, AoeMode.Auto => 1, _ => 2 },
            "Auto: AoE vs single target from the enemies each AoE would really hit (area, hitboxes, time to live).\n" +
            "Off: nothing that hits more than your target (focus mechanics). Force: AoEs count as hitting at least 3.",
            warnIndex: 0);
        if (ai >= 0) { t.Aoe = ai switch { 0 => AoeMode.SingleTarget, 1 => AoeMode.Auto, _ => AoeMode.Force }; changed = true; }

        if (job.Def.Travel is { } tr)
        {
            Next();
            if (Controls.Switch("travel", $"Keep {tr.Action}", t.KeepTravel,
                    $"On: keep {tr.Keep} {tr.Action} charge(s) for getting back to the target. Queuing {tr.Action} still uses one."))
            { t.KeepTravel = !t.KeepTravel; changed = true; }
        }

        if (job.RaiseMpCost > 0)
        {
            Next();
            if (Controls.Switch("rmp", "Keep raise MP", t.KeepRaiseMp,
                    $"On: never spend MP below {job.RaiseMpCost:N0} (what {job.RaiseName} costs). Off: all MP goes to damage."))
            { t.KeepRaiseMp = !t.KeepRaiseMp; changed = true; }
        }

        if (OpenerRunner.Pick(job, t) is { } op)
        {
            Next();
            if (Controls.Switch("op", "Opener", t.OpenerOnCountdown,
                    $"On: run \"{op.Name}\" when a countdown starts (first action {op.Prepull:0.0}s before 0).\n" +
                    $"Right-click: run it now, from the next GCD.\nSource: {op.Source}\n" +
                    string.Join("\n", op.Steps.Select((st, i) => $"{i + 1}. {string.Join(" + ", st)}"))))
            { t.OpenerOnCountdown = !t.OpenerOnCountdown; changed = true; }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) p.StartOpenerNow();
        }

        Next();
        if (Controls.Switch("move", "Auto-move", c.AutoMove,
                "BossMod's AI handles mechanics first (set its AI to mechanics only); then positionals, your range rules\n" +
                "(Settings > Movement) and moving into a pack when an AoE around you is worth it. Only to spots with floor,\n" +
                "outside danger zones, on a safe path. Stops if you move yourself." + (p.Mover.Status.Length > 0 ? $"\nNow: {p.Mover.Status}" : "")))
        { c.AutoMove = !c.AutoMove; changed = true; if (!c.AutoMove) p.Mover.Halt("off"); }

        Next();
        if (Controls.Switch("pot", "Potions", t.Potions,
                "Use your " + job.Def.MainStat + " potion in the plan (Settings: highest owned or a specific one).\nNow: " + p.PotionStatus))
        { t.Potions = !t.Potions; changed = true; p.PotionSettingsChanged(); }

        if (changed) c.Save();

        if (Facing.GameAutoFaceOn() == false)
        {
            ImGui.TextColored(HoldText, "Game setting \"Face target when using an action\" is off");
            ImGui.SameLine();
            if (ImGui.SmallButton("Turn it on")) Facing.TurnGameAutoFaceOn();
        }

        // Ability icons and timers live in Cooldown Grid (its Offense group); clicks there queue here.
        if (p.Opener.Status.Length > 0) ImGui.TextColored(HoldText, p.Opener.Status);
        if (p.Sequence.Running) ImGui.TextColored(HoldText, p.Sequence.Status);
        if (p.Queued is { } q)
        {
            ImGui.TextColored(HoldText, $"Queued: {q.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) p.ClearQueue();
        }
    }

}
