using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;
using Lumina.Excel.Sheets;

namespace CombatPilot.Engine;


/// <summary>
/// Positionals: reading where you are against the target's facing, whether the target has
/// positionals at all, and (auto-move on) walking you to the rear/flank in time for the GCD that
/// needs it, with vnavmesh. Destinations get the same checks as dashes (floor, BossMod danger
/// zones on the path and spot). If that can't be done, the planner's alternative is True North.
/// </summary>
public static class Positionals
{
    /// <summary>[Estimate] run speed, yalms per second.</summary>
    public const double RunSpeed = 6.0;
    /// <summary>How far outside the target's hitbox to stand (well inside 3 y melee range).</summary>
    private const float Stand = 1.2f;

    private static readonly PropertyInfo? OmniProp = typeof(BNpcBase).GetProperty("IsOmnidirectional");

    /// <summary>[Assumption] BNpcBase.IsOmnidirectional marks targets without positionals.</summary>
    public static bool IsOmni(IGameObject target)
    {
        if (OmniProp == null) return false;
        var row = Plugin.DataManager.GetExcelSheet<BNpcBase>().GetRowOrDefault(target.BaseId);
        return row != null && OmniProp.GetValue(row.Value) is true;
    }

    /// <summary>Which side of the target a point is on: rear within 45° of its back, flank 45-135°, else front.</summary>
    public static string SideOf(Vector3 point, IGameObject target)
    {
        var facing = new Vector3((float)Math.Sin(target.Rotation), 0, (float)Math.Cos(target.Rotation));
        var v = point - target.Position;
        v.Y = 0;
        if (v.LengthSquared() < 0.0001f) return "rear";
        var angle = Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(v), facing), -1, 1)) * 180 / Math.PI;
        return angle >= 135 ? "rear" : angle >= 45 ? "flank" : "front";
    }

    /// <summary>Degrees from the target's facing to the point (0 = straight in front, 180 = straight behind).</summary>
    public static double AngleOf(Vector3 point, IGameObject target)
    {
        var facing = new Vector3((float)Math.Sin(target.Rotation), 0, (float)Math.Cos(target.Rotation));
        var v = point - target.Position;
        v.Y = 0;
        if (v.LengthSquared() < 0.0001f) return 180;
        return Math.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(v), facing), -1, 1)) * 180 / Math.PI;
    }

    /// <summary>
    /// [Estimate] Degrees to stay inside a zone's edge. Seen live: Chaotic Spring from -108° to -127°
    /// (flank, just short of the rear's 135°) after auto-move called "close enough" by distance.
    /// </summary>
    public const double Margin = 20;

    /// <summary>The side you're on with the margin: rear beyond 155°, flank 65-115°, front under 25°, else "edge".</summary>
    public static string SafeSideOf(Vector3 point, IGameObject target)
    {
        var a = AngleOf(point, target);
        return a >= 135 + Margin ? "rear" : a >= 45 + Margin && a <= 135 - Margin ? "flank" : a <= 45 - Margin ? "front" : "edge";
    }

    public static PositionInfo Read(IGameObject me, IGameObject target, bool autoMove, DangerBridge? danger)
    {
        var p = new PositionInfo { Omni = IsOmni(target), Side = SideOf(me.Position, target), SafeSide = SafeSideOf(me.Position, target) };
        if (p.Omni) { p.Note = "target has no positionals"; return p; }
        var facing = new Vector3((float)Math.Sin(target.Rotation), 0, (float)Math.Cos(target.Rotation));
        var right = new Vector3(facing.Z, 0, -facing.X);
        var r = target.HitboxRadius + Stand;
        p.RearSpot = target.Position - facing * r;
        // The flank on your side.
        var toMe = me.Position - target.Position;
        var side = Vector3.Dot(toMe, right) >= 0 ? right : -right;
        p.FlankSpot = target.Position + side * r;
        if (!autoMove) { p.Note = "auto-move off"; return p; }
        p.RearEta = Eta(me.Position, p.RearSpot, danger, out var whyRear);
        p.FlankEta = Eta(me.Position, p.FlankSpot, danger, out var whyFlank);
        p.Note = (whyRear ?? "") + (whyFlank != null && whyFlank != whyRear ? (whyRear != null ? ", " : "") + whyFlank : "");
        return p;
    }

    private static double Eta(Vector3 from, Vector3 to, DangerBridge? danger, out string? why)
    {
        why = null;
        if (!GameState.FloorAt(to, from.Y)) { why = "no floor there"; return double.PositiveInfinity; }
        if (danger != null && danger.Zones() > 0 && !danger.DashSafe(from, to, out var known) && known)
        { why = "danger zone on the way"; return double.PositiveInfinity; }
        var d = Vector3.Distance(new Vector3(from.X, 0, from.Z), new Vector3(to.X, 0, to.Z));
        return d / RunSpeed + 0.2;
    }
}
