using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CombatPilot.Engine;

/// <summary>One decision point: what the planner wanted, what actually happened, what it cost.</summary>
public sealed record DecisionRecord(
    double FightTime, bool Gcd, string Wanted, string Actual, double? Lost, string Plan, string Note);

public sealed class FightSummary
{
    public string File { get; set; } = "";
    public DateTime Start { get; set; }
    public double Seconds { get; set; }
    public string Job { get; set; } = "";
    /// <summary>Combat Pilot version that flew the fight (so a log is never guessed against the wrong build).</summary>
    public string Version { get; set; } = typeof(FightLog).Assembly.GetName().Version?.ToString() ?? "";
    public string Mode { get; set; } = "";
    public string Place { get; set; } = "";
    public int Decisions { get; set; }
    public int Mismatches { get; set; }
    /// <summary>[Estimate] potency the planner thinks was lost vs its own best choice.</summary>
    public double Lost { get; set; }
    public int SafeModePresses { get; set; }
    public List<string> Notes { get; set; } = new();
    /// <summary>What actually happened (written to fights/*.report.json too): the after-action report.</summary>
    public FightReport Report { get; set; } = new();
}

/// <summary>
/// The after-action report: everything needed to judge a run without the IINACT log. DPS comes from
/// Combat Recorder (if loaded); the rest from what Combat Pilot saw go out.
/// </summary>
public sealed class FightReport
{
    public double Damage { get; set; }
    public double Dps { get; set; }
    public int Gcds { get; set; }
    public double AverageGcd { get; set; }
    /// <summary>GCD time lost: gaps longer than the GCD (+0.2 s), summed beyond the GCD.</summary>
    public double LostGcdSeconds { get; set; }
    public List<string> Gaps { get; set; } = new();
    public int Positionals { get; set; }
    public int PositionalsMissed { get; set; }
    public int PositionalsUnderTrueNorth { get; set; }
    /// <summary>Landed, but within 20° of the zone's edge.</summary>
    public int PositionalsNearEdge { get; set; }
    public List<string> PositionalMisses { get; set; } = new();
    public Dictionary<string, double> PerMinute { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
}

/// <summary>
/// Per-fight log: in memory while the fight runs, then one file per fight in the plugin's config
/// folder (fights/*.tsv: a JSON summary line, then one line per decision).
/// </summary>
public sealed class FightLog
{
    public FightSummary? Current { get; private set; }
    public List<DecisionRecord> CurrentDecisions { get; } = new();
    public List<FightSummary> History { get; } = new();
    private DateTime startedAt;
    private readonly string dir;
    private readonly int keep;

    public FightLog(int keep)
    {
        this.keep = keep;
        dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "fights");
        Directory.CreateDirectory(dir);
        LoadHistory();
    }

    public double FightTime => Current == null ? 0 : (DateTime.UtcNow - startedAt).TotalSeconds;

    public void Start(string job, string mode, string place)
    {
        if (Current != null) return;
        startedAt = DateTime.UtcNow;
        CurrentDecisions.Clear();
        lastGcdAt = -1;
        gcdSum = 0;
        Current = new FightSummary { Start = DateTime.Now, Job = job, Mode = mode, Place = place };
    }

    public void Add(DecisionRecord r)
    {
        if (Current == null) return;
        CurrentDecisions.Add(r);
        Current.Decisions++;
        if (r.Wanted != r.Actual) Current.Mismatches++;
        if (r.Lost is > 0) Current.Lost += r.Lost.Value;
    }

    public void Note(string text)
    {
        if (Current == null) return;
        if (!Current.Notes.Contains(text)) Current.Notes.Add(text);
    }

    public void SafeModePress() { if (Current != null) Current.SafeModePresses++; }

    private double lastGcdAt = -1, gcdSum;
    private string lastGcdName = "";

    /// <summary>An action went out (seen by the Pilot).</summary>
    public void Used(string name, bool gcd, double gcdLength)
    {
        if (Current == null) return;
        var r = Current.Report;
        r.Counts[name] = r.Counts.GetValueOrDefault(name) + 1;
        if (!gcd) return;
        var t = FightTime;
        if (lastGcdAt >= 0)
        {
            var gap = t - lastGcdAt;
            gcdSum += gap;
            if (gap > gcdLength + 0.2 && gap < 60)
            {
                r.LostGcdSeconds += gap - gcdLength;
                if (r.Gaps.Count < 40) r.Gaps.Add($"{lastGcdAt:0.0}s {lastGcdName} -> {name}: {gap:0.00}s");
            }
        }
        r.Gcds++;
        lastGcdAt = t;
        lastGcdName = name;
    }

