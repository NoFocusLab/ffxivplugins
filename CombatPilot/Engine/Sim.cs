using System;
using System.Collections.Generic;
using System.Linq;
using CombatPilot.Jobs;

namespace CombatPilot.Engine;

/// <summary>
/// One possible future: time, resources, statuses and cooldowns after a sequence of actions.
/// Times are seconds from "now". Cheap to copy; the planner clones it for every branch.
/// </summary>
public sealed class Sim
{
    public const double OgcdLock = 0.65;   // [Estimate] animation lock + latency of an oGCD
    public const double GcdLock = 0.65;    // after a weaponskill, before the first weave
    public const double TimePreference = 0.003;

    public double T;
    public double GcdReadyAt;
    public double LockUntil;
    public int Gauge;
    public string? Combo;
    public double ComboUntil;
    public Dictionary<string, (double Until, int Stacks)> Statuses = new();
    public List<(double Mult, double From, double Until)> PartyBuffs = new();
    public Dictionary<int, CdState> Cd = new();
    /// <summary>Boss downtime (no damage lands): from, until.</summary>
    public List<(double From, double Until)> Downtime = new();
    /// <summary>Per recast group: time spent sitting at full charges (wasted cooldown).</summary>
    public Dictionary<int, double> Idle = new();
    public double Damage;
    public List<string> Path = new();

    public Sim Clone() => new()
    {
        T = T, GcdReadyAt = GcdReadyAt, LockUntil = LockUntil, Gauge = Gauge, Combo = Combo, ComboUntil = ComboUntil,
        Statuses = new(Statuses), PartyBuffs = PartyBuffs, Downtime = Downtime, Cd = new(Cd), Idle = new(Idle), Damage = Damage, Path = new(Path),
    };

    public static Sim From(GameState g)
    {
        var s = new Sim
        {
            GcdReadyAt = g.GcdRemaining,
            LockUntil = g.AnimationLock,
            Gauge = g.Gauge,
            Combo = g.Combo,
            ComboUntil = g.ComboRemaining,
            PartyBuffs = g.PartyBuffs.ToList(),
            Downtime = g.Downtime,
            Cd = new(g.Cooldowns),
        };
        foreach (var (k, v) in g.Statuses) s.Statuses[k] = (v.Left, v.Stacks);
        return s;
    }

    public bool Has(string status) => Statuses.TryGetValue(status, out var v) && v.Until > T && v.Stacks > 0;
    public int StacksOf(string status) => Has(status) ? Statuses[status].Stacks : 0;

    /// <summary>Move the clock forward: statuses run out, cooldowns come back, idle time is counted.</summary>
    public void AdvanceTo(double t)
    {
        if (t <= T) return;
        var dt = t - T;
        foreach (var key in Cd.Keys.ToList())
        {
            var c = Cd[key];
            var left = dt;
            while (left > 0)
            {
                if (c.Charges >= c.MaxCharges)
                {
                    Idle[key] = Idle.GetValueOrDefault(key) + left;
                    c.NextChargeIn = 0;
                    break;
                }
                if (c.NextChargeIn > left) { c.NextChargeIn -= left; break; }
                left -= c.NextChargeIn;
                c.Charges++;
                c.NextChargeIn = c.Charges < c.MaxCharges ? c.RecastPerCharge : 0;
            }
            Cd[key] = c;
        }
        T = t;
        if (Combo != null && ComboUntil <= T) Combo = null;
    }

    public bool Ready(BoundAction a) =>
        a.CooldownGroup < 0 || !Cd.TryGetValue(a.CooldownGroup, out var c) || c.Charges > 0;

    public bool Meets(Condition c, JobBinding job)
    {
        var ok = true;
        if (c.Status != null) ok &= Has(c.Status);
        if (c.Gauge is { } g) ok &= Gauge >= g;
        if (c.Not != null) ok &= !Meets(c.Not, job);
        return ok;
    }

    /// <summary>Requirements other than range and cooldown.</summary>
    public bool Allowed(BoundAction a, JobBinding job)
    {
        var d = a.Def;
        if (d.Requires.Any(c => !Meets(c, job))) return false;
        if (d.GaugeCost > 0 && Gauge < d.GaugeCost && !(d.FreeWith != null && Has(d.FreeWith))) return false;
        return true;
    }

    public double DamageMult(JobBinding job)
    {
        var m = 1.0;
        foreach (var (name, v) in Statuses)
            if (v.Until > T && job.Def.Statuses.TryGetValue(name, out var sd)) m *= sd.DamageMult;
        foreach (var (mult, from, until) in PartyBuffs)
            if (from <= T && until > T) m *= mult;
        return m;
    }

