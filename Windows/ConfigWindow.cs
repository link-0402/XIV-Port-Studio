using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows;

/// <summary>Settings that apply across every item: defaults for new content, mod metadata, and layout.</summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    public ConfigWindow(Plugin plugin) : base(
        "XIV Port Studio — Settings###XPSConfig",
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = _plugin.Configuration;
        bool changed = false;
        ImGui.PushItemWidth(Theme.S(280));

        Ui.SectionHeader("New textures");
        bool bc7 = cfg.DefaultCompressBc7;
        if (ImGui.Checkbox("Compress new textures to BC7", ref bc7))
        {
            cfg.DefaultCompressBc7 = bc7;
            changed = true;
        }
        Ui.Tooltip("Starting value of the BC7 box on texture slots you add from now on. Existing slots keep their setting.");

        bool thumbs = cfg.ShowThumbnails;
        if (ImGui.Checkbox("Show texture previews", ref thumbs))
        {
            cfg.ShowThumbnails = thumbs;
            changed = true;
        }
        Ui.Tooltip("Small previews of each texture's source in the material inspector.");

        ImGui.Spacing();
        Ui.SectionHeader("Mod metadata", "Written into every mod you build. Name and description are set per item on the Item stage.");
        string author = cfg.ModAuthor, version = cfg.ModVersion, website = cfg.ModWebsite;
        changed |= Ui.LabeledInput("Author",  "##CfgAuthor",  ref author,  128);
        changed |= Ui.LabeledInput("Version", "##CfgVersion", ref version, 32);
        changed |= Ui.LabeledInput("Website", "##CfgWebsite", ref website, 256, "https://…");
        cfg.ModAuthor  = author;
        cfg.ModVersion = version;
        cfg.ModWebsite = website;

        ImGui.Spacing();
        Ui.SectionHeader("Layout");
        if (Ui.IconTextButton(FontAwesomeIcon.Columns, "Reset pane widths"))
        {
            cfg.BrowserWidth   = Theme.DefaultBrowserWidth;
            cfg.InspectorWidth = Theme.DefaultInspectorWidth;
            changed = true;
        }

        ImGui.Spacing();
        Ui.SectionHeader("About");
        bool penumbra = _plugin.PenumbraIpc.IsAvailable;
        Ui.Icon(FontAwesomeIcon.Circle, penumbra ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.TextUnformatted(penumbra ? "Penumbra connected (IPC v5)" : "Penumbra not available");
        Ui.Tooltip("Penumbra is used to find its mod folder and to register the mods you build.");
        Ui.HintWrapped("Textures are converted to .tex fully in-process: uncompressed textures use the game's B8G8R8A8 " +
                       "format, and BC7 compression runs inside the plugin.");

        ImGui.PopItemWidth();
        ImGui.Dummy(new Vector2(Theme.S(420), 0));

        if (changed)
            cfg.Save();
    }
}
