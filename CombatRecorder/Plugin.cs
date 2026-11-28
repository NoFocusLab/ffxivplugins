using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using CombatRecorder.Windows;

namespace CombatRecorder;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/crec";

    public Configuration Configuration { get; }
    public Recorder Recorder { get; }
    public Meter Meter { get; }
    public MeterWindow MeterWindow { get; }

    private readonly WindowSystem windowSystem = new("CombatRecorder");
    private readonly ConfigWindow configWindow;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<uint, double[]> dpsIpc;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Recorder = new Recorder(Configuration);
        Meter = new Meter(Configuration, Recorder);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
        MeterWindow = new MeterWindow(this);
        windowSystem.AddWindow(MeterWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Settings. /crec mark <note> (flag this moment for review) | on | off | stop (close the current file) | meter (show/hide the DPS meter) | meter move (position mode: show it now to move/resize) | reset (clear the meter)",
        });
        // For Combat Pilot's end-of-fight report: [damage, seconds] for a player (entity id) in the
        // running encounter, or the last one if none is running. Empty if they weren't in it.
        dpsIpc = PluginInterface.GetIpcProvider<uint, double[]>("CombatRecorder.Damage");
        dpsIpc.RegisterFunc(id =>
        {
            var e = Meter.Current ?? (Meter.History.Count > 0 ? Meter.History[0] : null);
            return e != null && e.Players.TryGetValue(id, out var c) ? new double[] { c.Damage, e.Seconds } : System.Array.Empty<double>();
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
    }

    public void Dispose()
    {
        dpsIpc.UnregisterFunc();
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windowSystem.RemoveAllWindows();
        Meter.Dispose();
        Recorder.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleConfig() => configWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "mark": Recorder.Mark(parts.Length > 1 ? parts[1] : "mark"); break;
            case "on": Configuration.Enabled = true; Configuration.Save(); ChatGui.Print("[CombatRecorder] On."); break;
            case "off": Configuration.Enabled = false; Configuration.Save(); Recorder.Close("turned off"); ChatGui.Print("[CombatRecorder] Off."); break;
            case "stop": Recorder.Close("stopped"); break;
            case "meter" when parts.Length > 1 && parts[1].Trim().ToLowerInvariant() is "move" or "pos" or "position":
                MeterWindow.Moving = !MeterWindow.Moving; break;
            case "meter": Configuration.ShowMeter = !Configuration.ShowMeter; Configuration.Save(); break;
            case "reset": Meter.Reset(); break;
            default: configWindow.Toggle(); break;
        }
    }
}
