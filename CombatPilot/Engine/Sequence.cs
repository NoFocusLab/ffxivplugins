using System;
using System.Collections.Generic;
using CombatPilot.Jobs;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatPilot.Engine;

/// <summary>
/// Presses a multi-button action in order (Ninja: Ten, Chi, then Ninjutsu, which the game turns into
/// Raiton). The planner sees it as one GCD; while it runs, nothing else is planned or pressed.
/// Each step goes out as soon as the game accepts it; it gives up after 6 s (moved, interrupted, a
/// wrong mudra) and the planner carries on from whatever state that left.
/// </summary>
public sealed unsafe class SequenceRunner
{
    private BoundAction? action;
    private List<string> steps = new();
    private int index;
    private DateTime started, lastPress;
    private string? finished;

    /// <summary>Steps still to press.</summary>
    public bool Running => action != null;
    public string Status { get; private set; } = "";

    public void Start(BoundAction a)
    {
        action = a;
        steps = a.Def.Sequence ?? new();
        index = 0;
        started = DateTime.UtcNow;
        lastPress = DateTime.MinValue;
        finished = null;
        Status = $"{a.Name}: {string.Join(" > ", steps)}";
        Plugin.Log.Debug($"[Pilot] sequence {Status}");
    }

    public void Abort(string why)
    {
        if (action == null) return;
        Plugin.Log.Debug($"[Pilot] sequence {action.Name} stopped: {why}");
        Status = $"{action.Name} stopped: {why}";
        action = null;
    }

    /// <summary>
    /// The name to log for the GCD that just went out: the whole sequence's name once its last
    /// button is pressed (so the opener and reports see "Raiton", not "Ninjutsu").
    /// </summary>
    public string? TakeFinished()
    {
        var f = finished;
        finished = null;
        return f;
    }

    /// <summary>Press the next step if the game will take it. Returns true while the sequence owns the frame.</summary>
    public bool Tick(JobBinding job, GameState g)
    {
        if (action == null) return false;
        var now = DateTime.UtcNow;
        if ((now - started).TotalSeconds > 6) { Abort("timed out"); return false; }
        if (index >= steps.Count) { action = null; return false; }
        if (!job.ByName.TryGetValue(steps[index], out var step)) { Abort($"no action {steps[index]} at this level"); return false; }
        if ((now - lastPress).TotalSeconds < 0.3) return true;

        var am = ActionManager.Instance();
        if (am == null) return true;
        var id = am->GetAdjustedActionId(step.PressId);
        // Buttons that hit something (a ninjutsu, Ten Chi Jin's steps) go on your target; mudras on you.
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id);
        var target = row?.CanTargetHostile == true && g.HasTarget ? g.TargetId : 0xE000_0000UL;
        if (am->GetActionStatus(ActionType.Action, id, target) != 0) return true; // not yet (GCD, lock)
        lastPress = now;
        if (!am->UseAction(ActionType.Action, id, target)) return true;
        Plugin.Log.Debug($"[Pilot] sequence {action.Name}: step {index + 1}/{steps.Count} {steps[index]} (#{id})");
        index++;
        if (index >= steps.Count)
        {
            finished = action.Name;
            Status = $"{action.Name} done";
            action = null;
        }
        return true;
    }
}
