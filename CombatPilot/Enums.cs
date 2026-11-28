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

public enum AnchorKind { Role, Name, Target }
public enum AnchorRole { Tank, Healer, Melee, Ranged }
