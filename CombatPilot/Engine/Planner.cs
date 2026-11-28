using System;
using System.Collections.Generic;
using System.Linq;
using CombatPilot.Jobs;

namespace CombatPilot.Engine;

/// <summary>What the planner wants to do now, and how every other first move compares.</summary>
public sealed class Decision
{
    /// <summary>The action to press now; null = nothing (skip the weave, or hold the GCD).</summary>
    public BoundAction? Action;
    public string Label = "(nothing)";
    public bool ForGcd;
    public double Score;
    /// <summary>Best score reachable after each possible first move (label -> score).</summary>
    public Dictionary<string, double> RootScores = new();
    /// <summary>The best sequence found after each first move (simulator traces, debugging).</summary>
    public Dictionary<string, List<string>> RootPaths = new();
    public Dictionary<string, double> RootDamage = new();
    public List<string> Path = new();
    public string? Error;
    /// <summary>What the planner saw (combo, tracked buffs and timers, gauge, MP), for the log.</summary>
    public string State = "";
    public DateTime MadeAt = DateTime.UtcNow;
}

public sealed class PlannerSettings
{
    public int HorizonGcds = 6;
    public int BeamWidth = 48;
    /// <summary>[Estimate] a guaranteed critical direct hit vs an average hit (crit ~25%/+60%, DH ~20%/+25%).</summary>
    public double CritDhFactor = 1.65;
    /// <summary>[Estimate] a guaranteed critical hit (no direct hit) vs an average hit.</summary>
    public double CritFactor = 1.35;
    /// <summary>Weave slots to leave free in the current window (Mit Planner has a press due).</summary>
    public int ReservedWeaves;
    /// <summary>Burst actions held by the bar's toggle.</summary>
    public HashSet<string> Held = new();
    public AoeMode Aoe = AoeMode.Auto;
    /// <summary>Travel action whose charges above Keep are all the planner may spend.</summary>
    public string? ReserveAction;
    public int ReserveKeep;
    /// <summary>Don't use an AoE that would hit an enemy that isn't fighting yet.</summary>
    public bool NoPull = true;
    /// <summary>A party member the partner rules allow is in range (ally-targeted actions).</summary>
    public bool HasPartner;
    /// <summary>Seconds of walking that make True North unnecessary (about one GCD).</summary>
    public double WalkWithin = 2.5;
    /// <summary>MP kept back (a raise's cost when the job's toggle is on).</summary>
    public int MpFloor;
}

/// <summary>
/// Looks ahead a few GCDs (with the weaves between them) and picks the first move of the best
/// sequence. Re-run every decision, so a missed GCD, a missed positional or walking out for a
/// mechanic just becomes the new starting point. Beam search: at each step only the best
/// BeamWidth sequences are kept (plus the best one for every possible first move, so every first
/// move gets a score for the report).
/// </summary>
public static class Planner
{
    private sealed class Node
    {
        public required Sim Sim;
        public bool NextIsGcd;
        public int WeavesLeft;
        public int Gcds;
        public string Root = "";
        public double Score;
    }

    /// <summary>[Estimate] seconds a proc needs left to count as usable later (2 GCDs).</summary>
    private const double ExpirySafety = 5.0;

    private const string Skip = "(skip)";
    private const string Hold = "(hold)";

