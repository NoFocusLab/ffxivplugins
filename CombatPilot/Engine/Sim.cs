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
    public double Mp;
    public int MaxMp = 10000;
    /// <summary>MP that spending may not go below (raise reserve).</summary>
    public int MpFloor;
    public int MpRegenPerTick = 200;
    public string? Combo;
    public double ComboUntil;
    public Dictionary<string, (double Until, int Stacks)> Statuses = new();
    public List<(double Mult, double From, double Until)> PartyBuffs = new();
    public Dictionary<int, CdState> Cd = new();
    /// <summary>Positionals (null: none to worry about).</summary>
    public PositionInfo? Pos;
    /// <summary>Boss downtime (no damage lands): from, until.</summary>
    public List<(double From, double Until)> Downtime = new();
    /// <summary>Per recast group: time spent sitting at full charges (wasted cooldown).</summary>
    public Dictionary<int, double> Idle = new();
    public double Damage;
    public List<string> Path = new();
    /// <summary>When the last GCD went out (a long-lock oGCD may only be the first weave after it).</summary>
    public double LastGcdAt = -10;
    /// <summary>Cast time of the last action applied (0 = instant).</summary>
    public double LastCast;

    /// <summary>The job's status table (for MP-over-time); set once by the planner.</summary>
    public Dictionary<string, StatusDef>? statusDefs;

    /// <summary>MP this action costs if pressed now.</summary>
    public int MpCostOf(BoundAction a)
    {
        if (a.MpCost <= 0) return 0;
        var cost = (double)a.MpCost;
        foreach (var m in a.Def.MpCostWith) if (Has(m.Status)) cost *= m.Mult;
        return (int)cost;
    }

    public Sim Clone() => new()
    {
        statusDefs = statusDefs,
        T = T, GcdReadyAt = GcdReadyAt, LastGcdAt = LastGcdAt, LockUntil = LockUntil, Gauge = Gauge, Mp = Mp, MaxMp = MaxMp, MpFloor = MpFloor, MpRegenPerTick = MpRegenPerTick, Combo = Combo, ComboUntil = ComboUntil,
        Statuses = new(Statuses), PartyBuffs = PartyBuffs, Downtime = Downtime, Pos = Pos, Cd = new(Cd), Idle = new(Idle), Damage = Damage, Path = new(Path),
    };

    public static Sim From(GameState g)
    {
        var s = new Sim
        {
            GcdReadyAt = g.GcdRemaining,
            // When the last GCD went out, from the game's GCD timer. Without it every real weave looked
            // "too long after the GCD" and Stardiver was never allowed (seen live: once in 12 minutes).
            LastGcdAt = g.GcdRemaining > 0.01 ? g.GcdRemaining - g.GcdLength : -10,
            LockUntil = g.AnimationLock,
            Gauge = g.Gauge,
            Mp = g.Mp,
            MaxMp = g.MaxMp,
            Combo = g.Combo,
            ComboUntil = g.ComboRemaining,
            PartyBuffs = g.PartyBuffs.ToList(),
            Downtime = g.Downtime,
            Pos = g.Position,
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
        // MP: natural regen plus MP-over-time buffs ([Estimate] smoothed; the game ticks every 3 s).
        if (Mp < MaxMp)
        {
            var perTick = (double)MpRegenPerTick;
            if (statusDefs != null)
                foreach (var (name, v) in Statuses)
                    if (v.Until > T && statusDefs.TryGetValue(name, out var sd) && sd.MpPerTick > 0)
                        perTick += sd.MpPerTick * Math.Min(dt, v.Until - T) / dt;
            Mp = Math.Min(MaxMp, Mp + perTick / 3 * dt);
        }
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
        if (d.RequiresCombo && !(d.Follows(Combo) && ComboUntil > T)) return false;
        var mp = MpCostOf(a);
        if (mp > 0 && Mp - mp < MpFloor) return false;
        if (d.UsesCharge is { } u && !u.FreeWith.Any(Has) && job.ByName.TryGetValue(u.Action, out var src) &&
            Cd.TryGetValue(src.CooldownGroup, out var sc) && sc.Charges <= 0) return false;
        if (d.GaugeCost > 0 && Gauge < d.GaugeCost && !(d.FreeWith != null && Has(d.FreeWith))) return false;
        return true;
    }

    /// <summary>The boost this action would get now, if any (first listed status that's up).</summary>
    public Boost? BoostFor(BoundAction a, bool combo = false) =>
        a.Def.Boosts.FirstOrDefault(b => Has(b.Status) && (!b.ComboOnly || combo));

    /// <summary>Cast time if pressed now (0 = instant).</summary>
    public double CastFor(BoundAction a) =>
        a.Def.CastTime <= 0 ? 0 : BoostFor(a) is { Instant: true } ? 0 : a.Def.CastTime;

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
    public double Apply(BoundAction a, JobBinding job, int hits, double critDhFactor, double critFactor = 1.35)
    {
        var d = a.Def;
        var combo = d.Follows(Combo) && ComboUntil > T;
        // "Only while X is up" is judged as the action starts, before it uses anything up (the
        // third Fell Cleave in Inner Release spends the last stack and still grants Burgeoning Fury;
        // found by the simulator: Primal Wrath never came up).
        var upAtStart = d.Grants.Where(gr => gr.IfStatus != null).Select(gr => gr.IfStatus!).Where(Has).ToHashSet();
        var potency = combo && a.ComboPotency > 0 ? a.ComboPotency : a.Potency;
        Mp -= MpCostOf(a);
        // Walking could do this job (True North while auto-move can reach the rear or flank).
        if (d.CostIfCanMove > 0 && Pos != null && !Pos.Omni && (Pos.RearEta < 999 || Pos.FlankEta < 999)) Damage -= d.CostIfCanMove;
        // Decided before anything is used up (Kassatsu both boosts and frees the ninjutsu).
        var freeCharge = d.UsesCharge != null && d.UsesCharge.FreeWith.Any(Has);
        var boost = BoostFor(a, combo);
        LastCast = CastFor(a);
        if (boost != null) potency = boost.Potency;
        // Positional: lands under True North, on a target without positionals, if you're already on
        // that side, or if auto-move can get you there before this GCD.
        if (d.Positional != null && d.PositionalBonus > 0 && potency > 0 &&
            (Has(job.Def.TrueNorthStatus) || (Pos != null && Pos.Satisfied(d.Positional, T))))
            potency += d.PositionalBonus;

        // Guaranteed crit+DH: the action's own, or a stack of a status that grants it to this action.
        var critDh = d.CritDh;
        string? stackUsed = null;
        foreach (var (name, sd) in job.Def.Statuses)
            if (sd.CritDhFor.Contains(d.Name) && Has(name)) { critDh = true; stackUsed = name; break; }
        // Guaranteed crit only (Life Surge: "*" = the next weaponskill that deals damage).
        var crit = false;
        if (!critDh && potency > 0)
            foreach (var (name, sd) in job.Def.Statuses)
                if (sd.CritFor.Count > 0 && Has(name) && (sd.CritFor.Contains(d.Name) || (a.IsGcd && sd.CritFor.Contains("*"))))
                { crit = true; stackUsed = name; break; }

        var dmg = 0.0;
        var down = Downtime.Any(w => w.From <= T && w.Until > T);
        if (potency > 0 && !down && hits > 0)
        {
            var h = hits;
            var total = potency * (!a.IsAoe ? 1 : 1 + (h - 1) * (1 - a.Falloff));
            dmg = total * DamageMult(job) * (critDh ? critDhFactor : crit ? critFactor : 1);
        }
        // Damage over time: the ticks it adds (a refresh loses the ticks the old one had left).
        if (d.Dot is { } dot && (!dot.ComboOnly || combo) && !down && hits > 0)
        {
            var had = Statuses.TryGetValue(dot.Status, out var old) && old.Until > T ? old.Until - T : 0;
            dmg += dot.Potency * Math.Max(0, dot.Duration - had) / 3 * DamageMult(job);
            Statuses[dot.Status] = (T + dot.Duration, 1);
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
        if (boost is { Consume: true }) UseStack(boost.Status);
        foreach (var c in d.ConsumesStack) UseStack(c);
        // Shadows (Bunshin): an extra hit on each damaging weaponskill (not ninjutsu).
        if (a.IsGcd && d.Sequence == null && potency > 0 && hits > 0)
            foreach (var (name, sd) in job.Def.Statuses)
            {
                if (sd.ExtraPerWeaponskill <= 0 || !Has(name)) continue;
                if (!down)
                {
                    var extra = sd.ExtraPerWeaponskill * DamageMult(job);
                    dmg += extra;
                    Damage += extra * Math.Max(0.5, 1 - TimePreference * T);
                }
                Gauge = Math.Min(job.Def.Gauge?.Max ?? 100, Gauge + sd.ExtraGauge);
                UseStack(name);
            }
        // A charge of another action (a mudra), unless something makes it free (Kassatsu).
        if (d.UsesCharge is { } use && !freeCharge && job.ByName.TryGetValue(use.Action, out var chargeOf) &&
            Cd.TryGetValue(chargeOf.CooldownGroup, out var mc))
        {
            if (mc.Charges >= mc.MaxCharges) mc.NextChargeIn = mc.RecastPerCharge;
            mc.Charges = Math.Max(0, mc.Charges - 1);
            Cd[chargeOf.CooldownGroup] = mc;
        }
        foreach (var c in d.Consumes) Statuses.Remove(c);

        foreach (var g in d.Grants)
        {
            if (g.ComboOnly && !combo) continue;
            if (g.IfStatus != null && !upAtStart.Contains(g.IfStatus)) continue;
            if (g.MinLevel > job.Level) continue;
            if (g.Gauge != 0) Gauge = Math.Min(job.Def.Gauge?.Max ?? 100, Gauge + g.Gauge);
            if (g.Mp != 0) Mp = Math.Min(MaxMp, Mp + g.Mp);
            if (g.Status != null) Grant(g, job);
        }
        if (d.Extends is { } e && Has(e.Status))
        {
            var cur = Statuses[e.Status];
            Statuses[e.Status] = (Math.Min(T + e.Max, cur.Until + e.Seconds), cur.Stacks);
        }
        if (d.ReducesRecast is { } r && r.MinLevel <= job.Level && job.ByName.TryGetValue(r.Action, out var other) && Cd.TryGetValue(other.CooldownGroup, out var oc))
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
            var continues = job.Actions.Any(x => x.Def.Follows(d.Name));
            if (continues && (d.ComboFrom == null || combo)) { Combo = d.Name; ComboUntil = T + 30; }
            else if (!d.KeepsCombo) Combo = null;
        }

        // A press with nothing to gain shouldn't tie with not pressing (found by the simulator: True
        // North used every time it was ready, for nothing). A token cost breaks the tie.
        if (!a.IsGcd) Damage -= 0.1;
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
