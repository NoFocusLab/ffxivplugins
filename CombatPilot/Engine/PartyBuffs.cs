using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
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
        learner.StartPull();
    }

    public void EndFight()
    {
        fightStart = DateTime.MinValue;
        learner.EndPull();
    }

    private readonly BurstLearner learner = new();
    public BurstLearner Learner => learner;
    private uint territory;
    private float timelineNow = -1;

    /// <summary>Watch the party and the target for buffs starting.</summary>
    public void Update(MitBridge? mit = null)
    {
        territory = mit?.FightTerritory() ?? 0;
        timelineNow = mit?.TimelineTime() ?? -1;
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
                {
                    lastStart[buff.Name] = started;
                    if (territory != 0 && timelineNow >= 0)
                        learner.Record(territory, buff.Name, timelineNow - (float)(now - started).TotalSeconds);
                }
            }
    }

    /// <summary>Where each upcoming window's time came from (for the Pilot tab).</summary>
    public Dictionary<string, string> Basis { get; } = new();

    /// <summary>
    /// Buff windows expected to start in the future (seconds from now), within <paramref name="ahead"/>.
    /// Buffs up right now aren't included: the game state already has them. Order of evidence:
    /// 1. this fight's learned schedule (your party's buff times on earlier pulls, by the synced
    ///    timeline clock), 2. one cooldown after the last time it was seen, 3. the opener.
    /// A window that would start during boss downtime is moved to when the boss is back
    /// ([Assumption] parties hold 2-minute buffs through downtime).
    /// </summary>
    public List<(double Mult, double From, double Until, string Name)> Upcoming(double ahead = 60,
        List<(double From, double Until)>? downtime = null, IEnumerable<string>? own = null)
    {
        var list = new List<(double, double, double, string)>();
        Basis.Clear();
        if (fightStart == DateTime.MinValue) return list;
        var now = DateTime.UtcNow;
        var fightTime = (now - fightStart).TotalSeconds;
        foreach (var buff in Table)
        {
            if (!PartyJobs.Contains(buff.Job)) continue;
            // Your own raid buff is the planner's to place, not a window to wait for.
            if (own != null && own.Contains(buff.Name)) continue;
            double next;
            string basis;
            if (territory != 0 && timelineNow >= 0 && learner.Next(territory, buff.Name, timelineNow) is { } learned)
            {
                next = learned - timelineNow;
                basis = "learned";
            }
            else if (lastStart.TryGetValue(buff.Name, out var last))
            {
                next = (last - now).TotalSeconds + buff.Cooldown;
                while (next < 0) next += buff.Cooldown; // missed one: assume it's on its normal cycle
                basis = "cycle";
            }
            else if (fightTime < GiveUpAfter) { next = Math.Max(0.5, OpenerDelay - fightTime); basis = "opener"; }
            else continue;
            if (downtime != null)
                foreach (var (from, until) in downtime)
                    if (next >= from && next < until) { next = until + 1; basis += ", after downtime"; }
            if (next > ahead) continue;
            list.Add((buff.Mult, next, next + buff.Duration, buff.Name));
            Basis[buff.Name] = basis;
        }
        return list;
    }
}

/// <summary>
/// Learns when your party actually uses its 2-minute buffs in each fight, by Mit Planner's synced
/// timeline clock (so phase pushes and branches line up between pulls). Keeps the last 8 pulls per
/// fight in fights/bursts.json. A start counts once two or more pulls agree within 8 s (median).
/// </summary>
public sealed class BurstLearner
{
    private const int KeepPulls = 8;
    private readonly string path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "bursts.json");
    /// <summary>territory -> pulls (newest last) -> buff -> timeline times.</summary>
    private Dictionary<uint, List<Dictionary<string, List<float>>>> data = new();
    private Dictionary<string, List<float>>? current;
    private uint currentTerritory;

    public BurstLearner()
    {
        try { if (File.Exists(path)) data = JsonConvert.DeserializeObject<Dictionary<uint, List<Dictionary<string, List<float>>>>>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Combat Pilot: couldn't read bursts.json"); }
    }

    public int PullsFor(uint territory) => data.TryGetValue(territory, out var l) ? l.Count : 0;

    public void StartPull() { current = new(); currentTerritory = 0; }

    public void Record(uint territory, string buff, float timelineTime)
    {
        if (current == null) return;
        currentTerritory = territory;
        if (!current.TryGetValue(buff, out var l)) current[buff] = l = new();
        if (l.Count == 0 || timelineTime - l[^1] > 30) l.Add(timelineTime);
    }

    public void EndPull()
    {
        if (current == null || currentTerritory == 0 || current.Count == 0) { current = null; return; }
        if (!data.TryGetValue(currentTerritory, out var pulls)) data[currentTerritory] = pulls = new();
        pulls.Add(current);
        while (pulls.Count > KeepPulls) pulls.RemoveAt(0);
        current = null;
        try { File.WriteAllText(path, JsonConvert.SerializeObject(data)); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Combat Pilot: couldn't write bursts.json"); }
    }

    /// <summary>The learned next start of this buff after the timeline time <paramref name="now"/>, or null.</summary>
    public float? Next(uint territory, string buff, float now)
    {
        if (!data.TryGetValue(territory, out var pulls)) return null;
        var nexts = new List<float>();
        foreach (var p in pulls)
            if (p.TryGetValue(buff, out var times) && times.FirstOrDefault(x => x > now + 0.5f) is var nx && nx > now + 0.5f)
                nexts.Add(nx);
        if (nexts.Count < 2) return null;
        nexts.Sort();
        // The earliest cluster that at least two pulls agree on (within 8 s).
        for (var i = 0; i + 1 < nexts.Count; i++)
        {
            var cluster = nexts.Where(x => x >= nexts[i] && x <= nexts[i] + 8).ToList();
            if (cluster.Count >= 2) return cluster[cluster.Count / 2];
        }
        return null;
    }
}
