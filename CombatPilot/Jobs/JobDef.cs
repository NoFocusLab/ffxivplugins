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
    /// <summary>
    /// Look at least this many GCDs ahead (Settings can go higher). Jobs whose combos or DoTs cycle
    /// slower than the default look-ahead need it: Dragoon's two combos take 10 GCDs, and with 6
    /// it clipped Power Surge and Chaotic Spring because the cost fell past the end of the plan.
    /// </summary>
    public int MinHorizonGcds { get; set; }
    public Dictionary<string, StatusDef> Statuses { get; set; } = new();
    public List<ActionDef> Actions { get; set; } = new();
    /// <summary>Simple fixed priority used if the planner fails (safe mode).</summary>
    public List<string> SafeOrder { get; set; } = new();
    /// <summary>Gap closer whose charges can be kept in reserve (bar toggle).</summary>
    public TravelDef? Travel { get; set; }
    /// <summary>Actions held while the bar's Burst toggle is on Hold.</summary>
    public List<string> Burst { get; set; } = new();
    /// <summary>Main stat, for picking potions (Strength, Dexterity, Intelligence, Mind).</summary>
    public string MainStat { get; set; } = "Strength";
    /// <summary>Actions shown as buttons on the bar (click = queue it). Empty: every oGCD.</summary>
    public List<string> Bar { get; set; } = new();
    /// <summary>False for casters: walking costs casts, so moving is weighed more heavily.</summary>
    public bool CastsWhileMoving { get; set; } = true;
    /// <summary>Status that makes every positional land (role action True North).</summary>
    public string TrueNorthStatus { get; set; } = "True North";
    /// <summary>Openers, first = default. Run from a countdown (or the bar).</summary>
    public List<OpenerDef> Openers { get; set; } = new();
    /// <summary>[Estimate] potency one MP is worth when still in hand at the end of the look-ahead (0: MP doesn't matter to this job's damage).</summary>
    public double MpValue { get; set; }
    /// <summary>[Estimate] MP regained per 3 s server tick in combat (2% of 10,000).</summary>
    public int MpRegenPerTick { get; set; } = 200;

    public ActionDef? Find(string name) => Actions.FirstOrDefault(a => a.Name == name);
}

public sealed class OpenerDef
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>Seconds before the countdown ends to press the first action (Warrior: Tomahawk at 0.7).</summary>
    public double Prepull { get; set; }
    /// <summary>Written for this level and up (below it the planner opens instead).</summary>
    public int MinLevel { get; set; }
    /// <summary>Each step: a GCD, then the oGCDs woven after it. "Potion" is skipped when potions are off.</summary>
    public List<List<string>> Steps { get; set; } = new();
}

public sealed class TravelDef
{
    public string Action { get; set; } = "";
    public int Keep { get; set; } = 1;
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
    /// <summary>
    /// Kept up by the normal rotation (Dragoon's Power Surge from every other combo): no value for
    /// time left at the end of the plan. Otherwise the planner refreshes it early just to end with a
    /// longer timer (seen live: Chaotic Spring combo every cycle, a Lance Barrage combo broken for it).
    /// </summary>
    public bool Maintained { get; set; }
    /// <summary>While up, these actions are guaranteed critical direct hits (one stack used per action).</summary>
    public List<string> CritDhFor { get; set; } = new();
    /// <summary>While up, these actions are guaranteed critical hits (not direct hits); "*" = the next weaponskill (Life Surge).</summary>
    public List<string> CritFor { get; set; } = new();
    /// <summary>[Estimate] potency a stack is worth when still up at the end of the look-ahead.</summary>
    public double TerminalValue { get; set; }
    public int MaxStacks { get; set; } = 0;
    /// <summary>While up, every damaging weaponskill adds this potency and uses a stack (Bunshin's shadow).</summary>
    public double ExtraPerWeaponskill { get; set; }
    /// <summary>Gauge each of those extra hits gives.</summary>
    public int ExtraGauge { get; set; }
    /// <summary>MP regained per 3 s tick while up (Lucid Dreaming 550).</summary>
    public int MpPerTick { get; set; }
    /// <summary>At MaxStacks, this status is replaced by AtMax (Burgeoning Fury -> Wrathful).</summary>
    public string? AtMax { get; set; }
    public double AtMaxDuration { get; set; }
}

