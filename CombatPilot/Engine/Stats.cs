using System;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace CombatPilot.Engine;

/// <summary>
/// Your character's substats (as the game shows them, so level sync is included) and what they
/// mean for the planner: crit rate and strength, direct-hit rate, and how much a guaranteed
/// critical direct hit (Inner Chaos, Primal Rend, Inner Release Fell Cleaves...) is worth against
/// an average hit. Formulas (Allagan Studies):
///   crit rate     = floor(200 * (CRIT - SUB) / DIV + 50) / 1000
///   crit strength = (1400 + floor(200 * (CRIT - SUB) / DIV)) / 1000
///   DH rate       = floor(550 * (DH - SUB) / DIV) / 1000, a direct hit is x1.25
/// </summary>
public static unsafe class Stats
{
    // BaseParam ids.
    private const int DirectHit = 22, CriticalHit = 27, SkillSpeed = 45, SpellSpeed = 46;

    public sealed record Snapshot(int Level, int Crit, int Dh, int Sks, int Sps,
        double CritRate, double CritMult, double DhRate, double CritDhFactor);

    /// <summary>
    /// Level modifiers (SUB, DIV). Level 100 is confirmed; the others are the published values for
    /// those caps ([Assumption] in-between levels use the nearest cap below).
    /// </summary>
    private static (int Sub, int Div) Mods(int level) => level switch
    {
        >= 100 => (420, 2780),
        >= 90 => (400, 1900),
        >= 80 => (380, 1300),
        >= 70 => (364, 900),
        _ => (0, 0),
    };

    public static Snapshot? Read(int level)
    {
        var ps = PlayerState.Instance();
        if (ps == null) return null;
        var (sub, div) = Mods(level);
        if (div == 0) return null;
        int crit = ps->Attributes[CriticalHit], dh = ps->Attributes[DirectHit];
        if (crit <= 0) return null;
        var critRate = Math.Floor(200.0 * (crit - sub) / div + 50) / 1000;
        var critMult = (1400 + Math.Floor(200.0 * (crit - sub) / div)) / 1000;
        var dhRate = Math.Max(0, Math.Floor(550.0 * (dh - sub) / div) / 1000);
        var average = (1 + critRate * (critMult - 1)) * (1 + dhRate * 0.25);
        var factor = critMult * 1.25 / average;
        return new Snapshot(level, crit, dh, ps->Attributes[SkillSpeed], ps->Attributes[SpellSpeed],
            critRate, critMult, dhRate, factor);
    }
}
