using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PluginHub.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private Guid selected;
    private string search = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Plugin Hub###PluginHubConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 460), MaximumSize = new Vector2(1400, 1400) };
    }

    private Configuration C => plugin.Configuration;

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##hubtabs")) return;
        if (ImGui.BeginTabItem("Bars")) { DrawBars(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Options")) { DrawOptions(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ Bars

    private void DrawBars()
    {
        if (C.Bars.All(b => b.Id != selected)) selected = C.Bars.FirstOrDefault()?.Id ?? Guid.Empty;

        ImGui.BeginChild("##barlist", new Vector2(160, 0), true);
        foreach (var b in C.Bars)
            if (ImGui.Selectable($"{b.Name}{(b.Visible ? "" : " (hidden)")}##{b.Id}", b.Id == selected)) selected = b.Id;
        ImGui.Spacing();
        if (ImGui.Button("Add bar", new Vector2(-1, 0)))
        {
            var bar = new Bar { Name = $"Bar {C.Bars.Count + 1}" };
            C.Bars.Add(bar);
            selected = bar.Id;
            C.Save();
            plugin.RequestSync();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##bar", Vector2.Zero, false);
        var sel = C.Bars.FirstOrDefault(b => b.Id == selected);
        if (sel == null) ImGui.TextDisabled("Add a bar to start.");
        else DrawBar(sel);
        ImGui.EndChild();
    }

    private void DrawBar(Bar bar)
    {
        var changed = false;
        var name = bar.Name;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Name", ref name, 40)) { bar.Name = name; changed = true; }
        ImGui.SameLine(0, 20);
        if (ImGui.Button("Delete bar") && ImGui.GetIO().KeyCtrl)
        {
            C.Bars.Remove(bar);
            C.Save();
            plugin.RequestSync();
            return;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hold Ctrl and click to delete this bar.");

        var visible = bar.Visible; if (ImGui.Checkbox("Show", ref visible)) { bar.Visible = visible; changed = true; }
        ImGui.SameLine();
        var locked = bar.Locked; if (ImGui.Checkbox("Lock (no moving or dragging tiles)", ref locked)) { bar.Locked = locked; changed = true; }
        var title = bar.ShowTitleBar; if (ImGui.Checkbox("Title bar", ref title)) { bar.ShowTitleBar = title; changed = true; }
        ImGui.SameLine();
        var bg = bar.ShowBackground; if (ImGui.Checkbox("Background", ref bg)) { bar.ShowBackground = bg; changed = true; }

        var cols = bar.Columns;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Columns", ref cols, 1, 24)) { bar.Columns = cols; changed = true; }
        var tileSize = bar.TileSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Tile size", ref tileSize, 20, 96)) { bar.TileSize = tileSize; changed = true; }

        ImGui.Separator();
        ImGui.TextUnformatted("Tiles");
        ImGui.TextDisabled("Optional per tile: a label (hover text / name tile) and a command run on click instead of opening the window.");
        DrawTiles(bar, ref changed);

        ImGui.Separator();
        DrawAdd(bar, ref changed);

        if (changed) C.Save();
    }

    private void DrawTiles(Bar bar, ref bool changed)
    {
        var installed = Plugin.InstalledByName();
        var height = Math.Min(260, 30 + bar.Tiles.Count * 30);
        if (!ImGui.BeginTable("##tiles", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
                new Vector2(0, height))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 26);
        ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableHeadersRow();

        int? remove = null, up = null, down = null;
        for (var i = 0; i < bar.Tiles.Count; i++)
        {
            var tile = bar.Tiles[i];
            installed.TryGetValue(tile.Target, out var p);
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var tex = plugin.Icons.Get(p, tile.Target);
            if (tex != null) ImGui.Image(tex.Handle, new Vector2(22));
            else ImGui.Dummy(new Vector2(22));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            var display = BarWindow.TileName(new Tile { Target = tile.Target }, p);
            if (p == null && !Targets.IsBuiltIn(tile.Target)) ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1), $"{display} (not installed)");
            else if (p is { IsLoaded: false }) ImGui.TextDisabled($"{display} (off)");
            else ImGui.TextUnformatted(display);

            ImGui.TableNextColumn();
            var label = tile.Label;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##label", "(plugin name)", ref label, 40)) { tile.Label = label; changed = true; }

            ImGui.TableNextColumn();
            var command = tile.Command;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##cmd", "(open window)", ref command, 120)) { tile.Command = command; changed = true; }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("^") && i > 0) up = i;
            ImGui.SameLine();
            if (ImGui.SmallButton("v") && i < bar.Tiles.Count - 1) down = i;
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) remove = i;
            ImGui.PopID();
        }
        ImGui.EndTable();

        if (up is { } u) { (bar.Tiles[u - 1], bar.Tiles[u]) = (bar.Tiles[u], bar.Tiles[u - 1]); changed = true; }
        if (down is { } d) { (bar.Tiles[d + 1], bar.Tiles[d]) = (bar.Tiles[d], bar.Tiles[d + 1]); changed = true; }
        if (remove is { } r) { bar.Tiles.RemoveAt(r); changed = true; }
    }

    private void DrawAdd(Bar bar, ref bool changed)
    {
        ImGui.TextUnformatted("Add to this bar");
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##search", "Search installed plugins", ref search, 60);

        var onBar = bar.Tiles.Select(t => t.Target).ToHashSet();
        var rows = Targets.All.Select(t => (Id: t.Id, Name: t.Name, Note: "built in", Loaded: true))
            .Concat(Plugin.InstalledByName().Values
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => (Id: p.InternalName, Name: p.Name, Note: p.IsLoaded ? p.Manifest.Author : "off", Loaded: p.IsLoaded)))
            .Where(r => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        r.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ImGui.BeginChild("##addlist", new Vector2(0, Math.Max(120, ImGui.GetContentRegionAvail().Y)), true);
        foreach (var row in rows)
        {
            ImGui.PushID(row.Id);
            var already = onBar.Contains(row.Id);
            if (already) ImGui.BeginDisabled();
            if (ImGui.SmallButton(already ? "On bar" : "Add"))
            {
                bar.Tiles.Add(new Tile { Target = row.Id });
                changed = true;
            }
            if (already) ImGui.EndDisabled();
            ImGui.SameLine();
            if (row.Loaded) ImGui.TextUnformatted(row.Name); else ImGui.TextDisabled(row.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"({row.Note})");
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    // ================================================================ Options

    private void DrawOptions()
    {
        var changed = false;
        var shift = C.ShiftClickToggles;
        if (ImGui.Checkbox("Shift+click a tile turns its plugin on or off", ref shift)) { C.ShiftClickToggles = shift; changed = true; }
        ImGui.TextDisabled("Uses Dalamud's /xltoggleplugin. A plugin that's in more than one collection can't be toggled this way.");

        var show = C.ShowDisabledPlugins;
        if (ImGui.Checkbox("Show tiles of plugins that are turned off (dimmed)", ref show)) { C.ShowDisabledPlugins = show; changed = true; }

        var dl = C.DownloadIcons;
        if (ImGui.Checkbox("Download other plugins' icons", ref dl)) { C.DownloadIcons = dl; changed = true; }
        ImGui.TextDisabled("Fetched once from the icon link in each plugin's listing and kept in the hub's config folder.\n" +
                           "Plugins without an icon show their name instead.");
        if (ImGui.Button("Re-download icons")) plugin.Icons.Clear();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Using the bars");
        ImGui.BulletText("Click: open the plugin's window (or run the tile's command).");
        ImGui.BulletText("Right-click: the plugin's settings.");
        ImGui.BulletText("Drag a tile onto another to move it, also between bars (bar must be unlocked).");
        ImGui.BulletText("Right-click an empty spot on a bar: lock, hide, settings.");
        ImGui.BulletText("/hub show | hide | lock | unlock applies to all bars.");

        if (changed) C.Save();
    }
}
