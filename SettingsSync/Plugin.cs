using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SettingsSync.Windows;

namespace SettingsSync;

/// <summary>
/// Game services this plugin uses. Created on the first UI frame, not in the constructor: the plugin
/// loads before the game is ready (so it can pull settings before other plugins read them), and only
/// the plugin interface and log are guaranteed that early.
/// </summary>
public sealed class Services
{
    [PluginService] public ICommandManager Commands { get; private set; } = null!;
    [PluginService] public IClientState ClientState { get; private set; } = null!;
    [PluginService] public IFramework Framework { get; private set; } = null!;
}

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/ssync";

    public Configuration Configuration { get; }
    public Syncer Syncer { get; }
    public string StartupNote { get; } = "";

    private readonly WindowSystem windowSystem = new("SettingsSync");
    private readonly MainWindow mainWindow;
    private readonly ConflictWindow conflictWindow;
    private readonly CancellationTokenSource life = new();
    private Services? services;
    private DateTime nextPeriodicCheck = DateTime.Now.AddMinutes(1);

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var ownDir = PluginInterface.GetPluginConfigDirectory();
        var root = Directory.GetParent(ownDir.TrimEnd('\\', '/'))!.FullName;
        Syncer = new Syncer(Configuration, root, ownDir);

        // Before any other plugin reads its settings: apply what was pulled last session, then pull.
        try
        {
            var applied = Syncer.ApplyPending();
            if (applied > 0) StartupNote = $"Applied {applied} file(s) pulled last session.";
            if (Configuration.AutoSync && Configuration.SyncOnOpen && Syncer.Configured) StartupSync();
        }
        catch (Exception e) { Log.Error(e, "Settings Sync: startup sync failed"); }

        mainWindow = new MainWindow(this);
        conflictWindow = new ConflictWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(conflictWindow);

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
    }

    private void StartupSync()
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(Configuration.StartupWaitSeconds, 2, 30));
        var cts = new CancellationTokenSource(wait);   // not disposed: a timed-out task may still hold it
        var task = Task.Run(() => Syncer.Run(SyncMode.Startup, cts.Token, groups: SyncGroups.Plugins));
        if (!task.Wait(wait + TimeSpan.FromSeconds(1)))
        {
            cts.Cancel();
            task.Wait(TimeSpan.FromSeconds(1));   // writes check the token, so nothing lands after this
            Log.Warning("Settings Sync: startup pull timed out; will sync once the game is up.");
            pendingRunningSync = true;
        }
    }

    private bool pendingRunningSync;
    private int seenConflicts;

    /// <summary>First frame: the game is up, so the other services can be fetched.</summary>
    private void EnsureServices()
    {
        if (services != null) return;
        services = PluginInterface.Create<Services>()!;
        services.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Settings Sync. /ssync now = sync now, /ssync push, /ssync pull",
        });
        services.ClientState.Logout += OnLogout;
        services.Framework.Update += OnUpdate;
        // Game is up: Dalamud setup can be read now (it needs the installed-plugin list).
        var groups = (pendingRunningSync ? SyncGroups.Plugins : SyncGroups.None) |
                     (Configuration.DalamudAuto && Syncer.Configured ? SyncGroups.Dalamud : SyncGroups.None);
        if (groups != SyncGroups.None) SyncNow(groups: groups);
    }

    private void OnDraw()
    {
        EnsureServices();
        if (Syncer.ConflictGeneration != seenConflicts) { seenConflicts = Syncer.ConflictGeneration; conflictWindow.Dismissed = false; }
        if (Syncer.Conflicts.Count > 0 && !conflictWindow.Dismissed) conflictWindow.IsOpen = true;
        windowSystem.Draw();
    }

    private void OnUpdate(IFramework fw)
    {
        // Plugin on/off switches pulled from the other PC (must run on the game thread).
        while (Syncer.Dalamud.Commands.TryDequeue(out var cmd))
        {
            try { services!.Commands.ProcessCommand(cmd); }
            catch (Exception e) { Log.Warning(e, $"Settings Sync: {cmd} failed"); }
        }

        if (DateTime.Now < nextPeriodicCheck) return;
        nextPeriodicCheck = DateTime.Now.AddMinutes(1);
        var minutes = Configuration.PushWhilePlayingMinutes;
        if (!Configuration.AutoSync || minutes <= 0 || Syncer.Busy || !Syncer.Configured) return;
        if (DateTime.Now - Syncer.LastAttempt >= TimeSpan.FromMinutes(minutes))
            _ = Task.Run(() => Syncer.Run(SyncMode.Running, life.Token, pushOnly: true, groups: AutoGroups(Configuration.AutoSync)));
    }

    private void OnLogout(int type, int code)
    {
        var groups = AutoGroups(Configuration.AutoSync && Configuration.SyncOnClose);
        if (groups != SyncGroups.None && Syncer.Configured)
            _ = Task.Run(() => Syncer.Run(SyncMode.Running, life.Token, pushOnly: true, groups: groups));
    }

    /// <summary>Groups that sync on their own: plugin settings when <paramref name="plugins"/>, Dalamud setup when its switch is on.</summary>
    private SyncGroups AutoGroups(bool plugins) =>
        (plugins ? SyncGroups.Plugins : SyncGroups.None) | (Configuration.DalamudAuto ? SyncGroups.Dalamud : SyncGroups.None);

    public void SyncNow(bool pushOnly = false, bool pullOnly = false, SyncGroups groups = SyncGroups.All) =>
        _ = Task.Run(() => Syncer.Run(SyncMode.Running, life.Token, pushOnly, pullOnly, groups));

    public void OpenInstaller(string search) =>
        PluginInterface.OpenPluginInstallerTo(Dalamud.Interface.PluginInstallerOpenKind.AllPlugins, search);

    public CancellationToken Life => life.Token;

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        if (services != null)
        {
            services.Commands.RemoveHandler(CommandName);
            services.ClientState.Logout -= OnLogout;
            services.Framework.Update -= OnUpdate;
        }
        windowSystem.RemoveAllWindows();

        // Game closing (or plugin turned off): push what changed, capped so exit isn't held up long.
        var closeGroups = AutoGroups(Configuration.AutoSync && Configuration.SyncOnClose);
        if (closeGroups != SyncGroups.None && Syncer.Configured)
        {
            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                Task.Run(() => Syncer.Run(SyncMode.Closing, cts.Token, groups: closeGroups)).Wait(TimeSpan.FromSeconds(9));
            }
            catch (Exception e) { Log.Warning(e, "Settings Sync: push on close failed"); }
        }
        // Dalamud settings pulled this session: merged in by a helper once the game has exited.
        try { Syncer.Dalamud.ScheduleApply(Syncer.PendingDalamudConfig()); }
        catch (Exception e) { Log.Warning(e, "Settings Sync: couldn't schedule the Dalamud settings update"); }
        life.Cancel();
        Syncer.Dispose();
        life.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "now": SyncNow(); break;
            case "push": SyncNow(pushOnly: true); break;
            case "pull": SyncNow(pullOnly: true); break;
            default: ToggleMain(); break;
        }
    }

    public void ToggleMain() => mainWindow.Toggle();

    public void OpenConflicts() { conflictWindow.Dismissed = false; conflictWindow.IsOpen = true; }
}
