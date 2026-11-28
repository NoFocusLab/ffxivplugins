using System;
using Dalamud.Plugin.Ipc;

namespace CooldownGrid;

/// <summary>
/// Combat Pilot (optional): fills the Offense group with the current job's damage abilities, and
/// while it's flying Live, icon clicks are queued there (woven into its plan) instead of pressed
/// straight away. Without Combat Pilot everything works as before.
/// </summary>
public static class CombatPilotBridge
{
    private static ICallGateSubscriber<uint[]>? offense;
    private static ICallGateSubscriber<bool>? live;
    private static ICallGateSubscriber<uint, bool>? queue;
    private static ICallGateSubscriber<uint>? queued;

    private static uint[] cachedOffense = Array.Empty<uint>();
    private static DateTime offenseAt = DateTime.MinValue;

    private static void Init()
    {
        if (offense != null) return;
        offense = Plugin.PluginInterface.GetIpcSubscriber<uint[]>("CombatPilot.Offense");
        live = Plugin.PluginInterface.GetIpcSubscriber<bool>("CombatPilot.Live");
        queue = Plugin.PluginInterface.GetIpcSubscriber<uint, bool>("CombatPilot.Queue");
        queued = Plugin.PluginInterface.GetIpcSubscriber<uint>("CombatPilot.Queued");
    }

    /// <summary>Combat Pilot's offense abilities for your job (empty if it isn't loaded or has no file for the job).</summary>
    public static uint[] Offense()
    {
        if ((DateTime.UtcNow - offenseAt).TotalSeconds < 1) return cachedOffense;
        offenseAt = DateTime.UtcNow;
        try { Init(); cachedOffense = offense!.InvokeFunc() ?? Array.Empty<uint>(); }
        catch (Exception) { cachedOffense = Array.Empty<uint>(); }
        return cachedOffense;
    }

    public static bool Live()
    {
        try { Init(); return live!.InvokeFunc(); }
        catch (Exception) { return false; }
    }

    /// <summary>Queue in Combat Pilot. False: not Live, not loaded, or not one of its job's actions.</summary>
    /// <param name="evenIfNotLive">Party-targetable actions: Combat Pilot picks the ally ("move to ally"
    /// dashes) even when it isn't flying; it uses them at once in that case.</param>
    public static bool TryQueue(uint actionId, bool evenIfNotLive = false)
    {
        try
        {
            Init();
            if (!evenIfNotLive && !Live()) return false;
            if (queue!.InvokeFunc(actionId)) return true;
            // Upgraded forms (Requiescat -> Imperator): Combat Pilot knows the live id.
            unsafe
            {
                var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
                var adjusted = am != null ? am->GetAdjustedActionId(actionId) : actionId;
                return adjusted != actionId && queue.InvokeFunc(adjusted);
            }
        }
        catch (Exception) { return false; }
    }

    public static uint Queued()
    {
        try { Init(); return queued!.InvokeFunc(); }
        catch (Exception) { return 0; }
    }
}
