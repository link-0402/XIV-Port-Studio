using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Stage 3 — the materials of the port: their shader and the textures that fill them.
/// The canvas lists materials; the inspector edits the selected one, with a card per
/// texture showing a preview, where it comes from, and where it lands in the game.
/// </summary>
internal sealed class MaterialsPanel : IStagePanel
{
    private enum SourceMode { File, Variants, White }

    private static readonly string[] SourceModeLabels = { "File", "Variant folder", "Dummy" };
    private static readonly int[]    WhiteDummySizes  = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };
    private static readonly string[] WhiteDummyLabels = WhiteDummySizes.Select(s => $"{s} × {s}").ToArray();
    private const int DefaultDummySize = 32;

    private const string ConfirmRemoveId = "Remove material?##XPSConfirmRemoveMat";
    private const string ConfirmPresetId = "Apply preset?##XPSConfirmPreset";

    private readonly Plugin _plugin;
    private readonly PortSession _session;
    private readonly TextureThumbnailCache _thumbnails;

    private const string ConfirmMatchId = "Match vanilla materials?##XPSConfirmMatch";

    private int _newPresetIndex;
    private int _applyPresetIndex;
    private int _pendingRemove = -1;
    private bool _pendingMatch;
    private uint _presetSuggestedFor;

    /// <summary>
    /// Points the "new material" preset at what suits the subject, once per subject: hair →
    /// Hair, face → Skin, tails and ears → whatever shader their vanilla material uses.
    /// </summary>
    private void SuggestPreset(PortSubject subject)
    {
        if (_presetSuggestedFor == subject.Key)
            return;
        _presetSuggestedFor = subject.Key;

        ShaderType? shader = subject.Kind switch
        {
            SubjectKind.Hair => ShaderType.Hair,
            SubjectKind.Face => ShaderType.Skin,
            SubjectKind.Gear => null,
            _ => subject.BaseRace is { } race
                ? _plugin.GameData.ReadVanillaMaterialLayout(subject, race)?.FirstOrDefault()?.ShaderType
                : null,
        };
        if (shader == null)
            return;

        for (int i = 0; i < MaterialPresets.All.Count; i++)
        {
            if (MaterialPresets.All[i].Shader == shader)
            {
                _newPresetIndex = _applyPresetIndex = i;
                return;
            }
        }
    }

    public MaterialsPanel(Plugin plugin, PortSession session, TextureThumbnailCache thumbnails)
    {
        _plugin     = plugin;
        _session    = session;
        _thumbnails = thumbnails;
    }

    public StageId         Id      => StageId.Materials;
    public string          Title   => "Materials";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Palette;
    public string          Purpose => "Each material's shader, and the texture files that fill it.";

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        if (_session.Subject == null)
        {
            Ui.Hint("Select an item or feature on the left first.");
            return;
        }

        var subject = _session.Subject!;
        SuggestPreset(subject);

        Ui.SectionHeader("Materials", subject is GearSubject
            ? "Materials are shared by every race. Each one creates a mesh slot on the Models stage."
            : "Each material creates a mesh slot on the Models stage. \"Match vanilla\" copies the game's own material layout.");

        ImGui.SetNextItemWidth(Theme.S(190));
        ImGui.Combo("##NewPreset", ref _newPresetIndex, MaterialPresets.Names, MaterialPresets.Names.Length);
        Ui.Tooltip("Starting point for a new material: its shader and texture slots.");
        ImGui.SameLine();
        if (Ui.IconTextButton(FontAwesomeIcon.Plus, "New material", "Adds a material using the preset on the left."))
            _session.AddMaterial(MaterialPresets.All[_newPresetIndex]);
        ImGui.SameLine();
        if (Ui.IconTextButton(FontAwesomeIcon.Magic, "Match vanilla",
                "Replaces the material list with the vanilla model's own materials: their names, shaders and texture slots."))
        {
            if (_session.Materials.Count == 0) _session.MatchVanillaMaterials();
            else                               _pendingMatch = true;
        }

        ImGui.Spacing();

        if (_session.Materials.Count == 0)
        {
            Ui.Hint(subject is GearSubject
                ? "(no materials yet — pick a preset and add one)"
                : "(no materials yet — \"Match vanilla\" is the quickest start: the game's material names are what your model should use)");
            return;
        }

        if (ImGui.BeginTable("##Materials", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("##state",  ImGuiTableColumnFlags.WidthFixed, Theme.S(20));
            ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Shader",   ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Textures", ImGuiTableColumnFlags.WidthFixed, Theme.S(64));
            ImGui.TableSetupColumn("Slots",    ImGuiTableColumnFlags.WidthFixed, Theme.S(44));
            ImGui.TableHeadersRow();

            for (int i = 0; i < _session.Materials.Count; i++)
                DrawMaterialRow(i);

            ImGui.EndTable();
        }

        Ui.Hint("Right-click a material for more actions.");
    }

    private void DrawMaterialRow(int i)
    {
        var mat = _session.Materials[i];
        var target = new Target(TargetKind.Material, i);
        ImGui.TableNextRow();
        ImGui.PushID(i);

        ImGui.TableNextColumn();
        MaterialMarker(i);

        ImGui.TableNextColumn();
        bool selected = _session.SelectedMaterial == i;
        var label = string.IsNullOrWhiteSpace(mat.Name) ? "(unnamed)" : mat.Name;
        if (ImGui.Selectable($"{label}##mat", selected, ImGuiSelectableFlags.SpanAllColumns))
        {
            _session.SelectedMaterial = i;
            _session.SelectedTexture = -1;
        }
        if (_session.TakeScrollRequest(target))
            ImGui.SetScrollHereY(0.3f);

        if (ImGui.BeginPopupContextItem("##matctx"))
        {
            _session.SelectedMaterial = i;
            if (ImGui.MenuItem("Duplicate"))
                _session.DuplicateMaterial(i);
            if (ImGui.MenuItem("Remove…"))
                _pendingRemove = i;
            ImGui.EndPopup();
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.Muted, ShaderInfo.DisplayName(mat.ShaderType));

        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.Muted, mat.Textures.Count.ToString());

        ImGui.TableNextColumn();
        int usedBy = _session.ModelMaterialSlots.Count(s => s.MaterialIndex == i);
        ImGui.TextColored(usedBy == 0 ? Theme.Warn : Theme.Muted, usedBy.ToString());
        Ui.Tooltip(usedBy == 0
            ? "No mesh slot uses this material, so no .mtrl is written for it. Assign it on the Models stage."
            : $"Used by {usedBy} mesh slot(s).");

        ImGui.PopID();
    }

    /// <summary>Marker for a material row: its own issues, or the worst of its textures'.</summary>
    private void MaterialMarker(int materialIndex)
    {
        var own = PortValidator.Worst(_session.Issues, new Target(TargetKind.Material, materialIndex));
        if (own != null)
        {
            IssueList.Marker(_session, new Target(TargetKind.Material, materialIndex));
            return;
        }

        var textureIssues = _session.Issues
            .Where(x => x.Target.Kind == TargetKind.Texture && x.Target.Index == materialIndex)
            .ToList();
        if (textureIssues.Count == 0)
        {
            IssueList.Marker(_session, new Target(TargetKind.Material, materialIndex));
            return;
        }

        var worst = textureIssues.Max(x => x.Severity);
        Ui.Icon(IssueList.IconFor(worst), Ui.SeverityColor(worst));
        Ui.Tooltip(string.Join("\n", textureIssues.Select(x => x.Message)));
    }

    /// <summary>
    /// Drawn from the window root: removal can be asked for from the canvas (context menu)
    /// or the inspector, and a modal must be opened and drawn under the same ID stack.
    /// </summary>
    public void DrawPopups()
    {
        if (_session.MatchVanillaRequested)
        {
            _session.MatchVanillaRequested = false;
            _pendingMatch = true;
        }

        if (_pendingMatch)
        {
            _pendingMatch = false;
            ImGui.OpenPopup(ConfirmMatchId);
        }

        if (ImGui.BeginPopupModal(ConfirmMatchId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Replace all {_session.Materials.Count} material(s) with the vanilla model's materials?");
            ImGui.TextColored(Theme.Muted, "Texture source paths set on the current materials are cleared.");
            ImGui.Spacing();
            if (ImGui.Button("Replace", new Vector2(Theme.S(100), 0)))
            {
                _session.MatchVanillaMaterials();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (_pendingRemove >= 0)
        {
            ImGui.OpenPopup(ConfirmRemoveId);
            _removeTarget = _pendingRemove;
            _pendingRemove = -1;
        }

        if (ImGui.BeginPopupModal(ConfirmRemoveId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var name = _removeTarget >= 0 && _removeTarget < _session.Materials.Count ? _session.Materials[_removeTarget].Name : "";
            ImGui.TextUnformatted($"Remove \"{name}\" and its texture set-up?");
            ImGui.TextColored(Theme.Muted, "Files on disk are not touched.");
            ImGui.Spacing();
            if (ImGui.Button("Remove", new Vector2(Theme.S(100), 0)))
            {
                _session.RemoveMaterial(_removeTarget);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private int _removeTarget = -1;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        var subject = _session.Subject;
        var mat = _session.CurrentMaterial;
        if (subject == null || mat == null)
        {
            Ui.Hint(subject == null ? "Nothing selected." : "Select a material to edit it.");
            return;
        }

        int index = _session.SelectedMaterial;
        DrawMaterialHeader(subject, mat, index);

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Accent, "Textures");
        ImGui.SameLine();
        Ui.AlignRight(ImGuiComponentsWidth("Add texture"));
        if (Ui.IconTextButton(FontAwesomeIcon.Plus, "Add texture", "Adds a texture slot for the next unused texture type."))
            _session.AddTexture(mat);
        ImGui.Separator();
        ImGui.Spacing();

        if (mat.Textures.Count == 0)
        {
            Ui.Hint("(no textures — add one to bind a diffuse / normal / … map)");
            return;
        }

        int remove = -1;
        for (int t = 0; t < mat.Textures.Count; t++)
        {
            if (DrawTextureCard(subject, mat, index, t))
                remove = t;
        }
        if (remove >= 0)
            _session.RemoveTexture(mat, remove);
    }

    private void DrawMaterialHeader(PortSubject subject, MaterialSetup mat, int index)
    {
        Ui.SectionHeader(string.IsNullOrWhiteSpace(mat.Name) ? "(unnamed material)" : mat.Name);

        string name = mat.Name;
        if (Ui.LabeledInput("Name", "##MatName", ref name, 128, subject.DefaultMaterialName(index + 1),
                "Also the prefix of every texture name in this material."))
        {
            mat.Name = name;
            _session.MarkDirty();
        }

        int shaderIdx = (int)mat.ShaderType;
        if (Ui.LabeledCombo("Shader", "##MatShader", ref shaderIdx, ShaderInfo.Labels,
                $"Shader pack: {ShaderInfo.ShaderPackName(mat.ShaderType)}"))
        {
            mat.ShaderType = (ShaderType)shaderIdx;
            _session.MarkDirty();
        }

        // Where the .mtrl will come from, so a shader with no template is obvious now, not at build time.
        Ui.Label("Template");
        var configured = mat.Textures.Select(t => t.Type).ToHashSet();
        var preset = MaterialPresetLibrary.FindPresetPath(mat.ShaderType, configured);
        if (preset != null)
            ImGui.TextColored(Theme.Muted, $"bundled preset · {System.IO.Path.GetFileNameWithoutExtension(preset)}");
        else if (_plugin.GameData.HasVanillaMaterial(subject))
            ImGui.TextColored(Theme.Warn, "no bundled preset — cloned from the vanilla material");
        else
            ImGui.TextColored(Theme.Bad, "no bundled preset and no vanilla material to clone");

        var slots = _session.ModelMaterialSlots
            .Select((s, i) => (s, i))
            .Where(x => x.s.MaterialIndex == index)
            .ToList();
        Ui.Label("Mesh slots", "Mesh slots on the Models stage that use this material. One .mtrl is written per slot.");
        if (slots.Count == 0)
        {
            ImGui.TextColored(Theme.Warn, "none — nothing is written for this material");
        }
        else
        {
            for (int k = 0; k < slots.Count; k++)
            {
                if (k > 0) ImGui.SameLine();
                if (ImGui.SmallButton($"{slots[k].i + 1}##slot{k}"))
                    _session.Focus(new Target(TargetKind.MeshSlot, slots[k].i));
                var slotName = _session.EffectiveMaterialName(slots[k].s);
                Ui.Tooltip(string.Join("\n", subject.MaterialRaces(_session.Models)
                    .Select(r => subject.MaterialGamePath(subject.MaterialNameFor(slotName, r), r))));
            }
        }

        ImGui.Spacing();
        Ui.Label("Preset");
        float applyW = ImGuiComponentsWidth("Apply");
        ImGui.SetNextItemWidth(-(applyW + ImGui.GetStyle().ItemSpacing.X));
        ImGui.Combo("##ApplyPreset", ref _applyPresetIndex, MaterialPresets.Names, MaterialPresets.Names.Length);
        ImGui.SameLine();
        if (Ui.IconTextButton(FontAwesomeIcon.Redo, "Apply", "Replaces this material's shader and texture slots with the preset's."))
            ImGui.OpenPopup(ConfirmPresetId);

        if (ImGui.BeginPopupModal(ConfirmPresetId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Replace the shader and all {mat.Textures.Count} texture slot(s) of \"{mat.Name}\"");
            ImGui.TextUnformatted($"with the \"{MaterialPresets.Names[_applyPresetIndex]}\" preset?");
            ImGui.TextColored(Theme.Muted, "Texture source paths set on this material are cleared.");
            ImGui.Spacing();
            if (ImGui.Button("Apply", new Vector2(Theme.S(100), 0)))
            {
                _session.ApplyPreset(mat, MaterialPresets.All[_applyPresetIndex]);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        if (Ui.IconTextButton(FontAwesomeIcon.Copy, "Duplicate"))
            _session.DuplicateMaterial(index);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Ui.IconTextButton(FontAwesomeIcon.Trash, "Remove"))
            _pendingRemove = index;
        ImGui.PopStyleColor();
    }

    /// <summary>Draws one texture as a card. Returns true when its remove button was clicked.</summary>
    private bool DrawTextureCard(PortSubject subject, MaterialSetup mat, int matIndex, int t)
    {
        var tex = mat.Textures[t];
        var target = new Target(TargetKind.Texture, matIndex, t);
        bool remove = false;

        ImGui.PushID(t);
        if (_session.TakeScrollRequest(target))
            ImGui.SetScrollHereY(0.2f);

        bool focused = _session.SelectedTexture == t;
        if (!ImGui.BeginTable("##card", 2, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.PadOuterX))
        {
            ImGui.PopID();
            return false;
        }

        bool showThumbs = _plugin.Configuration.ShowThumbnails;
        ImGui.TableSetupColumn("##thumb", ImGuiTableColumnFlags.WidthFixed, showThumbs ? Theme.ThumbSize : Theme.S(18));
        ImGui.TableSetupColumn("##body",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        if (focused)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.Focus));

        // ── Thumbnail + issue marker ─────────────────────────────────────
        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(0, Theme.S(2)));
        if (showThumbs)
            _thumbnails.Draw(tex, Theme.ThumbSize);
        IssueList.Marker(_session, target);

        // ── Body ─────────────────────────────────────────────────────────
        ImGui.TableNextColumn();
        ImGui.Dummy(new Vector2(0, Theme.S(2)));
        bool edited = false;

        int typeIdx = (int)tex.Type;
        ImGui.SetNextItemWidth(Theme.S(130));
        if (ImGui.Combo("##Type", ref typeIdx, TextureTypeInfo.Labels, TextureTypeInfo.Labels.Length))
        {
            var newType = TextureTypeInfo.FromIndex(typeIdx);
            // Follow the type with the postfix while it is still the default.
            if (tex.Postfix == MaterialNaming.DefaultPostfix(tex.Type))
                tex.Postfix = MaterialNaming.DefaultPostfix(newType);
            tex.Type = newType;
            edited = true;
        }
        Ui.Tooltip("What the material uses this texture for.");

        ImGui.SameLine();
        float deleteW = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(deleteW + ImGui.GetStyle().ItemSpacing.X));
        string postfix = tex.Postfix;
        if (ImGui.InputTextWithHint("##Postfix", "postfix", ref postfix, 64))
        {
            tex.Postfix = postfix;
            edited = true;
        }
        Ui.Tooltip($"Appended to the material's name to form the texture name: \"{MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix)}\".");

        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Trash, "Remove", "Remove this texture", danger: true))
            remove = true;

        // Source mode — one choice instead of two independent checkboxes. Stored as the
        // same two flags as before, so saved set-ups keep loading.
        var mode = tex.UseWhiteDummy ? SourceMode.White : tex.UseVariants ? SourceMode.Variants : SourceMode.File;
        var dummyOptions = DummyTextureLibrary.OptionsFor(tex.Type);
        var resolvedDummy = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);

        for (int m = 0; m < SourceModeLabels.Length; m++)
        {
            if (m > 0) ImGui.SameLine();
            if (ImGui.RadioButton($"{SourceModeLabels[m]}##mode", (int)mode == m))
            {
                tex.UseVariants   = m == (int)SourceMode.Variants;
                tex.UseWhiteDummy = m == (int)SourceMode.White;
                mode = (SourceMode)m;
                edited = true;
            }
            Ui.Tooltip(m switch
            {
                0 => "One image file (png / jpeg / dds), converted to .tex on build.",
                1 => "A folder of images — each becomes one option of a switch group in the mod.",
                _ => dummyOptions.Count > 1
                    ? "A placeholder texture — pick which one below. No source image needed."
                    : $"A bundled placeholder texture ({resolvedDummy.Label}). No source image needed.",
            });
        }

        ImGui.SameLine();
        Ui.AlignRight(ImGui.GetFrameHeight() + ImGui.CalcTextSize("BC7").X + ImGui.GetStyle().ItemInnerSpacing.X);
        bool bc7 = tex.CompressBc7;
        using (Ui.Disabled(mode == SourceMode.White && !resolvedDummy.IsGenerated))
        {
            if (ImGui.Checkbox("BC7", ref bc7))
            {
                tex.CompressBc7 = bc7;
                edited = true;
            }
        }
        Ui.Tooltip(mode == SourceMode.White && !resolvedDummy.IsGenerated
            ? "Not applicable — this bundled placeholder is copied in as-is."
            : "Compress to BC7 when building. Off: uncompressed B8G8R8A8.");

        if (mode == SourceMode.White)
        {
            if (dummyOptions.Count > 1)
            {
                var labels = dummyOptions.Select(o => o.Label).ToArray();
                int presetIdx = Math.Max(0, dummyOptions.ToList().FindIndex(o => o.Key == resolvedDummy.Key));
                ImGui.SetNextItemWidth(Theme.S(150));
                if (ImGui.Combo("##DummyPreset", ref presetIdx, labels, labels.Length))
                {
                    tex.DummyPreset = dummyOptions[presetIdx].Key;
                    resolvedDummy = dummyOptions[presetIdx];
                    edited = true;
                }
                Ui.Tooltip("Which placeholder texture fills this slot.");
                ImGui.SameLine();
            }

            if (resolvedDummy.IsGenerated)
            {
                int sizeIdx = Array.IndexOf(WhiteDummySizes, tex.WhiteDummySize);
                if (sizeIdx < 0) sizeIdx = Array.IndexOf(WhiteDummySizes, DefaultDummySize);
                ImGui.SetNextItemWidth(Theme.S(130));
                if (ImGui.Combo("##WhiteSize", ref sizeIdx, WhiteDummyLabels, WhiteDummyLabels.Length))
                {
                    tex.WhiteDummySize = WhiteDummySizes[sizeIdx];
                    edited = true;
                }
                Ui.Tooltip("Size of the generated white texture.");
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, $"Bundled \"{resolvedDummy.Label}\" texture — no size or format options.");
            }
        }
        else
        {
            string src = tex.SourcePath;
            var kind = mode == SourceMode.Variants ? PathKind.Folder : PathKind.File;
            var hint = mode == SourceMode.Variants ? "folder of variant images" : "png / jpeg / dds file";
            if (Ui.PathPicker("Source", ref src, hint, kind, "Image files{.png,.jpg,.jpeg,.dds}",
                    picked => { tex.SourcePath = picked; _session.SelectedTexture = t; _session.MarkDirty(); }))
            {
                tex.SourcePath = src;
                edited = true;
            }
        }

        var gamePath = subject.TextureGamePath(MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix));
        ImGui.TextColored(Theme.Faint, gamePath);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Game path this texture is written to. Click to copy.");
        if (ImGui.IsItemClicked())
            ImGui.SetClipboardText(gamePath);

        ImGui.EndTable();
        ImGui.PopID();

        if (edited)
        {
            _session.SelectedTexture = t;
            _session.MarkDirty();
        }

        ImGui.Spacing();
        return remove;
    }

    /// <summary>Width an icon+text button of this label takes, for right-aligning it.</summary>
    private static float ImGuiComponentsWidth(string label)
        => Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Plus, label);
}
