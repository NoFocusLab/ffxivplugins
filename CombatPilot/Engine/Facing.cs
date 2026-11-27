using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatPilot.Engine;

/// <summary>
/// Turning to face the target. Auto-move (positionals, range rules) leaves you facing the way you
/// walked; an action that needs the target in front then fails. Before any action on the target we
/// turn to it with the game's own auto-face (the same call its "face target when using an action"
/// option makes), and once more when auto-move stops.
/// </summary>
public static unsafe class Facing
{
    /// <summary>Degrees off the target before we bother turning (the game allows a wide front arc).</summary>
    private const float Slack = 30f;

    /// <summary>Angle between where you face and the target, 0-180.</summary>
    public static float OffBy(IGameObject me, IGameObject target)
    {
        var d = target.Position - me.Position;
        if (d.X * d.X + d.Z * d.Z < 0.01f) return 0;
        // Game rotation: 0 faces +Z, increasing counter-clockwise seen from above.
        var want = MathF.Atan2(d.X, d.Z);
        var diff = MathF.Abs(want - me.Rotation) % (2 * MathF.PI);
        if (diff > MathF.PI) diff = 2 * MathF.PI - diff;
        return diff * 180f / MathF.PI;
    }

    /// <summary>Turn to the target if not already facing it. True if a turn was made.</summary>
    public static bool Face(IGameObject? target)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var am = ActionManager.Instance();
        if (me == null || target == null || am == null || target.GameObjectId == me.GameObjectId) return false;
        if (OffBy(me, target) <= Slack) return false;
        var pos = target.Position;
        am->AutoFaceTargetPosition(&pos);
        return true;
    }
}
