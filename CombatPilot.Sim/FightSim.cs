using System;
using System.Collections.Generic;
using System.Linq;
using CombatPilot.Engine;
using CombatPilot.Jobs;
using SimState = CombatPilot.Engine.Sim;

namespace CombatPilot.Sim;

/// <summary>One action that went out in a simulated fight.</summary>
public sealed record Used(double At, string Name, bool Gcd, double Damage);

public sealed class SimResult
{
    public string Job = "";
    public string Fight = "";
    public double Seconds;
    public double Potency;
    public double PotencyPerSecond => Seconds > 0 ? Potency / Seconds : 0;
    public List<Used> Log = new();
    /// <summary>Seconds the GCD sat ready with nothing pressed, plus GCDs pushed back by oGCD animations.</summary>
    public double LostGcdSeconds;
    public double ClipSeconds;
    public List<string> OpenerProblems = new();
    public List<string> BindProblems = new();
    public List<string> Errors = new();

    public Dictionary<string, double> PerMinute() =>
        Log.GroupBy(u => u.Name).ToDictionary(g => g.Key, g => g.Count() / Seconds * 60);
}

/// <summary>
/// Runs Combat Pilot's planner through a fight. The "world" is the engine's own Sim, advanced by
/// the actions the planner picks; each decision sees the world as a GameState, exactly like in
/// game. Damage is expected damage (crit/direct hit averaged), positionals land (a dummy that never
/// turns; positioning is tested in game), the target is in melee range except in OutOfMelee windows.
/// </summary>
public sealed class FightSim
{
    private readonly JobBinding job;
    private readonly FightTimeline fight;
    private readonly PlannerSettings set;
    private readonly double gcd;
    /// <summary>Record each decision with the alternatives weighed (failing checks print it).</summary>
    public bool Trace;
    public List<string> TraceLines { get; } = new();

    public FightSim(JobBinding job, FightTimeline fight, double gcdLength = 2.5, PlannerSettings? settings = null)
    {
        this.job = job;
        this.fight = fight;
        gcd = gcdLength;
        set = settings ?? new PlannerSettings();
        set.HorizonGcds = Math.Clamp(Math.Max(set.HorizonGcds, job.Def.MinHorizonGcds), 2, 12);
        set.NoPull = false;
        set.WalkWithin = gcd;
        if (job.Def.Travel is { } tr) { set.ReserveAction = tr.Action; set.ReserveKeep = tr.Keep; }
    }

    private SimState NewWorld()
    {
        var w = new SimState
        {
            Pos = new PositionInfo { Omni = true },
            statusDefs = job.Def.Statuses,
            MpRegenPerTick = job.Def.MpRegenPerTick,
            Mp = 10000,
        };
        var data = Data.Actions(job.Def.Job);
        foreach (var a in job.Actions)
        {
            if (a.CooldownGroup < 0 || w.Cd.ContainsKey(a.CooldownGroup)) continue;
            var row = data[a.Name];
            var max = a.Def.Charges > 0 ? a.Def.Charges : Math.Max(1, row.MaxCharges);
            w.Cd[a.CooldownGroup] = new CdState { MaxCharges = max, Charges = max, RecastPerCharge = Math.Max(0.5, row.Recast), NextChargeIn = 0 };
        }
        if (fight.PartyBuffs.Count > 0)
            foreach (var b in fight.PartyBuffs) w.PartyBuffs.Add((b.Mult, b.From, b.Until));
        else if (fight.StandardPartyBuffsFrom > 0)
            for (var t = fight.StandardPartyBuffsFrom; t < fight.Duration; t += 120) w.PartyBuffs.Add((1.15, t, t + 20));
        foreach (var d in fight.Downtime) w.Downtime.Add((d[0], d[1]));
        return w;
    }

    /// <summary>The world as the planner would read it from the game at time T.</summary>
    private GameState Read(SimState w)
    {
        var now = w.T;
        var g = new GameState
        {
            GcdRemaining = Math.Max(0, w.GcdReadyAt - now),
            GcdLength = gcd,
            AnimationLock = Math.Max(0, w.LockUntil - now),
            Gauge = w.Gauge,
            Mp = (int)w.Mp,
            MaxMp = w.MaxMp,
            Combo = w.Combo != null && w.ComboUntil > now ? w.Combo : null,
            ComboRemaining = Math.Max(0, w.ComboUntil - now),
            Cooldowns = new Dictionary<int, CdState>(w.Cd),
            HasTarget = !fight.In(fight.Downtime, now),
            InCombat = true,
            TargetEngaged = true,
            TargetTtk = Math.Max(1, fight.Duration - now),
            Position = new PositionInfo { Omni = true },
        };
        foreach (var (name, v) in w.Statuses)
            if (v.Until > now && v.Stacks > 0) g.Statuses[name] = (v.Until - now, v.Stacks);
        foreach (var (mult, from, until) in w.PartyBuffs)
            if (until > now) g.PartyBuffs.Add((mult, from - now, until - now));
        foreach (var (from, until) in w.Downtime)
            if (until > now) g.Downtime.Add((from - now, until - now));
        var melee = !fight.In(fight.OutOfMelee, now);
        var adds = fight.Adds.Where(a => a[0] <= now && now < a[1]).Sum(a => (int)a[2]);
        foreach (var a in job.Actions)
        {
            if (a.IsItem) continue;
            if (melee || a.Def.Ranged || a.Def.RangedOk) g.InRange.Add(a.Id);
            if (a.IsAoe) g.HitTtks[a.Id] = Enumerable.Repeat(g.TargetTtk, 1 + adds).ToArray();
        }
        return g;
    }

