using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using XIVPortStudio.Services;
using XIVPortStudio.Services.Sims;

namespace XIVPortStudio.Models;

/// <summary>The workflow stages shown as tabs in the main window, in order.</summary>
internal enum StageId { Item, Models, Materials, Build }

/// <summary>What kind of object a <see cref="Target"/> points at.</summary>
internal enum TargetKind { Item, Model, MeshSlot, Material, Texture, Build }

/// <summary>
/// A reference to one object in the session: the thing the inspector shows, and the
/// thing a validation issue points at. <see cref="SubIndex"/> is only used by
/// textures, where <see cref="Index"/> is the owning material.
/// </summary>
internal readonly record struct Target(TargetKind Kind, int Index = -1, int SubIndex = -1)
{
    public StageId Stage => Kind switch
    {
        TargetKind.Item                          => StageId.Item,
        TargetKind.Model or TargetKind.MeshSlot  => StageId.Models,
        TargetKind.Material or TargetKind.Texture => StageId.Materials,
        _                                        => StageId.Build,
    };
}

/// <summary>A short-lived message for the status bar — the answer to an action the user just took.</summary>
internal sealed record StatusMessage(string Text, Severity Severity, DateTime At);

/// <summary>
/// Everything the user has configured for the selected item, plus which object each
/// stage is focused on. Panels read and edit this directly and call <see cref="MarkDirty"/>;
/// the session batches the writes into <see cref="Configuration"/>, so typing into a
/// field no longer serialises the whole plugin config on every keystroke.
///
/// Persistence stays keyed by <see cref="PortSubject.Key"/> in <c>Configuration.*ByItem</c>;
/// for gear that is the item row id, exactly as before, so existing saved set-ups load unchanged.
/// </summary>
internal sealed class PortSession
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(10);

    private readonly Plugin _plugin;
    private readonly Stopwatch _sinceDirty = new();
    private bool _dirty;

    public PortSession(Plugin plugin)
    {
        _plugin = plugin;
        ActiveStage = (StageId)Math.Clamp(plugin.Configuration.LastStage, 0, (int)StageId.Build);
    }

    // ── Document ─────────────────────────────────────────────────────────────

    /// <summary>Config key of the subject (see <see cref="PortSubject"/>); 0 when nothing is selected.</summary>
    public uint SubjectKey { get; private set; }

    /// <summary>What the port replaces: a piece of gear or a character feature.</summary>
    public PortSubject? Subject { get; private set; }

    public List<RaceModelEntry>    Models             { get; private set; } = new();
    public List<MaterialSetup>     Materials          { get; private set; } = new();
    public List<ModelMaterialSlot> ModelMaterialSlots { get; private set; } = new();
    public string ModName        { get; set; } = string.Empty;
    public string ModDescription { get; set; } = string.Empty;

    /// <summary>Bumped on every edit; the validator re-runs when it changes.</summary>
    public int Revision { get; private set; }

    /// <summary>Latest validation result, set by the main window's validation tick.</summary>
    public IReadOnlyList<PortIssue> Issues { get; set; } = Array.Empty<PortIssue>();

    // ── Focus ────────────────────────────────────────────────────────────────

    public StageId ActiveStage { get; private set; }

    public int SelectedModel    { get; set; } = -1;
    public int SelectedMeshSlot { get; set; } = -1;
    public int SelectedMaterial { get; set; } = -1;
    public int SelectedTexture  { get; set; } = -1;

    private Target? _scrollTo;

    public StatusMessage? Status { get; private set; }

    /// <summary>
    /// Set by another stage to have the Materials stage ask "replace with vanilla materials?"
    /// (the confirmation lives there, since it owns the material list).
    /// </summary>
    public bool MatchVanillaRequested { get; set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Item selection
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Switches to another subject, saving the current one first.</summary>
    public void SelectSubject(PortSubject? subject)
    {
        var key = subject?.Key ?? 0;
        if (key == SubjectKey && Subject != null)
            return;

        Flush();

        SubjectKey = key;
        Subject    = subject;
        Load();

        var cfg = _plugin.Configuration;
        cfg.LastItemRowId = key;   // holds any subject key; the name predates features
        cfg.Save();
        Revision++;
    }

    /// <summary>Clears all models, materials and the mod name for the current subject.</summary>
    public void ResetItem()
    {
        if (Subject == null) return;

        Models.Clear();
        Materials.Clear();
        ModelMaterialSlots.Clear();
        EnsureFixedModel();
        SelectedModel = SelectedMeshSlot = SelectedMaterial = SelectedTexture = -1;
        ModName        = DefaultModName(Subject);
        ModDescription = string.Empty;
        MarkDirty();
        Notify($"{Subject.KindLabel} inputs reset.", Severity.Info);
    }

    /// <summary>
    /// A single-race feature always has exactly one model entry, for its own race; the Models
    /// stage shows it as a fixed row instead of offering races to add.
    /// </summary>
    private void EnsureFixedModel()
    {
        if (Subject is not { MultiRace: false, BaseRace: { } race })
            return;
        Models.RemoveAll(m => m.RaceGender != race);
        if (Models.Count == 0)
            Models.Add(new RaceModelEntry { Race = race.Race, Gender = race.Gender });
    }

    /// <summary>
    /// Replaces the material list with the vanilla model's own materials — their names, shaders
    /// and texture roles — so the port's model and materials line up with what the game expects.
    /// </summary>
    public void MatchVanillaMaterials()
    {
        var subject = Subject;
        if (subject == null) return;

        var race = _plugin.GameData.ReferenceRace(subject);
        var layout = race != null ? _plugin.GameData.ReadVanillaMaterialLayout(subject, race.Value) : null;
        if (layout == null || layout.Count == 0)
        {
            Notify($"No vanilla model found for {subject.DisplayName}.", Severity.Warning);
            return;
        }

        if (_plugin.Configuration.DefaultCompressBc7)
            foreach (var slot in layout.SelectMany(m => m.Textures))
                slot.CompressBc7 = true;

        Materials.Clear();
        Materials.AddRange(layout);
        ModelMaterialSlots.Clear();
        SelectedMaterial = 0;
        SelectedTexture  = -1;
        MarkDirty();
        Notify($"Matched {layout.Count} vanilla material(s): {string.Join(", ", layout.Select(m => m.Name))}.", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stage / focus
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Switches the visible stage. Remembered across sessions with the next save.</summary>
    public void GoToStage(StageId stage)
    {
        ActiveStage = stage;
        _plugin.Configuration.LastStage = (int)stage;
    }

    /// <summary>
    /// Opens the stage that owns <paramref name="target"/>, selects it in the canvas so the
    /// inspector shows it, and asks the canvas to scroll it into view.
    /// </summary>
    public void Focus(Target target)
    {
        switch (target.Kind)
        {
            case TargetKind.Model:    SelectedModel = target.Index; break;
            case TargetKind.MeshSlot: SelectedMeshSlot = target.Index; SelectedModel = -1; break;
            case TargetKind.Material: SelectedMaterial = target.Index; SelectedTexture = -1; break;
            case TargetKind.Texture:  SelectedMaterial = target.Index; SelectedTexture = target.SubIndex; break;
        }

        GoToStage(target.Stage);
        _scrollTo = target;
    }

    /// <summary>True once for the row that <see cref="Focus"/> asked to bring into view.</summary>
    public bool TakeScrollRequest(Target row)
    {
        if (_scrollTo != row) return false;
        _scrollTo = null;
        return true;
    }

    public MaterialSetup? CurrentMaterial
        => SelectedMaterial >= 0 && SelectedMaterial < Materials.Count ? Materials[SelectedMaterial] : null;

    public RaceModelEntry? CurrentModel
        => SelectedModel >= 0 && SelectedModel < Models.Count ? Models[SelectedModel] : null;

    // ─────────────────────────────────────────────────────────────────────────
    // Status messages
    // ─────────────────────────────────────────────────────────────────────────

    public void Notify(string text, Severity severity) => Status = new StatusMessage(text, severity, DateTime.UtcNow);

    /// <summary>The current status message, or null once it has aged out.</summary>
    public StatusMessage? LiveStatus
        => Status != null && DateTime.UtcNow - Status.At < StatusLifetime ? Status : null;

    // ─────────────────────────────────────────────────────────────────────────
    // Models
    // ─────────────────────────────────────────────────────────────────────────

    public void AddModel(RaceGender rg)
    {
        if (Subject is { MultiRace: false }) return;
        var entry = new RaceModelEntry { Race = rg.Race, Gender = rg.Gender };
        Models.Add(entry);
        SortModels();
        SelectedModel = Models.IndexOf(entry);
        MarkDirty();
    }

    public void RemoveModel(int index)
    {
        if (index < 0 || index >= Models.Count || Subject is { MultiRace: false }) return;
        Models.RemoveAt(index);
        SelectedModel = Math.Min(SelectedModel, Models.Count - 1);
        MarkDirty();
    }

    /// <summary>
    /// Builds a placeholder model for a race: one empty mesh part per mesh slot (in order),
    /// already assigned to that slot's material, on the vanilla skeleton — ready to open and
    /// model in Blender via InstantEdit.
    /// </summary>
    public void BuildDummyModel(RaceModelEntry entry)
    {
        var subject = Subject;
        if (subject == null) return;

        if (Materials.Count == 0)
        {
            Notify("Add at least one material first — each becomes an empty mesh part in the dummy model.", Severity.Warning);
            return;
        }

        var gamePath = subject.ModelGamePath(entry.RaceGender);
        var vanilla = _plugin.GameData.GetVanillaModel(gamePath, out var readError);
        if (vanilla == null)
        {
            Notify($"Could not read vanilla model at {gamePath}.{(readError != null ? $" ({readError})" : "")}", Severity.Error);
            return;
        }

        SyncModelMaterialSlots();
        var materialNames = ModelMaterialSlots
            .Select(slot => $"{MaterialNaming.SanitizeFileName(subject.MaterialNameFor(EffectiveMaterialName(slot), entry.RaceGender))}.mtrl")
            .ToList();

        byte[] bytes;
        try
        {
            bytes = DummyModelBuilder.Build(vanilla, materialNames);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XPS] Failed to build dummy model for {0}", gamePath);
            Notify($"Failed to build dummy model: {ex.Message}", Severity.Error);
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "XIVPortStudio", "vanilla-models");
        Directory.CreateDirectory(dir);
        var fileName = subject is GearSubject gear
            ? $"{SanitizeFolderName(gear.Item.Name)}_{entry.RaceGender.RaceCode}_{SlotInfo.KeyMap[gear.Item.Slot]}.mdl"
            : Path.GetFileName(gamePath);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);

        entry.SourcePath = path;
        entry.IsVanillaDummy = true;
        MarkDirty();
        Notify($"Dummy model for {entry.RaceGender.DisplayName} written with {materialNames.Count} empty mesh part(s).", Severity.Info);
    }

    /// <summary>Keeps the model list ordered by race code (0101, 0201, 0301, …) rather than insertion order.</summary>
    private void SortModels()
        => Models.Sort((a, b) => string.CompareOrdinal(a.RaceGender.RaceCode, b.RaceGender.RaceCode));

    // ─────────────────────────────────────────────────────────────────────────
    // Materials
    // ─────────────────────────────────────────────────────────────────────────

    public void AddMaterial(MaterialPreset preset)
    {
        string name = Subject?.DefaultMaterialName(Materials.Count + 1) ?? "Material";
        var material = MaterialSetup.FromPreset(preset, name);
        ApplyTextureDefaults(material.Textures);
        Materials.Add(material);
        SelectedMaterial = Materials.Count - 1;
        SelectedTexture  = -1;
        MarkDirty();
    }

    /// <summary>Re-applies a preset to a material, replacing its shader and textures.</summary>
    public void ApplyPreset(MaterialSetup mat, MaterialPreset preset)
    {
        mat.ShaderType = preset.Shader;
        mat.Textures.Clear();
        foreach (var type in preset.Textures)
            mat.Textures.Add(new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type) });
        ApplyTextureDefaults(mat.Textures);
        SelectedTexture = -1;
        MarkDirty();
    }

    public void DuplicateMaterial(int index)
    {
        if (index < 0 || index >= Materials.Count) return;
        var copy = Materials[index].Clone();
        copy.Name = $"{copy.Name} copy";
        int insertAt = index + 1;
        Materials.Insert(insertAt, copy);

        // Keep existing slot assignments pointing at the same material, since
        // everything from insertAt onward just shifted down by one.
        foreach (var slot in ModelMaterialSlots)
            if (slot.MaterialIndex >= insertAt) slot.MaterialIndex++;

        SelectedMaterial = insertAt;
        SelectedTexture  = -1;
        MarkDirty();
    }

    public void RemoveMaterial(int index)
    {
        if (index < 0 || index >= Materials.Count) return;
        Materials.RemoveAt(index);

        // Keep surviving slot assignments pointing at the same material; slots that
        // pointed at the removed one get clamped back into range by the sync.
        foreach (var slot in ModelMaterialSlots)
            if (slot.MaterialIndex > index) slot.MaterialIndex--;

        SelectedMaterial = Math.Min(SelectedMaterial, Materials.Count - 1);
        SelectedTexture  = -1;
        MarkDirty();
    }

    public void AddTexture(MaterialSetup mat)
    {
        var used = mat.Textures.Select(t => t.Type).ToHashSet();
        var type = Enum.GetValues<TextureType>().FirstOrDefault(t => !used.Contains(t));
        var slot = new TextureSlot
        {
            Type        = type,
            Postfix     = MaterialNaming.DefaultPostfix(type),
            CompressBc7 = _plugin.Configuration.DefaultCompressBc7,
        };
        mat.Textures.Add(slot);
        SelectedTexture = mat.Textures.Count - 1;
        MarkDirty();
    }

    public void RemoveTexture(MaterialSetup mat, int index)
    {
        if (index < 0 || index >= mat.Textures.Count) return;
        mat.Textures.RemoveAt(index);
        SelectedTexture = Math.Min(SelectedTexture, mat.Textures.Count - 1);
        MarkDirty();
    }

    private void ApplyTextureDefaults(IEnumerable<TextureSlot> slots)
    {
        if (!_plugin.Configuration.DefaultCompressBc7) return;
        foreach (var slot in slots)
            slot.CompressBc7 = true;
    }

    /// <summary>Resolves a slot's effective on-disk material name (its override, or the assigned Material's own name).</summary>
    public string EffectiveMaterialName(ModelMaterialSlot slot)
    {
        if (Materials.Count == 0) return slot.NameOverride;
        var idx = Math.Clamp(slot.MaterialIndex, 0, Materials.Count - 1);
        return string.IsNullOrWhiteSpace(slot.NameOverride) ? Materials[idx].Name : slot.NameOverride;
    }

    /// <summary>
    /// Keeps the mesh slot list matched 1:1 with the Materials list: appends identity slots
    /// when Materials grows, truncates when it shrinks, and clamps any now-out-of-range
    /// MaterialIndex (e.g. after removing a material) back into range.
    /// </summary>
    public void SyncModelMaterialSlots()
    {
        while (ModelMaterialSlots.Count < Materials.Count)
            ModelMaterialSlots.Add(new ModelMaterialSlot { MaterialIndex = ModelMaterialSlots.Count });

        if (ModelMaterialSlots.Count > Materials.Count)
            ModelMaterialSlots.RemoveRange(Materials.Count, ModelMaterialSlots.Count - Materials.Count);

        foreach (var slot in ModelMaterialSlots)
            slot.MaterialIndex = Math.Clamp(slot.MaterialIndex, 0, Math.Max(Materials.Count - 1, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sims 4 importer hand-off
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Points the selected material at the files the Sims 4 importer just extracted: the
    /// diffuse folder becomes a variant slot (so every colour swatch in it turns into one
    /// option of a switch group when the mod is created), and the normal and specular
    /// images fill their slots as single textures. Slots that do not exist yet are added.
    /// Returns a message for the importer window to show.
    /// </summary>
    public string ApplySimsExtraction(SimsPackageInspector.ExtractionResult extraction)
    {
        if (Subject == null)
            return "Select an item or character feature in the main window first.";
        var mat = CurrentMaterial;
        if (mat == null)
            return "Select a material in the main window first.";

        var applied = new List<string>();

        if (extraction.DiffuseCount > 0 && Directory.Exists(extraction.DiffuseFolder))
        {
            var slot = GetOrAddTextureSlot(mat, TextureType.Diffuse);
            slot.SourcePath    = extraction.DiffuseFolder;
            slot.UseVariants   = true;
            slot.UseWhiteDummy = false;
            applied.Add($"diffuse ({extraction.DiffuseCount} variant(s))");
        }

        if (extraction.NormalFile != null && File.Exists(extraction.NormalFile))
        {
            var slot = GetOrAddTextureSlot(mat, TextureType.Normal);
            slot.SourcePath    = extraction.NormalFile;
            slot.UseVariants   = false;
            slot.UseWhiteDummy = false;
            applied.Add("normal");
        }

        if (extraction.SpecularFile != null && File.Exists(extraction.SpecularFile))
        {
            var slot = GetOrAddTextureSlot(mat, TextureType.Specular);
            slot.SourcePath    = extraction.SpecularFile;
            slot.UseVariants   = false;
            slot.UseWhiteDummy = false;
            applied.Add("specular");
        }

        if (applied.Count == 0)
            return "Nothing to apply — the extraction produced no diffuse, normal or specular textures.";

        MarkDirty();
        var message = $"Applied {string.Join(", ", applied)} to material \"{mat.Name}\".";
        Notify(message, Severity.Info);
        return message;
    }

    /// <summary>Finds the material's slot for a texture role, adding one when it has none.</summary>
    private static TextureSlot GetOrAddTextureSlot(MaterialSetup mat, TextureType type)
    {
        var existing = mat.Textures.FirstOrDefault(t => t.Type == type);
        if (existing != null)
            return existing;

        var slot = new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type) };
        mat.Textures.Add(slot);
        return slot;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Build hand-off
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A deep copy of everything the build needs, taken on the render thread so the
    /// worker never reads lists the UI is editing.
    /// </summary>
    public BuildRequest? CreateBuildRequest(string modRoot)
    {
        var subject = Subject;
        if (subject == null) return null;
        SyncModelMaterialSlots();
        var cfg = _plugin.Configuration;
        var gameData = _plugin.GameData;

        // Vanilla materials to clone, only for materials whose shader has no bundled preset —
        // looked up here, on the render thread, per mesh slot and per race copy.
        var templates = new Dictionary<string, (string GamePath, MtrlInfo Material)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in ModelMaterialSlots)
        {
            if (Materials.Count == 0) break;
            var mat = Materials[Math.Clamp(slot.MaterialIndex, 0, Materials.Count - 1)];
            if (MaterialPresetLibrary.FindPresetPath(mat.ShaderType, mat.Textures.Select(t => t.Type).ToHashSet()) != null)
                continue;

            var name = EffectiveMaterialName(slot);
            foreach (var race in subject.MaterialRaces(Models))
            {
                var key = BuildRequest.TemplateKey(name, race);
                if (!templates.ContainsKey(key))
                    templates[key] = gameData.FindVanillaMaterial(subject, name, race);
            }
        }

        List<ModImcOverride>? imc = null;
        if (subject is GearSubject gear && Materials.Count > 0)
            imc = gameData.GetImcOverridesForcingV1(gear.Item.Slot, gear.Item.ModelId);

        return new BuildRequest
        {
            NativeRaces        = gameData.GetNativeRaces(subject),
            VanillaTemplates   = templates,
            ImcOverrides       = imc,
            Subject            = subject,
            ModRoot            = modRoot,
            ModName            = SanitizeFolderName(string.IsNullOrWhiteSpace(ModName) ? DefaultModName(subject) : ModName.Trim()),
            Models             = Models.Select(m => m.Clone()).ToList(),
            Materials          = Materials.Select(m => m.Clone()).ToList(),
            ModelMaterialSlots = ModelMaterialSlots.Select(s => s.Clone()).ToList(),
            PluginDirectory    = Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
            Meta = new ModMetaInfo
            {
                Identifier  = GetOrCreateModId(),
                Author      = cfg.ModAuthor,
                Description = ModDescription,
                Version     = cfg.ModVersion,
                Website     = cfg.ModWebsite,
            },
        };
    }

    private Guid GetOrCreateModId()
    {
        var ids = _plugin.Configuration.ModIdByItem;
        if (!ids.TryGetValue(SubjectKey, out var id))
        {
            id = Guid.NewGuid();
            ids[SubjectKey] = id;
            MarkDirty();
        }
        return id;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records that the document changed. Also re-syncs mesh slots, since materials and
    /// mesh slots are edited on different stages and must never drift apart.
    /// </summary>
    public void MarkDirty()
    {
        SyncModelMaterialSlots();
        Revision++;
        _dirty = true;
        _sinceDirty.Restart();
    }

    /// <summary>Called once per frame: writes pending edits once they have settled.</summary>
    public void FlushIfDue()
    {
        if (_dirty && _sinceDirty.Elapsed >= SaveDelay)
            Flush();
    }

    /// <summary>Writes pending edits now — on item switch, window close and plugin unload.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;

        var cfg = _plugin.Configuration;
        if (SubjectKey != 0)
        {
            cfg.ModelsByItem[SubjectKey]             = Models.Select(m => m.Clone()).ToList();
            cfg.MaterialsByItem[SubjectKey]          = Materials.Select(m => m.Clone()).ToList();
            cfg.ModelMaterialSlotsByItem[SubjectKey] = ModelMaterialSlots.Select(s => s.Clone()).ToList();
            cfg.ModNameByItem[SubjectKey]            = ModName;
            if (string.IsNullOrWhiteSpace(ModDescription))
                cfg.ModDescriptionByItem.Remove(SubjectKey);
            else
                cfg.ModDescriptionByItem[SubjectKey] = ModDescription;
        }
        cfg.Save();
    }

    private void Load()
    {
        var cfg = _plugin.Configuration;

        Models = cfg.ModelsByItem.TryGetValue(SubjectKey, out var models)
            ? models.Select(m => m.Clone()).ToList()
            : new List<RaceModelEntry>();
        SortModels();

        Materials = cfg.MaterialsByItem.TryGetValue(SubjectKey, out var materials)
            ? materials.Select(m => m.Clone()).ToList()
            : new List<MaterialSetup>();

        ModelMaterialSlots = cfg.ModelMaterialSlotsByItem.TryGetValue(SubjectKey, out var slots)
            ? slots.Select(s => s.Clone()).ToList()
            : new List<ModelMaterialSlot>();
        SyncModelMaterialSlots();
        EnsureFixedModel();

        ModName = cfg.ModNameByItem.TryGetValue(SubjectKey, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : DefaultModName(Subject);
        ModDescription = cfg.ModDescriptionByItem.TryGetValue(SubjectKey, out var description) ? description : string.Empty;

        SelectedModel    = Models.Count > 0 ? 0 : -1;
        SelectedMeshSlot = -1;
        SelectedMaterial = Materials.Count > 0 ? 0 : -1;
        SelectedTexture  = -1;
        _scrollTo        = null;
        Issues           = Array.Empty<PortIssue>();
    }

    /// <summary>Auto-generated mod name used until the user overrides it.</summary>
    public static string DefaultModName(PortSubject? subject) => subject switch
    {
        GearSubject gear       => $"XIV Port Studio - {SanitizeFolderName(gear.Item.Name)}",
        FeatureSubject feature => $"XIV Port Studio - {FeatureNaming.KindLabel(feature.Kind)} {feature.Id} ({SanitizeFolderName(feature.Race.DisplayName)})",
        _                      => "XIV Port Studio - Mod",
    };

    /// <summary>Replaces path-invalid characters so item names can be used as folder names.</summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
