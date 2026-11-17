using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MaterialStash.Windows;

/// <summary>
/// Opens when a Stash / Sell (or other bell) run ends: what went to which retainer and how many, and what was
/// listed where, at what price. The last few runs can be picked from the list at the top.
/// </summary>
public sealed class ReportWindow : Window
{
    private readonly Plugin plugin;
    private int shown; // index into History (0 = newest)

    public ReportWindow(Plugin plugin) : base("Stash results###MaterialStashResults")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 220), MaximumSize = new Vector2(1200, 1400) };
        Size = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Show the newest report.</summary>
    public void ShowLatest()
    {
        shown = 0;
        IsOpen = true;
    }

    public override void Draw()
    {
        var history = plugin.Runner.History;
        if (history.Count == 0) { ImGui.TextDisabled("No runs yet this session."); return; }
        shown = Math.Clamp(shown, 0, history.Count - 1);
        var r = history[shown];

        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo("##run", Label(r)))
        {
            for (var i = 0; i < history.Count; i++)
                if (ImGui.Selectable($"{Label(history[i])}##{i}", i == shown)) shown = i;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy")) ImGui.SetClipboardText(r.ToText());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy this report as text.");

        if (r.Summary.Length > 0) ImGui.TextWrapped(r.Summary);
        ImGui.Separator();

        ImGui.BeginChild("##report", Vector2.Zero, false);
        if (r.Moves.Count > 0) DrawMoves(r);
        if (r.Listings.Count > 0) DrawListings(r);
        if (r.Empty) ImGui.TextDisabled("Nothing was moved or listed.");
        ImGui.EndChild();
    }

    private static string Label(RunReport r) =>
        $"{r.Title}{(r.DuringAutoRetainer ? " (AutoRetainer)" : "")} - {r.Ended:t}";

    private static void DrawMoves(RunReport r)
    {
        foreach (var g in r.Moves.GroupBy(m => m.Retainer))
        {
            var putIn = g.Where(m => m.Qty > 0).Sum(m => m.Qty);
            var out_ = -g.Where(m => m.Qty < 0).Sum(m => m.Qty);
            var head = $"{g.Key}  ({(putIn > 0 ? $"{putIn:N0} in" : "")}{(putIn > 0 && out_ > 0 ? ", " : "")}{(out_ > 0 ? $"{out_:N0} out" : "")})";
            if (!ImGui.CollapsingHeader($"{head}##{g.Key}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            if (!ImGui.BeginTable($"##m{g.Key}", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) continue;
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60);
            foreach (var m in g.OrderByDescending(m => m.Qty > 0).ThenBy(m => RunReport.ItemName(m.ItemId)))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(RunReport.ItemName(m.ItemId));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{Math.Abs(m.Qty):N0}");
                ImGui.TableNextColumn();
                if (m.Qty > 0) ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1), "in");
                else ImGui.TextColored(new Vector4(1f, 0.75f, 0.4f, 1), "out");
            }
            ImGui.EndTable();
        }
    }

    private static void DrawListings(RunReport r)
    {
        var total = r.Listings.Sum(l => l.Qty * l.UnitPrice);
        if (!ImGui.CollapsingHeader($"Listed on the market: {r.Listings.Count} listing(s), {total:N0} gil##listed", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (!ImGui.BeginTable("##listings", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Each", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableHeadersRow();
        foreach (var l in r.Listings)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(l.Retainer);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(RunReport.ItemName(l.ItemId));
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{l.Qty:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{l.UnitPrice:N0}");
            ImGui.TableNextColumn(); ImGui.TextUnformatted($"{l.Qty * l.UnitPrice:N0}");
        }
        ImGui.EndTable();
        ImGui.TextDisabled($"Total {total:N0} gil before the market tax.");
    }
}
