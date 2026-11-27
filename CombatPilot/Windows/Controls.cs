using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace CombatPilot.Windows;

/// <summary>
/// iOS-style controls drawn with ImGui: a sliding switch for on/off settings and a segmented
/// control for settings with a few choices. The knob slides over ~0.12 s.
/// </summary>
public static class Controls
{
    private static readonly Dictionary<string, float> knob = new();

    private static readonly uint OnColor = Rgba(52, 199, 89);        // iOS green
    private static readonly uint OffColor = Rgba(120, 120, 128, 140); // iOS grey
    private static readonly uint WarnColor = Rgba(255, 159, 10);     // iOS orange (held / paused)
    private static readonly uint KnobColor = Rgba(255, 255, 255);
    private static readonly uint KnobShadow = Rgba(0, 0, 0, 70);
    private static readonly uint TrackColor = Rgba(120, 120, 128, 70);
    private static readonly uint SegmentColor = Rgba(99, 99, 102);
    private static readonly uint TextOn = Rgba(255, 255, 255);
    private static readonly uint TextOff = Rgba(200, 200, 205);

    public static uint Rgba(int r, int g, int b, int a = 255) => (uint)((a << 24) | (b << 16) | (g << 8) | r);

    /// <summary>Label, then a sliding switch. Returns true when clicked (the caller flips the value).
    /// warnWhenOff: an "off" that matters (burst held, paused) shows orange instead of grey.</summary>
    public static bool Switch(string id, string label, bool on, string tip, bool warnWhenOff = false)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(0, 6);

        var h = ImGui.GetFrameHeight() * 0.8f;
        var w = h * 1.75f;
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - h) / 2);
        ImGui.InvisibleButton(id, new Vector2(w, ImGui.GetFrameHeight()));
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (ImGui.IsItemHovered() && tip.Length > 0) ImGui.SetTooltip(tip);

        // Slide the knob toward its side.
        var target = on ? 1f : 0f;
        var t = knob.TryGetValue(id, out var cur) ? cur : target;
        var step = ImGui.GetIO().DeltaTime / 0.12f;
        t = t < target ? Math.Min(target, t + step) : Math.Max(target, t - step);
        knob[id] = t;

        var draw = ImGui.GetWindowDrawList();
        var offCol = warnWhenOff ? WarnColor : OffColor;
        draw.AddRectFilled(pos, pos + new Vector2(w, h), Lerp(offCol, OnColor, t), h / 2);
        var r = h / 2 - 2;
        var center = pos + new Vector2(h / 2 + t * (w - h), h / 2);
        draw.AddCircleFilled(center + new Vector2(0, 1), r, KnobShadow);
        draw.AddCircleFilled(center, r, KnobColor);
        return clicked;
    }

    /// <summary>Label, then a segmented control (one segment selected; onIndex shows green, warnIndex orange). Returns the clicked index, or -1.</summary>
    public static int Segmented(string id, string label, string[] options, int selected, string tip, int onIndex = -1, int warnIndex = -1)
    {
        if (label.Length > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine(0, 6);
        }
        var h = ImGui.GetFrameHeight();
        var pad = 8f;
        var widths = new float[options.Length];
        var total = 0f;
        for (var i = 0; i < options.Length; i++) { widths[i] = ImGui.CalcTextSize(options[i]).X + pad * 2; total += widths[i]; }

        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + new Vector2(total + 4, h), TrackColor, h / 2);
        var clicked = -1;
        var x = pos.X + 2;
        for (var i = 0; i < options.Length; i++)
        {
            var a = new Vector2(x, pos.Y + 2);
            var b = new Vector2(x + widths[i], pos.Y + h - 2);
            ImGui.SetCursorScreenPos(a);
            ImGui.InvisibleButton($"{id}{i}", b - a);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) clicked = i;
            if (ImGui.IsItemHovered() && tip.Length > 0) ImGui.SetTooltip(tip);
            if (i == selected)
                draw.AddRectFilled(a, b, i == warnIndex ? WarnColor : i == onIndex ? OnColor : SegmentColor, (h - 4) / 2);
            var ts = ImGui.CalcTextSize(options[i]);
            draw.AddText(new Vector2(x + (widths[i] - ts.X) / 2, pos.Y + (h - ts.Y) / 2), i == selected ? TextOn : TextOff, options[i]);
            x += widths[i];
        }
        // Leave the cursor after the control, on the same line.
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(total + 4, h));
        return clicked;
    }

    private static uint Lerp(uint a, uint b, float t)
    {
        uint Ch(uint c, int s) => (c >> s) & 0xFF;
        uint Mix(int s) => (uint)Math.Round(Ch(a, s) + (Ch(b, s) - (float)Ch(a, s)) * t) << s;
        return Mix(0) | Mix(8) | Mix(16) | Mix(24);
    }
}
