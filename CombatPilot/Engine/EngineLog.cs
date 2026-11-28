using System;

namespace CombatPilot.Engine;

/// <summary>Logging for the engine core (the plugin points it at Dalamud's log; the simulator at the console).</summary>
public static class EngineLog
{
    public static Action<Exception, string> Warning = (_, _) => { };
}
