using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Dalamud.Game.ClientState.JobGauge.Types;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace CombatPilot.Jobs;

/// <summary>One job-file action matched to the game: ids, timing and potency.</summary>
public sealed class BoundAction
{
    public required ActionDef Def { get; init; }
    public uint Id { get; init; }
    public uint PressId { get; init; }
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
    /// <summary>An inventory item (potion), used with the item's id (HQ: id + 1,000,000).</summary>
    public bool IsItem { get; init; }
    public string Name => Def.Name;
}

/// <summary>A job file bound to the game data for the current level. Rebuilt on job or level change.</summary>
public sealed class JobBinding
{
    public JobDef Def { get; }
    public string Abbreviation { get; }
    public int Level { get; }
    public List<BoundAction> Actions { get; } = new();
    public Dictionary<string, BoundAction> ByName { get; } = new();
    /// <summary>Job-file names that didn't match any game action.</summary>
    public List<string> Unresolved { get; } = new();

    private const int GcdGroup = 58; // CooldownGroup column value of weaponskills/spells

    public JobBinding(JobDef def, string abbreviation, int level)
    {
        Def = def;
        Abbreviation = abbreviation;
        Level = level;
        var sheet = Plugin.DataManager.GetExcelSheet<Action>();
        var transient = Plugin.DataManager.GetExcelSheet<ActionTransient>();

        // Every non-PvP action any of this file's jobs can use, by name.
        var byName = new Dictionary<string, Action>();
        foreach (var row in sheet)
        {
            if (row.IsPvP || row.ClassJobLevel == 0) continue;
            if (!def.ClassJobs.Any(j => CategoryAllows(row.ClassJobCategory.ValueNullable, j))) continue;
            var n = row.Name.ExtractText();
            if (n.Length == 0) continue;
            // Prefer the hotbar version when a name exists twice.
            if (!byName.TryGetValue(n, out var have) || (!have.IsPlayerAction && row.IsPlayerAction)) byName[n] = row;
        }

        foreach (var a in def.Actions)
        {
            if (!byName.TryGetValue(a.Name, out var row)) { Unresolved.Add(a.Name); continue; }
            if (row.ClassJobLevel > level) continue;
            // Replaced by a trait at this level (Berserk -> Inner Release).
            if (a.UpgradedTo != null && byName.TryGetValue(a.UpgradedTo, out var up) && up.ClassJobLevel <= level) continue;
            var press = a.Press != null && byName.TryGetValue(a.Press, out var p) ? p : row;
            var bound = new BoundAction
            {
                Def = a,
                Id = row.RowId,
                PressId = press.RowId,
                Level = row.ClassJobLevel,
                IsGcd = row.CooldownGroup == GcdGroup || row.AdditionalCooldownGroup == GcdGroup,
                CooldownGroup = row.CooldownGroup == GcdGroup ? -1 : row.CooldownGroup,
                Icon = row.Icon,
                Potency = a.Potency,
                ComboPotency = a.ComboPotency,
            };
            ReadTooltip(bound, transient.GetRowOrDefault(row.RowId)?.Description.ExtractText());
            Actions.Add(bound);
            ByName[a.Name] = bound;
        }
    }

    private static readonly Regex PotencyRe = new(@"(?<!Cure )[Pp]otency of ([\d,]+)", RegexOptions.Compiled);
    private static readonly Regex ComboRe = new(@"Combo Potency: ([\d,]+)", RegexOptions.Compiled);

    /// <summary>
    /// [Assumption] English tooltips read "potency of N" and "Combo Potency: N". Used only when the
    /// text gives exactly one value (level-dependent tooltips can carry several); otherwise the job
    /// file's number stands. Either way the tooltip value is kept for the patch-day check.
    /// </summary>
    private static void ReadTooltip(BoundAction a, string? text)
    {
        if (string.IsNullOrEmpty(text) || a.Def.Potency <= 0) return;
        var values = PotencyRe.Matches(text).Select(m => Num(m.Groups[1].Value)).Distinct().ToList();
        if (values.Count != 1) return;
        a.TooltipPotency = values[0];
        a.Potency = values[0];
        a.PotencySource = "game";
        var combos = ComboRe.Matches(text).Select(m => Num(m.Groups[1].Value)).Distinct().ToList();
        if (combos.Count == 1 && a.Def.ComboPotency > 0) a.ComboPotency = combos[0];
    }

    private static double Num(string s) => double.TryParse(s.Replace(",", ""), out var v) ? v : 0;

    private static readonly Dictionary<string, PropertyInfo?> CategoryProps = new();

    /// <summary>ClassJobCategory has one bool column per job abbreviation (WAR, MRD, ...).</summary>
    private static bool CategoryAllows(ClassJobCategory? category, string abbreviation)
    {
        if (category == null) return false;
        if (!CategoryProps.TryGetValue(abbreviation, out var prop))
        {
            prop = typeof(ClassJobCategory).GetProperty(abbreviation, BindingFlags.Public | BindingFlags.Instance);
            CategoryProps[abbreviation] = prop;
        }
        return prop?.GetValue(category.Value) is true;
    }

    public const string PotionName = "Potion";
    public const string MedicatedName = "Medicated";
    /// <summary>Recast group key used for the potion (items have no action recast group).</summary>
    public const int PotionGroup = 100_000;

    /// <summary>Add (or replace) the potion as an oGCD the planner can place. 0 removes it.</summary>
    public void SetPotion(uint itemIdWithHq, string itemName)
    {
        Actions.RemoveAll(a => a.IsItem);
        ByName.Remove(PotionName);
        if (itemIdWithHq == 0) return;
        // [Estimate] a current-tier strength potion is worth about +6% damage for 30 s.
        Def.Statuses.TryAdd(MedicatedName, new StatusDef { DamageMult = 1.06 });
        var def = new ActionDef
        {
            Name = PotionName,
            Grants = new() { new Grant { Status = MedicatedName, Duration = 30 } },
        };
        var bound = new BoundAction
        {
            Def = def, Id = itemIdWithHq, PressId = itemIdWithHq, Level = 1, IsGcd = false,
            CooldownGroup = PotionGroup, IsItem = true, PotencySource = itemName,
        };
        Actions.Add(bound);
        ByName[PotionName] = bound;
    }

    /// <summary>The job's gauge value now (one small reader per job: gauges are job-specific).</summary>
    public int ReadGauge()
    {
        try
        {
            return Def.Job switch
            {
                "WAR" => Plugin.JobGauges.Get<WARGauge>().BeastGauge,
                _ => 0,
            };
        }
        catch (Exception) { return 0; }
    }
}

/// <summary>Status names by id (the engine works in names, so job files never carry ids).</summary>
public static class StatusNames
{
    private static readonly Dictionary<uint, string> cache = new();

    public static string Of(uint id)
    {
        if (cache.TryGetValue(id, out var n)) return n;
        n = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(id)?.Name.ExtractText() ?? "";
        cache[id] = n;
        return n;
    }
}
