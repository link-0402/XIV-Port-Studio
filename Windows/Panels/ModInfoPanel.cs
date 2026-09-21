using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Stage 1 — the modpack itself: its name, description, author, version and website
/// (written into meta.json), and a summary of what is in it. One build produces one mod
/// from every item in the pack.
/// </summary>
internal sealed class ModInfoPanel : IStagePanel
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;

    public ModInfoPanel(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
    }

    public StageId         Id      => StageId.ModInfo;
    public string          Title   => "Mod Info";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.InfoCircle;
    public string          Purpose => "The mod's name, description and the items it's built from.";

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        var cfg = _plugin.Configuration;

        Ui.SectionHeader("Mod", "Written into the mod's meta.json. One build makes one mod out of every item below.");

        string modName = _session.ModName;
        if (Ui.LabeledInput("Name", "##ModName", ref modName, 128, _session.DefaultModName(),
                "Name of the mod folder created under Penumbra's mod directory."))
        {
            _session.ModName = modName;
            _session.MarkDirty();
        }

        Ui.Label("Description");
        string description = _session.ModDescription;
        if (ImGui.InputTextMultiline("##ModDescription", ref description, 2048,
                new Vector2(-1, ImGui.GetTextLineHeight() * 4 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            _session.ModDescription = description;
            _session.MarkDirty();
        }
        if (string.IsNullOrEmpty(description))
            Ui.Tooltip($"Empty: \"Port of {_session.ModName}\" is used.");

        string author = cfg.ModAuthor, version = cfg.ModVersion, website = cfg.ModWebsite;
        bool changed = false;
        changed |= Ui.LabeledInput("Author",  "##ModAuthor",  ref author,  128, "", "Shared by every mod you build.");
        changed |= Ui.LabeledInput("Version", "##ModVersion", ref version, 32,  "1.0", "Shared by every mod you build.", Theme.S(120));
        changed |= Ui.LabeledInput("Website", "##ModWebsite", ref website, 256, "https://…", "Shared by every mod you build.");
        if (changed)
        {
            cfg.ModAuthor  = author;
            cfg.ModVersion = version;
            cfg.ModWebsite = website;
            _session.MarkDirty();   // saved with the next session flush
        }

        ImGui.Spacing();
        ImGui.Spacing();
        Ui.SectionHeader("In this modpack", "Add items on the Browse stage; set them up on the Details stage.");
        int models = _session.Items.Sum(i => i.Models.Count);
        int modelsSet = _session.Items.Sum(i => i.Models.Count(m => !string.IsNullOrWhiteSpace(m.SourcePath) || m.UseVariants));
        int materials = _session.Items.Sum(i => i.Materials.Count);
        int textures = _session.Items.Sum(i => i.Materials.Sum(m => m.Textures.Count));
        Row(FontAwesomeIcon.Boxes,   $"{_session.Items.Count} item(s)");
        Row(FontAwesomeIcon.Cube,    $"{modelsSet} of {models} race model(s) set");
        Row(FontAwesomeIcon.Palette, $"{materials} material(s), {textures} texture(s)");

        int errors   = PortValidator.Count(_session.Issues, StageId.Details, Severity.Error);
        int warnings = PortValidator.Count(_session.Issues, StageId.Details, Severity.Warning);
        ImGui.Spacing();
        if (errors + warnings > 0)
        {
            Ui.IssueBadge(errors, warnings);
            ImGui.SameLine();
        }
        if (ImGui.Button("Open Details"))
            _session.GoToStage(StageId.Details);
    }

    private static void Row(FontAwesomeIcon icon, string text)
    {
        Ui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        Ui.SectionHeader("Items", "Click one to open it on the Details stage.");
        if (_session.Items.Count == 0)
        {
            Ui.Hint("Nothing yet — add gear or a feature from the browser.");
            return;
        }

        foreach (var item in _session.Items)
        {
            ImGui.PushID((int)item.Key);
            Ui.Icon(item.Subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User,
                item.HasWork ? Theme.Accent : Theme.Muted);
            ImGui.SameLine();
            if (ImGui.Selectable(item.Subject.DisplayName))
                _session.Focus(new Target(TargetKind.Item, item.Key));
            Ui.Tooltip($"{item.Models.Count} race model(s), {item.Materials.Count} material(s)");
            ImGui.PopID();
        }
    }
}
