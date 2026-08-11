using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    // ── Material state ───────────────────────────────────────────────────────

    private List<MaterialSetup> _materials = new();
    private int                 _materialIndex = -1;
    private int                 _removeTextureAt = -1;   // handled after the table loop

    private bool _penumbraAvailable = false;

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
        LoadMaterialsForSelection();
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

        // ── Right column: material set-up ─────────────────────────────────
        ImGui.BeginChild("##XPSRight", new Vector2(-1, -1), true);
        DrawMaterialEditor();
        ImGui.EndChild();
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
                            LoadMaterialsForSelection();
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

        if (ImGui.Button("+ Add Material", new Vector2(120, 0)))
            AddMaterial();
        ImGui.SameLine();
        if (!(_materialIndex >= 0 && _materialIndex < _materials.Count)) ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate", new Vector2(90, 0)))
            DuplicateMaterial();
        ImGui.SameLine();
        if (ImGui.Button("Remove", new Vector2(90, 0)))
            RemoveMaterial();
        if (!(_materialIndex >= 0 && _materialIndex < _materials.Count)) ImGui.EndDisabled();

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
            ImGui.SetTooltip($"Shader pack: {ShaderInfo.ShaderPackPath(mat.ShaderType)}");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Textures ─────────────────────────────────────────────────────
        ImGui.Text("Textures:");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Add Texture"))
        {
            mat.Textures.Add(new TextureSlot());
            SaveMaterials();
        }

        ImGui.Spacing();

        if (mat.Textures.Count == 0)
        {
            ImGui.TextColored(ColMuted, "(no textures — add one to bind a diffuse/normal/… map)");
        }
        else
        {
            if (ImGui.BeginTable("##TexTable", 3,
                    ImGuiTableFlags.BordersV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Type",  ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Path",  ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableHeadersRow();

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

                    // Path input
                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(-1);
                    string texPath = tex.Path;
                    if (ImGui.InputTextWithHint($"##TexPath{t}", "chara/… or local file path", ref texPath, 512))
                    {
                        tex.Path = texPath;
                        SaveMaterials();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Game path (e.g. chara/equipment/e0164/material/v0001/mt_c0101b0001_d.tex) or a path to a texture file to import.");

                    // Remove button
                    ImGui.TableSetColumnIndex(2);
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
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Material actions
    // ─────────────────────────────────────────────────────────────────────────

    private void AddMaterial()
    {
        int idx = _materials.Count + 1;
        _materials.Add(new MaterialSetup
        {
            Name = $"Material {idx}",
            Textures = { new TextureSlot() { Type = TextureType.Diffuse } },
        });
        _materialIndex = _materials.Count - 1;
        SaveMaterials();
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
