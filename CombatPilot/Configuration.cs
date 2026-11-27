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

/// <summary>When Combat Pilot may start a fight on its own.</summary>
public enum EngageMode
{
    /// <summary>Never starts combat: waits until you're in combat.</summary>
    WaitForCombat,
    /// <summary>Attacks your target only if it's already fighting (in combat with someone).</summary>
    EngagedOnly,
    /// <summary>Attacks any attackable target you select, starting the fight (pulls, striking dummies).</summary>
    AnyTarget,
}

public enum AoeMode { Auto, SingleTarget, Force }
public enum PotionChoice { Highest, Specific }

/// <summary>Per-job toggles on the bar.</summary>
[Serializable]
public class JobToggles
{
    /// <summary>Hold burst cooldowns (the job file's Burst list).</summary>
    public bool HoldBurst { get; set; }
    public AoeMode Aoe { get; set; } = AoeMode.Auto;
    /// <summary>Keep the job file's travel charges in reserve (Warrior: 1 Onslaught).</summary>
    public bool KeepTravel { get; set; } = true;
    public bool Potions { get; set; } = false;
    /// <summary>Run the job's opener when a countdown is started (Live only).</summary>
    public bool OpenerOnCountdown { get; set; } = true;
    /// <summary>Which opener (name); empty = the job file's first.</summary>
    public string Opener { get; set; } = "";
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Per job abbreviation (WAR, ...).</summary>
    public Dictionary<string, PilotMode> Modes { get; set; } = new();

    // ---- when it acts
    /// <summary>When it may start a fight on its own (you're always covered once in combat).</summary>
    public EngageMode Engage { get; set; } = EngageMode.EngagedOnly;
    /// <summary>Seconds before the GCD comes back to press the next one (the game queues it).</summary>
    public float QueueWindow { get; set; } = 0.5f;

    public Dictionary<string, JobToggles> Toggles { get; set; } = new();
    public JobToggles TogglesFor(string job)
    {
        if (!Toggles.TryGetValue(job, out var t)) Toggles[job] = t = new JobToggles();
        return t;
    }

    // ---- potions
    public PotionChoice Potion { get; set; } = PotionChoice.Highest;
    /// <summary>For Specific: the item id (NQ id; HQ is used first if you have it).</summary>
    public uint PotionItemId { get; set; }

    // ---- bars
    public bool ShowBars { get; set; } = true;
    public float BarIconSize { get; set; } = 40;
    /// <summary>A clicked action waits this long for its slot before it's dropped.</summary>
    public float QueueSeconds { get; set; } = 6;
    /// <summary>Never use an AoE that would hit an enemy that isn't fighting yet (no accidental pulls).</summary>
    public bool NoPullWithAoe { get; set; } = true;

    // ---- RotationSolver Reborn
    public bool TurnOffRsr { get; set; } = true;
    public bool HandBackToRsr { get; set; } = true;

    // ---- planner
    public int HorizonGcds { get; set; } = 6;
    public int BeamWidth { get; set; } = 48;
    /// <summary>[Estimate] guaranteed crit+DH vs an average hit (used when not computed from your stats).</summary>
    public float CritDhFactor { get; set; } = 1.65f;
    /// <summary>Work it out from your Critical Hit / Direct Hit stats instead.</summary>
    public bool CritDhFromStats { get; set; } = true;

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
