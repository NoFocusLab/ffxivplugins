using System.Collections.Generic;
using System.Numerics;

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

/// <summary>An enemy the AoE math considers.</summary>
public sealed record EnemyInfo(ulong Id, string Name, Vector3 Position, float Hitbox, bool InCombat, bool IsTarget, double Ttk);

/// <summary>Everything the planner needs, read from the game once per decision.</summary>
public sealed partial class GameState
{
    public double GcdRemaining;
    public double GcdLength = 2.5;
    public double AnimationLock;
    public int Gauge;
    public int Mp;
    public int MaxMp = 10000;
    public string? Combo;
    public double ComboRemaining;
    /// <summary>The game's combo action id (even when the job file doesn't know it), for the log.</summary>
    public uint ComboRaw;
    /// <summary>Your statuses (from anyone): name -> (seconds left, stacks). Permanent ones get 999.</summary>
    public Dictionary<string, (double Left, int Stacks)> Statuses = new();
    /// <summary>Party buffs on you / debuffs on the target now, and predicted ones: multiplier, from, until (s from now).</summary>
    public List<(double Mult, double From, double Until)> PartyBuffs = new();
    public Dictionary<int, CdState> Cooldowns = new();
    public bool HasTarget;
    /// <summary>You moved since the last read (a hard cast would be interrupted).</summary>
    public bool PlayerMoving;
    public ulong TargetId;
    /// <summary>Actions the game says can reach the target right now (range and line of sight).</summary>
    public HashSet<uint> InRange = new();
    /// <summary>Enemies that count (in combat, or your target), with how long they're expected to live.</summary>
    public List<EnemyInfo> Enemies = new();
    /// <summary>Your target's expected time to live (s).</summary>
    public double TargetTtk = 999;
    /// <summary>Per AoE action: expected time to live of each enemy it would hit now.</summary>
    public Dictionary<uint, double[]> HitTtks = new();
    /// <summary>Per AoE action: it would also hit an enemy that isn't fighting yet (pulls it).</summary>
    public HashSet<uint> WouldPull = new();
    /// <summary>Dash/jump actions that would land you (or take you through) a danger zone right now.</summary>
    public HashSet<uint> UnsafeDash = new();
    public Dictionary<uint, string> UnsafeWhy = new();
    /// <summary>Positionals: where you are vs. the target, and whether you could get to its rear/flank.</summary>
    public PositionInfo? Position;
    /// <summary>Danger info: "BossMod: N zones", or why there's none.</summary>
    public string DangerNote = "";
    /// <summary>Per AoE action: it would hit something other than your target.</summary>
    public HashSet<uint> HitsOthers = new();
    public bool InCombat;
    /// <summary>Boss downtime ahead (from Mit Planner's timeline): seconds from now.</summary>
    public List<(double From, double Until)> Downtime = new();
    /// <summary>Your target is fighting someone already (its in-combat flag).</summary>
    public bool TargetEngaged;
    /// <summary>Why there's no usable target (for the status line).</summary>
    public string TargetNote = "";
}
