using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CombatPilot.Jobs;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace CombatPilot.Engine;

/// <summary>
/// The main loop. Every frame: keep the job binding current, watch what gets used (for the logs),
/// and when a GCD or a weave window comes up, plan. Live: press the plan's first move. Listen:
/// only record how it compares with what the other pilot pressed.
/// Safety: if the planner fails, or something was usable but nothing was pressed for two GCDs,
/// a fixed safe priority takes over; if that also stalls for two more GCDs, the pilot pauses and
/// tells you.
/// </summary>
public sealed unsafe class Pilot : IDisposable
{
    private readonly Configuration config;
    private readonly MitBridge mit = new();
    private readonly DangerBridge danger = new();
    public Mover Mover { get; } = new();
    private readonly RsrBridge rsr = new();
    public FightLog Log { get; }
    public PartyBuffTracker Party { get; } = new();
    public OpenerRunner Opener { get; } = new();
    public MitBridge Mit => mit;

    public JobBinding? Job { get; private set; }
    public PilotMode Mode { get; private set; }
    /// <summary>Kill switch: Live acts as Off until resumed.</summary>
    public bool Paused { get; private set; }
    public Decision? LastGcd { get; private set; }
    public Decision? LastWeave { get; private set; }
    /// <summary>The game state of the last plan (for the Pilot tab's AoE line).</summary>
    public GameState? LastState { get; private set; }
    public string Status { get; private set; } = "";
    public bool SafeMode { get; private set; }

    private uint jobId;
    private int level;
    private DateTime nextPlan = DateTime.MinValue;
    private DateTime lastPress = DateTime.MinValue;
    private uint lastPressId;
    private bool wasInFight;
    private bool killHeld;

    // Stall detection.
    private DateTime? stallSince;
    private DateTime? safeSince;

    // What actually got used (observed from the recast timers).
    private uint lastGcdAction;
    private float lastGcdElapsed;
    private readonly Dictionary<int, (int Charges, float Elapsed)> lastCd = new();
    private Decision? pendingGcd;
    private Decision? pendingWeave;
    private bool weaveUsedThisWindow;

    public Pilot(Configuration config)
    {
        this.config = config;
        Log = new FightLog(config.KeepFights);
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Log.End();
    }

    public void SetPaused(bool paused, string why)
    {
        if (Paused == paused) return;
        Paused = paused;
        Plugin.ChatGui.Print($"[Combat Pilot] {(paused ? "Paused" : "Resumed")}{(why.Length > 0 ? $": {why}" : "")}.");
        if (paused) { SafeMode = false; stallSince = safeSince = null; Mover.Halt("paused"); }
    }

    private void OnUpdate(IFramework fw)
    {
        try { Tick(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "Combat Pilot tick failed"); }
    }

    private void Tick()
    {
        KillSwitch();
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) { Job = null; return; }

        // Job / level changes: rebind, and turn RSR off or hand it back.
        var cj = me.ClassJob.RowId;
        if (cj != jobId || me.Level != level || Job == null)
        {
            jobId = cj;
            level = me.Level;
            var abbr = me.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "";
            var def = JobLibrary.For(abbr);
            Job = def == null ? null : new JobBinding(def, abbr, level);
            nextPotionCheck = DateTime.MinValue;
            var mode = Job == null ? PilotMode.Off : config.ModeFor(Job.Def.Job);
            if (mode == PilotMode.Live && config.TurnOffRsr && !Paused) rsr.TurnOff();
            else if (mode != PilotMode.Live && config.HandBackToRsr) rsr.HandBack();
        }
        Mode = Job == null ? PilotMode.Off : config.ModeFor(Job.Def.Job);
        if (Job == null || Mode == PilotMode.Off) { Status = Job == null ? "No job file for this job" : "Off"; EndFight(); return; }

        Opener.WatchCountdown(Plugin.Condition[ConditionFlag.InCombat]);

        // Countdown: the opener's first action goes out at its prepull time (Live only).
        if (Mode == PilotMode.Live && !Paused && OpenerRunner.Countdown() != null)
        {
            var g0 = GameState.Read(Job);
            if (Opener.CheckCountdown(Job, config.TogglesFor(Job.Def.Job), g0) is { } first) Press(first, g0);
            if (!Opener.Active) { Status = Opener.Status; return; }
        }

