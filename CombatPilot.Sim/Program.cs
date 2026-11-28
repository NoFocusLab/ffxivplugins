using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CombatPilot.Engine;
using Newtonsoft.Json;

namespace CombatPilot.Sim;

/// <summary>
/// dotnet run --project CombatPilot.Sim -- check                 every expectations file (CI)
/// dotnet run --project CombatPilot.Sim -- run DRG [fight.json]  one job on a fight (default: 5-min dummy), report to stdout
/// Exit code 1 when an expectation fails.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        EngineLog.Warning = (ex, msg) => Console.Error.WriteLine($"{msg}: {ex.Message}");
        var cmd = args.Length > 0 ? args[0] : "check";
        try
        {
            return cmd switch
            {
                "run" => Run(args[1], args.Length > 2 ? args[2] : null),
                _ => CheckAll(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static FightTimeline Fight(string? path, double seconds = 300) =>
        path == null ? new FightTimeline { Duration = seconds } : FightTimeline.Load(path);

    private static int Run(string jobName, string? fightPath)
    {
        var def = Data.Job(jobName);
        var job = Data.Bind(def, 100, out var problems);
        var sim = new FightSim(job, Fight(fightPath));
        var r = sim.Run();
        r.BindProblems = problems;
        r.OpenerProblems = sim.CheckOpener(100);
        Print(r);
        return 0;
    }

    private static void Print(SimResult r)
    {
        Console.WriteLine($"{r.Job} on {r.Fight}: {r.Seconds:0}s, {r.PotencyPerSecond:0.0} potency/s, lost GCD time {r.LostGcdSeconds:0.0}s (clipping {r.ClipSeconds:0.0}s)");
        foreach (var p in r.BindProblems) Console.WriteLine("  data: " + p);
        foreach (var p in r.OpenerProblems) Console.WriteLine("  opener: " + p);
        foreach (var e in r.Errors.Take(5)) Console.WriteLine("  planner: " + e);
        Console.WriteLine("  per minute: " + string.Join(", ", r.PerMinute().OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value:0.00}")));
    }

    /// <summary>Every CombatPilot.Sim/expect/*.json: run, compare, report. Non-zero exit on any failure.</summary>
    private static int CheckAll()
    {
        var dir = Path.Combine(Data.Root(), "CombatPilot.Sim", "expect");
        var failures = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            var e = JsonConvert.DeserializeObject<Expectations>(File.ReadAllText(file))!;
            var def = Data.Job(e.Job);
            var job = Data.Bind(def, 100, out var problems);
            var fight = e.Fight.Length > 0 ? FightTimeline.Load(Path.Combine(dir, "..", "fights", e.Fight)) : Fight(null, e.Seconds);
            var sim = new FightSim(job, fight, e.GcdLength);
            var r = sim.Run();
            r.BindProblems = problems;
            r.OpenerProblems = sim.CheckOpener(100);
            Print(r);

            var fails = new List<string>();
            fails.AddRange(problems.Select(p => "data: " + p));
            if (e.OpenerValid) fails.AddRange(r.OpenerProblems.Select(p => "opener: " + p));
            fails.AddRange(r.Errors.Take(3).Select(p => "planner: " + p));
            var pm = r.PerMinute();
            foreach (var (name, range) in e.PerMinute)
            {
                var v = pm.GetValueOrDefault(name);
                if (v < range[0] - 1e-9 || v > range[1] + 1e-9)
                    fails.Add($"{name}: {v:0.00}/min, expected {range[0]:0.##}-{range[1]:0.##} (at {string.Join(", ", r.Log.Where(u => u.Name == name).Select(u => u.At.ToString("0")))} s)");
            }
            if (r.LostGcdSeconds > e.MaxLostGcdSeconds) fails.Add($"lost GCD time {r.LostGcdSeconds:0.0}s, expected at most {e.MaxLostGcdSeconds:0.0}s");
            var gcds = r.Log.Where(u => u.Gcd).ToList();
            foreach (var name in e.NeverTwiceInARow)
                for (var i = 1; i < gcds.Count; i++)
                    if (gcds[i].Name == name && gcds[i - 1].Name == name) { fails.Add($"{name} twice in a row at {gcds[i].At:0.0}s (combo broken)"); break; }

            if (fails.Count == 0) Console.WriteLine($"  PASS {Path.GetFileName(file)}");
            else
            {
                // What the planner did and weighed, first 60 s: the failure explains itself.
                var tr = new FightSim(job, Fight(null, 60), e.GcdLength) { Trace = true };
                tr.Run();
                foreach (var line in tr.TraceLines) Console.WriteLine("    " + line);
                failures++;
                foreach (var f in fails) Console.WriteLine($"  FAIL {Path.GetFileName(file)}: {f}");
                // GitHub shows these as annotations on the pull request.
                Console.WriteLine($"::error title=Rotation check {e.Job}::{string.Join("; ", fails)}");
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
