using System;
using System.Linq;
using CombatPilot.Engine;
using CombatPilot.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CombatPilot;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IJobGauges JobGauges { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ISeStringEvaluator SeStringEvaluator { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/pilot";

    public Configuration Configuration { get; }
    public Pilot Pilot { get; }

    private readonly WindowSystem windows = new("CombatPilot");
    private readonly MainWindow main;
    private readonly OverlayWindow overlay;
    private readonly BarsWindow bars;
    /// <summary>"CombatPilot.Queue"(actionId): queue an action by id (e.g. from Mit Planner). False if it isn't one of the job's.</summary>
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<uint, bool> queueIpc;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<uint[]> offenseIpc;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<bool> liveIpc;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<uint> queuedIpc;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Pilot = new Pilot(Configuration);
        main = new MainWindow(this);
        overlay = new OverlayWindow(this) { IsOpen = Configuration.ShowOverlay };
        windows.AddWindow(main);
        windows.AddWindow(overlay);
        bars = new BarsWindow(this) { IsOpen = true };
        windows.AddWindow(bars);
        queueIpc = PluginInterface.GetIpcProvider<uint, bool>("CombatPilot.Queue");
        queueIpc.RegisterFunc(id =>
        {
            var a = Pilot.Job?.Actions.FirstOrDefault(x => x.Id == id || x.PressId == id);
            if (a == null) return false;
            Pilot.Queue(a);
            return true;
        });

        // For Cooldown Grid's Offense group: which abilities to show, and where clicks should go.
        offenseIpc = PluginInterface.GetIpcProvider<uint[]>("CombatPilot.Offense");
        offenseIpc.RegisterFunc(() =>
        {
            if (Pilot.Job is not { } j) return Array.Empty<uint>();
            var names = j.Def.Bar.Count > 0 ? j.Def.Bar : j.Actions.Where(a => !a.IsGcd && !a.IsItem && !a.Def.PlanNever).Select(a => a.Name).ToList();
            return names.Select(n => j.ByName.TryGetValue(n, out var a) && !a.IsItem ? a.Id : 0).Where(id => id != 0).Distinct().ToArray();
        });
        liveIpc = PluginInterface.GetIpcProvider<bool>("CombatPilot.Live");
        liveIpc.RegisterFunc(() => Pilot.Job != null && Pilot.Mode == PilotMode.Live && !Pilot.Paused);
        queuedIpc = PluginInterface.GetIpcProvider<uint>("CombatPilot.Queued");
        queuedIpc.RegisterFunc(() => Pilot.Queued?.Id ?? 0);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Combat Pilot. /pilot (window) | live | listen | off (for your current job) | pause | resume | report | engage wait|fighting|any | bar (show/hide the bar)",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += main.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= main.Toggle;
        windows.RemoveAllWindows();
        queueIpc.UnregisterFunc();
        offenseIpc.UnregisterFunc();
        liveIpc.UnregisterFunc();
        queuedIpc.UnregisterFunc();
        CommandManager.RemoveHandler(CommandName);
        Pilot.Dispose();
    }

    public void OpenMain() => main.IsOpen = true;

    public void SetOverlay(bool show)
    {
        Configuration.ShowOverlay = show;
        Configuration.Save();
        overlay.IsOpen = show;
    }

    private void OnCommand(string cmd, string args)
    {
        var a = args.Trim().ToLowerInvariant();
        switch (a)
        {
            case "live": SetMode(PilotMode.Live); return;
            case "listen": SetMode(PilotMode.Listen); return;
            case "off": SetMode(PilotMode.Off); return;
            case "pause": Pilot.SetPaused(true, "/pilot pause"); return;
            case "resume": Pilot.SetPaused(false, "/pilot resume"); return;
            case "report": main.ShowReports(); return;
            case "bar": Configuration.ShowBars = !Configuration.ShowBars; Configuration.Save(); return;
            case "engage wait": SetEngage(EngageMode.WaitForCombat); return;
            case "engage fighting": SetEngage(EngageMode.EngagedOnly); return;
            case "engage any": SetEngage(EngageMode.AnyTarget); return;
            default: main.Toggle(); return;
        }
    }

    private void SetEngage(EngageMode m)
    {
        Configuration.Engage = m;
        Configuration.Save();
        ChatGui.Print($"[Combat Pilot] Engage: {m}.");
    }

    public void SetMode(PilotMode mode)
    {
        if (Pilot.Job == null) { ChatGui.PrintError("[Combat Pilot] No job file for your current job yet."); return; }
        Configuration.Modes[Pilot.Job.Def.Job] = mode;
        Configuration.Save();
        Pilot.ModeChanged();
        ChatGui.Print($"[Combat Pilot] {Pilot.Job.Def.Job}: {mode}.");
    }
}
