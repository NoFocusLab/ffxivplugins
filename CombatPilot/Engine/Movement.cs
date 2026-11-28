using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;

namespace CombatPilot.Engine;

/// <summary>
/// BossMod's AI, read over IPC: where its AI wants you to go (towers, stacks, dodges) and whether a
/// spot is safe. With BossMod's AI set to handle mechanics only (no follow distance), any
/// destination it has is a mechanic, and mechanics come first.
/// </summary>
public sealed class BossModAi
{
    private readonly ICallGateSubscriber<bool> isNavigating = Plugin.PluginInterface.GetIpcSubscriber<bool>("BossMod.AI.IsNavigating");
    private readonly ICallGateSubscriber<Vector3?> naviTarget = Plugin.PluginInterface.GetIpcSubscriber<Vector3?>("BossMod.AI.NaviTargetPos");
    private readonly ICallGateSubscriber<Vector3, bool> isPositionSafe = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool>("BossMod.Hints.IsPositionSafe");
    private readonly ICallGateSubscriber<float> specialModeIn = Plugin.PluginInterface.GetIpcSubscriber<float>("BossMod.Hints.SpecialModeIn");

    public bool Loaded { get; private set; }

    /// <summary>BossMod's AI has somewhere it wants you to be (a mechanic).</summary>
    public Vector3? MechanicSpot()
    {
        try
        {
            Loaded = true;
            return isNavigating.InvokeFunc() ? naviTarget.InvokeFunc() : null;
        }
        catch (Exception) { Loaded = false; return null; }
    }

    /// <summary>True: safe, or no information.</summary>
    public bool Safe(Vector3 spot)
    {
        try { return isPositionSafe.InvokeFunc(spot); }
        catch (Exception) { return true; }
    }

    /// <summary>Seconds until a special mechanic (pyretic, misdirection...); MaxValue if none.</summary>
    public float SpecialIn()
    {
        try { return specialModeIn.InvokeFunc(); }
        catch (Exception) { return float.MaxValue; }
    }
}

/// <summary>
/// Auto-move: picks where to stand and walks you there with vnavmesh. Priorities:
/// 1. BossMod's AI mechanic (tower, stack, dodge): it moves you, Combat Pilot steps aside.
/// 2. Safety: every spot needs floor, a BossMod-safe spot and a safe path; never chosen otherwise.
/// 3. Positional for the next 2 GCDs (melee).
/// 4. Your rules for the job: within X of the anchor (party member by role or name, or none for
///    tanks), within Y of the target; or, when an AoE around you would do 15%+ more than single
///    target (time to live and the walk counted), within Z of the pack's centre.
/// Hands back when you move yourself (1.5 s) and holds still while you cast.
/// </summary>
public sealed class Mover
{
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo =
        Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
    private readonly ICallGateSubscriber<object> stop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    public readonly BossModAi Bmr = new();

    public bool Moving { get; private set; }
    public string Status { get; private set; } = "";
    /// <summary>Why it chose the spot it did (for the Pilot tab).</summary>
    public string Reason { get; private set; } = "";

    private DateTime startedAt, nextThink = DateTime.MinValue, holdUntil = DateTime.MinValue;

    /// <summary>Stop for an action and stay put a moment (weaves, auto-attacks), instead of walking off facing away.</summary>
    public void PauseForAction(string what)
    {
        if (Moving) Halt("stopped to use " + what);
        // Long enough for the action to go out, short enough to walk in the weave window: a 1 s hold
        // with a GCD and two weaves every 2.5 s never ended, so rear positionals were missed.
        holdUntil = DateTime.UtcNow.AddSeconds(0.35);
    }
    private Vector3 dest, lastPos;
    private DateTime lastPosAt = DateTime.MinValue, youMovedAt;
    private bool youMoved;

    public static void Defaults(MoveRules r, uint role)
    {
        // ClassJob.Role: 1 tank, 2 melee, 3 ranged (physical and casters), 4 healer.
        r.Anchor = role == 1 ? AnchorKind.Target : AnchorKind.Role;
        r.AnchorRole = AnchorRole.Tank;
        r.AnchorRange = role == 1 ? 0 : 20;
        r.TargetRange = role is 1 or 2 ? 3 : 25;
        r.PackRange = 0;
        r.AoeGain = 0.15f;
        r.Initialized = true;
    }