    public static Decision Decide(GameState g, JobBinding job, PlannerSettings set, bool forGcd)
    {
        var decision = new Decision { ForGcd = forGcd };
        try
        {
            var start = Sim.From(g);
            start.statusDefs = job.Def.Statuses;
            start.MpRegenPerTick = job.Def.MpRegenPerTick;
            start.MpFloor = set.MpFloor;
            var root = new Node { Sim = start, NextIsGcd = forGcd };
            if (!forGcd) root.WeavesLeft = Math.Max(0, WeavesBetween(Math.Max(0, start.LockUntil), start.GcdReadyAt) - set.ReservedWeaves);
            if (!forGcd && root.WeavesLeft == 0) { decision.Label = Skip; return decision; }

            var frontier = new List<Node> { root };
            var done = new List<Node>();
            var guard = 0;
            while (frontier.Count > 0 && guard++ < 200)
            {
                var next = new List<Node>();
                foreach (var n in frontier)
                {
                    var children = Expand(n, g, job, set);
                    foreach (var c in children)
                    {
                        if (c.Root.Length == 0) c.Root = c.Sim.Path.Count > 0 ? c.Sim.Path[0] : (n.NextIsGcd ? Hold : Skip);
                        if (c.Gcds >= set.HorizonGcds && c.NextIsGcd) { c.Score = Score(c.Sim, job); done.Add(c); }
                        else { c.Score = Score(c.Sim, job); next.Add(c); }
                    }
                }
                frontier = Prune(next, set.BeamWidth);
            }

            if (done.Count == 0) { decision.Error = "no sequence found"; return decision; }
            foreach (var grp in done.GroupBy(d => d.Root))
            {
                var top = grp.OrderByDescending(d => d.Score).First();
                decision.RootScores[grp.Key] = top.Score;
                decision.RootPaths[grp.Key] = top.Sim.Path;
                decision.RootDamage[grp.Key] = top.Sim.Damage;
            }
            var best = done.OrderByDescending(d => d.Score).First();
            decision.Score = best.Score;
            decision.Path = best.Sim.Path;
            decision.Label = best.Root;
            decision.Action = job.ByName.GetValueOrDefault(best.Root);
        }
        catch (Exception ex)
        {
            decision.Error = ex.Message;
            EngineLog.Warning(ex, "Combat Pilot planner failed");
        }
        return decision;
    }

    /// <summary>oGCDs that fit between two times without delaying the GCD.</summary>
    public static int WeavesBetween(double from, double gcdReadyAt) =>
        Math.Clamp((int)Math.Floor((gcdReadyAt - from + 0.05) / Sim.OgcdLock), 0, 2);

    private static List<Node> Prune(List<Node> nodes, int width)
    {
        if (nodes.Count <= width) return nodes;
        var sorted = nodes.OrderByDescending(n => n.Score).ToList();
        var keep = sorted.Take(width).ToList();
        // Keep every first move alive so it gets a score.
        foreach (var grp in sorted.GroupBy(n => n.Root))
            if (!keep.Any(k => k.Root == grp.Key)) keep.Add(grp.First());
        return keep;
    }

    private static bool Usable(Sim c, BoundAction a, JobBinding job, PlannerSettings set)
    {
        if (set.Held.Contains(a.Name) || !c.Ready(a) || !c.Allowed(a, job)) return false;
        if (a.Def.AllyTarget && !set.HasPartner) return false;
        if (a.Def.PlanNever) return false;
        // True North against a target with no positionals does nothing (found by the simulator).
        if (a.Def.CostIfCanMove > 0 && c.Pos is { Omni: true }) return false;
        // True North: not while auto-move can reach both the rear and the flank safely within a GCD
        // (walking covers every positional; seen live: 17 uses in 8 min with auto-move on).
        if (a.Def.CostIfCanMove > 0 && c.Pos is { Omni: false } p && p.RearEta <= set.WalkWithin && p.FlankEta <= set.WalkWithin)
            return false;
        // Pure movement (Elusive Jump, Winged Glide) is never planned: only the bar's queue presses it.
        if (a.Def.Moves != null || (a.Def.MovesToTarget && a.Def.Potency <= 0 && a.Def.Grants.Count == 0)) return false;
        if (set.ReserveAction == a.Name && a.CooldownGroup >= 0 && c.Cd.TryGetValue(a.CooldownGroup, out var cd) && cd.Charges <= set.ReserveKeep)
            return false;
        return true;
    }

