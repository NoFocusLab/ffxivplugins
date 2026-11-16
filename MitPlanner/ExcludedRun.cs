using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;

namespace MitPlanner;

/// <summary>
/// Duty runs that aren't valid dungeon data (rule, 2026-11-16), so they're left out of trash, route,
/// healing and party analysis:
///   trust    - Trust / Duty Support / Squadron (NPC party). Boss fights still count for boss mit tactics.
///   solo     - no other players in the party.
///   unsynced - your level is above the duty's level sync (Unrestricted Party / old content run overleveled).
/// NPC party members are BattleNpcs of sub-kind NpcPartyMember (9 in FFXIVClientStructs' BattleNpcSubKind).
/// </summary>
internal static class ExcludedRun
{
    private const int NpcPartyMember = 9;
    private static DateTime nextCheck = DateTime.MinValue;
    private static string? last;

    /// <summary>"trust", "solo", "unsynced", or null for a normal run. Checked at most once a second.</summary>
    public static string? Reason()
    {
        if (!Plugin.Condition[ConditionFlag.BoundByDuty]) return last = null;
        var now = DateTime.UtcNow;
        if (now < nextCheck) return last;
        nextCheck = now.AddSeconds(1);
        return last = Check();
    }

    private static string? Check()
    {
        foreach (var o in Plugin.ObjectTable)
            if (o is IBattleNpc n && (int)n.BattleNpcKind == NpcPartyMember) return "trust";
        if (Plugin.PartyList.Length <= 1) return "solo";
        var me = Plugin.ObjectTable.LocalPlayer;
        var sync = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Plugin.ClientState.TerritoryType)
            ?.ContentFinderCondition.ValueNullable?.ClassJobLevelSync ?? 0;
        if (me != null && sync > 0 && me.Level > sync) return "unsynced";
        return null;
    }
}