    /// <summary>
    /// Use an action at the current time. Returns the damage it adds. Hits: how many enemies it
    /// reaches. critDhFactor: [Estimate] guaranteed crit+DH vs an average hit.
    /// </summary>
    public double Apply(BoundAction a, JobBinding job, int hits, double critDhFactor)
    {
        var d = a.Def;
        var combo = d.ComboFrom != null && Combo == d.ComboFrom && ComboUntil > T;
        var potency = combo && a.ComboPotency > 0 ? a.ComboPotency : a.Potency;

        // Guaranteed crit+DH: the action's own, or a stack of a status that grants it to this action.
        var critDh = d.CritDh;
        string? stackUsed = null;
        foreach (var (name, sd) in job.Def.Statuses)
            if (sd.CritDhFor.Contains(d.Name) && Has(name)) { critDh = true; stackUsed = name; break; }

        var dmg = 0.0;
        var down = Downtime.Any(w => w.From <= T && w.Until > T);
        if (potency > 0 && !down)
        {
            var h = Math.Max(1, hits);
            var total = potency * (d.Aoe == null ? 1 : 1 + (h - 1) * (1 - d.Falloff));
            dmg = total * DamageMult(job) * (critDh ? critDhFactor : 1);
        }
        // Earlier damage is worth slightly more: without this, sequences that do the same things in a
        // different order tie, and procs drift to the end of their window (seen in testing: Primal
        // Ruination held to the last 0.1 s of its 20 s). [Estimate] 0.3% per second.
        Damage += dmg * Math.Max(0.5, 1 - TimePreference * T);

        // Costs: a free-with stack, else gauge.
        if (d.GaugeCost > 0)
        {
            if (d.FreeWith != null && Has(d.FreeWith)) UseStack(d.FreeWith);
            else Gauge -= d.GaugeCost;
        }
        if (stackUsed != null && stackUsed != d.FreeWith) UseStack(stackUsed);
        foreach (var c in d.Consumes) Statuses.Remove(c);

        foreach (var g in d.Grants)
        {
            if (g.ComboOnly && !combo) continue;
            if (g.IfStatus != null && !Has(g.IfStatus)) continue;
            if (g.Gauge != 0) Gauge = Math.Min(job.Def.Gauge?.Max ?? 100, Gauge + g.Gauge);
            if (g.Status != null) Grant(g, job);
        }
        if (d.Extends is { } e && Has(e.Status))
        {
            var cur = Statuses[e.Status];
            Statuses[e.Status] = (Math.Min(T + e.Max, cur.Until + e.Seconds), cur.Stacks);
        }
        if (d.ReducesRecast is { } r && job.ByName.TryGetValue(r.Action, out var other) && Cd.TryGetValue(other.CooldownGroup, out var oc))
        {
            var left = r.Seconds;
            while (left > 0 && oc.Charges < oc.MaxCharges)
            {
                if (oc.NextChargeIn > left) { oc.NextChargeIn -= left; break; }
                left -= oc.NextChargeIn;
                oc.Charges++;
                oc.NextChargeIn = oc.Charges < oc.MaxCharges ? oc.RecastPerCharge : 0;
            }
            Cd[other.CooldownGroup] = oc;
        }

        // Cooldown.
        if (a.CooldownGroup >= 0 && Cd.TryGetValue(a.CooldownGroup, out var cd))
        {
            if (cd.Charges >= cd.MaxCharges) cd.NextChargeIn = cd.RecastPerCharge;
            cd.Charges = Math.Max(0, cd.Charges - 1);
            Cd[a.CooldownGroup] = cd;
        }

        // Combo: a combo step continues it, other GCDs break it unless they keep it.
        if (a.IsGcd)
        {
            var continues = job.Actions.Any(x => x.Def.ComboFrom == d.Name);
            if (continues && (d.ComboFrom == null || combo)) { Combo = d.Name; ComboUntil = T + 30; }
            else if (!d.KeepsCombo) Combo = null;
        }

        Path.Add(d.Name);
        return dmg;
    }

    private void UseStack(string status)
    {
        if (!Statuses.TryGetValue(status, out var v)) return;
        if (v.Stacks <= 1) Statuses.Remove(status);
        else Statuses[status] = (v.Until, v.Stacks - 1);
    }

    private void Grant(Grant g, JobBinding job)
    {
        var name = g.Status!;
        job.Def.Statuses.TryGetValue(name, out var sd);
        Statuses.TryGetValue(name, out var cur);
        var active = cur.Until > T;
        double until;
        if (g.Extend && active) until = Math.Min(T + (g.Max > 0 ? g.Max : g.Duration), cur.Until + g.Duration);
        else until = T + g.Duration;
        int stacks;
        if (g.Stacks > 0 && sd?.MaxStacks > 0) stacks = Math.Min(sd.MaxStacks, (active ? cur.Stacks : 0) + g.Stacks);
        else stacks = Math.Max(1, g.Stacks);
        Statuses[name] = (until, stacks);

        if (sd?.AtMax is { } next && sd.MaxStacks > 0 && stacks >= sd.MaxStacks)
        {
            Statuses.Remove(name);
            Statuses[next] = (T + sd.AtMaxDuration, 1);
        }
    }
}
