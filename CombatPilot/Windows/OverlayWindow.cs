using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CombatPilot.Windows;

/// <summary>Small always-on panel: mode, the next GCD it wants, and a pause button.</summary>
public sealed class OverlayWindow : Window
{
    private readonly Plugin plugin;
    private static readonly Vector4 Live = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.7f, 0.3f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    public OverlayWindow(Plugin plugin) : base("Combat Pilot###CombatPilotOverlay")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;
    }

    public override bool DrawConditions() => plugin.Pilot.Job != null && plugin.Pilot.Mode != PilotMode.Off;

    public override void Draw()
    {
        var p = plugin.Pilot;
        var color = p.Paused || p.SafeMode ? Warn : p.Mode == PilotMode.Live ? Live : Dim;
        ImGui.TextColored(color, $"{p.Job!.Def.Job}: {p.Status}");
        if (p.LastGcd is { } d)
        {
            ImGui.TextUnformatted($"Next: {d.Label}");
            if (ImGui.IsItemHovered() && d.Path.Count > 0) ImGui.SetTooltip(string.Join(" > ", d.Path));
        }
        if (p.Mode == PilotMode.Live)
        {
            if (ImGui.SmallButton(p.Paused ? "Resume" : "Pause")) p.SetPaused(!p.Paused, "overlay button");
            ImGui.SameLine();
        }
        if (ImGui.SmallButton("Open")) plugin.OpenMain();
    }
}
