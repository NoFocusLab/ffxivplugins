using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace SettingsSync;

/// <summary>
/// Brings plugin windows that are off the screen back to the middle. Positions synced from a PC with a
/// bigger or differently arranged screen can put a window where it can't be reached. All plugins draw
/// in Dalamud's one ImGui context, so every open window (any plugin) can be checked and moved.
/// A window counts as off screen when less than a grabbable strip of its title bar is visible.
/// </summary>
public sealed class WindowRescue
{
    private const float MinVisibleWidth = 60f;   // px of title bar that must be on screen
    private const float TitleHeight = 20f;

    private DateTime nextCheck = DateTime.MinValue;
    private readonly Dictionary<string, int> attempts = new();

    /// <summary>Windows that keep going back off screen: their plugin sets the position itself every frame.</summary>
    public HashSet<string> Stubborn { get; } = new();

    /// <summary>Called once per frame from the plugin's draw; checks about once a second.</summary>
    public unsafe void Tick(Action<string> moved)
    {
        if (DateTime.Now < nextCheck) return;
        nextCheck = DateTime.Now.AddSeconds(1);

        var vp = ImGui.GetMainViewport();
        var screenMin = vp.Pos;
        var screenMax = vp.Pos + vp.Size;
        if (vp.Size.X < 200 || vp.Size.Y < 200) return;

        ref var windows = ref ImGui.GetCurrentContext().Windows;
        for (var i = 0; i < windows.Size; i++)
        {
            var w = windows[i];
            if (w.IsNull || !w.WasActive || w.Hidden) continue;
            var flags = w.Flags;
            if ((flags & (ImGuiWindowFlags.ChildWindow | ImGuiWindowFlags.Tooltip | ImGuiWindowFlags.Popup)) != 0) continue;

            var pos = w.Pos;
            var size = w.Size;
            // Visible part of the title strip (or the whole window, if it has no title bar).
            var stripBottom = (flags & ImGuiWindowFlags.NoTitleBar) != 0 ? pos.Y + size.Y : pos.Y + TitleHeight;
            var visibleW = MathF.Min(pos.X + size.X, screenMax.X) - MathF.Max(pos.X, screenMin.X);
            var visibleH = MathF.Min(stripBottom, screenMax.Y) - MathF.Max(pos.Y, screenMin.Y);
            if (visibleW >= MathF.Min(MinVisibleWidth, size.X) && visibleH >= MathF.Min(TitleHeight / 2, size.Y)) continue;

            var name = Marshal.PtrToStringUTF8((IntPtr)w.Name) ?? "";
            if (name.Length == 0 || Stubborn.Contains(name)) continue;
            var tries = attempts.GetValueOrDefault(name) + 1;
            attempts[name] = tries;
            if (tries > 3) { Stubborn.Add(name); continue; }   // its plugin keeps moving it back

            var fit = Vector2.Min(size, vp.Size);
            ImGuiP.SetWindowPos(w, screenMin + (vp.Size - fit) / 2, ImGuiCond.Always);
            moved(Label(name));
        }
    }

    /// <summary>"Material Stash###MsMain" -> "Material Stash".</summary>
    public static string Label(string name)
    {
        var cut = name.IndexOf("##", StringComparison.Ordinal);
        var label = cut >= 0 ? name[..cut] : name;
        return label.Length > 0 ? label : name.TrimStart('#');
    }
}
