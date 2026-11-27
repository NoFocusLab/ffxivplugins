using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CombatPilot.Engine;

public enum PartnerMode { JobOrder, Name }

/// <summary>Who an ally-targeted action (Dancer's Closed Position, Astrologian cards) goes on.</summary>
public sealed class PartnerRule
{
    public PartnerMode Mode { get; set; } = PartnerMode.JobOrder;
    /// <summary>For Name: the party member's name. If they're not there (dead, out of range), the job order is used.</summary>
    public string Name { get; set; } = "";
    /// <summary>Job abbreviations, best first. Jobs not listed come after, then tanks, then healers.</summary>
    public List<string> JobOrder { get; set; } = new(Partners.DefaultOrder);
}

public static unsafe class Partners
{
    /// <summary>
    /// [Estimate] A general "who benefits most" order (high personal damage and burst-aligned jobs
    /// first). Change it per action in Settings; The Balance publishes per-job partner priorities.
    /// </summary>
    public static readonly string[] DefaultOrder =
        { "SAM", "PCT", "VPR", "MNK", "NIN", "DRG", "RPR", "BLM", "RDM", "SMN", "MCH", "BRD", "DNC" };

    private static readonly HashSet<string> Tanks = new() { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };
    private static readonly HashSet<string> Healers = new() { "WHM", "SCH", "AST", "SGE", "CNJ" };

    /// <summary>The party member this action should go on now, or null if no one is reachable.</summary>
    public static IBattleChara? Pick(BoundAction a, PartnerRule rule, out string why)
    {
        why = "";
        var me = Plugin.ObjectTable.LocalPlayer;
        var am = ActionManager.Instance();
        if (me == null || am == null) { why = "not logged in"; return null; }
        var candidates = new List<(IBattleChara Who, string Job)>();
        foreach (var m in Plugin.PartyList)
        {
            if (m.GameObject is not IBattleChara bc || bc.GameObjectId == me.GameObjectId) continue;
            if (bc.CurrentHp == 0) continue;
            if (Vector3.Distance(bc.Position, me.Position) > 30) continue;
            if (ActionManager.GetActionInRangeOrLoS(a.Id, (GameObjectStruct*)me.Address, (GameObjectStruct*)bc.Address) != 0) continue;
            candidates.Add((bc, m.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? ""));
        }
        if (candidates.Count == 0) { why = "no party member in range"; return null; }

        if (rule.Mode == PartnerMode.Name && rule.Name.Length > 0)
        {
            var named = candidates.FirstOrDefault(c => string.Equals(c.Who.Name.TextValue, rule.Name, StringComparison.OrdinalIgnoreCase));
            if (named.Who != null) { why = $"{rule.Name} (by name)"; return named.Who; }
            why = $"{rule.Name} not in range, by job order: ";
        }

        int Rank(string job)
        {
            var i = rule.JobOrder.FindIndex(j => string.Equals(j, job, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (Healers.Contains(job)) return 2000;
            if (Tanks.Contains(job)) return 1000;
            return 500;
        }
        var best = candidates.OrderBy(c => Rank(c.Job)).First();
        why += $"{best.Who.Name.TextValue} ({best.Job})";
        return best.Who;
    }
}
