using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace PluginHub;

/// <summary>One button on a bar.</summary>
[Serializable]
public class Tile
{
    /// <summary>The plugin's InternalName, or one of the built-in targets (<see cref="Targets"/>).</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Optional: a command run on left-click instead of opening the plugin's window (e.g. "/vnav").</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Optional: a label used for hover text and name tiles instead of the plugin's name.</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>A hotbar-style window of tiles.</summary>
[Serializable]
public class Bar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Plugins";
    public int Columns { get; set; } = 6;
    public int TileSize { get; set; } = 40;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; } = false;
    public bool ShowTitleBar { get; set; } = false;
    public bool ShowBackground { get; set; } = true;
    public List<Tile> Tiles { get; set; } = new();
}

/// <summary>Targets that aren't plugins.</summary>
public static class Targets
{
    public const string Installer = "@installer";
    public const string Updates = "@updates";
    public const string DalamudSettings = "@settings";
    public const string HubSettings = "@hub";

    public static readonly (string Id, string Name, string Help)[] All =
    {
        (Installer, "Plugin installer", "Opens the plugin installer at your installed plugins."),
        (Updates, "Plugin updates", "Opens the plugin installer at plugins with updates."),
        (DalamudSettings, "Dalamud settings", "Opens Dalamud's settings."),
        (HubSettings, "Plugin Hub settings", "Opens these bars' settings."),
    };

    public static bool IsBuiltIn(string target) => target.StartsWith('@');
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<Bar> Bars { get; set; } = new();

    /// <summary>Shift+click a tile turns its plugin on or off (Dalamud's /xltoggleplugin).</summary>
    public bool ShiftClickToggles { get; set; } = true;

    /// <summary>Download and show third-party plugins' own icons.</summary>
    public bool DownloadIcons { get; set; } = true;

    /// <summary>Dim tiles of plugins that are installed but turned off.</summary>
    public bool ShowDisabledPlugins { get; set; } = true;

    /// <summary>Set after the first run filled in a starter bar.</summary>
    public bool Seeded { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