        var inCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (!inCombat && !Opener.Active && !MayEngage()) { EndFight(); return; }
        if (!wasInFight)
        {
            wasInFight = true;
            var place = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                .GetRowOrDefault(Plugin.ClientState.TerritoryType)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            Log.Start(Job.Def.Job, Paused && Mode == PilotMode.Live ? "Paused" : Mode.ToString(), place);
            Party.StartFight();
        }
        Party.Update(mit);

        Observe();

        // Auto-move (Live, not paused, bar toggle on): mechanics (BossMod) first, then positionals and your rules.
        var rules = config.TogglesFor(Job.Def.Job).Move;
        if (!rules.Initialized) { Mover.Defaults(rules, me.ClassJob.ValueNullable?.Role ?? 0); config.Save(); }
        Mover.Update(me, Job, LastState, LastGcd, rules, danger, Mode == PilotMode.Live && !Paused && config.AutoMove);

        var am = ActionManager.Instance();
        if (am == null || me.IsCasting) return;

        // Planning runs off the game thread; its result is used on a later frame if still current.
        if (planning != null)
        {
            if (!planning.IsCompleted) return;
            var (d, forGcdPlanned, at) = planning.Result;
            planning = null;
            Act(d, forGcdPlanned, at);
            return;
        }
        if (DateTime.UtcNow < nextPlan) return;
        nextPlan = DateTime.UtcNow.AddMilliseconds(80);

        var g = GameState.Read(Job, Party, mit, danger, config.AutoMove);
        if (g.AnimationLock > 0.05) return;

        var forGcd = g.GcdRemaining <= config.QueueWindow;
        if (!forGcd && g.GcdRemaining < Sim.OgcdLock + 0.05) return; // too late to weave, too early to queue
        if (!forGcd && weaveUsedThisWindow && Planner.WeavesBetween(0, g.GcdRemaining) == 0) return;

        // You clicked something on the bar: it goes in this slot if it fits, before any planning.
        if (Queued is { } q && Mode == PilotMode.Live && !Paused)
        {
            if (DateTime.UtcNow > queuedUntil) { Queued = null; }
            else if (q.IsGcd == forGcd)
            {
                if (Press(q, g)) { Plugin.Log.Debug($"[Pilot] queued {q.Name} pressed"); Log.Note($"you queued {q.Name}"); Queued = null; }
                return;
            }
        }

        // Opener: its steps instead of the planner, until it's done or off script.
        if (Opener.Active)
        {
            var potionsOn = config.TogglesFor(Job.Def.Job).Potions && Job.ByName.ContainsKey(JobBinding.PotionName);
            if (Opener.Next(Job, g, forGcd, potionsOn) is { } oa)
            {
                if (g.UnsafeDash.Contains(oa.Id))
                {
                    Log.Note($"opener: held {oa.Name} ({g.UnsafeWhy.GetValueOrDefault(oa.Id, "unsafe")})");
                    if (forGcd) Opener.SkipGcd(); else Opener.WeavePressed(); // skip it; the planner can use it later
                    return;
                }
                if (Press(oa, g) && !forGcd) Opener.WeavePressed();
                return;
            }
            if (Opener.Active) return;
            Log.Note($"opener {Opener.Status}");
        }

