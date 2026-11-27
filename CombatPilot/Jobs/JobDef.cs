using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace CombatPilot.Jobs;

// The job files (Jobs/*.json, embedded) describe each job as data, so the engine holds nothing
// job-specific. A new patch or expansion means editing a job file, not the engine.

public sealed class JobDef
{
    public string Job { get; set; } = "";
    /// <summary>Class/job abbreviations this file covers (WAR and its base class MRD).</summary>
    public List<string> ClassJobs { get; set; } = new();
    public string Patch { get; set; } = "";
    public string Source { get; set; } = "";
    public GaugeDef? Gauge { get; set; }
    /// <summary>[Estimate] average potency per second, used to value buffs that outlast the look-ahead.</summary>
    public double PotencyPerSecond { get; set; } = 220;
    public Dictionary<string, StatusDef> Statuses { get; set; } = new();
    public List<ActionDef> Actions { get; set; } = new();
    /// <summary>Simple fixed priority used if the planner fails (safe mode).</summary>
    public List<string> SafeOrder { get; set; } = new();

    public ActionDef? Find(string name) => Actions.FirstOrDefault(a => a.Name == name);
}

public sealed class GaugeDef
{
    public string Name { get; set; } = "";
    public int Max { get; set; } = 100;
    /// <summary>[Estimate] potency one point of gauge is worth when left over at the end of the look-ahead.</summary>
    public double ValuePerUnit { get; set; }
}

public sealed class StatusDef
{
    /// <summary>Multiplies all damage while up (e.g. Surging Tempest 1.10).</summary>
    public double DamageMult { get; set; } = 1.0;
    /// <summary>While up, these actions are guaranteed critical direct hits (one stack used per action).</summary>
    public List<string> CritDhFor { get; set; } = new();
    /// <summary>[Estimate] potency a stack is worth when still up at the end of the look-ahead.</summary>
    public double TerminalValue { get; set; }
    public int MaxStacks { get; set; } = 0;
    /// <summary>At MaxStacks, this status is replaced by AtMax (Burgeoning Fury -> Wrathful).</summary>
    public string? AtMax { get; set; }
    public double AtMaxDuration { get; set; }
}

public sealed class ActionDef
{
    public string Name { get; set; } = "";
    /// <summary>The hotbar action that turns into this one (Fell Cleave -> Inner Chaos). Default: itself.</summary>
    public string? Press { get; set; }
    /// <summary>A trait replaces this action with another at a higher level (Berserk -> Inner Release).</summary>
    public string? UpgradedTo { get; set; }
    public double Potency { get; set; }
    public string? ComboFrom { get; set; }
    public double ComboPotency { get; set; }
    /// <summary>Guaranteed critical direct hit.</summary>
    public bool CritDh { get; set; }
    /// <summary>Usable from range (no melee needed).</summary>
    public bool Ranged { get; set; }
    public bool RangedOk { get; set; }
    /// <summary>A GCD that doesn't break the current combo (since 7.0 most don't).</summary>
    public bool KeepsCombo { get; set; }
    /// <summary>"self" (around you) or "target" (around the target); null = single target.</summary>
    public string? Aoe { get; set; }
    public double Radius { get; set; }
    /// <summary>Potency lost per additional target (0.5 = 50% less).</summary>
    public double Falloff { get; set; }
    public int GaugeCost { get; set; }
    /// <summary>Status whose stack makes this free (and is used up by it).</summary>
    public string? FreeWith { get; set; }
    public List<Condition> Requires { get; set; } = new();
    public List<string> Consumes { get; set; } = new();
    public List<Grant> Grants { get; set; } = new();
    public Extend? Extends { get; set; }
    public Reduce? ReducesRecast { get; set; }

    [JsonIgnore] public string PressName => Press ?? Name;
}

public sealed class Condition
{
    public string? Status { get; set; }
    public int? Gauge { get; set; }
    public Condition? Not { get; set; }
}

public sealed class Grant
{
    public int Gauge { get; set; }
    public string? Status { get; set; }
    public double Duration { get; set; }
    public int Stacks { get; set; }
    /// <summary>Add to the remaining time instead of resetting it, up to Max.</summary>
    public bool Extend { get; set; }
    public double Max { get; set; }
    /// <summary>Only when the action lands as a combo step.</summary>
    public bool ComboOnly { get; set; }
    /// <summary>Only while this status is up.</summary>
    public string? IfStatus { get; set; }
}

public sealed class Extend
{
    public string Status { get; set; } = "";
    public double Seconds { get; set; }
    public double Max { get; set; }
}

public sealed class Reduce
{
    public string Action { get; set; } = "";
    public double Seconds { get; set; }
}

public static class JobLibrary
{
    private static List<JobDef>? all;

    public static IReadOnlyList<JobDef> All => all ??= Load();

    public static JobDef? For(string abbreviation) =>
        All.FirstOrDefault(j => j.ClassJobs.Contains(abbreviation, StringComparer.OrdinalIgnoreCase));

    private static List<JobDef> Load()
    {
        var list = new List<JobDef>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("CombatPilot.Jobs.") && n.EndsWith(".json")))
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                if (JsonConvert.DeserializeObject<JobDef>(reader.ReadToEnd()) is { } def) list.Add(def);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Combat Pilot: couldn't read job file {name}");
            }
        }
        return list;
    }
}
