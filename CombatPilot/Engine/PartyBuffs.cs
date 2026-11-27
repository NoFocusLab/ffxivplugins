using System;
using System.Collections.Generic;
using System.Linq;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Types;

namespace CombatPilot.Engine;

/// <summary>A party raid buff: who brings it, how long it lasts, how often, and what it's worth.</summary>
public sealed record RaidBuff(string Name, string Job, double Duration, double Cooldown, double Mult, bool OnTarget);

/// <summary>
/// Predicts when the party's 2-minute buffs will be up, so the planner can line bursts up with them.
/// Each buff's last start is seen on the party (or on the target, for debuffs like Chain Stratagem);
/// the next one is expected one cooldown later. A buff not seen yet this fight is expected in the
/// opener; if it still hasn't shown up a while into the fight, it's left out until it does.
/// </summary>
public sealed class PartyBuffTracker
{
    /// <summary>
    /// [Estimate] Durations/cooldowns are Dawntrail values; multipliers are flat averages (crit and
    /// direct-hit rate buffs converted to their average damage gain).
    /// </summary>
    public static readonly RaidBuff[] Table =
    {
        new("Battle Litany", "DRG", 20, 120, 1.04, false),
        new("Brotherhood", "MNK", 20, 120, 1.05, false),
        new("Arcane Circle", "RPR", 20, 120, 1.03, false),
        new("Embolden", "RDM", 20, 120, 1.05, false),
        new("Technical Finish", "DNC", 20, 120, 1.05, false),
        new("Searing Light", "SMN", 20, 120, 1.05, false),
        new("Starry Muse", "PCT", 20, 120, 1.05, false),
        new("Radiant Finale", "BRD", 20, 110, 1.04, false),
        new("Battle Voice", "BRD", 20, 120, 1.05, false),
        new("Divination", "AST", 20, 120, 1.06, false),
        new("Chain Stratagem", "SCH", 20, 120, 1.04, true),
        new("Dokumori", "NIN", 20, 120, 1.05, true),
    };

    public static readonly Dictionary<string, RaidBuff> ByName = Table.ToDictionary(b => b.Name);

    /// <summary>[Estimate] seconds into a fight the opener's buffs usually land.</summary>
    public const double OpenerDelay = 7;
    /// <summary>A buff not seen this long into the fight is treated as not coming.</summary>
    public const double GiveUpAfter = 30;

    private readonly Dictionary<string, DateTime> lastStart = new();
    private DateTime fightStart = DateTime.MinValue;

    /// <summary>Party jobs (abbreviations) bringing buffs, refreshed every update.</summary>
    public List<string> PartyJobs { get; } = new();

    public void StartFight()
    {
        lastStart.Clear();
        fightStart = DateTime.UtcNow;
    }

    public void EndFight() => fightStart = DateTime.MinValue;

    /// <summary>Watch the party and the target for buffs starting.</summary>
    public void Update()
    {
        PartyJobs.Clear();
        var now = DateTime.UtcNow;
        var holders = new List<IBattleChara>();
        foreach (var m in Plugin.PartyList)
        {
            var abbr = m.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "";
            if (abbr.Length > 0) PartyJobs.Add(abbr);
            if (m.GameObject is IBattleChara bc) holders.Add(bc);
        }
        if (Plugin.ObjectTable.LocalPlayer is { } me && holders.All(h => h.GameObjectId != me.GameObjectId)) holders.Add(me);
        if (Plugin.TargetManager.Target is IBattleChara t) holders.Add(t);

        foreach (var h in holders)
            foreach (var st in h.StatusList)
            {
                if (st.StatusId == 0 || !ByName.TryGetValue(StatusNames.Of(st.StatusId), out var buff)) continue;
                var started = now.AddSeconds(-(buff.Duration - Math.Min(buff.Duration, st.RemainingTime)));
                if (!lastStart.TryGetValue(buff.Name, out var prev) || (started - prev).TotalSeconds > buff.Duration)
                    lastStart[buff.Name] = started;
            }
    }

    /// <summary>
    /// Buff windows expected to start in the future (seconds from now), within <paramref name="ahead"/>.
    /// Buffs up right now aren't included: the game state already has them.
    /// </summary>
    public List<(double Mult, double From, double Until, string Name)> Upcoming(double ahead = 60)
    {
        var list = new List<(double, double, double, string)>();
        if (fightStart == DateTime.MinValue) return list;
        var now = DateTime.UtcNow;
        var fightTime = (now - fightStart).TotalSeconds;
        foreach (var buff in Table)
        {
            if (!PartyJobs.Contains(buff.Job)) continue;
            double next;
            if (lastStart.TryGetValue(buff.Name, out var last))
            {
                next = (last - now).TotalSeconds + buff.Cooldown;
                while (next < 0) next += buff.Cooldown; // missed one: assume it's on its normal cycle
            }
            else if (fightTime < GiveUpAfter) next = Math.Max(0.5, OpenerDelay - fightTime);
            else continue;
            if (next > ahead) continue;
            list.Add((buff.Mult, next, next + buff.Duration, buff.Name));
        }
        return list;
    }
}
