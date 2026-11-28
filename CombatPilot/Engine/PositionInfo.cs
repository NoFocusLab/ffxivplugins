using System.Numerics;

namespace CombatPilot.Engine;

/// <summary>Where you stand relative to the target, and whether you could get to its rear / flank in time, safely.</summary>
public sealed class PositionInfo
{
    /// <summary>The target has no positionals (omnidirectional: many trial bosses): every positional lands.</summary>
    public bool Omni;
    /// <summary>"rear", "flank" or "front": where you are now.</summary>
    public string Side = "front";
    /// <summary>The side you're on with a safety margin (20° inside the zone); "edge" near a boundary.</summary>
    public string SafeSide = "front";
    /// <summary>Seconds to walk to the rear / nearest flank; +inf when auto-move is off or the spot isn't safe.</summary>
    public double RearEta = double.PositiveInfinity, FlankEta = double.PositiveInfinity;
    public Vector3 RearSpot, FlankSpot;
    public string Note = "";

    public bool Satisfied(string positional, double t) =>
        Omni || SafeSide == positional || (positional == "rear" ? RearEta : FlankEta) + 0.4 <= t; // 0.4 s spare
}