    public void Update(IGameObject me, JobBinding job, GameState? g, Decision? plan, MoveRules rules, DangerBridge danger, bool allowed)
    {
        var now = DateTime.UtcNow;
        if (!Moving && lastPosAt != DateTime.MinValue && Vector3.Distance(me.Position, lastPos) > 0.3f) { youMoved = true; youMovedAt = now; }
        if (youMoved && (now - youMovedAt).TotalSeconds > 1.5) youMoved = false;
        lastPos = me.Position;
        lastPosAt = now;

        if (!allowed || g == null || !g.HasTarget) { Halt(allowed ? "" : Status == "" ? "" : "off"); return; }
        if (Bmr.MechanicSpot() is { } m) { Halt("BossMod is handling a mechanic"); Reason = $"mechanic at {Vector3.Distance(me.Position, m):0.0}y"; return; }
        if (Bmr.SpecialIn() < 3) { Halt("special mechanic coming: holding"); return; }
        if (youMoved) { Halt("you're moving: not taking over"); return; }
        if (Plugin.ObjectTable.LocalPlayer?.IsCasting == true) return; // never cut a cast
        if (now < nextThink || now < holdUntil) return;
        nextThink = now.AddMilliseconds(250);

        var target = Plugin.TargetManager.Target;
        if (target == null) { Halt(""); return; }
        var pick = Choose(me, target, job, g, plan, rules, danger, out var reason);
        Reason = reason;
        if (pick == null) { Halt(reason); return; }
        // Close enough: for a positional, when you're well inside the zone (by angle, not distance:
        // 1 y off at a dummy's edge is 30° and can be the wrong side); otherwise within 1.2 y.
        var forPositional = reason.EndsWith("for the positional");
        var inPlace = forPositional
            ? Positionals.SafeSideOf(me.Position, target) == reason.Split(' ')[0] || Vector3.Distance(Flat(pick.Value), Flat(me.Position)) < 0.4f
            : Vector3.Distance(Flat(pick.Value), Flat(me.Position)) < 1.2f;
        if (inPlace) { Halt($"in place ({reason})"); return; }
        if (Moving && (now - startedAt).TotalSeconds > 4) { Halt("gave up (4 s)"); return; }
        if (!Moving || Vector3.Distance(pick.Value, dest) > 1f)
        {
            try
            {
                if (moveCloseTo.InvokeFunc(pick.Value, false, 0.3f))
                {
                    if (!Moving) startedAt = now;
                    Moving = true;
                    dest = pick.Value;
                    Status = $"moving: {reason}";
                }
                else Halt("vnavmesh refused the move");
            }
            catch (Exception) { Halt("vnavmesh not loaded"); }
        }
    }