public sealed class ActionDef
{
    public string Name { get; set; } = "";
    /// <summary>The hotbar action that turns into this one (Fell Cleave -> Inner Chaos). Default: itself.</summary>
    public string? Press { get; set; }
    /// <summary>Other buttons it can appear on (Drakesbane: Wheeling Thrust's after Fang and Claw, and the reverse).</summary>
    public List<string> PressAlso { get; set; } = new();
    /// <summary>A trait replaces this action with another at a higher level (Berserk -> Inner Release).</summary>
    public string? UpgradedTo { get; set; }
    public double Potency { get; set; }
    /// <summary>Combo step it follows; several allowed, separated by "|" (Lance Barrage: "True Thrust|Raiden Thrust").</summary>
    public string? ComboFrom { get; set; }
    public double ComboPotency { get; set; }
    /// <summary>Only usable as a combo step (Drakesbane only exists after Fang and Claw / Wheeling Thrust).</summary>
    public bool RequiresCombo { get; set; }
    /// <summary>Damage over time put on the target (snapshots your buffs when applied).</summary>
    public DotDef? Dot { get; set; }
    /// <summary>Guaranteed critical direct hit.</summary>
    public bool CritDh { get; set; }
    /// <summary>Usable from range (no melee needed).</summary>
    public bool Ranged { get; set; }
    public bool RangedOk { get; set; }
    /// <summary>A GCD that doesn't break the current combo (since 7.0 most don't).</summary>
    public bool KeepsCombo { get; set; }
    /// <summary>
    /// Override of the shape read from game data: "self" (circle around you), "target" (circle around
    /// the target), "cone" or "line" (from you toward the target). Null: use the game's data.
    /// </summary>
    public string? Aoe { get; set; }
    public double Radius { get; set; }
    /// <summary>Cone angle in degrees (game data has no angle; default 90).</summary>
    public double Angle { get; set; }
    /// <summary>Line width (default: the game's).</summary>
    public double Width { get; set; }
    /// <summary>Potency lost per additional target (0.5 = 50% less). Read from the tooltip when it says so.</summary>
    public double Falloff { get; set; }
    public int GaugeCost { get; set; }
    /// <summary>Status whose stack makes this free (and is used up by it).</summary>
    public string? FreeWith { get; set; }
    public List<Condition> Requires { get; set; } = new();
    public List<string> Consumes { get; set; } = new();
    public List<Grant> Grants { get; set; } = new();
    public Extend? Extends { get; set; }
    /// <summary>Moves you to the target (dash/jump): checked against danger zones and for floor first.</summary>
    public bool MovesToTarget { get; set; }
    /// <summary>"rear" or "flank": bonus potency when hit from there (or under True North).</summary>
    public string? Positional { get; set; }
    /// <summary>Extra potency when the positional lands (applies to the combo potency too).</summary>
    public double PositionalBonus { get; set; }
    /// <summary>Moves you a fixed distance from where you face: "back" (Elusive Jump, Displacement) or "forward".</summary>
    public string? Moves { get; set; }
    public double MoveDistance { get; set; }
    public Reduce? ReducesRecast { get; set; }
    /// <summary>
    /// Pressed as a series of buttons (Ninja: "Ten", "Chi", "Ninjutsu"). Planned as one GCD of GcdTime
    /// seconds; the Pilot presses the steps in order (each step: a job-file action name).
    /// </summary>
    public List<string>? Sequence { get; set; }
    /// <summary>GCD time this action takes, when fixed (mudras + ninjutsu: 0.5 each + 1.5). 0: your GCD.</summary>
    public double GcdTime { get; set; }
    /// <summary>Seconds after starting it before an oGCD can be woven (Raiton: after the two mudras and the lock).</summary>
    public double WeaveFrom { get; set; }
    /// <summary>Uses a charge of another action (ninjutsu: a mudra charge), unless one of FreeWith is up.</summary>
    public ChargeUse? UsesCharge { get; set; }
    /// <summary>Statuses this uses one stack of (Fleeting Raiju: Raiju Ready).</summary>
    public List<string> ConsumesStack { get; set; } = new();
    /// <summary>Number of hits at the tooltip's potency (Dream Within a Dream: 3).</summary>
    public int Hits { get; set; } = 1;
    /// <summary>Never chosen by the planner (mudras, the Ninjutsu button): only pressed as a step.</summary>
    public bool PlanNever { get; set; }
    /// <summary>MP cost; null = the game's (Action sheet). Set it when a trait changes the cost (Paladin halves spell costs).</summary>
    public int? MpCost { get; set; }
    /// <summary>Cost multipliers while a status is up (Black Mage: Astral Fire doubles fire costs).</summary>
    public List<MpCostWith> MpCostWith { get; set; } = new();
    /// <summary>Cast time in seconds (spells). A boost marked instant removes it (Divine Might, Requiescat).</summary>
    public double CastTime { get; set; }
    /// <summary>The hard cast (no instant boost) is only for when no melee GCD reaches (Paladin's Holy Spirit).</summary>
    public bool CastOnlyAtRange { get; set; }
    /// <summary>Potency under a status, first match wins (Holy Spirit: Divine Might 500, then Requiescat 700).</summary>
    public List<Boost> Boosts { get; set; } = new();
    /// <summary>
    /// For actions that appear on another button in sequence (Confiteor -> Blade of Faith -> ...):
    /// a status the engine adds while the Press button currently shows this action, so the chain
    /// can be required and granted like any status.
    /// </summary>
    public string? ShownAs { get; set; }
    /// <summary>Cast on a party member chosen by the partner rules (Dancer's Closed Position, Astrologian cards).</summary>
    public bool AllyTarget { get; set; }
    /// <summary>
    /// [Estimate] Potency-equivalent cost of using this when auto-move could get you there instead
    /// (True North: keep the charge for when walking isn't possible).
    /// </summary>
    public double CostIfCanMove { get; set; }
    /// <summary>[Estimate] seconds you're locked after this oGCD when longer than usual (Stardiver). 0: the default.</summary>
    public double AnimationLock { get; set; }

