using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SettingsSync;

/// <summary>Display name and on/off of a plugin as another PC last reported it.</summary>
public sealed class PluginInfo
{
    public string Name { get; set; } = "";
    public bool On { get; set; }
}

/// <summary>One line in the activity list: a push, pull, restore or conflict choice.</summary>
public sealed class ActivityEntry
{
    public DateTime When { get; set; }
    public string Kind { get; set; } = "";      // Push / Pull / Restore / Conflict / Error
    public string Detail { get; set; } = "";
}

/// <summary>
/// What this PC last agreed with GitHub on, kept in pluginConfigs/SettingsSync/state.json (never synced).
/// BaseShas is the git blob SHA of every synced file at the last successful sync; comparing it with
/// the local file and the remote file tells who changed what.
/// </summary>
public sealed class LocalState
{
    public string? LastCommit { get; set; }

    /// <summary>
    /// False on a PC that has never synced: nothing is uploaded until you confirm the first sync, so a
    /// half-set-up PC can't overwrite the others. PCs that synced before this existed count as done.
    /// </summary>
    public bool FirstSyncDone { get; set; }

    /// <summary>While the first sync is open: files you chose "keep this PC's copy" for, allowed up once.</summary>
    public HashSet<string> AllowedPushes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BaseShas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? LastPush { get; set; }
    public DateTime? LastPull { get; set; }
    public string? LastPullFrom { get; set; }
    public List<ActivityEntry> Activity { get; set; } = new();
    public Dictionary<string, PluginInfo> PluginInfo { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pulled while the game was running: applied at the next startup, before other plugins load.</summary>
    public Dictionary<string, string> PendingShas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string PathOf(string dir) => Path.Combine(dir, "state.json");

    public static LocalState Load(string dir)
    {
        try
        {
            var p = PathOf(dir);
            if (File.Exists(p))
            {
                var s = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(p));
                if (s != null)
                {
                    s.BaseShas = new Dictionary<string, string>(s.BaseShas, StringComparer.OrdinalIgnoreCase);
                    s.PendingShas = new Dictionary<string, string>(s.PendingShas, StringComparer.OrdinalIgnoreCase);
                    s.PluginInfo = new Dictionary<string, PluginInfo>(s.PluginInfo, StringComparer.OrdinalIgnoreCase);
                    s.AllowedPushes = new HashSet<string>(s.AllowedPushes, StringComparer.OrdinalIgnoreCase);
                    if (s.LastCommit != null) s.FirstSyncDone = true;
                    return s;
                }
            }
        }
        catch (Exception e) { Plugin.Log.Warning(e, "Settings Sync: state.json unreadable, starting fresh"); }
        return new LocalState();
    }

    private readonly object activityLock = new();

    /// <summary>A copy of the activity list, safe to read from the window while a sync adds to it.</summary>
    public List<ActivityEntry> ActivitySnapshot() { lock (activityLock) return Activity.ToList(); }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var tmp = PathOf(dir) + ".new";
        string json;
        lock (activityLock) json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, PathOf(dir), true);
    }

    public void Log(string kind, string detail)
    {
        lock (activityLock) LogLocked(kind, detail);
    }

    private void LogLocked(string kind, string detail)
    {
        Activity.Insert(0, new ActivityEntry { When = DateTime.Now, Kind = kind, Detail = detail });
        if (Activity.Count > 30) Activity.RemoveRange(30, Activity.Count - 30);
    }
}

/// <summary>
/// The GitHub sign-in, encrypted with Windows DPAPI for the current Windows user, in
/// pluginConfigs/SettingsSync/auth.bin (never synced). Another Windows account or PC can't decrypt it.
/// </summary>
public static class Secrets
{
    private static string PathOf(string dir) => Path.Combine(dir, "auth.bin");

    public static string? LoadToken(string dir)
    {
        try
        {
            var p = PathOf(dir);
            if (!File.Exists(p)) return null;
            var plain = Unprotect(File.ReadAllBytes(p));
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) { Plugin.Log.Warning(e, "Settings Sync: couldn't read sign-in"); return null; }
    }

    public static void SaveToken(string dir, string? token)
    {
        Directory.CreateDirectory(dir);
        var p = PathOf(dir);
        if (string.IsNullOrEmpty(token)) { if (File.Exists(p)) File.Delete(p); return; }
        File.WriteAllBytes(p, Protect(Encoding.UTF8.GetBytes(token)));
    }

    // ---- DPAPI (crypt32) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    private static byte[] Protect(byte[] data) =>
        Run(data, (ref DataBlob i, out DataBlob o) => CryptProtectData(ref i, "SettingsSync", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out o))
        ?? throw new InvalidOperationException("Windows couldn't encrypt the sign-in.");

    private static byte[]? Unprotect(byte[] data) =>
        Run(data, (ref DataBlob i, out DataBlob o) => CryptUnprotectData(ref i, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out o));

    private delegate bool Crypt(ref DataBlob input, out DataBlob output);

    private static byte[]? Run(byte[] data, Crypt fn)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            if (!fn(ref input, out var output)) return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}
