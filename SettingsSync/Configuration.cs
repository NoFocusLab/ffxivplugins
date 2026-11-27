using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace SettingsSync;

/// <summary>
/// Settings for this PC. Never synced itself (SettingsSync.json and the SettingsSync folder are
/// always excluded), so each PC keeps its own machine name, sign-in and switches.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>GitHub repository holding the settings, as "owner/name". Private repo you created.</summary>
    public string Repository { get; set; } = "";

    /// <summary>Client ID of your GitHub App (starts with Iv). Not a secret.</summary>
    public string ClientId { get; set; } = "";

    public string Branch { get; set; } = "main";

    /// <summary>Shown next to every push and pull ("Desktop", "Laptop").</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    // ---- Plugin settings group ----
    /// <summary>Master switch for automatic sync. Off = only the Sync now buttons do anything.</summary>
    public bool AutoSync { get; set; } = true;
    public bool SyncOnOpen { get; set; } = true;
    public bool SyncOnClose { get; set; } = true;
    /// <summary>While playing, push changed settings at most this often (minutes). 0 = off.</summary>
    public int PushWhilePlayingMinutes { get; set; } = 15;
    /// <summary>How long the game may wait at startup for the pull (seconds).</summary>
    public int StartupWaitSeconds { get; set; } = 6;

    // ---- Device type (e.g. a handheld) ----
    /// <summary>
    /// Empty = a normal PC (all plugin settings shared). A name such as "Handheld" makes this PC keep the
    /// plugins in <see cref="PerDevicePlugins"/> separate: shared only with PCs of the same type.
    /// </summary>
    public string DeviceType { get; set; } = "";
    /// <summary>Plugins (by settings name, e.g. "PluginHub") whose settings belong to this device type.</summary>
    public List<string> PerDevicePlugins { get; set; } = new();

    // ---- Dalamud setup group (repo list, plugin on/off, favourites, theme, info bar, update prefs) ----
    /// <summary>Sync Dalamud setup automatically: when the game starts, on logout/close, and with the periodic push.</summary>
    public bool DalamudAuto { get; set; } = true;
    /// <summary>Plugins from other PCs you don't want here: not listed as missing.</summary>
    public List<string> IgnoredMissing { get; set; } = new();

    /// <summary>
    /// Automatically move any plugin window that's off the screen (e.g. a position synced from a bigger
    /// screen) to the middle, every <see cref="RescueIntervalSeconds"/>. Off = only the "Check windows now"
    /// button (plus one check shortly after the game starts).
    /// </summary>
    public bool RescueOffscreenWindows { get; set; } = true;
    public int RescueIntervalSeconds { get; set; } = 15;

    /// <summary>At game start, send crash reports from the last session(s) to the repository's "logs" branch.</summary>
    public bool AutoSendCrashReports { get; set; } = true;

    /// <summary>Files bigger than this are never synced (caches, logs).</summary>
    public int MaxFileMb { get; set; } = 5;

    /// <summary>
    /// Paths (relative to pluginConfigs, with /) that are never synced. * = any characters except /,
    /// ** = anything including /, ? = one character. Case-insensitive.
    /// </summary>
    public List<string> Excludes { get; set; } = new(DefaultExcludes);

    public static readonly string[] DefaultExcludes =
    {
        "**/*.bak", "**/*.bak?", "**/*.v[0-9]*", "**/*_backup.json", "**/*.log", "**/*.tmp", "**/*.gz",
        "Claude outputs/**",
        "vnavmesh/meshcache/**",
        "CombatRecorder/logs/**",
        "MitPlanner/trashlog/**",
        "AutoHook/replays/**",
        "TurnInManager/dumps/**",
        "MaterialStash/dumps/**",
        "WahTools/logs/**",
        "InventoryTools/history.csv",
    };

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
