using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XIVPortStudio.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    public ConfigWindow(Plugin plugin) : base(
        "XIV Port Studio — Configuration###XPSConfig",
        ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar)
    {
        _plugin = plugin;
        Size          = new Vector2(420, 90);
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.TextWrapped(
            "XIV Port Studio helps you set up materials and textures for gear ported " +
            "from other games. Select an item, then build its material set on the main window.");

        ImGui.Spacing();

        var penumbra = _plugin.PenumbraIpc;
        ImGui.Text(penumbra.IsAvailable
            ? "Penumbra: connected (IPC v5)"
            : "Penumbra: not available");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Penumbra IPC is used to inspect mod folders and, later, to export the finished item as a mod.");
    }
}