    /// <summary>A positional GCD went out: where you were (degrees from the target's front) and True North.</summary>
    public void Positional(string name, string need, double angle, bool trueNorth)
    {
        if (Current == null) return;
        var r = Current.Report;
        r.Positionals++;
        if (trueNorth) { r.PositionalsUnderTrueNorth++; return; }
        var inside = need == "rear" ? angle - 135 : Math.Min(angle - 45, 135 - angle);
        if (inside < 0)
        {
            r.PositionalsMissed++;
            if (r.PositionalMisses.Count < 40) r.PositionalMisses.Add($"{FightTime:0.0}s {name}: needed {need}, at {angle:0}°");
        }
        else if (inside < 20) r.PositionalsNearEdge++;
    }

    public void SetDamage(double damage, double seconds)
    {
        if (Current == null || seconds <= 0) return;
        Current.Report.Damage = damage;
        Current.Report.Dps = damage / seconds;
    }

    public void End()
    {
        if (Current == null) return;
        var f = Current;
        Current = null;
        f.Seconds = (DateTime.UtcNow - startedAt).TotalSeconds;
        if (f.Seconds < 5 && f.Decisions == 0) return;
        f.File = $"{f.Start:yyyyMMdd-HHmmss}-{f.Job}.tsv";
        var rep = f.Report;
        if (rep.Gcds > 1) rep.AverageGcd = gcdSum / (rep.Gcds - 1);
        foreach (var (k, v) in rep.Counts) rep.PerMinute[k] = Math.Round(v / Math.Max(1, f.Seconds) * 60, 2);
        try
        {
            // The small after-action report, readable without the decision log or IINACT.
            File.WriteAllText(Path.Combine(dir, Path.ChangeExtension(f.File, ".report.json")), JsonConvert.SerializeObject(f, Formatting.Indented));
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Combat Pilot: couldn't write fight report"); }
        try
        {
            using var w = new StreamWriter(Path.Combine(dir, f.File));
            w.WriteLine("# " + JsonConvert.SerializeObject(f));
            w.WriteLine("time\tgcd\twanted\tactual\tlost\tplan\tnote");
            foreach (var r in CurrentDecisions)
                w.WriteLine(string.Join('\t', r.FightTime.ToString("0.00", CultureInfo.InvariantCulture), r.Gcd ? "GCD" : "oGCD",
                    r.Wanted, r.Actual, r.Lost?.ToString("0", CultureInfo.InvariantCulture) ?? "", r.Plan, r.Note));
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Combat Pilot: couldn't write fight log"); }
        History.Insert(0, f);
        Prune();
    }

    public List<DecisionRecord> Load(FightSummary f)
    {
        var list = new List<DecisionRecord>();
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(dir, f.File)).Skip(2))
            {
                var c = line.Split('\t');
                if (c.Length < 7) continue;
                list.Add(new DecisionRecord(
                    double.Parse(c[0], CultureInfo.InvariantCulture), c[1] == "GCD", c[2], c[3],
                    double.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : null, c[5], c[6]));
            }
        }
        catch (Exception) { }
        return list;
    }

    public string Folder => dir;

    private void LoadHistory()
    {
        try
        {
            foreach (var path in Directory.GetFiles(dir, "*.tsv").OrderByDescending(p => p))
            {
                using var r = new StreamReader(path);
                var first = r.ReadLine();
                if (first == null || !first.StartsWith("# ")) continue;
                if (JsonConvert.DeserializeObject<FightSummary>(first[2..]) is { } s) { s.File = Path.GetFileName(path); History.Add(s); }
                if (History.Count >= keep) break;
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Combat Pilot: couldn't read fight history"); }
    }

    private void Prune()
    {
        while (History.Count > keep)
        {
            var old = History[^1];
            History.RemoveAt(History.Count - 1);
            try { File.Delete(Path.Combine(dir, old.File)); } catch (Exception) { }
        }
    }
}
