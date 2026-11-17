using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;

namespace PluginHub;

/// <summary>
/// Finds a picture for a tile: our own plugins' icons are built in; other plugins' icons are downloaded once
/// from the icon link in their listing and kept in the hub's config folder. No icon -> null (draw the name).
/// </summary>
public sealed class IconCache : IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ConcurrentDictionary<string, byte> pending = new();
    private readonly ConcurrentDictionary<string, byte> failed = new();
    private readonly Assembly assembly = Assembly.GetExecutingAssembly();
    private readonly string folder;
    private readonly Configuration config;

    public IconCache(Configuration config)
    {
        this.config = config;
        folder = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "icons");
        Directory.CreateDirectory(folder);
    }

    public IDalamudTextureWrap? Get(IExposedPlugin? plugin, string target)
    {
        // The installer tile shows Dalamud's own logo, read from the user's Dalamud install (not shipped
        // with us); our puzzle-piece icon is the fallback if the file isn't there.
        if (target == Targets.Installer && DalamudLogo() is { } logo)
            return logo;

        if (BuiltIn(target, plugin) is { } resource)
            return Plugin.TextureProvider.GetFromManifestResource(assembly, resource).TryGetWrap(out var own, out _) ? own : null;

        if (plugin == null || !config.DownloadIcons) return null;
        var url = plugin.Manifest.IconUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;

        var file = Path.Combine(folder, $"{Safe(target)}-{Hash(url)}.png");
        if (File.Exists(file))
        {
            if (failed.ContainsKey(file)) return null;
            if (Plugin.TextureProvider.GetFromFile(new FileInfo(file)).TryGetWrap(out var tex, out var ex)) return tex;
            if (ex != null) failed.TryAdd(file, 0); // not an image Dalamud can read (e.g. webp): fall back to the name
            return null;
        }

        if (!failed.ContainsKey(file) && pending.TryAdd(file, 0)) _ = Download(uri, file);
        return null;
    }

    /// <summary>
    /// An icon shipped inside the hub, if there is one for this tile: the built-in tiles (installer,
    /// updates, Dalamud settings, hub settings), our own plugins, and stand-ins for other plugins that
    /// have no usable icon (PluginHub/Icons/&lt;InternalName&gt;.png, e.g. Dagobert). These win over downloads.
    /// </summary>
    private string? BuiltIn(string target, IExposedPlugin? plugin)
    {
        var name = target switch
        {
            Targets.HubSettings => Plugin.OwnInternalName,
            Targets.Installer => "Extra.Installer",
            Targets.Updates => "Extra.Updates",
            Targets.DalamudSettings => "Extra.DalamudSettings",
            _ => null,
        };
        if (name == null)
        {
            var own = plugin == null || string.Equals(plugin.Manifest.Author, "NoFocusLab", StringComparison.OrdinalIgnoreCase);
            if (own && Has($"PluginHub.Icons.{target}.png")) name = target;
            else if (Has($"PluginHub.Icons.Extra.{target}.png")) name = $"Extra.{target}";
        }
        return name != null && Has($"PluginHub.Icons.{name}.png") ? $"PluginHub.Icons.{name}.png" : null;
    }

    private IDalamudTextureWrap? DalamudLogo()
    {
        try
        {
            var file = new FileInfo(Path.Combine(Plugin.PluginInterface.DalamudAssetDirectory.FullName, "UIRes", "logo.png"));
            if (!file.Exists) return null;
            return Plugin.TextureProvider.GetFromFile(file).TryGetWrap(out var tex, out _) ? tex : null;
        }
        catch
        {
            return null;
        }
    }

    private bool Has(string resource) => assembly.GetManifestResourceInfo(resource) != null;

    private async Task Download(Uri uri, string file)
    {
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("icon too large");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxBytes) throw new InvalidDataException("icon too large");
            var tmp = file + ".part";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, file, true);
        }
        catch (Exception e)
        {
            failed.TryAdd(file, 0);
            Plugin.Log.Debug($"Plugin Hub: no icon from {uri}: {e.Message}");
        }
        finally
        {
            pending.TryRemove(file, out _);
        }
    }

    /// <summary>Forget download failures and delete saved icons, so they're fetched again.</summary>
    public void Clear()
    {
        failed.Clear();
        foreach (var f in Directory.GetFiles(folder))
        {
            try { File.Delete(f); } catch { /* in use; replaced next time */ }
        }
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..10];

    public void Dispose() => http.Dispose();
}
