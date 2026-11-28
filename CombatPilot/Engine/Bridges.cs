using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
/// BossMod's danger knowledge (as Rescue Helper uses it): "BossMod.Hints.IsDashSafe(from, to)" checks
/// a dash's path and landing spot against every forbidden zone BossMod knows (boss AoEs, puddles,
/// the arena edge). Not loaded / no answer = no information (treated as safe), and it's said so on
/// the Pilot tab.
/// </summary>
public sealed class DangerBridge
{
    private readonly ICallGateSubscriber<Vector3, Vector3, bool> isDashSafe =
        Plugin.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
    private readonly ICallGateSubscriber<int> zoneCount =
        Plugin.PluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount");

    /// <summary>True: safe or unknown. False: BossMod says the dash ends (or passes) somewhere bad.</summary>
    public bool DashSafe(Vector3 from, Vector3 to, out bool known)
    {
        known = false;
        try
        {
            var ok = isDashSafe.InvokeFunc(from, to);
            known = true;
            return ok;
        }
        catch (Exception) { return true; }
    }

    public int Zones()
    {
        try { return zoneCount.InvokeFunc(); }
        catch (Exception) { return -1; }
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

    public static bool Loaded => Find() is { IsLoaded: true };

    private static Dalamud.Plugin.IExposedPlugin? Find() =>
        Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p =>
            p.InternalName.StartsWith("RotationSolver", StringComparison.OrdinalIgnoreCase));

    // ---- Unloading RSR while Live (Dalamud's /xldisableplugintemp: until re-enabled or the game
    // restarts; your saved plugin list is untouched, so Settings Sync doesn't carry it to other PCs).
    private string? unloadedName;
    private DateTime lastUnload = DateTime.MinValue, reloadedAt = DateTime.MinValue;
    private bool autoAfterReload;
    public bool WeUnloaded => unloadedName != null;
    public int TimesUnloaded { get; private set; }

    /// <summary>Unload RSR if it's loaded (checked every frame while Live; at most every 5 s).</summary>
    public void KeepUnloaded()
    {
        if (Find() is not { IsLoaded: true } p || (DateTime.UtcNow - lastUnload).TotalSeconds < 5) return;
        lastUnload = DateTime.UtcNow;
        TimesUnloaded++;
        unloadedName = p.InternalName;
        autoAfterReload = false;
        Plugin.CommandManager.ProcessCommand($"/xldisableplugintemp \"{p.InternalName}\"");
        Plugin.Log.Information($"[Pilot] RotationSolver unloaded while Live ({p.InternalName})");
    }

    /// <summary>Load RSR again if we unloaded it; autoOn: then start its rotation (hand back).</summary>
    public void Reload(bool autoOn = false)
    {
        autoOn = false; // seen live: "/rotation auto" after reloading made RSR pick a target and attack
        if (unloadedName == null) return;
        Plugin.CommandManager.ProcessCommand($"/xlenableplugintemp \"{unloadedName}\"");
        Plugin.Log.Information($"[Pilot] RotationSolver loaded again ({unloadedName})");
        unloadedName = null;
        reloadedAt = DateTime.UtcNow;
        autoAfterReload = autoOn;
    }

    /// <summary>Kept for the frame loop; RSR is never started by Combat Pilot (it comes back loaded and off).</summary>
    public void Tick() { }

    public void TurnOff()
    {
        if (!Loaded) return;
        Plugin.CommandManager.ProcessCommand("/rotation off");
        WeTurnedItOff = true;
    }

    private Dalamud.Plugin.Ipc.ICallGateSubscriber<bool>? active;
    private DateTime lastForcedOff = DateTime.MinValue;
    public int TimesForcedOff { get; private set; }

    /// <summary>RSR's own "is the rotation running" (IPC RotationSolverReborn.AutorotationActive). Null if unknown.</summary>
    public bool? Active()
    {
        if (!Loaded) return false;
        try
        {
            active ??= Plugin.PluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
            return active.InvokeFunc();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// While Combat Pilot flies Live: RSR switches itself back on (start on countdown, auto-on when
    /// attacked, ...). Checked every frame; turned off again at once (at most twice a second).
    /// </summary>
    public void KeepOff()
    {
        if (!Loaded || Active() != true || (DateTime.UtcNow - lastForcedOff).TotalSeconds < 0.5) return;
        lastForcedOff = DateTime.UtcNow;
        TimesForcedOff++;
        Plugin.CommandManager.ProcessCommand("/rotation off");
        WeTurnedItOff = true;
        Plugin.Log.Debug("[Pilot] RSR switched itself on: turned it off again");
    }

    /// <summary>Leaving Live after only turning RSR off: leave it off (you start it yourself).</summary>
    public void HandBack() => WeTurnedItOff = false;
}
