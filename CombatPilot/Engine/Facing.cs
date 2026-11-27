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

    /// <summary>
    /// Turn to the target (every time: not gated on our own angle maths, in case it's mirrored).
    /// Returns how far off you were before turning, for the log.
    /// </summary>
    public static float Face(IGameObject? target)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var am = ActionManager.Instance();
        if (me == null || target == null || am == null || target.GameObjectId == me.GameObjectId) return 0;
        var off = OffBy(me, target);
        var pos = target.Position;
        am->AutoFaceTargetPosition(&pos);
        return off;
    }

    /// <summary>The game's "Face target when using an action" (Character Config > Control Settings > Target). Null: unknown.</summary>
    public static bool? GameAutoFaceOn()
    {
        try { return Plugin.GameConfig.TryGet(Dalamud.Game.Config.UiControlOption.AutoFaceTargetOnAction, out uint v) ? v != 0 : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Turn the game's option on (only from your click in the Pilot tab or bar).</summary>
    public static void TurnGameAutoFaceOn()
    {
        try { Plugin.GameConfig.Set(Dalamud.Game.Config.UiControlOption.AutoFaceTargetOnAction, 1u); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Combat Pilot: couldn't change the auto-face option"); }
    }
}