    [JsonIgnore] public string PressName => Press ?? Name;

    private string[]? comboFroms;
    /// <summary>True if this action continues a combo after <paramref name="previous"/>.</summary>
    public bool Follows(string? previous) =>
        previous != null && ComboFrom != null && (comboFroms ??= ComboFrom.Split('|', StringSplitOptions.TrimEntries)).Contains(previous);
}

public sealed class MpCostWith
{
    public string Status { get; set; } = "";
    public double Mult { get; set; } = 1;
}

public sealed class ChargeUse
{
    public string Action { get; set; } = "";
    public List<string> FreeWith { get; set; } = new();
}

public sealed class Boost
{
    public string Status { get; set; } = "";
    /// <summary>Only when the action lands as a combo step (Kazematoi on Aeolian Edge).</summary>
    public bool ComboOnly { get; set; }
    public double Potency { get; set; }
    /// <summary>Uses one stack (or the status) up.</summary>
    public bool Consume { get; set; } = true;
    /// <summary>Removes the cast time.</summary>
    public bool Instant { get; set; } = true;
}

public sealed class DotDef
{
    /// <summary>The status it puts on the target (read back from the target, yours only).</summary>
    public string Status { get; set; } = "";
    /// <summary>Potency per tick (every 3 s).</summary>
    public double Potency { get; set; }
    public double Duration { get; set; }
    /// <summary>Only when the action lands as a combo step.</summary>
    public bool ComboOnly { get; set; }
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
    /// <summary>MP restored (Paladin's Riot Blade combo: 1000).</summary>
    public int Mp { get; set; }
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
    /// <summary>Only from this level (the trait that adds it).</summary>
    public int MinLevel { get; set; }
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
    public int MinLevel { get; set; }
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