    private static IEnumerable<Node> Expand(Node n, GameState g, JobBinding job, PlannerSettings set)
    {
        var s = n.Sim;
        var nearNow = s.Path.Count == 0;


        if (n.NextIsGcd)
        {
            var t = Math.Max(s.GcdReadyAt, s.LockUntil);
            var any = false;
            var at0 = s.Clone();
            at0.AdvanceTo(t);
            foreach (var a in job.Actions)
            {
                if (!a.IsGcd) continue;
                if (!Usable(at0, a, job, set)) continue;
                if (nearNow && (!g.HasTarget || !Reaches(a, g, set))) continue;
                // A hard cast can't start while you're moving.
                if (nearNow && g.PlayerMoving && at0.CastFor(a) > 0) continue;
                if (a.Def.CastOnlyAtRange && at0.CastFor(a) > 0 &&
                    (!nearNow || job.Actions.Any(x => x.IsGcd && !x.Def.Ranged && !x.Def.RangedOk && Reaches(x, g, set)))) continue;
                var c = at0.Clone();
                c.Apply(a, job, Hits(a, g, set, c.T), set.CritDhFactor, set.CritFactor);
                c.GcdReadyAt = t + (a.Def.GcdTime > 0 ? a.Def.GcdTime : g.GcdLength);
                c.LastGcdAt = t;
                // Casting: no weaving until the cast (plus slide-cast margin) is done. Sequences
                // (mudras): not until the last button.
                c.LockUntil = t + Math.Max(Math.Max(Sim.GcdLock, a.Def.WeaveFrom), c.LastCast > 0 ? c.LastCast + 0.1 : 0);
                any = true;
                yield return After(n, c, gcd: true);
            }
            // Holding the GCD: only worth considering when nothing reaches the target right now.
            if (!any || (nearNow && !job.Actions.Any(a => a.IsGcd && Reaches(a, g, set) && !a.Def.Ranged && !a.Def.RangedOk)))
            {
                var c = s.Clone();
                c.AdvanceTo(t + g.GcdLength);
                c.GcdReadyAt = c.T;
                c.Path.Add(Hold);
                yield return new Node { Sim = c, NextIsGcd = true, Gcds = n.Gcds + 1, Root = n.Root };
            }
            yield break;
        }

        // Weave slot: skip, or one oGCD that fits before the GCD comes back.
        var skip = s.Clone();
        yield return new Node { Sim = skip, NextIsGcd = true, Gcds = n.Gcds, Root = n.Root.Length > 0 ? n.Root : Skip };
        if (n.WeavesLeft <= 0) yield break;
        var at = Math.Max(s.LockUntil, s.T);
        if (at + Sim.OgcdLock > s.GcdReadyAt + 0.05) yield break;
        var w0 = s.Clone();
        w0.AdvanceTo(at);
        foreach (var a in job.Actions)
        {
            if (a.IsGcd) continue;
            var lockFor = a.Def.AnimationLock > 0 ? a.Def.AnimationLock : Sim.OgcdLock;
            // A long animation (Stardiver) only as the first weave after a GCD, never after another
            // oGCD (seen live: Geirskogul + Stardiver pushed the next GCD back ~0.8 s, 11 times in 9 min).
            if (a.Def.AnimationLock > Sim.OgcdLock && at > w0.LastGcdAt + Sim.GcdLock + 0.45) continue;
            if (!Usable(w0, a, job, set)) continue;
            if (a.Def.Potency > 0 && nearNow && (!g.HasTarget || !Reaches(a, g, set))) continue;
            if (a.Def.Potency > 0 && !g.HasTarget) continue;
            var c = w0.Clone();
            c.Apply(a, job, Hits(a, g, set, c.T), set.CritDhFactor, set.CritFactor);
            c.LockUntil = at + lockFor;
            // Clipping: a long lock past the GCD delays the GCD.
            if (c.LockUntil > c.GcdReadyAt)
            {
                // The horizon counts GCDs, so a late GCD would look free: charge the lost time.
                c.Damage -= 2 * (c.LockUntil - c.GcdReadyAt) * job.Def.PotencyPerSecond; // x2: a late GCD drifts everything after it
                c.GcdReadyAt = c.LockUntil;
            }
            var left = lockFor > Sim.OgcdLock ? 0 : n.WeavesLeft - 1;
            yield return new Node { Sim = c, NextIsGcd = left <= 0, WeavesLeft = left, Gcds = n.Gcds, Root = n.Root };
        }
    }

    private static Node After(Node parent, Sim c, bool gcd) => new()
    {
        Sim = c,
        NextIsGcd = false,
        WeavesLeft = WeavesBetween(c.LockUntil, c.GcdReadyAt),
        Gcds = parent.Gcds + (gcd ? 1 : 0),
        Root = parent.Root,
    };

    /// <summary>
    /// Can it hit something right now. Single-target: the game's range/line-of-sight check on your
    /// target. AoE: something that counts is inside its area, it doesn't pull a new enemy (setting),
    /// and with AoE off it hits nothing but your target.
    /// </summary>
    public static bool Reaches(BoundAction a, GameState g, PlannerSettings? set = null)
    {
        if (g.UnsafeDash.Contains(a.Id)) return false; // would land in a known danger zone
        if (!a.IsAoe) return g.InRange.Contains(a.Id);
        if (set != null && set.NoPull && g.WouldPull.Contains(a.Id)) return false;
        if (set?.Aoe == AoeMode.SingleTarget && g.HitsOthers.Contains(a.Id)) return false;
        if (a.Shape is Shape.CircleTarget or Shape.Cone or Shape.Line && !g.InRange.Contains(a.Id)) return false;
        return g.HitTtks.TryGetValue(a.Id, out var h) && h.Length > 0;
    }

