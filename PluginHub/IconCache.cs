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
        var resource = $"PluginHub.Icons.{target}.png";
        if (assembly.GetManifestResourceInfo(resource) != null &&
            (plugin == null || string.Equals(plugin.Manifest.Author, "NoFocusLab", StringComparison.OrdinalIgnoreCase)))
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
