using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Stage 1 — what is being replaced, where its files live, and the mod it becomes
/// (name, description, author). The inspector shows what the game already has for it:
/// which races ship their own model, and (gear) how many dye variants it has or
/// (features) which material names the vanilla model uses.
/// </summary>
internal sealed class ItemStagePanel : IStagePanel
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;

    // Per-subject facts read from game data, looked up once per subject.
    private uint _factsFor;
    private HashSet<RaceGender> _nativeRaces = new();
    private int _dyeVariants;
    private IReadOnlyList<string> _vanillaMaterials = new List<string>();

    public ItemStagePanel(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
    }

    public StageId         Id      => StageId.Item;
    public string          Title   => _session.Subject?.KindLabel ?? "Item";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Tshirt;
    public string          Purpose => "What you are replacing, and the details of the mod it becomes.";

    public void DrawCanvas()
    {
        var subject = _session.Subject;
        if (subject == null)
        {
            EmptyState();
            return;
        }

        Ui.SectionHeader(subject.DisplayName);
        if (subject is GearSubject gear)
        {
            Ui.LabeledValue("Slot", SlotInfo.DisplayLabelMap[gear.Item.Slot]);
            Ui.LabeledValue("Model", gear.Item.ModelIdDisplay, "Model set id and variant.");
            Ui.LabeledValue("Materials", subject.MaterialFolder(null),
                "Every .mtrl is written to the v0001 folder; dye variants are redirected to it.");
        }
        else if (subject is FeatureSubject feature)
        {
            Ui.LabeledValue("Race", feature.Race.DisplayName);
            Ui.LabeledValue("Id", feature.IdDisplay);
            Ui.LabeledValue("Model", feature.ModelGamePath(feature.Race));
            Ui.LabeledValue("Materials", feature.MaterialFolder(feature.Race),
                feature.MultiRace ? "Each race you add on the Models stage gets its own copy, in its own folder." : null);
        }
        Ui.LabeledValue("Textures", subject.TextureFolder,
            subject.MultiRace && subject is FeatureSubject ? "Written once; every race's materials point here." : null);

        ImGui.Spacing();
        ImGui.Spacing();
        Ui.SectionHeader("Mod", "Written into the mod's meta.json. Name and description are saved per item or feature; author, version and website apply to every mod you build.");

        string modName = _session.ModName;
        if (Ui.LabeledInput("Name", "##ModName", ref modName, 128, PortSession.DefaultModName(subject),
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

        var cfg = _plugin.Configuration;
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
        Ui.SectionHeader("Your set-up");
        StageSummary(StageId.Models, FontAwesomeIcon.Cube,
            $"{_session.Models.Count(m => !string.IsNullOrWhiteSpace(m.SourcePath))} of {_session.Models.Count} race model(s) set, " +
            $"{_session.ModelMaterialSlots.Count} mesh slot(s)");
        StageSummary(StageId.Materials, FontAwesomeIcon.Palette,
            $"{_session.Materials.Count} material(s), {_session.Materials.Sum(m => m.Textures.Count)} texture(s)");
    }

    public void DrawInspector()
    {
        var subject = _session.Subject;
        if (subject == null)
        {
            Ui.Hint("Nothing selected.");
            return;
        }

        LoadFacts(subject);
        Ui.SectionHeader("In the game", "What the vanilla game already ships.");

        if (subject is GearSubject)
        {
            Ui.Label("Dye variants");
            ImGui.TextUnformatted(_dyeVariants > 0 ? _dyeVariants.ToString() : "—");
            Ui.Tooltip("Each variant gets an Imc override pointing it at your material, so the port shows whichever one is equipped.");
            ImGui.Spacing();
        }
        else
        {
            DrawVanillaMaterials(subject);
            ImGui.Spacing();
        }

        // Single-race features only ever apply to their own race, so the table only helps gear and hair.
        if (!subject.MultiRace)
            return;

        ImGui.TextColored(Theme.Muted, subject is GearSubject ? "Races with their own model" : $"Races that have {subject.KindLabel.ToLowerInvariant()} {((FeatureSubject)subject).Id}");
        Ui.Tooltip(subject is GearSubject
            ? "Races not listed fall back to another race's model. Adding a model for one of them adds an Eqdp override automatically."
            : "Adding a race that does not ship this id may not work: the game might never ask for the file.");
        ImGui.Spacing();

        if (ImGui.BeginTable("##NativeRaces", 2, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var rg in RaceInfo.AllRaces)
            {
                ImGui.TableNextColumn();
                bool native = _nativeRaces.Contains(rg);
                Ui.Icon(native ? FontAwesomeIcon.Check : FontAwesomeIcon.Minus, native ? Theme.Good : Theme.Faint);
                ImGui.SameLine();
                ImGui.TextColored(native ? Theme.Muted : Theme.Faint, rg.DisplayName);
            }
            ImGui.EndTable();
        }
    }

    /// <summary>The material names the vanilla model references — the names a port's model must use.</summary>
    private void DrawVanillaMaterials(PortSubject subject)
    {
        ImGui.TextColored(Theme.Muted, "Vanilla materials");
        Ui.Tooltip("The material names the game's own model uses. Your model's mesh parts should reference the same names; " +
                   "\"Match vanilla materials\" on the Materials stage sets these up for you.");
        if (_vanillaMaterials.Count == 0)
        {
            ImGui.TextColored(Theme.Warn, "No vanilla model found.");
            return;
        }

        foreach (var name in _vanillaMaterials)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted(name.TrimStart('/'));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy.");
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(name);
        }

        ImGui.Spacing();
        if (Ui.IconTextButton(FontAwesomeIcon.Magic, "Match vanilla materials",
                "Replaces your material list with these materials, their shaders and texture slots."))
        {
            if (_session.Materials.Count == 0)
            {
                _session.MatchVanillaMaterials();
            }
            else
            {
                // Replacing existing materials needs a confirmation, which the Materials stage owns.
                _session.MatchVanillaRequested = true;
                _session.GoToStage(StageId.Materials);
            }
        }
    }

    private void StageSummary(StageId stage, FontAwesomeIcon icon, string text)
    {
        ImGui.PushID((int)stage);
        Ui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);

        int errors = PortValidator.Count(_session.Issues, stage, Severity.Error);
        int warnings = PortValidator.Count(_session.Issues, stage, Severity.Warning);
        if (errors + warnings > 0)
        {
            ImGui.SameLine();
            Ui.IssueBadge(errors, warnings);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"Open {stage}"))
            _session.GoToStage(stage);
        ImGui.PopID();
    }

    private static void EmptyState()
    {
        ImGui.Spacing();
        Ui.Icon(FontAwesomeIcon.HandPointLeft, Theme.Accent);
        ImGui.SameLine();
        ImGui.TextUnformatted("Pick what your port replaces from the list on the left.");
        ImGui.Spacing();
        Ui.HintWrapped("Gear is picked by slot and item; hair, faces, tails and ears by race and id. Then work through the " +
                       "stages above: models, materials and their textures, and build the mod. Your set-up is saved per " +
                       "item or feature, so you can switch freely.");
    }

    private void LoadFacts(PortSubject subject)
    {
        if (_factsFor == subject.Key)
            return;

        _factsFor    = subject.Key;
        _nativeRaces = _plugin.GameData.GetNativeRaces(subject);
        _dyeVariants = subject is GearSubject gear
            ? _plugin.GameData.GetImcOverridesForcingV1(gear.Item.Slot, gear.Item.ModelId)?.Count ?? 0
            : 0;
        _vanillaMaterials = subject.BaseRace is { } race
            ? _plugin.GameData.GetVanillaMaterialNames(subject, race)
            : new List<string>();
    }
}
