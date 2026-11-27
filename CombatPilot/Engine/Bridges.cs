using System;
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

    public float PressDueIn()
    {
        try { return pressDueIn.InvokeFunc(); }
        catch (Exception) { return float.MaxValue; }
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
