using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using XIVPortStudio.Models;
using XIVPortStudio.Services;

namespace XIVPortStudio.Windows;

/// <summary>
/// Primary plugin window.
///   Left  – item picker: choose an equipment slot, then an item within that slot.
///   Right – material set-up: create materials for the selected item, choosing the
///           shader type, material name and the textures that belong to it.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    // ── Item picker state ────────────────────────────────────────────────────

    private readonly EquipSlot[] _allSlots = SlotInfo.AllSlots.ToArray();
    private readonly string[]    _slotLabels;
    private int                  _slotIndex = 1;

    private string                 _search       = string.Empty;
    private List<GameDataService.GameItem> _allItems   = new();
    private List<GameDataService.GameItem> _filtered  = new();
    private uint                   _selectedRowId = 0;

    // ── Model state ──────────────────────────────────────────────────────────

    private List<RaceModelEntry> _models = new();
    private int                  _removeModelAt = -1;     // handled after the model list loop
    private int                  _modelRaceAddIndex = 0;  // selected race in the "add race" combo

    // ── Material state ───────────────────────────────────────────────────────

    private List<MaterialSetup> _materials = new();
    private int                 _materialIndex = -1;
    private int                 _removeTextureAt = -1;   // handled after the table loop
    private int                 _presetIndex = 0;        // selected material preset
    private string              _modName = string.Empty; // user-editable; falls back to an auto name when empty
    private string              _exportResult = string.Empty;

    private bool _penumbraAvailable = false;

    // Native OPENFILENAME/SHBrowseForFolder dialogs don't play well with .NET's marshaler
    // and can stall the game's render thread, so use Dalamud's own ImGui-drawn file
    // dialog instead (same approach Penumbra and most other Dalamud plugins use).
    private readonly FileDialogManager _fileDialogManager = new();

    // ── Texture white-dummy size options ────────────────────────────────────

    private static readonly int[]    WhiteDummySizes  = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };
    private static readonly string[] WhiteDummyLabels  = WhiteDummySizes.Select(s => $"{s} x {s}").ToArray();

    // ── Colours ──────────────────────────────────────────────────────────────

    private static readonly Vector4 ColAdd    = new(0.41f, 0.86f, 0.48f, 1f);
    private static readonly Vector4 ColDel    = new(0.94f, 0.36f, 0.42f, 1f);
    private static readonly Vector4 ColMuted  = new(0.50f, 0.50f, 0.63f, 1f);
    private static readonly Vector4 ColAccent = new(0.49f, 0.30f, 1.00f, 1f);

    // ─────────────────────────────────────────────────────────────────────────
    // Ctor / Dispose
    // ─────────────────────────────────────────────────────────────────────────

    public MainWindow(Plugin plugin) : base(
        "XIV Port Studio###XPSMain",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin = plugin;
        Size          = new Vector2(940, 620);
        SizeCondition = ImGuiCond.FirstUseEver;

        _slotLabels = _allSlots.Select(s => SlotInfo.DisplayLabelMap[s]).ToArray();

        var cfg = plugin.Configuration;
        _slotIndex = Math.Clamp(cfg.LastSlotIndex, 0, _allSlots.Length - 1);
        _search    = cfg.LastItemSearch;

        ReloadItemList();
        RestoreItemSelection(cfg.LastItemRowId);
        LoadModelsForSelection();
        LoadMaterialsForSelection();
        LoadModNameForSelection();
    }

    public void Dispose() { }

    public void OnPenumbraStateChanged()
    {
        _penumbraAvailable = _plugin.PenumbraIpc.IsAvailable;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Draw
    // ─────────────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        _penumbraAvailable = _plugin.PenumbraIpc.IsAvailable;

        DrawStatusBar();
        ImGui.Spacing();

        // ── Left column: item picker ──────────────────────────────────────
        float leftW = 330;
        ImGui.BeginChild("##XPSLeft", new Vector2(leftW, -1), true);
        DrawItemPicker();
        ImGui.EndChild();

        ImGui.SameLine();

        // ── Right column: model + material set-up ──────────────────────────
        ImGui.BeginChild("##XPSRight", new Vector2(-1, -1), true);
        DrawModelEditor();
        ImGui.Spacing();
        DrawMaterialEditor();
        ImGui.EndChild();

        _fileDialogManager.Draw();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status bar
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawStatusBar()
    {
        if (_penumbraAvailable)
        {
            ImGui.TextColored(ColAdd, "● Penumbra: connected");
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, $"({_plugin.GameData.CacheCount} items cached)");
        }
        else
        {
            ImGui.TextColored(ColDel, "● Penumbra: not available");
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, "(item browser still works; Penumbra IPC disabled)");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Item picker (left column)
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawItemPicker()
    {
        ImGui.TextColored(ColAccent, "Item");
        ImGui.Separator();

        ImGui.Text("Slot:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##Slot", ref _slotIndex, _slotLabels, _slotLabels.Length))
        {
            _selectedRowId = 0;
            ReloadItemList();
            SaveItemSelectionToConfig();
        }

        ImGui.Spacing();

        ImGui.Text("Item:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##ItemSearch", "Search items…", ref _search, 256))
        {
            ApplyFilter();
            _plugin.Configuration.LastItemSearch = _search;
        }

        ImGui.Spacing();

        // Item list
        float lineH  = ImGui.GetTextLineHeightWithSpacing();
        float listH  = ImGui.GetContentRegionAvail().Y - lineH * 5f;
        if (listH < lineH * 3f) listH = lineH * 3f;

        if (ImGui.BeginChild("##ItemList", new Vector2(-1, listH), true))
        {
            if (_filtered.Count == 0)
            {
                ImGui.TextColored(ColMuted, "(no items match)");
            }
            else
            {
                const int maxRows = 1000;
                int shown = Math.Min(_filtered.Count, maxRows);

                for (int i = 0; i < shown; i++)
                {
                    var item = _filtered[i];
                    bool sel = item.RowId == _selectedRowId;
                    if (ImGui.Selectable($"{item.Name}  (ID: {item.ModelIdDisplay})##it{i}", sel))
                    {
                        if (_selectedRowId != item.RowId)
                        {
                            _selectedRowId = item.RowId;
                            LoadModelsForSelection();
                            LoadMaterialsForSelection();
                            LoadModNameForSelection();
                            SaveItemSelectionToConfig();
                        }
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }

                if (_filtered.Count > maxRows)
                    ImGui.TextColored(ColMuted, $"(showing {maxRows} of {_filtered.Count} — refine your search)");
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();

        // Selected item summary
        var selected = _selectedRowId != 0
            ? _plugin.GameData.GetItemByRowId(_selectedRowId)
            : null;

        if (selected != null)
        {
            var prefix = SlotInfo.ItemPrefix(selected.Slot);
            var folder = $"chara/{prefix}*{selected.ModelIdPadded}*";
            ImGui.TextColored(ColMuted, $"Selected: {selected.Name}");
            ImGui.TextColored(ColMuted, $"Slot: {SlotInfo.DisplayLabelMap[selected.Slot]}   Model: {selected.ModelIdDisplay}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Item folder pattern: {folder} — models/materials/textures for this item use paths like this.");
        }
        else
        {
            ImGui.TextColored(ColMuted, "No item selected.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Model editor (right column, above materials)
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawModelEditor()
    {
        ImGui.TextColored(ColAccent, "Model");
        ImGui.Separator();

        if (_selectedRowId == 0)
        {
            ImGui.TextColored(ColMuted, "Select an item on the left to set up its model.");
            ImGui.Spacing();
            ImGui.Separator();
            return;
        }

        var item = _plugin.GameData.GetItemByRowId(_selectedRowId);
        if (item == null)
        {
            ImGui.TextColored(ColMuted, "Item data unavailable.");
            ImGui.Spacing();
            ImGui.Separator();
            return;
        }

        var nativeRaces = _plugin.GameData.GetNativeModelRaces(item.Slot, item.ModelId);

        float lineH = ImGui.GetTextLineHeightWithSpacing();
        float listH = Math.Clamp(_models.Count, 1, 4) * lineH + 8f;

        if (ImGui.BeginChild("##ModelList", new Vector2(-1, listH), true))
        {
            if (_models.Count == 0)
            {
                ImGui.TextColored(ColMuted, "(no models configured — add a race below)");
            }
            else
            {
                for (int i = 0; i < _models.Count; i++)
                {
                    var m = _models[i];
                    bool native = nativeRaces.Contains(m.RaceGender);
                    ImGui.PushID(i);

                    if (!native)
                    {
                        ImGui.TextColored(ColAccent, "!");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("This race has no model by default — an Eqdp meta manipulation will be " +
                                              "added so the game uses this custom model instead of falling back to another race.");
                        ImGui.SameLine();
                    }

                    ImGui.Text(m.RaceGender.DisplayName);
                    ImGui.SameLine(180);

                    string src = m.SourcePath;
                    ImGui.SetNextItemWidth(230);
                    if (ImGui.InputTextWithHint("##ModelSrc", "path to .mdl file", ref src, 512))
                    {
                        m.SourcePath = src;
                        m.IsVanillaDummy = false;
                        SaveModels();
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("…##ModelBrowse"))
                    {
                        _fileDialogManager.OpenFileDialog("Select model file", "Model files{.mdl}", (ok, path) =>
                        {
                            if (!ok) return;
                            m.SourcePath = path;
                            m.IsVanillaDummy = false;
                            SaveModels();
                        });
                    }

                    ImGui.SameLine();
                    if (ImGui.SmallButton("Dummy##ModelVanilla"))
                        UseVanillaDummy(m, item);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Builds a placeholder model on this race's vanilla skeleton, with one empty " +
                                          "mesh part per configured material (in order) already assigned to it — " +
                                          "ready to model in Blender via InstantEdit.");

                    ImGui.SameLine();
                    if (ImGui.SmallButton("X##ModelDel"))
                        _removeModelAt = i;

                    if (m.IsVanillaDummy)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ColMuted, "(empty dummy — model it in Blender)");
                    }

                    ImGui.PopID();
                }
            }
        }
        ImGui.EndChild();

        if (_removeModelAt >= 0 && _removeModelAt < _models.Count)
        {
            _models.RemoveAt(_removeModelAt);
            _removeModelAt = -1;
            SaveModels();
        }

        var configured = _models.Select(m => m.RaceGender).ToHashSet();
        var addable = RaceInfo.AllRaces
            .Where(rg => !configured.Contains(rg))
            .OrderByDescending(rg => nativeRaces.Contains(rg))
            .ThenBy(rg => rg.RaceCode, StringComparer.Ordinal)
            .ToList();

        if (addable.Count > 0)
        {
            var labels = addable
                .Select(rg => nativeRaces.Contains(rg) ? rg.DisplayName : $"{rg.DisplayName} (needs override)")
                .ToArray();
            if (_modelRaceAddIndex >= labels.Length) _modelRaceAddIndex = 0;

            ImGui.SetNextItemWidth(220);
            ImGui.Combo("##AddRace", ref _modelRaceAddIndex, labels, labels.Length);
            ImGui.SameLine();
            if (ImGui.Button("+ Add Race"))
            {
                var rg = addable[_modelRaceAddIndex];
                _models.Add(new RaceModelEntry { Race = rg.Race, Gender = rg.Gender });
                SortModels();
                SaveModels();
            }
        }
        else
        {
            ImGui.TextColored(ColMuted, "(all races configured)");
        }

        ImGui.Spacing();
        ImGui.Separator();
    }

    /// <summary>
    /// Builds a placeholder model for a race: one empty mesh part per configured
    /// material (in order), already assigned to that material, on the vanilla
    /// skeleton — ready to open and model in Blender via InstantEdit.
    /// </summary>
    private void UseVanillaDummy(RaceModelEntry entry, GameDataService.GameItem item)
    {
        if (_materials.Count == 0)
        {
            _exportResult = "Add at least one material first — each becomes an empty mesh part in the dummy model.";
            return;
        }

        var gamePath = ModelNaming.ModelGamePath(item.Slot, item.ModelId, entry.RaceGender.RaceCode);
        var vanilla = _plugin.GameData.GetVanillaModel(gamePath, out var readError);
        if (vanilla == null)
        {
            _exportResult = $"Could not read vanilla model at {gamePath}.{(readError != null ? $" ({readError})" : "")}";
            return;
        }

        var materialNames = _materials
            .Select(m => $"{MaterialNaming.SanitizeFileName(string.IsNullOrWhiteSpace(m.Name) ? "material" : m.Name)}.mtrl")
            .ToList();

        byte[] bytes;
        try
        {
            bytes = DummyModelBuilder.Build(vanilla, materialNames);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XPS] Failed to build dummy model for {0}", gamePath);
            _exportResult = $"Failed to build dummy model: {ex.Message}";
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "XIVPortStudio", "vanilla-models");
        Directory.CreateDirectory(dir);
        var fileName = $"{SanitizeFolderName(item.Name)}_{entry.RaceGender.RaceCode}_{SlotInfo.KeyMap[item.Slot]}.mdl";
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);

        entry.SourcePath = path;
        entry.IsVanillaDummy = true;
        SaveModels();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Material editor (right column)
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawMaterialEditor()
    {
        ImGui.TextColored(ColAccent, "Materials");
        ImGui.Separator();

        if (_selectedRowId == 0)
        {
            ImGui.TextColored(ColMuted, "Select an item on the left to set up its materials.");
            return;
        }

        var selected = _plugin.GameData.GetItemByRowId(_selectedRowId);
        ImGui.TextColored(ColMuted, selected != null
            ? $"Materials for: {selected.Name}  (ID: {selected.ModelIdDisplay})"
            : $"Materials for item row #{_selectedRowId}");

        ImGui.Spacing();

        // ── Material list ────────────────────────────────────────────────
        float lineH   = ImGui.GetTextLineHeightWithSpacing();
        float listH   = Math.Clamp(_materials.Count, 1, 6) * lineH + 8f;
        float editorH = ImGui.GetContentRegionAvail().Y - listH - lineH * 6f;
        if (editorH < lineH * 4f) editorH = lineH * 4f;

        if (ImGui.BeginChild("##MatList", new Vector2(-1, listH), true))
        {
            if (_materials.Count == 0)
            {
                ImGui.TextColored(ColMuted, "(no materials yet — add one below)");
            }
            else
            {
                for (int i = 0; i < _materials.Count; i++)
                {
                    var m  = _materials[i];
                    bool sel = i == _materialIndex;
                    var label = string.IsNullOrWhiteSpace(m.Name)
                        ? $"(unnamed)  —  {ShaderInfo.DisplayName(m.ShaderType)}"
                        : $"{m.Name}  —  {ShaderInfo.DisplayName(m.ShaderType)}";
                    if (ImGui.Selectable($"{label}##mat{i}", sel))
                    {
                        _materialIndex = i;
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }
            }
        }
        ImGui.EndChild();

        // ── Material action buttons ──────────────────────────────────────
        ImGui.Spacing();

        ImGui.Text("Preset:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        ImGui.Combo("##MatPreset", ref _presetIndex, MaterialPresets.Names, MaterialPresets.Names.Length);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pre-made material types. \"+ Add Material\" creates a material with this preset's shader and texture slots.");
        ImGui.SameLine();
        if (ImGui.Button("+ Add Material", new Vector2(130, 0)))
            AddMaterial();

        ImGui.Spacing();

        bool hasSelection = _materialIndex >= 0 && _materialIndex < _materials.Count;
        if (!hasSelection) ImGui.BeginDisabled();
        if (ImGui.Button("Apply preset", new Vector2(110, 0)))
            ApplyPresetToSelected();
        ImGui.SameLine();
        if (ImGui.Button("Duplicate", new Vector2(90, 0)))
            DuplicateMaterial();
        ImGui.SameLine();
        if (ImGui.Button("Remove", new Vector2(90, 0)))
            RemoveMaterial();
        if (!hasSelection) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();

        // ── Material editor ──────────────────────────────────────────────
        if (_materialIndex < 0 || _materialIndex >= _materials.Count)
        {
            ImGui.TextColored(ColMuted, "Select a material from the list above to edit it.");
            return;
        }

        var mat = _materials[_materialIndex];

        ImGui.TextColored(ColAccent, "Material:");
        ImGui.Separator();

        // Name
        ImGui.Text("Name:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        string matName = mat.Name;
        if (ImGui.InputText("##MatName", ref matName, 128))
        {
            mat.Name = matName;
            SaveMaterials();
        }

        ImGui.SameLine();

        ImGui.Text("Variant:");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Material-variant index (v000N). Keep 1 unless the item has multiple material slots.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        int matVariant = mat.MaterialVariant;
        if (ImGui.DragInt("##MatVariant", ref matVariant, 0.1f, 1, 999))
        {
            if (matVariant < 1) matVariant = 1;
            mat.MaterialVariant = matVariant;
            SaveMaterials();
        }

        if (selected != null)
        {
            var matGamePath = MaterialNaming.MaterialGamePath(selected.Slot, selected.ModelId, mat.MaterialVariant, mat.Name);
            ImGui.TextColored(ColMuted, matGamePath);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Game path the generated .mtrl is written to — a clone of the closest vanilla material, " +
                                  "rewired to the textures below.");
        }

        ImGui.Spacing();

        // Shader type
        ImGui.Text("Shader type:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        int shaderIdx = (int)mat.ShaderType;
        if (ImGui.Combo("##MatShader", ref shaderIdx, ShaderInfo.Labels, ShaderInfo.Labels.Length))
        {
            mat.ShaderType = (ShaderType)shaderIdx;
            SaveMaterials();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Shader pack: {ShaderInfo.ShaderPackName(mat.ShaderType)}");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Textures ─────────────────────────────────────────────────────
        ImGui.Text("Textures:");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Add Texture"))
        {
            var slot = new TextureSlot { Type = TextureType.Diffuse };
            slot.Postfix = MaterialNaming.DefaultPostfix(slot.Type);
            mat.Textures.Add(slot);
            SaveMaterials();
        }

        ImGui.Spacing();

        if (mat.Textures.Count == 0)
        {
            ImGui.TextColored(ColMuted, "(no textures — add one to bind a diffuse/normal/… map)");
        }
        else
        {
            if (ImGui.BeginTable("##TexTable", 8,
                    ImGuiTableFlags.BordersV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Type",       ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Game path",  ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Postfix",    ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Local file", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Variant",    ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("White",      ImGuiTableColumnFlags.WidthFixed, 42);
                ImGui.TableSetupColumn("BC7",        ImGuiTableColumnFlags.WidthFixed, 42);
                ImGui.TableSetupColumn("",           ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableHeadersRow();

                var item = _plugin.GameData.GetItemByRowId(_selectedRowId);

                for (int t = 0; t < mat.Textures.Count; t++)
                {
                    var tex = mat.Textures[t];
                    ImGui.TableNextRow();

                    // Type combo
                    ImGui.TableSetColumnIndex(0);
                    int typeIdx = (int)tex.Type;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.Combo($"##TexType{t}", ref typeIdx, TextureTypeInfo.Labels, TextureTypeInfo.Labels.Length))
                    {
                        tex.Type = TextureTypeInfo.FromIndex(typeIdx);
                        SaveMaterials();
                    }

                    // Game path (read-only)
                    ImGui.TableSetColumnIndex(1);
                    if (item != null)
                    {
                        var textureName = MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix);
                        var gamePath = MaterialNaming.TextureGamePath(item.Slot, item.ModelId, textureName);
                        ImGui.TextColored(ColMuted, gamePath);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Game path this texture is written to: the material's name plus this row's postfix.");
                    }
                    else
                    {
                        ImGui.TextColored(ColMuted, "—");
                    }

                    // Postfix input — appended to the material's name to form the texture name.
                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(-1);
                    string texPostfix = tex.Postfix;
                    if (ImGui.InputTextWithHint($"##TexPostfix{t}", "postfix", ref texPostfix, 64))
                    {
                        tex.Postfix = texPostfix;
                        SaveMaterials();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Appended to the material's name (\"{mat.Name}\") to form the texture name, " +
                                          "e.g. postfix \"normal\" on material \"mt_c0201e0025_top_b\" → \"mt_c0201e0025_top_b_normal\".");

                    // Local source file / variant folder / white-dummy size
                    ImGui.TableSetColumnIndex(3);
                    if (tex.UseWhiteDummy)
                    {
                        ImGui.SetNextItemWidth(-1);
                        int sizeIdx = Array.IndexOf(WhiteDummySizes, tex.WhiteDummySize);
                        if (sizeIdx < 0) sizeIdx = Array.IndexOf(WhiteDummySizes, 256);
                        if (ImGui.Combo($"##TexWhiteSize{t}", ref sizeIdx, WhiteDummyLabels, WhiteDummyLabels.Length))
                        {
                            tex.WhiteDummySize = WhiteDummySizes[sizeIdx];
                            SaveMaterials();
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Size of the generated fully white, fully opaque placeholder texture.");
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(-36);
                        string texSource = tex.SourcePath;
                        string sourceHint = tex.UseVariants ? "folder of variant images" : "png / jpeg / dds file";
                        if (ImGui.InputTextWithHint($"##TexSource{t}", sourceHint, ref texSource, 512))
                        {
                            tex.SourcePath = texSource;
                            SaveMaterials();
                        }
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"…##TexBrowse{t}"))
                        {
                            if (tex.UseVariants)
                            {
                                _fileDialogManager.OpenFolderDialog("Select variant images folder", (ok, path) =>
                                {
                                    if (!ok)
                                        return;
                                    tex.SourcePath = path;
                                    SaveMaterials();
                                });
                            }
                            else
                            {
                                _fileDialogManager.OpenFileDialog("Select texture source", "Image files{.png,.jpg,.jpeg,.dds}",
                                    (ok, path) =>
                                    {
                                        if (!ok)
                                            return;
                                        tex.SourcePath = path;
                                        SaveMaterials();
                                    });
                            }
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(tex.UseVariants
                                ? "Folder containing the variant images (png/jpeg/dds). Each file becomes one option of a switch group in the mod."
                                : "Local image file that fills this slot. Converted to .tex when the mod is created.");
                        }
                    }

                    // Variant checkbox
                    ImGui.TableSetColumnIndex(4);
                    bool variants = tex.UseVariants;
                    if (ImGui.Checkbox($"##TexVar{t}", ref variants))
                    {
                        tex.UseVariants = variants;
                        if (variants) tex.UseWhiteDummy = false;
                        SaveMaterials();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Set up this slot with variants: point the source at a folder of images; each becomes a switchable option in the mod.");

                    // White dummy checkbox
                    ImGui.TableSetColumnIndex(5);
                    bool whiteDummy = tex.UseWhiteDummy;
                    if (ImGui.Checkbox($"##TexWhite{t}", ref whiteDummy))
                    {
                        tex.UseWhiteDummy = whiteDummy;
                        if (whiteDummy) tex.UseVariants = false;
                        SaveMaterials();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Fill this slot with a generated fully white, fully opaque texture instead of a local file — no source image needed.");

                    // BC7 compression checkbox
                    ImGui.TableSetColumnIndex(6);
                    bool compress = tex.CompressBc7;
                    if (ImGui.Checkbox($"##TexBc7{t}", ref compress))
                    {
                        tex.CompressBc7 = compress;
                        SaveMaterials();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Compress to BC7 on creation. When off, the texture stays uncompressed (RGBA, B8G8R8A8).");

                    // Remove button
                    ImGui.TableSetColumnIndex(7);
                    if (ImGui.SmallButton($"X##TexDel{t}"))
                        _removeTextureAt = t;
                }

                ImGui.EndTable();

                if (_removeTextureAt >= 0 && _removeTextureAt < mat.Textures.Count)
                {
                    mat.Textures.RemoveAt(_removeTextureAt);
                    _removeTextureAt = -1;
                    SaveMaterials();
                }
            }
        }

        // ── Mod creation ─────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Mod name:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(320);
        string modNameInput = _modName;
        if (ImGui.InputText("##ModName", ref modNameInput, 128))
        {
            _modName = modNameInput;
            SaveModName();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Name of the mod folder created under Penumbra's mod directory.");

        ImGui.Spacing();

        if (ImGui.Button("Create Mod", new Vector2(120, 0)))
            CreateMod();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Converts all configured local textures to .tex and writes them into a new mod folder under Penumbra's mod directory.");

        if (!string.IsNullOrEmpty(_exportResult))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_exportResult);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Material actions
    // ─────────────────────────────────────────────────────────────────────────

    private void AddMaterial()
    {
        var preset = MaterialPresets.All[_presetIndex];
        var item = _plugin.GameData.GetItemByRowId(_selectedRowId);
        string name = item != null
            ? MaterialNaming.DefaultName(item.Slot, item.ModelId, _materials.Count + 1)
            : "Material";
        _materials.Add(MaterialSetup.FromPreset(preset, name));
        _materialIndex = _materials.Count - 1;
        SaveMaterials();
    }

    /// <summary>Re-applies the selected preset to the current material, replacing its shader and textures.</summary>
    private void ApplyPresetToSelected()
    {
        if (_materialIndex < 0 || _materialIndex >= _materials.Count) return;
        var preset = MaterialPresets.All[_presetIndex];
        var mat    = _materials[_materialIndex];
        mat.ShaderType = preset.Shader;
        mat.Textures.Clear();
        foreach (var type in preset.Textures)
            mat.Textures.Add(new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type) });
        SaveMaterials();
    }

    /// <summary>
    /// Converts every configured local texture to .tex and writes the files into
    /// a new mod folder under Penumbra's mod directory, using their game paths.
    /// Texture slots marked as variants become single-select switch groups in
    /// the mod's meta.json; everything else is written as default files.
    /// </summary>
    private void CreateMod()
    {
        var item = _plugin.GameData.GetItemByRowId(_selectedRowId);
        if (item == null) { _exportResult = "Select an item first."; return; }
        if (_materials.Count == 0 && _models.Count == 0) { _exportResult = "No materials or models defined for this item."; return; }

        var modRoot = _plugin.PenumbraIpc.GetModDirectory();
        if (string.IsNullOrEmpty(modRoot))
        {
            _exportResult = "Penumbra is not available — cannot resolve the mod directory.";
            return;
        }

        string modName = SanitizeFolderName(string.IsNullOrWhiteSpace(_modName) ? DefaultModName(item) : _modName.Trim());
        string modPath = Path.Combine(modRoot, modName);
        Directory.CreateDirectory(modPath);

        int converted = 0, skipped = 0, failed = 0;
        var details = new StringBuilder();
        var defaultFiles = new List<KeyValuePair<string, string>>();
        var groups = new List<ModVariantGroup>();
        var eqdpOverrides = new List<ModEqdpOverride>();

        var nativeRaces = _plugin.GameData.GetNativeModelRaces(item.Slot, item.ModelId);
        foreach (var model in _models)
        {
            var gamePath = ModelNaming.ModelGamePath(item.Slot, item.ModelId, model.RaceGender.RaceCode);
            if (!ProcessModel(model, gamePath, modPath, details, ref converted, ref skipped, ref failed))
                continue;

            defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
            if (!nativeRaces.Contains(model.RaceGender))
                eqdpOverrides.Add(new ModEqdpOverride { RaceGender = model.RaceGender, Slot = item.Slot, SetId = item.ModelId });
        }

        int materialsCreated = 0, materialsSkipped = 0;
        foreach (var mat in _materials)
        {
            var replacements = new Dictionary<TextureType, string>();

            foreach (var tex in mat.Textures)
            {
                var textureName = MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix);
                var gamePath = MaterialNaming.TextureGamePath(item.Slot, item.ModelId, textureName);
                bool ok;

                if (tex.UseWhiteDummy)
                {
                    ok = ProcessWhiteDummy(tex, textureName, gamePath, modPath, details, ref converted, ref failed);
                    if (ok) defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
                }
                else if (tex.UseVariants)
                {
                    ok = ProcessVariantFolder(mat, tex, textureName, gamePath, modPath, details, groups, ref converted, ref skipped, ref failed);
                }
                else
                {
                    ok = ProcessSingleTexture(tex, textureName, gamePath, modPath, details, ref converted, ref skipped, ref failed);
                    if (ok) defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
                }

                if (ok) replacements[tex.Type] = gamePath;
            }

            if (ProcessMaterial(item, mat, replacements, modPath, details, ref materialsCreated, ref materialsSkipped))
            {
                var matGamePath = MaterialNaming.MaterialGamePath(item.Slot, item.ModelId, mat.MaterialVariant, mat.Name);
                defaultFiles.Add(new KeyValuePair<string, string>(matGamePath, matGamePath));
            }
        }

        ModMetaWriter.Write(modPath, modName, defaultFiles, groups, eqdpOverrides);

        // Register the new mod folder with Penumbra directly, so it shows up without
        // the user having to run a full mod-directory rediscovery.
        var addResult = _plugin.PenumbraIpc.AddMod(modName);

        var summary = new StringBuilder();
        summary.Append($"{converted} file(s) converted, {skipped} skipped, {failed} failed.");
        summary.Append($" {materialsCreated} material(s) created, {materialsSkipped} skipped.");
        if (groups.Count > 0)
            summary.Append($" {groups.Count} variant group(s) created.");
        if (eqdpOverrides.Count > 0)
            summary.Append($" {eqdpOverrides.Count} race model override(s) added.");
        summary.Append(addResult is PenumbraApiEc.Success or PenumbraApiEc.NothingDone
            ? " Registered with Penumbra."
            : $" Penumbra did not pick it up automatically ({addResult}) — run a mod rediscovery.");
        if (failed == 0)
            summary.Append($" Mod folder: {modPath}");
        _exportResult = summary.ToString() + "\n" + details;

        Plugin.Log.Information("[XPS] Mod creation for {0}: {1} converted, {2} skipped, {3} failed, {4} variant groups, {5} eqdp overrides, AddMod={6}. Path: {7}",
            item.Name, converted, skipped, failed, groups.Count, eqdpOverrides.Count, addResult, modPath);
    }

    /// <summary>Copies a race's local .mdl source file into the mod at its game path. Returns true on success.</summary>
    private bool ProcessModel(RaceModelEntry model, string gamePath, string modPath, StringBuilder details,
        ref int converted, ref int skipped, ref int failed)
    {
        if (string.IsNullOrWhiteSpace(model.SourcePath))
        {
            skipped++;
            details.AppendLine($"  - skipped: {model.RaceGender.DisplayName} model (no local file set)");
            return false;
        }

        if (!File.Exists(model.SourcePath))
        {
            failed++;
            details.AppendLine($"  X failed:  {model.RaceGender.DisplayName} model — file not found ({model.SourcePath})");
            return false;
        }

        var localPath = Path.Combine(modPath, gamePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.Copy(model.SourcePath, localPath, true);
        converted++;
        details.AppendLine($"  ok:       {gamePath}{(model.IsVanillaDummy ? " (empty dummy — still needs modeling)" : "")}");
        return true;
    }

    /// <summary>
    /// Clones a vanilla material and rewires it to the textures that were successfully
    /// converted for this material, matched by <see cref="TextureType"/> to whichever of
    /// vanilla's own texture slots carries that role. Returns true on success.
    /// </summary>
    private bool ProcessMaterial(GameDataService.GameItem item, MaterialSetup mat, Dictionary<TextureType, string> replacements,
        string modPath, StringBuilder details, ref int materialsCreated, ref int materialsSkipped)
    {
        if (replacements.Count == 0)
        {
            if (mat.Textures.Count > 0)
            {
                materialsSkipped++;
                details.AppendLine($"  - skipped material: {mat.Name} (no textures converted)");
            }
            return false;
        }

        var template = _plugin.GameData.FindVanillaMaterialTemplate(item.Slot, item.ModelId, mat.MaterialVariant);
        if (template == null)
        {
            materialsSkipped++;
            details.AppendLine($"  X failed material: {mat.Name} — no vanilla material found to clone for this item/variant");
            return false;
        }

        if (!MaterialPatcher.TryBuildPatchedMaterial(template.Value.RawBytes, template.Value.Material, replacements,
                out var mtrlData, out var appliedTypes, out var error))
        {
            materialsSkipped++;
            details.AppendLine($"  X failed material: {mat.Name} — {error}");
            return false;
        }

        var gamePath = MaterialNaming.MaterialGamePath(item.Slot, item.ModelId, mat.MaterialVariant, mat.Name);
        var localPath = Path.Combine(modPath, gamePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllBytes(localPath, mtrlData);
        materialsCreated++;

        var unapplied = replacements.Keys.Except(appliedTypes).ToList();
        var note = unapplied.Count > 0
            ? $" (cloned from {template.Value.GamePath}; vanilla has no slot for: {string.Join(", ", unapplied)})"
            : $" (cloned from {template.Value.GamePath})";
        details.AppendLine($"  ok:       {gamePath}{note}");
        return true;
    }

    /// <summary>Converts a single source image and writes it at its game path. Returns true on success.</summary>
    private bool ProcessSingleTexture(TextureSlot tex, string textureName, string gamePath, string modPath, StringBuilder details,
        ref int converted, ref int skipped, ref int failed)
    {
        if (string.IsNullOrWhiteSpace(tex.SourcePath))
        {
            skipped++;
            details.AppendLine($"  - skipped: {textureName} (no local file set)");
            return false;
        }

        if (!File.Exists(tex.SourcePath))
        {
            failed++;
            details.AppendLine($"  X failed:  {textureName} — file not found ({tex.SourcePath})");
            return false;
        }

        if (!TextureConverter.Convert(tex.SourcePath, tex.CompressBc7, out var texData, out var error))
        {
            failed++;
            details.AppendLine($"  X failed:  {textureName} — {error}");
            return false;
        }

        var localPath = Path.Combine(modPath, gamePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllBytes(localPath, texData);
        converted++;
        details.AppendLine($"  ok:       {gamePath}");
        return true;
    }

    /// <summary>Generates a fully white placeholder texture and writes it at its game path. Returns true on success.</summary>
    private bool ProcessWhiteDummy(TextureSlot tex, string textureName, string gamePath, string modPath, StringBuilder details,
        ref int converted, ref int failed)
    {
        if (!TextureConverter.CreateWhiteDummy(tex.WhiteDummySize, tex.CompressBc7, out var texData, out var error))
        {
            failed++;
            details.AppendLine($"  X failed:  {textureName} — {error}");
            return false;
        }

        var localPath = Path.Combine(modPath, gamePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllBytes(localPath, texData);
        converted++;
        details.AppendLine($"  ok:       {gamePath} (white {tex.WhiteDummySize}x{tex.WhiteDummySize} dummy)");
        return true;
    }

    /// <summary>
    /// Converts every image in the slot's folder into a variant of the texture
    /// and registers a single-select switch group for it. Returns true when at
    /// least one variant was added.
    /// </summary>
    private bool ProcessVariantFolder(MaterialSetup mat, TextureSlot tex, string textureName, string gamePath, string modPath,
        StringBuilder details, List<ModVariantGroup> groups, ref int converted, ref int skipped, ref int failed)
    {
        var folder = tex.SourcePath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            failed++;
            details.AppendLine($"  X failed:  {textureName} — variant folder not found ({folder})");
            return false;
        }

        var images = Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".dds")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (images.Count == 0)
        {
            failed++;
            details.AppendLine($"  X failed:  {textureName} — no image files found in variant folder");
            return false;
        }

        var group = new ModVariantGroup
        {
            Name     = $"{mat.Name} / {textureName}",
            GamePath = gamePath,
        };
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var imagePath in images)
        {
            if (!TextureConverter.Convert(imagePath, tex.CompressBc7, out var texData, out var error))
            {
                failed++;
                details.AppendLine($"  X failed:  {textureName} variant '{Path.GetFileName(imagePath)}' — {error}");
                continue;
            }

            var stem = MaterialNaming.SanitizeFileName(Path.GetFileNameWithoutExtension(imagePath));
            var fileName = $"{MaterialNaming.SanitizeFileName(textureName)}_{stem}.tex";
            int counter = 2;
            while (!usedNames.Add(fileName))
                fileName = $"{MaterialNaming.SanitizeFileName(textureName)}_{stem}_{counter++}.tex";

            var variantRelPath = $"variants/{fileName}";
            var variantPath = Path.Combine(modPath, "variants", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(variantPath)!);
            File.WriteAllBytes(variantPath, texData);

            group.Options.Add((Path.GetFileNameWithoutExtension(imagePath), variantRelPath));
            converted++;
            details.AppendLine($"  ok:       {gamePath} ← {variantRelPath}");
        }

        if (group.Options.Count == 0)
        {
            failed++;
            details.AppendLine($"  X failed:  {textureName} — no variants could be converted");
            return false;
        }

        groups.Add(group);
        return true;
    }

    /// <summary>Replaces path-invalid characters so item names can be used as folder names.</summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    private void DuplicateMaterial()
    {
        if (_materialIndex < 0 || _materialIndex >= _materials.Count) return;
        var copy = _materials[_materialIndex].Clone();
        copy.Name = $"{copy.Name} copy";
        _materials.Insert(_materialIndex + 1, copy);
        _materialIndex++;
        SaveMaterials();
    }

    private void RemoveMaterial()
    {
        if (_materialIndex < 0 || _materialIndex >= _materials.Count) return;
        _materials.RemoveAt(_materialIndex);
        _materialIndex = Math.Min(_materialIndex, _materials.Count - 1);
        SaveMaterials();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Persistence / helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Persists the current material list under the selected item.</summary>
    private void SaveMaterials()
    {
        var cfg = _plugin.Configuration;
        if (_selectedRowId == 0) return;
        cfg.MaterialsByItem[_selectedRowId] = _materials
            .Select(m => m.Clone())
            .ToList();
        cfg.Save();
    }

    /// <summary>Loads the material list for the currently selected item.</summary>
    private void LoadMaterialsForSelection()
    {
        _materials = _plugin.Configuration.MaterialsByItem.TryGetValue(_selectedRowId, out var list)
            ? list.Select(m => m.Clone()).ToList()
            : new List<MaterialSetup>();
        _materialIndex = _materials.Count > 0 ? 0 : -1;
    }

    /// <summary>Persists the current model list under the selected item.</summary>
    private void SaveModels()
    {
        var cfg = _plugin.Configuration;
        if (_selectedRowId == 0) return;
        cfg.ModelsByItem[_selectedRowId] = _models
            .Select(m => m.Clone())
            .ToList();
        cfg.Save();
    }

    /// <summary>Loads the model list for the currently selected item.</summary>
    private void LoadModelsForSelection()
    {
        _models = _plugin.Configuration.ModelsByItem.TryGetValue(_selectedRowId, out var list)
            ? list.Select(m => m.Clone()).ToList()
            : new List<RaceModelEntry>();
        SortModels();
    }

    /// <summary>Keeps the model list ordered by race code (0101, 0201, 0301, …) rather than insertion order.</summary>
    private void SortModels()
        => _models.Sort((a, b) => string.CompareOrdinal(a.RaceGender.RaceCode, b.RaceGender.RaceCode));

    /// <summary>Persists the current mod name under the selected item.</summary>
    private void SaveModName()
    {
        var cfg = _plugin.Configuration;
        if (_selectedRowId == 0) return;
        cfg.ModNameByItem[_selectedRowId] = _modName;
        cfg.Save();
    }

    /// <summary>Loads the mod name for the currently selected item, defaulting to an auto-generated one.</summary>
    private void LoadModNameForSelection()
    {
        if (_plugin.Configuration.ModNameByItem.TryGetValue(_selectedRowId, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            _modName = name;
            return;
        }

        var item = _selectedRowId != 0 ? _plugin.GameData.GetItemByRowId(_selectedRowId) : null;
        _modName = DefaultModName(item);
    }

    /// <summary>Auto-generated mod name used until the user overrides it.</summary>
    private static string DefaultModName(GameDataService.GameItem? item)
        => item != null ? $"XIV Port Studio - {SanitizeFolderName(item.Name)}" : "XIV Port Studio - Mod";

    private void SaveItemSelectionToConfig()
    {
        var cfg  = _plugin.Configuration;
        cfg.LastSlotIndex   = _slotIndex;
        cfg.LastItemRowId   = _selectedRowId;
        cfg.LastItemSearch  = _search;
        cfg.Save();
    }

    private void RestoreItemSelection(uint rowId)
    {
        var item = rowId != 0 ? _plugin.GameData.GetItemByRowId(rowId) : null;
        if (item != null)
        {
            // If the saved item belongs to the current slot, restore it.
            if (item.Slot == _allSlots[_slotIndex])
                _selectedRowId = item.RowId;
            else
            {
                // Otherwise jump to the slot the saved item belongs to.
                int slotIdx = Array.IndexOf(_allSlots, item.Slot);
                if (slotIdx >= 0)
                {
                    _slotIndex = slotIdx;
                    ReloadItemList();
                    _selectedRowId = item.RowId;
                }
            }
        }
        ApplyFilter();
    }

    private void ReloadItemList()
    {
        _allItems = _plugin.GameData.GetAllItemsForSlot(_allSlots[_slotIndex]);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(_search))
        {
            _filtered = _allItems;
            return;
        }

        var q = _search.Trim();
        bool StartsWithQ(GameDataService.GameItem i) =>
            i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdPadded.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdDisplay.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        bool ContainsQ(GameDataService.GameItem i) =>
            i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdPadded.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdDisplay.Contains(q, StringComparison.OrdinalIgnoreCase);

        var starts   = _allItems.Where(StartsWithQ)
                                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var contains = _allItems.Where(i => !StartsWithQ(i) && ContainsQ(i))
                                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

        _filtered = starts.Concat(contains).ToList();
    }
}
