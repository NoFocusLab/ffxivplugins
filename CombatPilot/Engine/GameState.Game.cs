using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatPilot.Engine;

/// <summary>
/// Time to live per enemy, from how fast its HP has been dropping over the last few seconds.
/// No drop seen (or too little data) = 999 s (treated as "lives through the look-ahead").
/// </summary>
public static class HpHistory
{
    private static readonly Dictionary<ulong, Queue<(DateTime At, uint Hp)>> samples = new();
    private const double Window = 6;

    public static double Ttk(ulong id, uint hp, uint maxHp)
    {
        var now = DateTime.UtcNow;
        if (!samples.TryGetValue(id, out var q)) samples[id] = q = new();
        if (q.Count == 0 || (now - q.Last().At).TotalSeconds >= 0.25) q.Enqueue((now, hp));
        while (q.Count > 0 && (now - q.Peek().At).TotalSeconds > Window) q.Dequeue();
        if (samples.Count > 200) samples.Clear();
        var first = q.Peek();
        var dt = (now - first.At).TotalSeconds;
        if (dt < 1.5 || first.Hp <= hp) return 999;
        var rate = (first.Hp - hp) / dt;
        return Math.Min(999, hp / rate);
    }
}

public sealed partial class GameState
{
    /// <summary>[Assumption] statuses that make an enemy take no damage, by name.</summary>
    private static bool Invulnerable(IBattleChara n)
    {
        foreach (var st in n.StatusList)
        {
            if (st.StatusId == 0) continue;
            var name = StatusNames.Of(st.StatusId);
            if (name.Contains("Invincib", StringComparison.OrdinalIgnoreCase) || name.Contains("Invulnerab", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Which enemies count, and for each AoE action which of them it would hit from where you
    /// stand now (shape and size from game data, hitboxes included), with their time to live.
    /// Enemies that can't be damaged are left out; enemies not fighting yet are noted as "would pull".
    /// </summary>
    private static unsafe void ReadEnemies(GameState s, JobBinding job, IBattleChara me, IBattleChara? target, uint starterId)
    {
        var others = new List<(EnemyInfo Info, bool Counts)>();
        foreach (var n in Plugin.ObjectTable.OfType<IBattleNpc>())
        {
            if (n.CurrentHp == 0 || !n.IsTargetable || Vector3.Distance(n.Position, me.Position) > 30) continue;
            var isTarget = s.HasTarget && n.GameObjectId == s.TargetId;
            if (!isTarget && !ActionManager.CanUseActionOnTarget(starterId, (GameObjectStruct*)n.Address)) continue;
            if (Invulnerable(n)) continue;
            var inCombat = (n.StatusFlags & StatusFlags.InCombat) != 0;
            var info = new EnemyInfo(n.GameObjectId, n.Name.TextValue, n.Position, n.HitboxRadius, inCombat, isTarget,
                HpHistory.Ttk(n.GameObjectId, n.CurrentHp, n.MaxHp));
            if (isTarget) s.TargetTtk = info.Ttk;
            others.Add((info, inCombat || isTarget));
        }
        s.Enemies = others.Where(o => o.Counts).Select(o => o.Info).ToList();

        var from = me.Position;
        var toward = target != null && s.HasTarget ? target.Position - me.Position : new Vector3((float)Math.Sin(me.Rotation), 0, (float)Math.Cos(me.Rotation));
        toward.Y = 0;
        if (toward.LengthSquared() < 0.0001f) toward = new Vector3(0, 0, 1);
        toward = Vector3.Normalize(toward);
        foreach (var a in job.Actions)
        {
            if (!a.IsAoe) continue;
            var hit = new List<double>();
            foreach (var (e, counts) in others)
            {
                if (!Inside(a, e, from, target?.Position, toward, s.HasTarget)) continue;
                if (!counts) { s.WouldPull.Add(a.Id); continue; }
                hit.Add(e.Ttk);
                if (!e.IsTarget) s.HitsOthers.Add(a.Id);
            }
            s.HitTtks[a.Id] = hit.ToArray();
        }
    }

    private static bool Inside(BoundAction a, EnemyInfo e, Vector3 me, Vector3? target, Vector3 dir, bool hasTarget)
    {
        var r = a.Radius + e.Hitbox;
        switch (a.Shape)
        {
            case Shape.CircleSelf:
                return Flat(e.Position - me).Length() <= r;
            case Shape.CircleTarget:
                return hasTarget && target != null && Flat(e.Position - target.Value).Length() <= r;
            case Shape.Cone:
            {
                var v = Flat(e.Position - me);
                var d = v.Length();
                if (d <= e.Hitbox) return true;
                if (d > r) return false;
                var cos = Vector3.Dot(v / d, dir);
                return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI <= a.Angle / 2 + Math.Atan2(e.Hitbox, d) * 180 / Math.PI;
            }
            case Shape.Line:
            {
                var v = Flat(e.Position - me);
                var along = Vector3.Dot(v, dir);
                var side = (v - dir * along).Length();
                return along >= -e.Hitbox && along <= a.Radius + e.Hitbox && side <= a.Width / 2 + e.Hitbox;
            }
            default:
                return false;
        }
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>
    /// Is there floor at this spot? A ray from 3 y above straight down (game collision). No hit, or
    /// floor more than 4 y below where you stand now, counts as no floor ([Assumption] thresholds:
    /// small steps and slopes are fine, ledges and the edge of the map aren't).
    /// </summary>
    public static bool FloorAt(Vector3 spot, float myY)
    {
        try
        {
            var origin = spot + new Vector3(0, 3, 0);
            if (!BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0, -1, 0), out var hit, 12f)) return false;
            return hit.Point.Y >= myY - 4f;
        }
        catch (Exception) { return true; }
    }

    private static double? DurationOf(JobBinding job, string status)
    {
        foreach (var a in job.Def.Actions)
            foreach (var gr in a.Grants)
                if (gr.Status == status && gr.Duration > 0 && gr.Duration < 900) return gr.Duration;
        return null;
    }

    private static Vector3 lastPos;
    private static DateTime lastPosAt = DateTime.MinValue;

    public static unsafe GameState Read(JobBinding job, PartyBuffTracker? party = null, MitBridge? mit = null, DangerBridge? danger = null, bool autoMove = false)
    {
        var s = new GameState();
        var me = Plugin.ObjectTable.LocalPlayer;
        var am = ActionManager.Instance();
        if (me == null || am == null) return s;

        s.InCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        var nowT = DateTime.UtcNow;
        if ((nowT - lastPosAt).TotalSeconds < 0.5) s.PlayerMoving = Vector3.Distance(me.Position, lastPos) > 0.05f;
        lastPos = me.Position;
        lastPosAt = nowT;
        s.AnimationLock = am->AnimationLock;
        var gcd = am->GetRecastGroupDetail(57);
        if (gcd != null)
        {
            s.GcdRemaining = gcd->IsActive ? Math.Max(0, gcd->Total - gcd->Elapsed) : 0;
            if (gcd->IsActive && gcd->Total > 1) s.GcdLength = gcd->Total;
        }
        // Read the GCD length from a combo starter when the GCD isn't rolling (skill speed, haste).
        var starter = job.Actions.FirstOrDefault(a => a.IsGcd && a.Def.ComboFrom == null && !a.IsAoe);
        if (starter != null && !(gcd != null && gcd->IsActive))
        {
            var t = ActionManager.GetAdjustedRecastTime(ActionType.Action, starter.Id) / 1000.0;
            if (t > 1) s.GcdLength = t;
        }

        s.Gauge = job.ReadGauge();
        s.Mp = (int)me.CurrentMp;
        s.MaxMp = Math.Max(1, (int)me.MaxMp);
        var comboId = am->Combo.Action;
        s.ComboRemaining = am->Combo.Timer;
        s.ComboRaw = s.ComboRemaining > 0 ? comboId : 0;
        if (comboId != 0 && s.ComboRemaining > 0)
            s.Combo = job.Actions.FirstOrDefault(a => a.Id == comboId || a.PressId == comboId)?.Name;

        foreach (var st in me.StatusList)
        {
            if (st.StatusId == 0) continue;
            var name = StatusNames.Of(st.StatusId);
            if (name.Length == 0) continue;
            // 0 = no timer (permanent), or one just applied whose timer isn't filled in yet: use the
            // job file's duration when it grants this status (seen: Power Surge "999 s" right after Spiral Blow).
            var left = st.RemainingTime > 0 ? st.RemainingTime : DurationOf(job, name) ?? 999;
            s.Statuses[name] = (left, Math.Max(1, (int)st.Param));
            // Your own raid buff (Battle Litany on a Dragoon) is valued by the job file, not counted twice.
            if (PartyBuffTracker.ByName.TryGetValue(name, out var rb) && !job.Def.Statuses.ContainsKey(name)) s.PartyBuffs.Add((rb.Mult, 0, left));
        }
        job.AddGaugeStatuses(s.Statuses);
        // Button chains (Confiteor -> Blade of Faith -> ...): the button shows the next step.
        foreach (var a in job.Actions)
            if (a.Def.ShownAs != null && !a.IsItem && a.PressId != a.Id && am->GetAdjustedActionId(a.PressId) == a.Id)
                s.Statuses[a.Def.ShownAs] = (30, 1);

        // Cooldowns per recast group (charges included).
        foreach (var a in job.Actions)
        {
            if (a.IsItem)
            {
                var rem = am->IsRecastTimerActive(ActionType.Item, a.Id)
                    ? Math.Max(0, am->GetRecastTime(ActionType.Item, a.Id) - am->GetRecastTimeElapsed(ActionType.Item, a.Id)) : 0;
                s.Cooldowns[a.CooldownGroup] = new CdState { MaxCharges = 1, RecastPerCharge = 270, Charges = rem > 0 ? 0 : 1, NextChargeIn = rem };
                continue;
            }
            if (a.CooldownGroup < 0 || s.Cooldowns.ContainsKey(a.CooldownGroup)) continue;
            // The action's own id, not the button's current form: while Wrathful, Inner Release's
            // button is Primal Wrath (1 s recast), and reading that made Inner Release look ready
            // every GCD in the plan.
            var id = a.Id;
            var total = am->GetRecastTime(ActionType.Action, id);
            var elapsed = am->GetRecastTimeElapsed(ActionType.Action, id);
            var max = Math.Max(1, (int)ActionManager.GetMaxCharges(id, 0));
            var recastAll = ActionManager.GetAdjustedRecastTime(ActionType.Action, id) / 1000.0;
            var perCharge = max > 1 ? recastAll : recastAll;
            var cd = new CdState { MaxCharges = max, RecastPerCharge = perCharge > 0 ? perCharge : 1 };
            if (max > 1)
            {
                cd.Charges = (int)am->GetCurrentCharges(id);
                var remainingAll = total > 0 ? total - elapsed : 0;
                cd.NextChargeIn = cd.Charges < max && remainingAll > 0 ? Math.Max(0, remainingAll - perCharge * (max - cd.Charges - 1)) : 0;
            }
            else
            {
                var remaining = am->IsRecastTimerActive(ActionType.Action, id) ? Math.Max(0, total - elapsed) : 0;
                cd.Charges = remaining > 0 ? 0 : 1;
                cd.NextChargeIn = remaining;
            }
            s.Cooldowns[a.CooldownGroup] = cd;
        }

        // Target, range and enemy counts. "Attackable" is the game's own check (works for striking
        // dummies, which don't carry the hostile flag).
        var target = Plugin.TargetManager.Target as IBattleChara;
        var starterId = starter?.Id ?? job.Actions.FirstOrDefault(a => a.IsGcd)?.Id ?? 0;
        if (target == null) s.TargetNote = "no target";
        else if (target.CurrentHp == 0 || !target.IsTargetable) s.TargetNote = "target is dead or untargetable";
        else if (!ActionManager.CanUseActionOnTarget(starterId, (GameObjectStruct*)target.Address)) s.TargetNote = "target can't be attacked";
        else
        {
            s.HasTarget = true;
            s.TargetId = target.GameObjectId;
            s.TargetEngaged = (target.StatusFlags & StatusFlags.InCombat) != 0;
            foreach (var st in target.StatusList)
            {
                if (st.StatusId == 0) continue;
                var tname = StatusNames.Of(st.StatusId);
                var mine = st.SourceId == me.EntityId;
                // Your own debuffs (Dokumori, Kunai's Bane) are valued by the job file, not as party buffs.
                if (PartyBuffTracker.ByName.TryGetValue(tname, out var rb) && !(mine && job.Def.Statuses.ContainsKey(tname)))
                    s.PartyBuffs.Add((rb.Mult, 0, st.RemainingTime));
                // Your damage over time and debuffs on the target (Chaotic Spring, Kunai's Bane).
                if (mine && (job.Def.Statuses.ContainsKey(tname) || job.Actions.Any(a => a.Def.Dot?.Status == tname)))
                    s.Statuses[tname] = (st.RemainingTime, 1);
            }
            var src = (GameObjectStruct*)me.Address;
            var dst = (GameObjectStruct*)target.Address;
            var actions = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            // Ring to ring, the way the game measures range: centre distance minus both hitbox radii.
            var reach = Vector2.Distance(new(me.Position.X, me.Position.Z), new(target.Position.X, target.Position.Z))
                        - target.HitboxRadius - me.HitboxRadius;
            foreach (var a in job.Actions)
            {
                if (a.IsItem) continue;
                var rc = ActionManager.GetActionInRangeOrLoS(a.Id, src, dst);
                // While you face away (auto-move walking to a positional) the game refuses melee actions
                // with a "line of sight" (562) or "not in range" (565/566) code, even right next to the
                // target. The Pilot turns to the target before pressing, so what counts is our own measure:
                // ring to ring within the action's own range (melee 3 y). Seen live: melee GCDs dropped
                // right after a positional move, Death Blossom 7-10x per run.
                var rawRange = actions.GetRowOrDefault(a.Id) is { } row ? (int)row.Range : 0;
                var range = rawRange > 0 ? rawRange : 3;
                if (rc == 0 || (rc is 562 or 565 or 566 && reach <= range)) s.InRange.Add(a.Id);
                else if (!a.IsAoe && a.IsGcd && a.Def.Potency > 0 && s.RangeNote.Length == 0)
                    s.RangeNote = $"{a.Name} out: code {rc}, {reach:0.0} y from ring";
                // Pressed on yourself but hits through later buttons (Ten Chi Jin: Fuma, Raiton, Suiton):
                // the game's range check on the self-cast button always says no, so the planner never
                // started it (seen live: Ten Chi Jin "next" for 2.5 min, used once in 6 min). Ninjutsu reach 25 y.
                else if (a.Def.Sequence != null && a.Def.Potency > 0 && !a.IsAoe &&
                         actions.GetRowOrDefault(a.Id)?.CanTargetHostile != true && reach <= 25)
                    s.InRange.Add(a.Id);
            }
        }

        ReadEnemies(s, job, me, target, starterId);

        // Movement actions: where would it put me? Unsafe if BossMod says the path/spot is in a danger
        // zone, or there's no floor there (off a ledge, off the map).
        var zones = danger?.Zones() ?? -1;
        s.DangerNote = zones < 0 ? "BossMod not loaded: danger zones unchecked (floor still checked)" : $"BossMod: {zones} danger zone(s); floor checked";
        var facing = new Vector3((float)Math.Sin(me.Rotation), 0, (float)Math.Cos(me.Rotation));
        foreach (var a in job.Actions)
        {
            Vector3 land;
            if (a.Def.MovesToTarget)
            {
                if (!s.HasTarget || target == null) continue;
                // [Assumption] you land at the edge of the target's hitbox on the line from you to it.
                var dir = target.Position - me.Position;
                var dist = dir.Length();
                land = dist > target.HitboxRadius && dist > 0.01f ? target.Position - dir / dist * target.HitboxRadius : me.Position;
            }
            else if (a.Def.Moves is "back" or "forward" && a.Def.MoveDistance > 0)
                land = me.Position + facing * (float)(a.Def.Moves == "back" ? -a.Def.MoveDistance : a.Def.MoveDistance);
            else continue;

            if (!FloorAt(land, me.Position.Y)) { s.UnsafeDash.Add(a.Id); s.UnsafeWhy[a.Id] = "no floor"; continue; }
            if (danger != null && zones > 0 && !danger.DashSafe(me.Position, land, out var known) && known)
            { s.UnsafeDash.Add(a.Id); s.UnsafeWhy[a.Id] = "danger zone"; }
        }

        if (s.HasTarget && target != null && job.Actions.Any(a => a.Def.Positional != null))
            s.Position = Positionals.Read(me, target, autoMove, danger);

        if (mit != null) s.Downtime = mit.Downtime();
        if (party != null)
            foreach (var (mult, from, until, _) in party.Upcoming(60, s.Downtime, job.Def.Statuses.Keys)) s.PartyBuffs.Add((mult, from, until));
        return s;
    }
}
