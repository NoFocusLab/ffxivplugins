using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PluginHub.Windows;

namespace PluginHub;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/hub";
    public const string OwnInternalName = "PluginHub";

    public Configuration Configuration { get; }
    public IconCache Icons { get; }

    private readonly WindowSystem windows = new("PluginHub");
    private readonly ConfigWindow configWindow;
    private readonly Dictionary<Guid, BarWindow> barWindows = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Seed();
        Icons = new IconCache(Configuration);
        configWindow = new ConfigWindow(this);
        windows.AddWindow(configWindow);
        SyncBarWindows();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Plugin Hub settings. /hub show | hide (all bars) | lock | unlock (all bars)",
        });
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
    }

    public void ToggleConfig() => configWindow.Toggle();

    /// <summary>All installed plugins, by InternalName (first wins if a dev copy duplicates one).</summary>
    public static Dictionary<string, IExposedPlugin> InstalledByName()
    {
        var map = new Dictionary<string, IExposedPlugin>(StringComparer.Ordinal);
        foreach (var p in PluginInterface.InstalledPlugins)
            if (!map.TryGetValue(p.InternalName, out var have) || (!have.IsLoaded && p.IsLoaded)) map[p.InternalName] = p;
        return map;
    }

    /// <summary>First run: one bar with the NoFocusLab plugins that are installed, plus the installer.</summary>
    private void Seed()
    {
        if (Configuration.Seeded) return;
        var bar = new Bar { Name = "Plugins" };
        foreach (var p in PluginInterface.InstalledPlugins
                     .Where(p => p.InternalName != OwnInternalName &&
                                 string.Equals(p.Manifest.Author, "NoFocusLab", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(p => p.InternalName).Select(g => g.First())
                     .OrderBy(p => p.Name))
            bar.Tiles.Add(new Tile { Target = p.InternalName });
        bar.Tiles.Add(new Tile { Target = Targets.Installer });
        bar.Tiles.Add(new Tile { Target = Targets.HubSettings });
        Configuration.Bars.Add(bar);
        Configuration.Seeded = true;
        Configuration.Save();
    }

    /// <summary>Keep one window per bar after bars are added or removed.</summary>
    public void SyncBarWindows()
    {
        var ids = Configuration.Bars.Select(b => b.Id).ToHashSet();
        foreach (var gone in barWindows.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            windows.RemoveWindow(barWindows[gone]);
            barWindows.Remove(gone);
        }
        foreach (var bar in Configuration.Bars.Where(b => !barWindows.ContainsKey(b.Id)))
        {
            var w = new BarWindow(this, bar.Id);
            barWindows[bar.Id] = w;
            windows.AddWindow(w);
        }
    }

    private void Draw()
    {
        windows.Draw();
        if (pendingSync) { pendingSync = false; SyncBarWindows(); }
    }

    private bool pendingSync;

    /// <summary>Bars changed during drawing: rebuild the windows after this frame.</summary>
    public void RequestSync() => pendingSync = true;

    // ---------------------------------------------------------------- tile actions

    public void LeftClick(Tile tile, IExposedPlugin? plugin)
    {
        if (!string.IsNullOrWhiteSpace(tile.Command)) { RunCommand(tile.Command); return; }
        if (Targets.IsBuiltIn(tile.Target)) { OpenBuiltIn(tile.Target); return; }
        if (plugin == null) { PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, tile.Target); return; }
        if (!plugin.IsLoaded) { ChatGui.PrintError($"{plugin.Name} is turned off. Shift+click its tile to turn it on."); return; }
        try
        {
            if (plugin.HasMainUi) plugin.OpenMainUi();
            else if (plugin.HasConfigUi) plugin.OpenConfigUi();
            else ChatGui.Print($"{plugin.Name} has no window. Give its tile a command in /hub.");
        }
        catch (Exception e) { Log.Warning($"Plugin Hub: couldn't open {plugin.Name}: {e.Message}"); }
    }

    public void RightClick(Tile tile, IExposedPlugin? plugin)
    {
        if (Targets.IsBuiltIn(tile.Target)) { OpenBuiltIn(tile.Target); return; }
        if (plugin == null || !plugin.IsLoaded) { PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, plugin?.Name ?? tile.Target); return; }
        try
        {
            if (plugin.HasConfigUi) plugin.OpenConfigUi();
            else if (plugin.HasMainUi) plugin.OpenMainUi();
            else PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, plugin.Name);
        }
        catch (Exception e) { Log.Warning($"Plugin Hub: couldn't open {plugin.Name} settings: {e.Message}"); }
    }

    public void ToggleLoaded(IExposedPlugin? plugin)
    {
        if (plugin == null) return;
        if (plugin.InternalName == OwnInternalName) { ChatGui.PrintError("Plugin Hub can't turn itself off from its own bar."); return; }
        // Dalamud's own command; it reports in chat (and can't toggle a plugin that's in more than one collection).
        CommandManager.ProcessCommand($"/xltoggleplugin {plugin.InternalName}");
    }

    private void OpenBuiltIn(string target)
    {
        switch (target)
        {
            case Targets.Installer: PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins); break;
            case Targets.Updates: PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins); break;
            case Targets.DalamudSettings: PluginInterface.OpenDalamudSettingsTo(SettingsOpenKind.General); break;
            case Targets.HubSettings: ToggleConfig(); break;
        }
    }

    private static void RunCommand(string command)
    {
        var cmd = command.Trim();
        if (!cmd.StartsWith('/')) cmd = "/" + cmd;
        // Plugin and Dalamud commands only; game text commands (/gearset, macros) need the game's chat box.
        if (!CommandManager.ProcessCommand(cmd))
            ChatGui.PrintError($"Plugin Hub: {cmd.Split(' ')[0]} isn't a plugin command (game commands like /gearset aren't supported on tiles).");
    }

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "show": SetAll(b => b.Visible = true); return;
            case "hide": SetAll(b => b.Visible = false); return;
            case "lock": SetAll(b => b.Locked = true); return;
            case "unlock": SetAll(b => b.Locked = false); return;
            default: ToggleConfig(); return;
        }
    }

    private void SetAll(Action<Bar> change)
    {
        foreach (var b in Configuration.Bars) change(b);
        Configuration.Save();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        Icons.Dispose();
    }
}