        RefreshPotion();
        var t = config.TogglesFor(Job.Def.Job);
        var set = new PlannerSettings
        {
            HorizonGcds = Math.Clamp(config.HorizonGcds, 2, 12),
            BeamWidth = Math.Clamp(config.BeamWidth, 8, 256),
            CritDhFactor = config.CritDhFromStats && StatsNow()?.CritDhFactor is { } f ? f : config.CritDhFactor,
            ReservedWeaves = !forGcd && mit.PressDueIn() <= g.GcdRemaining + 0.3f ? 1 : 0,
            Held = t.HoldBurst ? Job.Def.Burst.ToHashSet() : new HashSet<string>(),
            Aoe = t.Aoe,
            ReserveAction = t.KeepTravel ? Job.Def.Travel?.Action : null,
            ReserveKeep = Job.Def.Travel?.Keep ?? 0,
            NoPull = config.NoPullWithAoe,
        };
        var job = Job;
        LastState = g;
        var started = DateTime.UtcNow;
        planning = Task.Run(() => (Planner.Decide(g, job, set, forGcd), forGcd, started));
    }

    private Task<(Decision, bool, DateTime)>? planning;

    /// <summary>Use a finished plan, if the moment it was made for hasn't passed.</summary>
    private void Act(Decision d, bool forGcd, DateTime plannedAt)
    {
        if (Job == null) return;
        if (forGcd) { LastGcd = d; pendingGcd = d; } else { LastWeave = d; pendingWeave = d; }

        var live = Mode == PilotMode.Live && !Paused;
        var g = GameState.Read(Job);
        Status = (live ? (SafeMode ? "LIVE (safe mode)" : "LIVE") : Mode == PilotMode.Live ? "Paused" : "Listening") +
                 (g.HasTarget ? "" : $" - {g.TargetNote}") + (Opener.Active ? $" - {Opener.Status}" : "");
        if (!live) return;

        if (g.AnimationLock > 0.05) return;
        // Stale: the slot it was planned for is gone (a weave plan after the window closed, etc.).
        if ((DateTime.UtcNow - plannedAt).TotalMilliseconds > 400) return;
        if (forGcd && g.GcdRemaining > config.QueueWindow) return;
        if (!forGcd && (g.GcdRemaining <= config.QueueWindow || g.GcdRemaining < Sim.OgcdLock + 0.05)) return;

        Plugin.Log.Debug($"[Pilot] {(forGcd ? "GCD" : "weave")}: {d.Label}  plan {string.Join(" > ", d.Path.Take(6))}");
        if (d.Error != null)
        {
            Log.Note($"planner error: {d.Error}");
            Plugin.Log.Warning($"[Pilot] planner error: {d.Error}");
            if (forGcd && g.GcdRemaining <= 0.05) SafePress(g);
            return;
        }
        if (d.Action != null) { Press(d.Action, g); return; }
        if (forGcd) CheckStall(g);
    }

    /// <summary>Out of combat: may it start the fight on your target? Sets Status when not.</summary>
    private bool MayEngage()
    {
        if (config.Engage == EngageMode.WaitForCombat) { Status = "Waiting for combat (engage: wait)"; return false; }
        if (Opener.CancelHold) { Status = "Countdown cancelled: waiting (combat or a new countdown)"; return false; }
        var g = GameState.Read(Job!);
        if (!g.HasTarget) { Status = $"Out of combat: {g.TargetNote}"; return false; }
        if (config.Engage == EngageMode.EngagedOnly && !g.TargetEngaged)
        { Status = "Target isn't fighting yet (engage: engaged only)"; return false; }
        return true;
    }

    // ================================================================ pressing

    private bool Press(BoundAction a, GameState g)
    {
        var am = ActionManager.Instance();
        var now = DateTime.UtcNow;
        bool ok;
        if (a.IsItem)
        {
            if (a.Id == lastPressId && (now - lastPress).TotalSeconds < 0.5) return false;
            lastPressId = a.Id;
            lastPress = now;
            ok = am->UseAction(ActionType.Item, a.Id, 0xE000_0000, 65535);
            Plugin.Log.Debug($"[Pilot] potion #{a.Id} -> {(ok ? "ok" : "refused")}");
            if (ok) nextPotionCheck = DateTime.MinValue;
            return ok;
        }
        var id = am->GetAdjustedActionId(a.PressId);
        if (id == lastPressId && (now - lastPress).TotalSeconds < 0.25) return false;
        lastPressId = id;
        lastPress = now;
        var target = a.Def.Potency > 0 && g.HasTarget ? g.TargetId : 0xE000_0000UL;
        ok = am->UseAction(ActionType.Action, id, target);
        Plugin.Log.Debug($"[Pilot] press {a.Name} (#{id}) -> {(ok ? "ok" : "refused")}; gcd {g.GcdRemaining:0.00}s");
        if (a.IsGcd) CheckStall(g);
        return ok;
    }

    // ================================================================ bar: queue, potions

    /// <summary>Clicked on the bar. Live: goes into the next slot it fits. Otherwise: used now.</summary>
    public BoundAction? Queued { get; private set; }
    private DateTime queuedUntil;

    public void Queue(BoundAction a)
    {
        if (Mode == PilotMode.Live && !Paused)
        {
            Queued = a;
            queuedUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(config.QueueSeconds, 1, 30));
            return;
        }
        if (Job != null) Press(a, GameState.Read(Job));
    }

    public void ClearQueue() => Queued = null;

    private DateTime nextPotionCheck = DateTime.MinValue;
    public string PotionStatus { get; private set; } = "";

    /// <summary>Keep the potion action in step with the toggle and your inventory (every 10 s).</summary>
    private void RefreshPotion()
    {
        if (Job == null || DateTime.UtcNow < nextPotionCheck) return;
        nextPotionCheck = DateTime.UtcNow.AddSeconds(10);
        if (!config.TogglesFor(Job.Def.Job).Potions) { Job.SetPotion(0, ""); PotionStatus = "off"; return; }
        var pick = Potions.Pick(config, Job.Def.MainStat);
        Job.SetPotion(pick?.UseId ?? 0, pick?.Name ?? "");
        PotionStatus = pick == null ? (config.Potion == PotionChoice.Specific ? "chosen potion not in your bags" : $"no {Job.Def.MainStat} potion in your bags") : pick.Label;
    }

    public void PotionSettingsChanged() => nextPotionCheck = DateTime.MinValue;

    /// <summary>Something usable sat unpressed for two GCDs: safe mode; two more: pause.</summary>
    private void CheckStall(GameState g)
    {
        if (g.GcdRemaining > 0.05 || !g.HasTarget) { stallSince = null; return; }
        if (SafeCandidates(g).Count == 0) { stallSince = null; return; } // nothing usable: holding is right
        var now = DateTime.UtcNow;
        stallSince ??= now;
        if ((now - stallSince.Value).TotalSeconds < g.GcdLength * 2) return;
        SafePress(g);
    }

    private void SafePress(GameState g)
    {
        var now = DateTime.UtcNow;
        if (!SafeMode)
        {
            SafeMode = true;
            safeSince = now;
            Log.Note("safe mode");
            Plugin.ChatGui.PrintError("[Combat Pilot] Planner stalled: safe mode (fixed priority).");
        }
        var pick = SafeCandidates(g).FirstOrDefault();
        if (pick != null) { Log.SafeModePress(); Press(pick, g); }
        if (safeSince is { } since && (now - since).TotalSeconds > g.GcdLength * 4 && g.GcdRemaining <= 0.05)
        {
            SetPaused(true, "stalled in safe mode too, pausing; press the kill-switch key or /pilot resume");
            Log.Note("paused after stall");
        }
    }

    private List<BoundAction> SafeCandidates(GameState g)
    {
        var list = new List<BoundAction>();
        var am = ActionManager.Instance();
        if (am == null) return list;
        foreach (var name in Job!.Def.SafeOrder)
        {
            if (!Job.ByName.TryGetValue(name, out var a)) continue;
            var id = am->GetAdjustedActionId(a.PressId);
            if (id != a.Id) continue;
            if (!Planner.Reaches(a, g)) continue;
            if (am->GetActionStatus(ActionType.Action, id, g.TargetId) != 0 && g.GcdRemaining <= 0.05) continue;
            list.Add(a);
        }
        return list;
    }

    // ================================================================ watching what got used

    /// <summary>
    /// Detect new GCDs (recast group 58 restarts) and oGCDs (their recast group loses a charge or
    /// restarts), and log each against the last decision made for that slot.
    /// </summary>
    private void Observe()
    {
        var am = ActionManager.Instance();
        if (am == null || Job == null) return;

        var gcd = am->GetRecastGroupDetail(57);
        if (gcd != null && gcd->IsActive)
        {
            var restarted = gcd->ActionId != lastGcdAction ? gcd->Elapsed < 0.5f : gcd->Elapsed + 0.05f < lastGcdElapsed;
            if (restarted)
            {
                var used = NameOf(gcd->ActionId);
                Opener.OnGcdUsed(used);
                Record(pendingGcd, used, true);
                pendingGcd = null;
                // The weave window before this GCD is over.
                if (pendingWeave != null && !weaveUsedThisWindow) Record(pendingWeave, "(nothing)", false);
                pendingWeave = null;
                weaveUsedThisWindow = false;
                stallSince = null;
                if (SafeMode && safeSince is { } s && (DateTime.UtcNow - s).TotalSeconds > 1 && LastGcd?.Error == null && LastGcd?.Action != null)
                {
                    SafeMode = false;
                    safeSince = null;
                }
            }
            lastGcdAction = gcd->ActionId;
            lastGcdElapsed = gcd->Elapsed;
        }

        foreach (var a in Job.Actions)
        {
            if (a.IsGcd || a.IsItem || a.CooldownGroup < 0) continue;
            var id = am->GetAdjustedActionId(a.Id);
            var charges = (int)am->GetCurrentCharges(id);
            var elapsed = am->GetRecastTimeElapsed(ActionType.Action, id);
            if (lastCd.TryGetValue(a.CooldownGroup, out var prev))
            {
                var used = charges < prev.Charges || (am->IsRecastTimerActive(ActionType.Action, id) && elapsed + 0.05f < prev.Elapsed && elapsed < 1f);
                if (used)
                {
                    var det = am->GetRecastGroupDetail(am->GetRecastGroup((int)ActionType.Action, id));
                    var name = NameOf(det != null && det->ActionId != 0 ? det->ActionId : id);
                    Record(pendingWeave, name, false);
                    pendingWeave = null;
                    weaveUsedThisWindow = true;
                }
            }
            lastCd[a.CooldownGroup] = (charges, elapsed);
        }
    }

    private string heldWanted = "";

    private void Record(Decision? d, string actual, bool gcd)
    {
        if (d == null) return;
        var wanted = d.Label;
        double? lost = null;
        if (d.RootScores.TryGetValue(actual, out var got) && d.RootScores.Count > 0)
            lost = Math.Max(0, d.RootScores.Values.Max() - got);
        else if (actual == "(nothing)" && d.RootScores.TryGetValue("(skip)", out var skipped))
            lost = Math.Max(0, d.RootScores.Values.Max() - skipped);
        if (actual == "(nothing)" && wanted == "(skip)") return;
        // An oGCD left unpressed is counted once per stretch, not again in every weave window.
        if (!gcd && actual == "(nothing)")
        {
            if (wanted == heldWanted) return;
            heldWanted = wanted;
        }
        else if (!gcd || actual == heldWanted) heldWanted = "";
        Log.Add(new DecisionRecord(Log.FightTime, gcd, wanted, actual, lost, string.Join(" > ", d.Path.Take(8)), d.Error ?? ""));
    }

    private string NameOf(uint actionId)
    {
        var a = Job?.Actions.FirstOrDefault(x => x.Id == actionId);
        if (a != null) return a.Name;
        return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId)?.Name.ExtractText() ?? $"#{actionId}";
    }

    private void EndFight()
    {
        if (!wasInFight) return;
        wasInFight = false;
        pendingGcd = pendingWeave = null;
        SafeMode = false;
        stallSince = safeSince = null;
        Party.EndFight();
        Opener.Stop("ended (fight over)");
        Mover.Halt("");
        Log.End();
    }

    // ================================================================ kill switch

    private void KillSwitch()
    {
        var ks = Plugin.KeyState;
        var down = ks[config.KillKey] &&
                   (!config.KillCtrl || ks[Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL]) &&
                   (!config.KillShift || ks[Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT]) &&
                   (!config.KillAlt || ks[Dalamud.Game.ClientState.Keys.VirtualKey.MENU]);
        if (down && !killHeld) SetPaused(!Paused, "kill-switch key");
        killHeld = down;
    }

    /// <summary>Mode change from the UI or a command: also turns RSR off / hands it back.</summary>
    public void ModeChanged()
    {
        if (Job == null) return;
        var mode = config.ModeFor(Job.Def.Job);
        if (mode == PilotMode.Live && config.TurnOffRsr) rsr.TurnOff();
        else if (mode != PilotMode.Live && config.HandBackToRsr) rsr.HandBack();
    }

    public void Rebind() => Job = null;

    private Stats.Snapshot? stats;
    private DateTime statsAt = DateTime.MinValue;

    /// <summary>Your substats, re-read every 5 s (gear swaps, level sync).</summary>
    public Stats.Snapshot? StatsNow()
    {
        if (DateTime.UtcNow - statsAt > TimeSpan.FromSeconds(5))
        {
            stats = Stats.Read(level);
            statsAt = DateTime.UtcNow;
        }
        return stats;
    }

    /// <summary>Bar button: run the opener from the next GCD.</summary>
    public void StartOpenerNow()
    {
        if (Job == null || OpenerRunner.Pick(Job, config.TogglesFor(Job.Def.Job)) is not { } def) return;
        Opener.Start(def);
    }
}
