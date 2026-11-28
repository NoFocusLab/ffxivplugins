using System;
using System.Linq;
using System.Numerics;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatPilot.Engine;

/// <summary>
/// "Move to ally" dashes you queue (Monk's Thunderclap, Sage's Icarus, any dash the game lets you aim
/// at a party member): there's no way to pick the ally from the grid, so this picks one. Rules: not
/// you, not a tank (unless allowed as a fallback), alive, in the action's range and line of sight,
/// standing somewhere BossMod calls safe, with a safe path there, and at least 3 y away (a dash to
/// someone beside you does nothing). Nearest first: the shortest safe move.
/// [Assumption] "nearest safe non-tank" is the default the user asked for; change here if a fight
/// wants the farthest (e.g. to cross the arena).
/// </summary>
public static unsafe class AllyDash
{
    private static readonly System.Collections.Generic.HashSet<string> Tanks = new() { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };

    public static IBattleChara? Pick(BoundAction a, BossModAi bmr, DangerBridge danger, bool tankFallback, out string why)
    {
        why = "";
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) { why = "not logged in"; return null; }
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(a.Id);
        var range = row?.Range > 0 ? row.Value.Range : 20;
        var options = new System.Collections.Generic.List<(IBattleChara Who, bool Tank, float Dist)>();
        foreach (var m in Plugin.PartyList)
        {
            if (m.GameObject is not IBattleChara bc || bc.GameObjectId == me.GameObjectId || bc.CurrentHp == 0) continue;
            var dist = Vector3.Distance(bc.Position, me.Position);
            if (dist < 3 || dist > range + bc.HitboxRadius) continue;
            if (ActionManager.GetActionInRangeOrLoS(a.Id, (GameObjectStruct*)me.Address, (GameObjectStruct*)bc.Address) != 0) continue;
            if (!bmr.Safe(bc.Position)) continue;                                                     // they're standing in something
            if (danger.Zones() > 0 && !danger.DashSafe(me.Position, bc.Position, out var known) && known) continue; // the way there isn't safe
            var job = m.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "";
            options.Add((bc, Tanks.Contains(job), dist));
        }
        var pick = options.Where(o => !o.Tank).OrderBy(o => o.Dist).FirstOrDefault();
        if (pick.Who == null && tankFallback) pick = options.OrderBy(o => o.Dist).FirstOrDefault();
        if (pick.Who == null) { why = options.Count == 0 ? "no party member safe and in range" : "only a tank is safe and in range"; return null; }
        why = $"{pick.Who.Name.TextValue} ({pick.Dist:0}y{(pick.Tank ? ", tank: fallback" : "")})";
        return pick.Who;
    }
}
