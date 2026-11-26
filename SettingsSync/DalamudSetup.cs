using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SettingsSync;

/// <summary>
/// The "Dalamud setup" sync group. Turns parts of Dalamud's own setup into small virtual files so the
/// normal three-way sync (and its conflict window) handles them:
/// <list type="bullet">
/// <item><c>dalamud/config/&lt;Key&gt;.json</c>: chosen entries of dalamudConfig.json (repo list, favourites,
/// theme...). Dalamud reads that file before any plugin loads and rewrites it on exit, so pulled
/// entries are merged in by a small helper after the game has closed.</item>
/// <item><c>dalamud/plugins/&lt;InternalName&gt;</c>: whether each installed plugin is on. Applied live with
/// Dalamud's /xlenableplugin and /xldisableplugin.</item>
/// </list>
/// Plugins installed on another PC but not here can't be installed by a plugin; they're listed instead.
/// </summary>
public sealed class DalamudSetup
{
    public const string Prefix = "dalamud/";
    public const string ConfigPrefix = "dalamud/config/";
    public const string PluginPrefix = "dalamud/plugins/";

    /// <summary>dalamudConfig.json entries that are the same on every PC. Everything else stays per PC.</summary>
    public static readonly string[] ConfigKeys =
    {
        "ThirdRepoList",
        "FavoritePluginInternalName", "HiddenPluginInternalName", "PinnedPluginInternalName",
        "SavedStylesVersioned", "ChosenStyle",
        "DtrOrder", "DtrIgnore",
        "PluginAutoUpdatePreferences", "AutoUpdateBehavior",
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string configPath;   // ...\XIVLauncher\dalamudConfig.json
    private readonly string ownDir;

    /// <summary>On/off commands waiting for the game thread ("/xlenableplugin X").</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>States this PC just switched by command: reported as switched until Dalamud has saved them.</summary>
    private readonly ConcurrentDictionary<string, (bool On, DateTime Until)> recent = new(StringComparer.OrdinalIgnoreCase);

    public DalamudSetup(string xivLauncherDir, string ownDir)
    {
        configPath = Path.Combine(xivLauncherDir, "dalamudConfig.json");
        this.ownDir = ownDir;
    }

    public bool Available => File.Exists(configPath);

    // ------------------------------------------------------------------ reading this PC's setup

    private JsonObject? ReadConfig()
    {
        try
        {
            using var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(fs) as JsonObject;
        }
        catch (Exception e) { Plugin.Log.Warning(e, "Settings Sync: couldn't read dalamudConfig.json"); return null; }
    }

    private static byte[] ToBytes(JsonNode? node) => Encoding.UTF8.GetBytes((node?.ToJsonString(Pretty) ?? "null") + "\n");

    /// <summary>Virtual files for this PC: repo path -> contents.</summary>
    /// <param name="includePlugins">False while the game is closing: plugins are being unloaded, so "is it loaded" means nothing.</param>
    public Dictionary<string, byte[]> Read(Func<string, bool> excluded, bool includePlugins = true)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var cfg = ReadConfig();
        if (cfg == null) return result;

        foreach (var key in ConfigKeys)
            if (cfg.TryGetPropertyValue(key, out var node))
                result[$"{ConfigPrefix}{key}.json"] = ToBytes(node);

        if (!includePlugins) return result;

        // On/off wanted by Dalamud's default collection; plugins not in it fall back to "is it loaded".
        var want = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (cfg["DefaultProfile"]?["Plugins"]?["$values"] is JsonArray entries)
            foreach (var e in entries)
                if ((string?)e?["InternalName"] is { } n && e["IsEnabled"] is { } en)
                    want[n] = (bool)en;

        foreach (var p in Plugin.PluginInterface.InstalledPlugins)
        {
            if (p.IsDev || p.InternalName.Equals("SettingsSync", StringComparison.OrdinalIgnoreCase)) continue;
            var path = PluginPrefix + p.InternalName;
            if (excluded(path)) continue;
            var on = want.TryGetValue(p.InternalName, out var w) ? w : p.IsLoaded;
            if (recent.TryGetValue(p.InternalName, out var r))
            {
                if (DateTime.Now < r.Until && on != r.On) on = r.On;
                else recent.TryRemove(p.InternalName, out _);
            }
            result[path] = PluginState(p.Name, on);
        }
        return result;
    }

    public static byte[] PluginState(string name, bool on) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { name, enabled = on }) + "\n");

    public static (string Name, bool On) ParseState(byte[] data, string fallbackName)
    {
        try
        {
            var j = JsonDocument.Parse(data).RootElement;
            return (j.TryGetProperty("name", out var n) ? n.GetString() ?? fallbackName : fallbackName,
                    j.TryGetProperty("enabled", out var e) && e.GetBoolean());
        }
        catch { return (fallbackName, false); }
    }

    public bool IsInstalled(string internalName) =>
        Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ applying

    /// <summary>Queues /xlenableplugin or /xldisableplugin (run on the game thread by the plugin).</summary>
    public void SwitchPlugin(string internalName, bool on)
    {
        recent[internalName] = (on, DateTime.Now.AddMinutes(3));
        Commands.Enqueue($"/xl{(on ? "enable" : "disable")}plugin {internalName}");
    }

    /// <summary>
    /// Game closing: writes dalamudConfig.json with the pulled entries merged in, and starts a hidden
    /// helper that copies it into place once the game process has exited (Dalamud saves its own copy on
    /// exit, which would overwrite an earlier write). The current file is backed up first (last 5 kept).
    /// </summary>
    public bool ScheduleApply(IReadOnlyDictionary<string, byte[]> pendingConfig)
    {
        if (pendingConfig.Count == 0) return false;
        var cfg = ReadConfig();
        if (cfg == null) return false;
        foreach (var (path, data) in pendingConfig)
        {
            var key = Path.GetFileNameWithoutExtension(path[ConfigPrefix.Length..]);
            if (!ConfigKeys.Contains(key)) continue;
            cfg[key] = JsonNode.Parse(data);
        }

        var backups = Path.Combine(ownDir, "backups");
        Directory.CreateDirectory(backups);
        File.Copy(configPath, Path.Combine(backups, $"dalamudConfig-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
        foreach (var old in new DirectoryInfo(backups).GetFiles("dalamudConfig-*.json").OrderByDescending(f => f.Name).Skip(5))
            old.Delete();

        var merged = Path.Combine(ownDir, "dalamudConfig.merged.json");
        File.WriteAllText(merged, cfg.ToJsonString(Pretty), new UTF8Encoding(false));

        var pid = Environment.ProcessId;
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $"try {{ Wait-Process -Id {pid} -ErrorAction SilentlyContinue }} catch {{}}; Start-Sleep -Seconds 2; " +
                     $"Copy-Item -LiteralPath {Q(merged)} -Destination {Q(configPath)} -Force; " +
                     $"Remove-Item -LiteralPath {Q(merged)} -Force";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Plugin.Log.Info($"Settings Sync: {pendingConfig.Count} Dalamud setting(s) will be applied after the game closes.");
        return true;
    }
}
