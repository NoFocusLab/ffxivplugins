using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace CombatPilot.Engine;

/// <summary>
/// Mit Planner presses mitigation itself. It tells us when its next planned press is due
/// ("MitPlanner.PressDueIn", seconds; float.MaxValue = none), so we leave a weave slot for it.
/// Its reactive trash presses aren't announced; the game's animation lock keeps us from pressing
/// over them.
/// </summary>
public sealed class MitBridge
{
    private readonly ICallGateSubscriber<float> pressDueIn =
        Plugin.PluginInterface.GetIpcSubscriber<float>("MitPlanner.PressDueIn");

    private readonly ICallGateSubscriber<float[]> downtime =
        Plugin.PluginInterface.GetIpcSubscriber<float[]>("MitPlanner.Downtime");
    private readonly ICallGateSubscriber<float> timelineTime =
        Plugin.PluginInterface.GetIpcSubscriber<float>("MitPlanner.TimelineTime");
    private readonly ICallGateSubscriber<uint> territory =
        Plugin.PluginInterface.GetIpcSubscriber<uint>("MitPlanner.FightTerritory");

    public float PressDueIn()
    {
        try { return pressDueIn.InvokeFunc(); }
        catch (Exception) { return float.MaxValue; }
    }

    /// <summary>Upcoming boss downtime from Mit Planner's running timeline: (from, until) seconds from now.</summary>
    public List<(double From, double Until)> Downtime()
    {
        var list = new List<(double, double)>();
        try
        {
            var a = downtime.InvokeFunc();
            for (var i = 0; i + 1 < a.Length; i += 2) list.Add((a[i], a[i + 1]));
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Mit Planner's synced timeline clock, or -1 (no timeline / Mit Planner not loaded).</summary>
    public float TimelineTime()
    {
        try { return timelineTime.InvokeFunc(); }
        catch (Exception) { return -1; }
    }

    public uint FightTerritory()
    {
        try { return territory.InvokeFunc(); }
        catch (Exception) { return 0; }
    }
}

/// <summary>
/// RotationSolver Reborn: turned off when Combat Pilot flies a job, back on when you switch to a
/// job Combat Pilot doesn't fly (if allowed). [Assumption] RSR's "/rotation off" and
/// "/rotation auto" commands; unverified in game.
/// </summary>
public sealed class RsrBridge
{
    public bool WeTurnedItOff { get; private set; }

    public static bool Loaded =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded &&
            (p.InternalName.Equals("RotationSolver", StringComparison.OrdinalIgnoreCase) ||
             p.InternalName.StartsWith("RotationSolver", StringComparison.OrdinalIgnoreCase)));

    public void TurnOff()
    {
        if (!Loaded) return;
        Plugin.CommandManager.ProcessCommand("/rotation off");
        WeTurnedItOff = true;
    }

    public void HandBack()
    {
        if (!WeTurnedItOff || !Loaded) return;
        Plugin.CommandManager.ProcessCommand("/rotation auto");
        WeTurnedItOff = false;
    }
}
