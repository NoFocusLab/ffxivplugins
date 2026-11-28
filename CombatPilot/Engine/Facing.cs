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

    private static DateTime lastKeep = DateTime.MinValue, turnedAt = DateTime.MinValue;
    public static int DirectTurns { get; private set; }

    /// <summary>
    /// Between actions (standing still, in combat): keep facing the target so auto-attacks don't stop
    /// and the next action has it in front. The game's auto-face first; if you're still turned away
    /// half a second later, set the rotation directly (same convention BossMod uses: atan2(dx, dz)).
    /// </summary>
    public static void Keep(IGameObject? target)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || target == null || me.IsCasting || target.GameObjectId == me.GameObjectId) return;
        var now = DateTime.UtcNow;
        if ((now - lastKeep).TotalSeconds < 0.3) return;
        lastKeep = now;
        // Only when clearly turned away (auto-attacks need the target in front); fewer turns = calmer camera.
        if (OffBy(me, target) < 90) { turnedAt = DateTime.MinValue; return; }
        if (turnedAt == DateTime.MinValue || (now - turnedAt).TotalSeconds > 2)
        {
            _ = Face(target);
            turnedAt = now;
            return;
        }
        if ((now - turnedAt).TotalSeconds >= 0.5)
        {
            var d = target.Position - me.Position;
            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)me.Address)->SetRotation(MathF.Atan2(d.X, d.Z));
            DirectTurns++;
            turnedAt = now;
        }
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
