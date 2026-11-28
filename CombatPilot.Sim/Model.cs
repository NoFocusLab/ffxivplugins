using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CombatPilot.Jobs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CombatPilot.Sim;

/// <summary>One action's data from the public game tables (tools/sim/build_action_data.py).</summary>
public sealed class ActionData
{
    public uint Id { get; set; }
    public int Level { get; set; }
    public int CdGroup { get; set; }
    public int CdGroup2 { get; set; }
    public double Recast { get; set; }
    public double Cast { get; set; }
    public int MaxCharges { get; set; }
    public int Range { get; set; }
    public int EffectRange { get; set; }
    public int CastType { get; set; }
    public int XAxis { get; set; }
    public int MpCost { get; set; }
    public bool CanTargetParty { get; set; }
    public uint ComboFrom { get; set; }
    public bool AffectsPosition { get; set; }
}

/// <summary>
/// A fight as the simulator sees it. Everything is in seconds from the pull. The dummy is an empty
/// timeline; a real fight comes from a Cactbot timeline (Mit Planner's converter) or a parse.
/// Profiles tuned against a timeline are keyed by Territory.
/// </summary>
public sealed class FightTimeline
{
    public string Name { get; set; } = "Striking dummy";
    public uint Territory { get; set; }
    public double Duration { get; set; } = 300;
    /// <summary>Target gone (untargetable / no damage): from, until.</summary>
    public List<double[]> Downtime { get; set; } = new();
    /// <summary>You can't reach the target in melee (mechanics): from, until. Ranged/rangedOk actions still land.</summary>
    public List<double[]> OutOfMelee { get; set; } = new();
    /// <summary>Extra enemies in range: from, until, count (AoE math).</summary>
    public List<double[]> Adds { get; set; } = new();
    /// <summary>Party raid buffs (not yours): name, from, until, damage multiplier.</summary>
    public List<PartyBuff> PartyBuffs { get; set; } = new();
    /// <summary>Raid buffs every 120 s from this time (a typical party), when PartyBuffs is empty. 0 = none.</summary>
    public double StandardPartyBuffsFrom { get; set; }

    public bool In(List<double[]> windows, double t) => windows.Any(w => w[0] <= t && t < w[1]);

    public static FightTimeline Load(string path) =>
        JsonConvert.DeserializeObject<FightTimeline>(File.ReadAllText(path)) ?? new FightTimeline();
}

public sealed class PartyBuff
{
    public string Name { get; set; } = "";
    public double From { get; set; }
    public double Until { get; set; }
    public double Mult { get; set; } = 1.05;
}

/// <summary>What a job should do on a fight, from its guide (The Balance): checked on every build.</summary>
public sealed class Expectations
{
    public string Job { get; set; } = "";
    public string Fight { get; set; } = "";
    public double Seconds { get; set; } = 300;
    public double GcdLength { get; set; } = 2.5;
    /// <summary>Action -> [min, max] uses per minute.</summary>
    public Dictionary<string, double[]> PerMinute { get; set; } = new();
    /// <summary>Most seconds of GCD lost to clipping/holding over the fight.</summary>
    public double MaxLostGcdSeconds { get; set; } = 1.0;
    /// <summary>Every opener step usable in order at its level.</summary>
    public bool OpenerValid { get; set; } = true;
    /// <summary>Actions that must never go out back to back as GCDs (combo breaks): e.g. "Fang and Claw".</summary>
    public List<string> NeverTwiceInARow { get; set; } = new();
}

public static class Data
{
    /// <summary>The repository root (walks up until it finds the CombatPilot folder).</summary>
    public static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "CombatPilot", "Jobs"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }

    public static JobDef Job(string job) =>
        JsonConvert.DeserializeObject<JobDef>(File.ReadAllText(Path.Combine(Root(), "CombatPilot", "Jobs", job + ".json")))!;

    public static Dictionary<string, ActionData> Actions(string job)
    {
        var all = JObject.Parse(File.ReadAllText(Path.Combine(Root(), "CombatPilot.Sim", "actions.json")));
        return all[job]?["actions"]?.ToObject<Dictionary<string, ActionData>>() ?? new();
    }

    /// <summary>Bind a job file to the data snapshot at a level, the way the plugin binds to the game.</summary>
    public static JobBinding Bind(JobDef def, int level, out List<string> problems)
    {
        problems = new();
        var data = Actions(def.Job);
        var bound = new List<BoundAction>();
        foreach (var a in def.Actions)
        {
            if (!data.TryGetValue(a.Name, out var row)) { problems.Add($"{a.Name}: not in the game data"); continue; }
            if (row.Level > level) continue;
            if (a.UpgradedTo != null && data.TryGetValue(a.UpgradedTo, out var up) && up.Level <= level) continue;
            var pressId = a.Press != null && data.TryGetValue(a.Press, out var p) ? p.Id : row.Id;
            var isGcd = row.CdGroup == 58 || row.CdGroup2 == 58 || a.Sequence != null;
            var b = new BoundAction
            {
                Def = a,
                Id = row.Id,
                PressId = pressId,
                Level = row.Level,
                IsGcd = isGcd,
                CooldownGroup = row.CdGroup == 58 || a.UsesCharge != null ? -1 : row.CdGroup,
                CanTargetParty = row.CanTargetParty,
                Potency = a.Potency * Math.Max(1, a.Hits),
                ComboPotency = a.ComboPotency,
                MpCost = a.MpCost ?? row.MpCost,
                Falloff = a.Falloff,
            };
            // Same shape rules as the plugin (JobBinding.Game.cs ReadShape).
            b.Radius = row.EffectRange;
            b.Width = row.XAxis;
            b.Shape = row.CastType switch
            {
                2 or 5 => row.Range == 0 ? Shape.CircleSelf : Shape.CircleTarget,
                3 or 13 => Shape.Cone,
                4 or 12 => Shape.Line,
                _ => Shape.Single,
            };
            if (row.EffectRange == 0 && b.Shape != Shape.Single) b.Shape = Shape.Single;
            if (a.Aoe != null)
                b.Shape = a.Aoe switch { "self" => Shape.CircleSelf, "target" => Shape.CircleTarget, "cone" => Shape.Cone, "line" => Shape.Line, _ => Shape.Single };
            if (a.Radius > 0) b.Radius = a.Radius;
            foreach (var also in a.PressAlso)
                if (data.TryGetValue(also, out var ar)) b.PressAlsoIds.Add(ar.Id);
            bound.Add(b);
        }
        return new JobBinding(def, def.Job, level, bound);
    }
}
