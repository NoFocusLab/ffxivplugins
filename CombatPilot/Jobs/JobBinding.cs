using System.Collections.Generic;

namespace CombatPilot.Jobs;

public enum Shape { Single, CircleSelf, CircleTarget, Cone, Line }

/// <summary>One job-file action matched to the game: ids, timing and potency.</summary>
public sealed class BoundAction
{
    public required ActionDef Def { get; init; }
    public uint Id { get; init; }
    public uint PressId { get; init; }
    /// <summary>Other buttons this action can appear on.</summary>
    public List<uint> PressAlsoIds { get; } = new();
    public int Level { get; init; }
    public bool IsGcd { get; init; }
    /// <summary>Recast group (cooldowns shared between actions, e.g. Upheaval/Orogeny); -1 = none of its own.</summary>
    public int CooldownGroup { get; init; }
    public uint Icon { get; init; }
    public double Potency { get; set; }
    public double ComboPotency { get; set; }
    /// <summary>"game" when read from the tooltip, "data" when from the job file.</summary>
    public string PotencySource { get; set; } = "data";
    public double? TooltipPotency { get; set; }
    // ---- area of effect (from game data: CastType, EffectRange, XAxisModifier; the job file can override)
    public Shape Shape { get; set; } = Shape.Single;
    public double Radius { get; set; }
    public double Width { get; set; }
    public double Angle { get; set; } = 90;
    public double Falloff { get; set; }
    public bool IsAoe => Shape != Shape.Single;

    /// <summary>The game lets you aim it at a party member (with MovesToTarget: a "move to ally" dash).</summary>
    public bool CanTargetParty { get; init; }
    /// <summary>An inventory item (potion), used with the item's id (HQ: id + 1,000,000).</summary>
    public bool IsItem { get; init; }
    /// <summary>MP cost (job file override, else the game's).</summary>
    public int MpCost { get; set; }
    public string Name => Def.Name;
}

/// <summary>
/// A job file bound to action data for one level: the game's (JobBinding.Game.cs, in the plugin) or a
/// snapshot of it (the offline simulator). Rebuilt on job or level change.
/// </summary>
public sealed partial class JobBinding
{
    public JobDef Def { get; }
    public string Abbreviation { get; }
    public int Level { get; }
    public List<BoundAction> Actions { get; } = new();
    public Dictionary<string, BoundAction> ByName { get; } = new();
    /// <summary>MP a raise costs on this job (0: no raise). Kept in reserve when the job's toggle says so.</summary>
    public int RaiseMpCost { get; private set; }
    public string RaiseName { get; private set; } = "";
    /// <summary>Job-file names that didn't match any action.</summary>
    public List<string> Unresolved { get; } = new();

    /// <summary>Simulator: a binding from actions already built (from an action-data snapshot).</summary>
    public JobBinding(JobDef def, string abbreviation, int level, IEnumerable<BoundAction> actions)
    {
        Def = def;
        Abbreviation = abbreviation;
        Level = level;
        foreach (var a in actions) { Actions.Add(a); ByName[a.Name] = a; }
    }
}