    /// <summary>Press an action in the world (the same steps the planner takes in its look-ahead).</summary>
    private double Press(SimState w, BoundAction a, SimResult r)
    {
        var now = w.T;
        var hits = fight.In(fight.Downtime, now) ? 0 : a.IsAoe ? 1 + fight.Adds.Where(x => x[0] <= now && now < x[1]).Sum(x => (int)x[2]) : 1;
        var dmg = w.Apply(a, job, hits, set.CritDhFactor, set.CritFactor);
        if (a.IsGcd)
        {
            w.GcdReadyAt = now + (a.Def.GcdTime > 0 ? a.Def.GcdTime : gcd);
            w.LastGcdAt = now;
            w.LockUntil = now + Math.Max(Math.Max(SimState.GcdLock, a.Def.WeaveFrom), w.LastCast > 0 ? w.LastCast + 0.1 : 0);
        }
        else
        {
            var lockFor = a.Def.AnimationLock > 0 ? a.Def.AnimationLock : SimState.OgcdLock;
            w.LockUntil = now + lockFor;
            if (w.LockUntil > w.GcdReadyAt)
            {
                r.ClipSeconds += w.LockUntil - w.GcdReadyAt;
                w.GcdReadyAt = w.LockUntil;
            }
        }
        r.Log.Add(new Used(now, a.Name, a.IsGcd, dmg));
        return dmg;
    }

    /// <summary>The fight with the planner choosing everything (the opener is checked separately).</summary>
    public SimResult Run()
    {
        var r = new SimResult { Job = job.Def.Job, Fight = fight.Name, Seconds = fight.Duration };
        var w = NewWorld();
        var guard = 0;
        while (w.T < fight.Duration && guard++ < 20000)
        {
            if (w.LockUntil > w.T + 1e-6) { w.AdvanceTo(w.LockUntil); continue; }
            var forGcd = w.GcdReadyAt <= w.T + 1e-6;
            if (!forGcd && w.T + SimState.OgcdLock > w.GcdReadyAt + 0.05) { w.AdvanceTo(w.GcdReadyAt); continue; }
            var g = Read(w);
            var d = Planner.Decide(g, job, set, forGcd);
            if (d.Error != null) { r.Errors.Add($"{w.T:0.0}s: {d.Error}"); w.AdvanceTo(w.T + 0.1); continue; }
            if (d.Action == null)
            {
                if (forGcd)
                {
                    // Holding the GCD: lost time unless the target is gone.
                    if (g.HasTarget) r.LostGcdSeconds += 0.1;
                    w.AdvanceTo(w.T + 0.1);
                }
                else w.AdvanceTo(w.GcdReadyAt);
                continue;
            }
            if (Trace)
                TraceLines.Add($"{w.T,6:0.00} {(forGcd ? "GCD " : "oGCD")} {d.Label,-20} | " +
                    string.Join(", ", d.RootScores.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key} {x.Value - d.Score:+0;-0}")) +
                    $" | gauge {w.Gauge}, " + string.Join(" ", w.Statuses.Where(s => s.Value.Until > w.T).Select(s => $"{s.Key}{(s.Value.Stacks > 1 ? "x" + s.Value.Stacks : "")}:{s.Value.Until - w.T:0}")) +
                    " | cd " + string.Join(" ", w.Cd.Where(c => c.Value.Charges < c.Value.MaxCharges).Select(c => $"{c.Key}:{c.Value.Charges}/{c.Value.MaxCharges}@{c.Value.NextChargeIn:0}")) +
                    string.Concat(d.RootScores.OrderByDescending(x => x.Value).Take(3).Select(x =>
                        $"\n         {x.Key} {x.Value - d.Score:+0;-0} (damage {d.RootDamage.GetValueOrDefault(x.Key):0}, rest {x.Value - d.RootDamage.GetValueOrDefault(x.Key):0}): {string.Join(" > ", d.RootPaths.GetValueOrDefault(x.Key) ?? new())}")));
            r.Potency += Press(w, d.Action, r);
        }
        r.LostGcdSeconds += r.ClipSeconds;
        return r;
    }

    /// <summary>
    /// The job file's opener, step by step from the pull: each step must be usable (cooldown,
    /// requirements, combo) when its turn comes. Catches openers that don't fit the job data.
    /// </summary>
    public List<string> CheckOpener(int level)
    {
        var problems = new List<string>();
        var op = job.Def.Openers.FirstOrDefault(o => o.MinLevel <= level);
        if (op == null) return problems;
        var w = NewWorld();
        var r = new SimResult();
        for (var i = 0; i < op.Steps.Count; i++)
        {
            for (var k = 0; k < op.Steps[i].Count; k++)
            {
                var name = op.Steps[i][k];
                if (name == "Potion") continue;
                if (!job.ByName.TryGetValue(name, out var a)) { problems.Add($"step {i + 1}: {name} isn't a bound action at level {level}"); continue; }
                if (a.IsGcd != (k == 0)) problems.Add($"step {i + 1}: {name} is {(a.IsGcd ? "a GCD" : "an oGCD")} but sits {(k == 0 ? "first" : "as a weave")}");
                var at = a.IsGcd ? Math.Max(w.GcdReadyAt, w.LockUntil) : w.LockUntil;
                w.AdvanceTo(Math.Max(w.T, at));
                if (!w.Ready(a)) problems.Add($"step {i + 1}: {name} still on cooldown");
                else if (!w.Allowed(a, job)) problems.Add($"step {i + 1}: {name} not usable (requirements/combo/gauge)");
                Press(w, a, r);
            }
        }
        return problems;
    }
}
