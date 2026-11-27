using System;
using System.Linq;
using System.Numerics;
using CombatPilot.Engine;
using CombatPilot.Jobs;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatPilot.Windows;

/// <summary>
/// The job's control bar, loaded for whichever job you're on: toggles (mode, engage, burst, AoE,
/// travel reserve, potions) and action buttons. Clicking an action queues it: in Live it goes into
/// the next slot it fits, ahead of the plan; otherwise it's used right away like a hotbar button.
/// </summary>
public sealed unsafe class BarsWindow : Window
{
    private readonly Plugin plugin;
    private static readonly Vector4 On = new(0.35f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 Hold = new(0.85f, 0.55f, 0.2f, 1f);
    private static readonly Vector4 Neutral = new(0.3f, 0.3f, 0.35f, 1f);
    private const uint DimColor = 0xA0000000;
    private const uint QueuedColor = 0xFF33CCFF;
    private const uint TextColor = 0xFFFFFFFF;
    private const uint ShadowColor = 0xFF000000;

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

        // ---- toggles
        var mode = c.ModeFor(job.Def.Job);
        if (Toggle($"{mode}##mode", mode == PilotMode.Live ? On : mode == PilotMode.Listen ? Neutral : Hold,
                   "Mode for this job: Live (presses) / Listen (RSR flies, logged) / Off. Click to cycle."))
            plugin.SetMode((PilotMode)(((int)mode + 1) % 3));
        ImGui.SameLine();
        if (mode == PilotMode.Live && Toggle(p.Paused ? "Paused##pause" : "Pause##pause", p.Paused ? Hold : Neutral, "Kill switch: pause/resume Live."))
            p.SetPaused(!p.Paused, "bar");
        if (mode == PilotMode.Live) ImGui.SameLine();

        var engage = c.Engage switch { EngageMode.WaitForCombat => "Engage: wait", EngageMode.EngagedOnly => "Engage: fighting", _ => "Engage: any" };
        if (Toggle(engage + "##engage", c.Engage == EngageMode.AnyTarget ? On : Neutral,
                   "Starting a fight: wait for combat / attack targets already fighting / attack any target (pulls, dummies). Click to cycle."))
        { c.Engage = (EngageMode)(((int)c.Engage + 1) % 3); changed = true; }
        ImGui.SameLine();

        if (job.Def.Burst.Count > 0)
        {
            if (Toggle(t.HoldBurst ? "Burst: HOLD##burst" : "Burst: auto##burst", t.HoldBurst ? Hold : On,
                       "Hold: " + string.Join(", ", job.Def.Burst) + " are not used until you switch back (or click one)."))
            { t.HoldBurst = !t.HoldBurst; changed = true; }
            ImGui.SameLine();
        }

        var aoe = t.Aoe switch { AoeMode.Auto => "AoE: auto", AoeMode.SingleTarget => "AoE: off", _ => "AoE: force" };
        if (Toggle(aoe + "##aoe", t.Aoe == AoeMode.Auto ? On : Hold,
                   "Auto: AoE vs single target worked out from the enemies each AoE would actually hit (area, hitboxes,\n" +
                   "how long they'll live). Off: no AoE that hits anything but your target (focus mechanics, things\n" +
                   "that shouldn't be hit). Force: treat AoEs as hitting at least 3. Click to cycle."))
        { t.Aoe = (AoeMode)(((int)t.Aoe + 1) % 3); changed = true; }

        if (job.Def.Travel is { } tr)
        {
            ImGui.SameLine();
            if (Toggle(t.KeepTravel ? $"Keep {tr.Keep} {tr.Action}##travel" : $"Spend all {tr.Action}##travel", t.KeepTravel ? On : Neutral,
                       $"Keep {tr.Keep} {tr.Action} charge(s) for getting back to the target. Clicking {tr.Action} on the bar still uses one."))
            { t.KeepTravel = !t.KeepTravel; changed = true; }
        }

        if (OpenerRunner.Pick(job, t) is { } op)
        {
            ImGui.SameLine();
            if (Toggle(t.OpenerOnCountdown ? "Opener: countdown##op" : "Opener: off##op", t.OpenerOnCountdown ? On : Neutral,
                       $"Run \"{op.Name}\" when a countdown is started (first action at {op.Prepull:0.0}s before 0).\n" +
                       $"Right-click: run it now, from the next GCD.\nSource: {op.Source}\n" +
                       string.Join("\n", op.Steps.Select((st, i) => $"{i + 1}. {string.Join(" + ", st)}"))))
            { t.OpenerOnCountdown = !t.OpenerOnCountdown; changed = true; }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) p.StartOpenerNow();
        }

