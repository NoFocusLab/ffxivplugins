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
    public string Mode { get; set; } = "";
    public string Place { get; set; } = "";
    public int Decisions { get; set; }
    public int Mismatches { get; set; }
    /// <summary>[Estimate] potency the planner thinks was lost vs its own best choice.</summary>
    public double Lost { get; set; }
    public int SafeModePresses { get; set; }
    public List<string> Notes { get; set; } = new();
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

    public void End()
    {
        if (Current == null) return;
        var f = Current;
        Current = null;
        f.Seconds = (DateTime.UtcNow - startedAt).TotalSeconds;
        if (f.Seconds < 5 && f.Decisions == 0) return;
        f.File = $"{f.Start:yyyyMMdd-HHmmss}-{f.Job}.tsv";
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