    /// <summary>The best safe spot under the rules, or null (stay).</summary>
    private Vector3? Choose(IGameObject me, IGameObject target, JobBinding job, GameState g, Decision? plan, MoveRules r,
        DangerBridge danger, out string reason)
    {
        var anchor = Anchor(me, target, r);
        var tr = target.HitboxRadius;

        // Positional within the next two GCDs.
        string? need = null;
        if (plan != null && g.Position is { Omni: false } pos && !HasStatus(job.Def.TrueNorthStatus))
        {
            var gcds = 0;
            foreach (var name in plan.Path)
            {
                if (!job.ByName.TryGetValue(name, out var a) || !a.IsGcd) continue;
                if (a.Def.Positional != null) { need = a.Def.Positional; break; }
                if (++gcds >= 2) break;
            }
            // Already well inside that zone (not just over its edge): stay.
            if (need != null && pos.SafeSide == need) need = null;
        }

        bool Ok(Vector3 p) => GameState.FloorAt(p, me.Position.Y) && Bmr.Safe(p) &&
                              !(danger.Zones() > 0 && !danger.DashSafe(me.Position, p, out var k) && k);
        bool Rules(Vector3 p, double targetRange) =>
            Vector3.Distance(Flat(p), Flat(target.Position)) - tr <= targetRange &&
            (anchor == null || r.AnchorRange <= 0 || Vector3.Distance(Flat(p), Flat(anchor.Position)) <= r.AnchorRange);

        // 1. Positional spot.
        if (need != null && g.Position is { } pi)
        {
            var spot = need == "rear" ? pi.RearSpot : pi.FlankSpot;
            if (Ok(spot)) { reason = $"{need} for the positional"; return spot; }
        }

        // 2. AoE around me: worth walking to the pack?
        var aoe = job.Actions.Where(a => a.IsGcd && a.Shape == Shape.CircleSelf && a.Potency > 0).OrderByDescending(a => a.Potency).FirstOrDefault();
        var st = job.Actions.Where(a => a.IsGcd && !a.IsAoe && a.Potency > 0 && !a.Def.Ranged).Select(a => Math.Max(a.Potency, a.ComboPotency)).DefaultIfEmpty(0).Max();
        if (aoe != null && g.Enemies.Count >= 2 && st > 0)
        {
            var center = new Vector3(g.Enemies.Average(e => e.Position.X), target.Position.Y, g.Enemies.Average(e => e.Position.Z));
            var z = r.PackRange > 0 ? r.PackRange : Math.Max(1, aoe.Radius - 1);
            Vector3? best = null;
            var bestGain = 0.0;
            foreach (var p in Ring(center, 0, (float)z, 8).Prepend(center))
            {
                if (!Rules(p, Math.Max(r.TargetRange, z + 30)) || !Ok(p)) continue; // pack spot may be far from the target's ring
                var eta = Vector3.Distance(Flat(p), Flat(me.Position)) / Positionals.RunSpeed;
                var hits = g.Enemies.Count(e => Vector3.Distance(Flat(e.Position), Flat(p)) - e.Hitbox <= aoe.Radius && e.Ttk > eta + g.GcdLength);
                var aoeValue = aoe.Potency * (1 + (hits - 1) * (1 - aoe.Falloff));
                // The walk costs GCDs for casters (no casting on the move), little for instant jobs.
                var walkCost = (job.Def.CastsWhileMoving ? 0.1 : 1.0) * st * eta / Math.Max(1, g.GcdLength);
                var gain = (aoeValue - walkCost) / st - 1;
                if (gain > bestGain) { bestGain = gain; best = p; }
            }
            if (best != null && bestGain >= r.AoeGain) { reason = $"pack for {aoe.Name} (+{bestGain:P0})"; return best; }
        }

        // 3. Rules: if already fine, stay; else the nearest safe spot that satisfies them.
        if (Rules(me.Position, r.TargetRange)) { reason = "in range"; return null; }
        var stand = Math.Max(0.5f, Math.Min(r.TargetRange - 0.5f, r.TargetRange * 0.8f));
        var candidates = Ring(target.Position, tr + stand, tr + stand, 16)
            .Where(p => Rules(p, r.TargetRange) && Ok(p))
            .OrderBy(p => Vector3.Distance(Flat(p), Flat(me.Position)))
            .ToList();
        if (candidates.Count == 0) { reason = "no safe spot meets the rules"; return null; }
        reason = anchor != null && r.AnchorRange > 0 ? $"within {r.TargetRange:0}y of target and {r.AnchorRange:0}y of {anchor.Name}" : $"within {r.TargetRange:0}y of target";
        return candidates[0];
    }

    private static IGameObject? Anchor(IGameObject me, IGameObject target, MoveRules r)
    {
        if (r.Anchor == AnchorKind.Target) return null;
        foreach (var m in Plugin.PartyList)
        {
            if (m.GameObject is not { } o || o.GameObjectId == me.GameObjectId) continue;
            if (r.Anchor == AnchorKind.Name && m.Name.TextValue.Equals(r.AnchorName, StringComparison.OrdinalIgnoreCase)) return o;
            if (r.Anchor == AnchorKind.Role && (m.ClassJob.ValueNullable?.Role ?? 0) == r.AnchorRole switch
                { AnchorRole.Tank => 1, AnchorRole.Melee => 2, AnchorRole.Ranged => 3, _ => 4 })
                return o;
        }
        return null;
    }

    private static IEnumerable<Vector3> Ring(Vector3 c, float rMin, float rMax, int n)
    {
        foreach (var rr in rMin == rMax ? new[] { rMax } : new[] { (rMin + rMax) / 2, rMax })
            for (var i = 0; i < n; i++)
            {
                var a = Math.PI * 2 * i / n;
                yield return new Vector3(c.X + rr * (float)Math.Sin(a), c.Y, c.Z + rr * (float)Math.Cos(a));
            }
    }

    private static bool HasStatus(string name) =>
        name.Length > 0 && Plugin.ObjectTable.LocalPlayer?.StatusList.Any(s => StatusNames.Of(s.StatusId) == name) == true;

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    public void Halt(string why)
    {
        var was = Moving;
        if (Moving) { try { stop.InvokeAction(); } catch (Exception) { } }
        Moving = false;
        // Walking leaves you facing the way you went: turn back to the target.
        if (was) Facing.Face(Plugin.TargetManager.Target);
        Status = why;
    }
}
