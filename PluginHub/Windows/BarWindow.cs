using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace PluginHub.Windows;

/// <summary>One bar of tiles, drawn like a hotbar.</summary>
public sealed class BarWindow : Window
{
    private const string DragType = "PLUGINHUB_TILE";
    private static readonly byte[] DragData = { 1 };

    /// <summary>The tile being dragged (any bar).</summary>
    private static (Guid Bar, int Index)? dragging;

    private readonly Plugin plugin;
    private readonly Guid barId;
    private bool pushedStyle;

    public BarWindow(Plugin plugin, Guid barId) : base($"Plugin bar###PluginHubBar{barId}")
    {
        this.plugin = plugin;
        this.barId = barId;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    private Configuration C => plugin.Configuration;
    private Bar? Bar => C.Bars.FirstOrDefault(b => b.Id == barId);

    public override bool DrawConditions() => Bar is { Visible: true };

    public override void PreDraw()
    {
        var bar = Bar!;
        WindowName = $"{bar.Name}###PluginHubBar{barId}";
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollWithMouse;
        if (!bar.ShowTitleBar) Flags |= ImGuiWindowFlags.NoTitleBar;
        if (!bar.ShowBackground) Flags |= ImGuiWindowFlags.NoBackground;
        if (bar.Locked) Flags |= ImGuiWindowFlags.NoMove;
        ShowCloseButton = false;
        ImGui.SetNextWindowPos(new Vector2(60, 300 + 60 * C.Bars.IndexOf(bar)), ImGuiCond.FirstUseEver);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(3, 3));
        pushedStyle = true;
    }

    public override void PostDraw()
    {
        if (pushedStyle) ImGui.PopStyleVar(2);
        pushedStyle = false;
    }

    public override void Draw()
    {
        var bar = Bar;
        if (bar == null) return;
        var installed = Plugin.InstalledByName();
        var size = new Vector2(Math.Clamp(bar.TileSize, 16, 128));
        var columns = Math.Max(1, bar.Columns);

        var shown = 0;
        for (var i = 0; i < bar.Tiles.Count; i++)
        {
            var tile = bar.Tiles[i];
            installed.TryGetValue(tile.Target, out var p);
            if (p != null && !p.IsLoaded && !C.ShowDisabledPlugins) continue;
            if (shown++ % columns != 0) ImGui.SameLine();
            DrawTile(bar, i, tile, p, size);
        }

        if (shown == 0)
        {
            ImGui.Dummy(size);
            DropTarget(bar, 0);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"{bar.Name}: empty. /hub to add plugins.");
        }

        if (ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##barmenu");
        if (ImGui.BeginPopup("##barmenu"))
        {
            ImGui.TextDisabled(bar.Name);
            if (ImGui.MenuItem(bar.Locked ? "Unlock bar" : "Lock bar")) { bar.Locked = !bar.Locked; C.Save(); }
            if (ImGui.MenuItem("Hide bar")) { bar.Visible = false; C.Save(); }
            if (ImGui.MenuItem("Plugin Hub settings")) plugin.ToggleConfig();
            ImGui.EndPopup();
        }
    }

