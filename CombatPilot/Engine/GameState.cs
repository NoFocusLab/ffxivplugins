using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatPilot.Engine;

/// <summary>Cooldown state of one recast group, as the planner sees it.</summary>
public struct CdState
{
    public int Charges;
    public int MaxCharges;
    public double RecastPerCharge;
    /// <summary>Seconds (from now) until the next charge comes back; 0 when full.</summary>
    public double NextChargeIn;
}

/// <summary>Everything the planner needs, read from the game once per decision.</summary>
public sealed class GameState
{
    public double GcdRemaining;
    public double GcdLength = 2.5;
    public double AnimationLock;
    public int Gauge;
    public string? Combo;
    public double ComboRemaining;
    /// <summary>Your statuses (from anyone): name -> (seconds left, stacks). Permanent ones get 999.</summary>
    public Dictionary<string, (double Left, int Stacks)> Statuses = new();
    /// <summary>Damage multiplier from party buffs on you and debuffs on the target, with seconds left.</summary>
    public List<(double Mult, double Left)> PartyBuffs = new();
    public Dictionary<int, CdState> Cooldowns = new();
    public bool HasTarget;
    public ulong TargetId;
    /// <summary>Actions the game says can reach the target right now (range and line of sight).</summary>
    public HashSet<uint> InRange = new();
    public int EnemiesNearMe;
    public int EnemiesNearTarget;
    public bool InCombat;

    /// <summary>
    /// [Estimate] Party damage buffs by status name, as a flat multiplier. Crit/direct-hit rate buffs
    /// are converted to their average value. Used only while active (no prediction yet).
    /// </summary>
    public static readonly Dictionary<string, double> PartyBuffTable = new()
    {
        ["Battle Litany"] = 1.04, ["Chain Stratagem"] = 1.04, ["Battle Voice"] = 1.05, ["Devilment"] = 1.09,
        ["Divination"] = 1.06, ["Brotherhood"] = 1.05, ["Arcane Circle"] = 1.03, ["Embolden"] = 1.05,
        ["Technical Finish"] = 1.05, ["Searing Light"] = 1.05, ["Starry Muse"] = 1.05, ["Radiant Finale"] = 1.04,
        ["Dokumori"] = 1.05, ["Mug"] = 1.05,
    };

    public static unsafe GameState Read(JobBinding job)
    {
        var s = new GameState();
        var me = Plugin.ObjectTable.LocalPlayer;
        var am = ActionManager.Instance();
        if (me == null || am == null) return s;

        s.InCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        s.AnimationLock = am->AnimationLock;
        var gcd = am->GetRecastGroupDetail(57);
        if (gcd != null)
        {
            s.GcdRemaining = gcd->IsActive ? Math.Max(0, gcd->Total - gcd->Elapsed) : 0;
            if (gcd->IsActive && gcd->Total > 1) s.GcdLength = gcd->Total;
        }
        // Read the GCD length from a combo starter when the GCD isn't rolling (skill speed, haste).
        var starter = job.Actions.FirstOrDefault(a => a.IsGcd && a.Def.ComboFrom == null && a.Def.Aoe == null);
        if (starter != null && !(gcd != null && gcd->IsActive))
        {
            var t = ActionManager.GetAdjustedRecastTime(ActionType.Action, starter.Id) / 1000.0;
            if (t > 1) s.GcdLength = t;
        }

        s.Gauge = job.ReadGauge();
        var comboId = am->Combo.Action;
        s.ComboRemaining = am->Combo.Timer;
        if (comboId != 0 && s.ComboRemaining > 0)
            s.Combo = job.Actions.FirstOrDefault(a => a.Id == comboId || a.PressId == comboId)?.Name;

        foreach (var st in me.StatusList)
        {
            if (st.StatusId == 0) continue;
            var name = StatusNames.Of(st.StatusId);
            if (name.Length == 0) continue;
            var left = st.RemainingTime > 0 ? st.RemainingTime : 999;
            s.Statuses[name] = (left, Math.Max(1, (int)st.Param));
            if (PartyBuffTable.TryGetValue(name, out var m)) s.PartyBuffs.Add((m, left));
        }

        // Cooldowns per recast group (charges included).
        foreach (var a in job.Actions)
        {
            if (a.CooldownGroup < 0 || s.Cooldowns.ContainsKey(a.CooldownGroup)) continue;
            var id = am->GetAdjustedActionId(a.Id);
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

        // Target, range and enemy counts.
        var target = Plugin.TargetManager.Target as IBattleChara;
        if (target != null && target.CurrentHp > 0 && target.IsTargetable && (target.StatusFlags & StatusFlags.Hostile) != 0)
        {
            s.HasTarget = true;
            s.TargetId = target.GameObjectId;
            foreach (var st in target.StatusList)
            {
                if (st.StatusId == 0) continue;
                if (PartyBuffTable.TryGetValue(StatusNames.Of(st.StatusId), out var m)) s.PartyBuffs.Add((m, st.RemainingTime));
            }
            var src = (GameObjectStruct*)me.Address;
            var dst = (GameObjectStruct*)target.Address;
            foreach (var a in job.Actions)
                if (ActionManager.GetActionInRangeOrLoS(a.Id, src, dst) == 0) s.InRange.Add(a.Id);
        }

        var enemies = Plugin.ObjectTable.OfType<IBattleNpc>()
            .Where(n => (n.StatusFlags & StatusFlags.Hostile) != 0 && n.CurrentHp > 0 && n.IsTargetable &&
                        ((n.StatusFlags & StatusFlags.InCombat) != 0 || n.GameObjectId == s.TargetId))
            .ToList();
        // [Assumption] 5y AoEs reach enemies whose hitbox edge is within 5y.
        s.EnemiesNearMe = enemies.Count(n => Vector3.Distance(n.Position, me.Position) - n.HitboxRadius <= 5);
        if (target != null)
            s.EnemiesNearTarget = enemies.Count(n => Vector3.Distance(n.Position, target.Position) - n.HitboxRadius <= 5);
        return s;
    }
}
