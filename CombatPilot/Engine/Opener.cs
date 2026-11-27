using System;
using System.Collections.Generic;
using System.Linq;
using CombatPilot.Jobs;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CombatPilot.Engine;

/// <summary>
/// Plays a job file's opener step by step: each step is a GCD, then the oGCDs woven after it.
/// Started by an in-game countdown (the first action goes out Prepull seconds before 0, e.g.
/// Tomahawk at -0.7 s) or from the bar. It hands back to the planner when it's done, or when it
/// goes off script (a different GCD went out twice, or a step couldn't be pressed for 2 GCDs);
/// the planner then continues from whatever state the opener left.
/// </summary>
public sealed unsafe class OpenerRunner
{
    public OpenerDef? Def { get; private set; }
    public bool Active { get; private set; }
    public int Step { get; private set; }
    public string Status { get; private set; } = "";

    private bool gcdDone;
    private int weave;
    private int offScript;
    private DateTime slotSince = DateTime.MaxValue;
    private bool firedThisCountdown;
    private float lastCountdown;

    /// <summary>
    /// A countdown was called off (it vanished with time still left). Until combat starts some other
    /// way or a new countdown begins, Combat Pilot won't start a fight on its own.
    /// </summary>
    public bool CancelHold { get; private set; }

    /// <summary>Call every frame: spots a countdown being cancelled.</summary>
    public void WatchCountdown(bool inCombat)
    {
        var cd = Countdown();
        if (cd != null) { CancelHold = false; lastCountdown = cd.Value; return; }
        if (lastCountdown > 0.3f && !inCombat)
        {
            CancelHold = true;
            Status = "Countdown cancelled: not pulling";
            Plugin.ChatGui.Print("[Combat Pilot] Countdown cancelled: not pulling.");
            if (Active) { Active = false; Plugin.Log.Debug("[Pilot] opener stopped: countdown cancelled"); }
        }
        lastCountdown = 0;
        if (inCombat) CancelHold = false;
    }

    /// <summary>The countdown's seconds left, or null when none is running.</summary>
    public static float? Countdown()
    {
        var agent = AgentCountDownSettingDialog.Instance();
        if (agent == null || !agent->Active || agent->TimeRemaining <= 0) return null;
        return agent->TimeRemaining;
    }

    /// <summary>The chosen opener, if your level has what it was written for (else the planner opens).</summary>
    public static OpenerDef? Pick(JobBinding job, JobToggles t) =>
        job.Def.Openers.Where(o => o.MinLevel <= job.Level).FirstOrDefault(o => o.Name == t.Opener)
        ?? job.Def.Openers.FirstOrDefault(o => o.MinLevel <= job.Level);

    /// <summary>
    /// Countdown watch: when it reaches the opener's prepull time, returns the first GCD to press
    /// (and starts the opener). Null otherwise.
    /// </summary>
    public BoundAction? CheckCountdown(JobBinding job, JobToggles t, GameState g)
    {
        var cd = Countdown();
        if (cd == null) { firedThisCountdown = false; if (!Active) Status = ""; return null; }
        var def = Pick(job, t);
        if (def == null || !t.OpenerOnCountdown || Active || firedThisCountdown) return null;
        Status = $"Opener \"{def.Name}\" at {def.Prepull:0.0}s ({cd:0.0}s left)" + (g.HasTarget ? "" : " - no target yet");
        if (cd > def.Prepull || !g.HasTarget) return null;
        firedThisCountdown = true;
        Start(def);
        return job.ByName.GetValueOrDefault(def.Steps[0][0]);
    }

    public void Start(OpenerDef def)
    {
        Def = def;
        Active = true;
        Step = 0;
        gcdDone = false;
        weave = 0;
        offScript = 0;
        slotSince = DateTime.UtcNow;
        Status = $"Opener \"{def.Name}\": step 1/{def.Steps.Count}";
        Plugin.Log.Debug($"[Pilot] opener {def.Name} started");
    }

    public void Stop(string why)
    {
        if (!Active) return;
        Active = false;
        Status = $"Opener {why}";
        Plugin.Log.Debug($"[Pilot] opener stopped: {why}");
    }

    /// <summary>The opener's action for this slot, or null (nothing to press now).</summary>
    public BoundAction? Next(JobBinding job, GameState g, bool forGcd, bool potionsOn)
    {
        if (!Active || Def == null) return null;
        if (forGcd)
        {
            if (gcdDone) { Step++; gcdDone = false; weave = 0; slotSince = DateTime.UtcNow; }
            while (Step < Def.Steps.Count && !job.ByName.ContainsKey(Def.Steps[Step][0])) Step++; // not at this level
            if (Step >= Def.Steps.Count) { Stop("done"); return null; }
            Status = $"Opener \"{Def.Name}\": step {Step + 1}/{Def.Steps.Count}";
            if ((DateTime.UtcNow - slotSince).TotalSeconds > g.GcdLength * 2 + 0.5)
            {
                Stop($"off script (couldn't press {Def.Steps[Step][0]})");
                return null;
            }
            return job.ByName[Def.Steps[Step][0]];
        }
        if (!gcdDone || Step >= Def.Steps.Count) return null;
        var cur = Def.Steps[Step];
        while (weave < cur.Count - 1)
        {
            var name = cur[1 + weave];
            if ((name == JobBinding.PotionName && !potionsOn) || !job.ByName.TryGetValue(name, out var w)) { weave++; continue; }
            return w;
        }
        return null;
    }

    public void WeavePressed() => weave++;

    /// <summary>Skip this step's GCD (and its weaves): e.g. a jump that would land in a danger zone.</summary>
    public void SkipGcd()
    {
        if (!Active || Def == null || Step >= Def.Steps.Count) return;
        gcdDone = true;
        weave = Def.Steps[Step].Count;
    }

    /// <summary>A GCD went out (seen from the recast timer).</summary>
    public void OnGcdUsed(string name)
    {
        if (!Active || Def == null || Step >= Def.Steps.Count) return;
        if (gcdDone) return; // the next slot hasn't been asked for yet
        gcdDone = true;
        weave = 0;
        if (name != Def.Steps[Step][0] && ++offScript >= 2) Stop("off script (different GCDs went out)");
    }
}