    /// <summary>
    /// How many enemies an action hits at time t (seconds from now): the ones inside its area that
    /// are expected to still be alive then. AoE force: at least 3.
    /// </summary>
    private static int Hits(BoundAction a, GameState g, PlannerSettings set, double t)
    {
        if (!a.IsAoe) return g.TargetTtk > t || g.TargetTtk >= 999 ? 1 : 0;
        var n = g.HitTtks.TryGetValue(a.Id, out var h) ? h.Count(x => x > t) : 0;
        return set.Aoe == AoeMode.Force ? Math.Max(3, n) : n;
    }

    /// <summary>Damage so far plus what's left in hand, minus cooldown time wasted.</summary>
    public static double Score(Sim s, JobBinding job)
    {
        var def = job.Def;
        var v = s.Damage;
        v += s.Gauge * (def.Gauge?.ValuePerUnit ?? 0);
        // MP in hand, plus what MP-over-time buffs will still restore (only up to the cap).
        if (def.MpValue > 0)
        {
            var mp = s.Mp;
            foreach (var (name, st) in s.Statuses)
                if (st.Until > s.T && def.Statuses.TryGetValue(name, out var msd) && msd.MpPerTick > 0)
                    mp += msd.MpPerTick / 3.0 * (st.Until - s.T);
            v += Math.Min(mp, s.MaxMp) * def.MpValue;
        }

        foreach (var (name, st) in s.Statuses)
        {
            var left = st.Until - s.T;
            if (left <= 0 || !def.Statuses.TryGetValue(name, out var sd)) continue;
            if (sd.DamageMult > 1 && !sd.Maintained) v += (sd.DamageMult - 1) * def.PotencyPerSecond * Math.Min(left, 30);
            // A proc or stack only counts as "still in hand" if there's time left to use it: one
            // about to run out is worth nothing (testing: an Inner Release stack ran out unused).
            if (sd.TerminalValue > 0 && left > ExpirySafety) v += sd.TerminalValue * Math.Max(1, st.Stacks);
        }

        // Partway through a combo: the next step's combo bonus is still to come.
        if (s.Combo != null && s.ComboUntil > s.T + 3)
        {
            var bonus = job.Actions.Where(a => a.Def.Follows(s.Combo)).Select(a => a.ComboPotency - a.Potency).DefaultIfEmpty(0).Max();
            v += Math.Max(0, bonus) * 0.8;
        }

        // Cooldowns left sitting at full charges: that time is lost for good.
        foreach (var (group, idle) in s.Idle)
        {
            if (idle <= 0 || !s.Cd.TryGetValue(group, out var cd)) continue;
            // Only real cooldowns: a 1-second recast gated by gauge (Bhavacakra) "sits ready" most of
            // the fight and charged ~400 potency a second (found by the simulator: it made every
            // longer sequence, like Raiton into Fleeting Raiju, look ~1,900 potency worse).
            if (cd.RecastPerCharge < 5) continue;
            var worth = job.Actions.Where(a => a.CooldownGroup == group).Select(a => ActionWorth(a, job)).DefaultIfEmpty(0).Max();
            v -= idle * worth / Math.Max(1, cd.RecastPerCharge);
        }
        return v;
    }

    private static double ActionWorth(BoundAction a, JobBinding job)
    {
        var w = a.Potency;
        foreach (var gr in a.Def.Grants)
        {
            w += gr.Gauge * (job.Def.Gauge?.ValuePerUnit ?? 0);
            if (gr.Status != null && job.Def.Statuses.TryGetValue(gr.Status, out var sd))
            {
                w += sd.TerminalValue * Math.Max(1, gr.Stacks);
                // A damage window it opens is part of what it's worth (Kunai's Bane: +10% for 15 s;
                // without this, a cooldown waiting on a setup looked cheap to leave idle).
                if (sd.DamageMult > 1 && !sd.Maintained) w += (sd.DamageMult - 1) * job.Def.PotencyPerSecond * gr.Duration;
            }
        }
        return w;
    }
}
