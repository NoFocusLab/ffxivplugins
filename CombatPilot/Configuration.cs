using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;

namespace CombatPilot;

public enum PilotMode
{
    /// <summary>Combat Pilot does nothing for this job.</summary>
    Off,
    /// <summary>Another pilot (RSR) flies; Combat Pilot plans alongside and logs where it would differ.</summary>
    Listen,
    /// <summary>Combat Pilot presses the damage buttons (RSR is turned off for this job).</summary>
    Live,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Per job abbreviation (WAR, ...).</summary>
    public Dictionary<string, PilotMode> Modes { get; set; } = new();

    // ---- when it acts
    /// <summary>Only act in combat (never pulls for you).</summary>
    public bool OnlyInCombat { get; set; } = true;
    /// <summary>Seconds before the GCD comes back to press the next one (the game queues it).</summary>
    public float QueueWindow { get; set; } = 0.5f;

    // ---- RotationSolver Reborn
    public bool TurnOffRsr { get; set; } = true;
    public bool HandBackToRsr { get; set; } = true;

    // ---- planner
    public int HorizonGcds { get; set; } = 6;
    public int BeamWidth { get; set; } = 48;
    /// <summary>[Estimate] guaranteed crit+DH vs an average hit.</summary>
    public float CritDhFactor { get; set; } = 1.65f;

    // ---- kill switch (pause/resume)
    public VirtualKey KillKey { get; set; } = VirtualKey.END;
    public bool KillCtrl { get; set; } = true;
    public bool KillShift { get; set; } = true;
    public bool KillAlt { get; set; } = false;

    // ---- display / logs
    public bool ShowOverlay { get; set; } = true;
    public int KeepFights { get; set; } = 100;

    public PilotMode ModeFor(string job) => Modes.TryGetValue(job, out var m) ? m : PilotMode.Off;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