        if (job.Actions.Any(a => a.Def.Positional != null))
        {
            ImGui.SameLine();
            if (Toggle(c.AutoMove ? "Auto-move: on##move" : "Auto-move: off##move", c.AutoMove ? On : Neutral,
                       "Walk to the rear/flank in time for positionals (vnavmesh), only to spots with floor and outside\n" +
                       "known danger zones; otherwise True North covers it. Stops if you move yourself." +
                       (p.Mover.Status.Length > 0 ? $"\nNow: {p.Mover.Status}" : "")))
            { c.AutoMove = !c.AutoMove; changed = true; if (!c.AutoMove) p.Mover.Halt("off"); }
        }

        ImGui.SameLine();
        if (Toggle(t.Potions ? "Potions: on##pot" : "Potions: off##pot", t.Potions ? On : Neutral,
                   "Use your " + job.Def.MainStat + " potion in the plan (Settings: highest owned or a specific one).\nNow: " + p.PotionStatus))
        { t.Potions = !t.Potions; changed = true; p.PotionSettingsChanged(); }

        if (changed) c.Save();

        // ---- action buttons
        var names = job.Def.Bar.Count > 0 ? job.Def.Bar : job.Actions.Where(a => !a.IsGcd && !a.IsItem).Select(a => a.Name).ToList();
        var size = new Vector2(c.BarIconSize);
        var first = true;
        foreach (var name in names.Concat(t.Potions && job.ByName.ContainsKey(JobBinding.PotionName) ? new[] { JobBinding.PotionName } : Array.Empty<string>()))
        {
            if (!job.ByName.TryGetValue(name, out var a)) continue;
            if (!first) ImGui.SameLine();
            first = false;
            DrawAction(a, size);
        }
        if (p.Opener.Status.Length > 0) ImGui.TextColored(Hold, p.Opener.Status);
        if (p.Queued is { } q)
        {
            ImGui.TextColored(Hold, $"Queued: {q.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) p.ClearQueue();
        }
    }

    private static bool Toggle(string label, Vector4 color, string tip)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        var clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        return clicked;
    }

    private void DrawAction(BoundAction a, Vector2 size)
    {
        var am = ActionManager.Instance();
        uint icon = a.Icon;
        float remaining = 0;
        int charges = 0, max = 1;
        if (a.IsItem)
        {
            icon = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(a.Id % 1_000_000)?.Icon ?? 0;
            if (am != null && am->IsRecastTimerActive(ActionType.Item, a.Id))
                remaining = am->GetRecastTime(ActionType.Item, a.Id) - am->GetRecastTimeElapsed(ActionType.Item, a.Id);
        }
        else if (am != null)
        {
            var id = am->GetAdjustedActionId(a.PressId);
            var live = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id);
            if (live != null) icon = live.Value.Icon;
            max = Math.Max(1, (int)ActionManager.GetMaxCharges(id, 0));
            var total = am->GetRecastTime(ActionType.Action, id);
            var elapsed = am->GetRecastTimeElapsed(ActionType.Action, id);
            if (max > 1)
            {
                charges = (int)am->GetCurrentCharges(id);
                var per = total / max;
                remaining = charges < max && total > 0 ? total - elapsed - per * (max - charges - 1) : 0;
            }
            else if (am->IsRecastTimerActive(ActionType.Action, id)) remaining = total - elapsed;
        }

        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.PushID(a.Name);
        ImGui.Image(tex.Handle, size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var draw = ImGui.GetWindowDrawList();
        var bottomRight = topLeft + size;
        if (remaining > 0.05f && (max == 1 || charges == 0)) draw.AddRectFilled(topLeft, bottomRight, DimColor);
        if (plugin.Pilot.Queued == a) draw.AddRect(topLeft - new Vector2(1), bottomRight + new Vector2(1), QueuedColor, 3f, ImDrawFlags.None, 3f);
        if (remaining > 0.05f)
        {
            var text = remaining >= 10 ? $"{(int)remaining}" : $"{remaining:0.0}";
            var ts = ImGui.CalcTextSize(text);
            var pos = topLeft + (size - ts) / 2;
            draw.AddText(pos + new Vector2(1, 1), ShadowColor, text);
            draw.AddText(pos, TextColor, text);
        }
        if (max > 1)
        {
            var text = charges.ToString();
            var ts = ImGui.CalcTextSize(text);
            var pos = bottomRight - ts - new Vector2(2, 1);
            draw.AddText(pos + new Vector2(1, 1), ShadowColor, text);
            draw.AddText(pos, TextColor, text);
        }
        if (hovered) ImGui.SetTooltip(a.IsItem ? $"Potion: {plugin.Pilot.PotionStatus}\nClick: use it next weave (Live) / now" : $"{a.Name}\nClick: use it next {(a.IsGcd ? "GCD" : "weave")} (Live), or now");
        if (clicked) plugin.Pilot.Queue(a);
    }
}