    private void DrawTile(Bar bar, int index, Tile tile, IExposedPlugin? p, Vector2 size)
    {
        var name = TileName(tile, p);
        var missing = p == null && !Targets.IsBuiltIn(tile.Target);
        var off = p is { IsLoaded: false };

        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var clicked = ImGui.InvisibleButton($"##tile{index}", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var rightClick = clicked && ImGui.IsMouseReleased(ImGuiMouseButton.Right);

        if (!bar.Locked && ImGui.BeginDragDropSource())
        {
            dragging = (bar.Id, index);
            ImGui.SetDragDropPayload(DragType, DragData);
            ImGui.TextUnformatted($"Move {name}");
            ImGui.EndDragDropSource();
        }
        DropTarget(bar, index);

        // Picture, or the name if there's no picture.
        var draw = ImGui.GetWindowDrawList();
        var rounding = size.X * 0.17f;
        var alpha = off || missing ? 0.35f : 1f;
        var tex = plugin.Icons.Get(p, tile.Target);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.12f, 0.92f)), rounding);
        if (tex != null)
        {
            draw.AddImageRounded(tex.Handle, min, max, Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(new Vector4(1, 1, 1, alpha)), rounding);
        }
        else
        {
            DrawName(draw, min, size, name, ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, alpha)));
        }
        if (missing) draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.9f, 0.3f, 0.3f, 0.9f)), rounding, ImDrawFlags.None, 2);
        if (hovered)
        {
            draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, ImGui.IsItemActive() ? 0.18f : 0.10f)), rounding);
            draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.7f)), rounding, ImDrawFlags.None, 1.5f);
            Tooltip(tile, p, name, missing, off);
        }

        if (!clicked) return;
        if (ImGui.GetIO().KeyShift && C.ShiftClickToggles && p != null && !rightClick) plugin.ToggleLoaded(p);
        else if (rightClick) plugin.RightClick(tile, p);
        else plugin.LeftClick(tile, p);
    }

    private void DropTarget(Bar bar, int index)
    {
        if (bar.Locked || !ImGui.BeginDragDropTarget()) return;
        var payload = ImGui.AcceptDragDropPayload(DragType);
        if (!payload.IsNull && dragging is { } from)
        {
            Move(from.Bar, from.Index, bar.Id, index);
            dragging = null;
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>Drop a tile onto another: it takes that tile's place (within a bar or across bars).</summary>
    private void Move(Guid fromBar, int fromIndex, Guid toBar, int toIndex)
    {
        var src = C.Bars.FirstOrDefault(b => b.Id == fromBar);
        var dst = C.Bars.FirstOrDefault(b => b.Id == toBar);
        if (src == null || dst == null || fromIndex < 0 || fromIndex >= src.Tiles.Count) return;
        var tile = src.Tiles[fromIndex];
        src.Tiles.RemoveAt(fromIndex);
        dst.Tiles.Insert(Math.Clamp(toIndex, 0, dst.Tiles.Count), tile);
        C.Save();
    }

    public static string TileName(Tile tile, IExposedPlugin? p)
    {
        if (!string.IsNullOrWhiteSpace(tile.Label)) return tile.Label;
        if (Targets.IsBuiltIn(tile.Target))
            {
            var builtIn = Targets.All.FirstOrDefault(t => t.Id == tile.Target).Name;
            return string.IsNullOrEmpty(builtIn) ? tile.Target : builtIn;
        }
        return p?.Name ?? tile.Target;
    }

    private void Tooltip(Tile tile, IExposedPlugin? p, string name, bool missing, bool off)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22);
        ImGui.TextUnformatted(name);
        string? about = Targets.IsBuiltIn(tile.Target)
            ? Targets.All.FirstOrDefault(t => t.Id == tile.Target).Help
            : p?.Manifest.Punchline;
        if (!string.IsNullOrWhiteSpace(about)) ImGui.TextDisabled(about);
        if (missing) ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1), "Not installed. Click to look for it in the plugin installer.");
        else if (off) ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1), "Turned off.");

        ImGui.Separator();
        if (!string.IsNullOrWhiteSpace(tile.Command)) ImGui.TextDisabled($"Click: {tile.Command}");
        else if (Targets.IsBuiltIn(tile.Target)) ImGui.TextDisabled("Click: open");
        else if (p is { IsLoaded: true })
            ImGui.TextDisabled(p.HasMainUi ? "Click: open" : p.HasConfigUi ? "Click: settings" : "Click: (no window; set a command in /hub)");
        if (p is { IsLoaded: true } && !Targets.IsBuiltIn(tile.Target))
            ImGui.TextDisabled(p.HasConfigUi ? "Right-click: settings" : "Right-click: show in installer");
        if (p != null && C.ShiftClickToggles && tile.Target != Plugin.OwnInternalName)
            ImGui.TextDisabled(p.IsLoaded ? "Shift+click: turn off" : "Shift+click: turn on");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>The name centred in the tile, wrapped by word; initials if it won't fit.</summary>
    private static void DrawName(ImDrawListPtr draw, Vector2 min, Vector2 size, string name, uint colour)
    {
        const float pad = 3;
        var inner = size.X - pad * 2;
        var lineH = ImGui.GetTextLineHeight();
        var lines = Wrap(name, inner);
        if (lines == null || lines.Count * lineH > size.Y - pad * 2) lines = new List<string> { Initials(name) };

        draw.PushClipRect(min, min + size, true);
        var y = min.Y + (size.Y - lines.Count * lineH) / 2;
        foreach (var line in lines)
        {
            var w = ImGui.CalcTextSize(line).X;
            draw.AddText(new Vector2(min.X + (size.X - w) / 2, y), colour, line);
            y += lineH;
        }
        draw.PopClipRect();
    }

    private static List<string>? Wrap(string text, float width)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ImGui.CalcTextSize(word).X > width) return null;
            var attempt = current.Length == 0 ? word : current + " " + word;
            if (ImGui.CalcTextSize(attempt).X <= width) { current = attempt; continue; }
            lines.Add(current);
            current = word;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static string Initials(string name)
    {
        var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var s = words.Length > 1
            ? string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])))
            : string.Concat(name.Where(char.IsUpper).Take(3));
        return s.Length > 0 ? s : name[..Math.Min(2, name.Length)];
    }
}
