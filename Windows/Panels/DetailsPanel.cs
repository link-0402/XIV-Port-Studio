using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using XIVPortStudio.Models;
using XIVPortStudio.Services;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows.Panels;

/// <summary>
/// Stage 2 — one item of the modpack in full: its race model files, its materials
/// (one per mesh slot, in order) and their textures, with its Penumbra metadata below. Rows carry
/// their main inputs inline — the path fields also take files dropped from the Sims importer
/// or from Explorer — and the inspector on the right shows the selected row in full.
/// </summary>
internal sealed class DetailsPanel : IStagePanel
{
    private const string ConfirmRemoveItemId     = "Remove from modpack?##XPSConfirmRemoveItem";
    private const string ConfirmRemoveMaterialId = "Remove material?##XPSConfirmRemoveMat";
    private const string ConfirmMatchId          = "Match vanilla materials?##XPSConfirmMatch";
    private const string ConfirmPresetId         = "Apply preset?##XPSConfirmPreset";
    private const string ConfirmPasteId          = "Paste material##XPSConfirmPaste";

    private readonly Plugin _plugin;
    private readonly PortSession _session;
    private readonly TextureSlotEditor _textures;
    private readonly ModelInspector _inspector;
    private readonly ItemMetaPanel _meta;

    // Confirmations asked for from a row, opened and drawn at the window root.
    private uint _askRemoveItem;
    private (uint Item, int Material)? _askRemoveMaterial;
    private uint _askMatch;
    private (uint Item, int Material, int Preset)? _askPreset;
    private (uint Item, int After)? _askPaste;
    private string? _openPopup;

    /// <summary>Suggested preset per subject, looked up once (tails and ears read their vanilla material).</summary>
    private readonly Dictionary<uint, int> _suggestedPreset = new();

    public DetailsPanel(Plugin plugin, PortSession session, TextureThumbnailCache thumbnails)
    {
        _plugin    = plugin;
        _session   = session;
        _textures  = new TextureSlotEditor(session, thumbnails);
        _inspector = new ModelInspector(plugin, session, _textures, this);
        _meta      = new ItemMetaPanel(plugin, session);
    }

    public StageId         Id      => StageId.Details;
    public string          Title   => "File Setup";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Cube;
    public string          Purpose => "Every model in the modpack, with its race files, materials and textures.";

    private bool ShowThumbs => _plugin.Configuration.ShowThumbnails;

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        if (!_session.Loaded)
        {
            Ui.Hint("Reading the game's item list" + "…");
            return;
        }

        var item = _session.CurrentItem;
        if (item == null)
        {
            Ui.Icon(FontAwesomeIcon.ArrowLeft, Theme.Muted);
            ImGui.SameLine();
            Ui.HintWrapped(_session.Items.Count == 0
                ? "Nothing in the modpack yet. Add gear or a character feature on the Browse stage."
                : "Pick one of the modpack's items on the left to set it up.");
            return;
        }

        DrawHeader(item);

        var cfg = _plugin.Configuration;
        float splitter = Theme.SplitterWidth;
        float total    = ImGui.GetContentRegionAvail().Y;
        float minPane  = Theme.S(90);
        float metaH    = Math.Clamp(Theme.S(cfg.MetaPaneHeight), minPane, Math.Max(minPane, total - minPane - splitter));
        float treeH    = total - metaH - splitter;

        if (ImGui.BeginChild("##TreeArea", new Vector2(-1, treeH), false))
            DrawTree(item);
        ImGui.EndChild();
        // The tree is also a drop area for gear dragged out of the Browse stage.
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);

        bool released = Ui.HorizontalSplitter("##SplitMeta", ref metaH, minPane, total - minPane - splitter,
            ImGui.GetContentRegionAvail().X, invert: true);
        if (ImGui.IsItemActive() || released)
            cfg.MetaPaneHeight = metaH / Theme.S(1);
        if (released)
            cfg.Save();

        if (ImGui.BeginChild("##MetaArea", new Vector2(-1, -1), false))
            _meta.Draw(item);
        ImGui.EndChild();
    }

    /// <summary>The item this stage is showing, above the tree.</summary>
    private void DrawHeader(PortItem item)
    {
        var subject = item.Subject;
        var target  = new Target(TargetKind.Item, item.Key);

        Ui.Icon(subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User, Theme.Accent);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(subject.DisplayName);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Faint, $"{subject.KindLabel} · {subject.IdDisplay}");

        var worst = PortValidator.WorstUnder(_session.Issues, target);
        if (worst != null)
        {
            ImGui.SameLine();
            Ui.Icon(IssueList.IconFor(worst.Value), Ui.SeverityColor(worst.Value));
            Ui.Tooltip(string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(target)).Select(i => i.Message)));
        }
        ImGui.Separator();
    }

    /// <summary>The selected item's race models, materials and textures.</summary>
    private void DrawTree(PortItem item)
    {
        if (!ImGui.BeginTable("##DetailsTree", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX
                                                 | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupColumn("Name",     ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Set-up",   ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("##issues", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, Theme.S(22));

        ImGui.PushID((int)item.Key);
        for (int i = 0; i < item.Models.Count; i++)
        {
            ImGui.PushID($"model{i}");
            DrawRaceModel(item, i);
            ImGui.PopID();
        }
        DrawAddModelRow(item);

        for (int m = 0; m < item.Materials.Count; m++)
        {
            ImGui.PushID($"mat{m}");
            DrawMaterial(item, m);
            ImGui.PopID();
        }
        DrawAddMaterialRow(item);
        ImGui.PopID();

        ImGui.EndTable();
    }

    /// <summary>
    /// The row that adds a model file. A single-race feature always has its one row (see
    /// PortSession.EnsureFixedModel), so it is only drawn for gear and hair, which start empty.
    /// </summary>
    private void DrawAddModelRow(PortItem item)
    {
        if (!item.Subject.MultiRace)
            return;

        var addable = _session.AddableRaces(item);
        bool feature = item.Subject is FeatureSubject;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (Ui.Disabled(addable.Count == 0))
        {
            if (AddRowButton("Race model", addable.Count > 0
                    ? (feature
                        ? $"Add a model for another race that has {item.Subject.KindLabel.ToLowerInvariant()} {((FeatureSubject)item.Subject).Id}."
                        : "Add a model file for another race/gender.")
                    : feature
                        ? $"Every race that has this {item.Subject.KindLabel.ToLowerInvariant()} already has a model."
                        : "Every race already has a model."))
                ImGui.OpenPopup("##addrace");
        }
        DrawAddRacePopup(item, addable);

        ImGui.TableNextColumn();
        if (feature)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Faint, addable.Count > 0
                ? $"{addable.Count} other race(s) have this {item.Subject.KindLabel.ToLowerInvariant()} id"
                : "no other race has this id");
            Ui.Tooltip("Only races the game has this id for can be added: any other race could never equip it.");
        }
        else if (item.Models.Count == 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Faint, "no model files yet — without one, only materials and textures are replaced");
        }
    }

    private void DrawAddRacePopup(PortItem item, System.Collections.Generic.List<RaceGender> addable)
    {
        if (!ImGui.BeginPopup("##addrace"))
            return;

        if (addable.Count == 0)
        {
            Ui.Hint("No race left to add.");
            ImGui.EndPopup();
            return;
        }

        // Gear can claim a race that has no vanilla model of its own (an Eqdp override makes the
        // game use it); a feature is limited to races that ship this id, so there is nothing to split.
        var native = _plugin.GameData.GetNativeRaces(item.Subject);
        bool separated = false;
        foreach (var rg in addable.OrderByDescending(rg => native.Contains(rg)).ThenBy(rg => rg.RaceCode, StringComparer.Ordinal))
        {
            if (!native.Contains(rg) && !separated)
            {
                separated = true;
                ImGui.Separator();
                ImGui.TextColored(Theme.Muted, "Needs a race override:");
            }
            if (ImGui.Selectable(rg.DisplayName))
                _session.AddModel(item, rg);
        }
        ImGui.EndPopup();
    }

    // ── Race model ───────────────────────────────────────────────────────────

    private void DrawRaceModel(PortItem item, int index)
    {
        var subject = item.Subject;
        var model = item.Models[index];
        var target = new Target(TargetKind.Model, item.Key, index);
        bool native = _plugin.GameData.GetNativeRaces(subject).Contains(model.RaceGender);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        TreeNode("##race", target, FontAwesomeIcon.Cube, model.RaceGender.DisplayName, null, ImGuiTreeNodeFlags.Leaf);
        if (!native)
        {
            ImGui.SameLine();
            if (subject is GearSubject)
            {
                ImGui.TextColored(Theme.Accent, "override");
                Ui.Tooltip("This race has no model by default \u2014 an Eqdp override is added so the game uses this one.");
            }
            else
            {
                ImGui.TextColored(Theme.Warn, "no vanilla");
                Ui.Tooltip($"This race does not ship this {subject.KindLabel.ToLowerInvariant()}, so the game may never ask for the file.");
            }
        }

        // The row only shows what this race builds from; the file itself is set in the inspector
        // (or by dropping one onto the row).
        ImGui.TableNextColumn();
        float actionsW = subject.MultiRace ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X : 0;
        var (text, color, tooltip) = Describe(item, model);

        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        if (ImGui.Button($"{text}##source", new Vector2(Math.Max(Theme.S(40), ImGui.GetContentRegionAvail().X - actionsW), 0)))
            _session.Selection = target;
        ImGui.PopStyleColor(2);
        Ui.Tooltip(tooltip);

        var drop = ModelDropRule(item, model);
        if (FileDrop.Target(drop.Reject, out var dropped))
            drop.OnDrop(dropped);

        if (subject.MultiRace)
        {
            bool baseRace = PortSession.IsBaseRace(item, model.RaceGender);
            ImGui.SameLine();
            using (Ui.Disabled(baseRace))
            {
                if (Ui.IconButton(FontAwesomeIcon.Trash, "removerace", baseRace
                        ? $"This is the race the {subject.KindLabel.ToLowerInvariant()} was opened for \u2014 it is what the port replaces."
                        : "Remove this race (the file on disk is not touched)", danger: true))
                    _session.RemoveModel(item, index);
            }
        }

        ImGui.TableNextColumn();
        IssueList.Marker(_session, target);
    }

    /// <summary>What a race row shows: where its model comes from, in one line.</summary>
    private static (string Text, Vector4 Color, string Tooltip) Describe(PortItem item, RaceModelEntry model)
    {
        const string edit = "Click to edit it in the inspector, or drop a .mdl file here.";

        if (model.UseDummy)
            return ($"generated dummy \u00b7 {item.Materials.Count} mesh part(s)", Theme.Accent,
                "The build generates this model from the race's vanilla skeleton, one empty mesh part per material.\n\n" + edit);

        if (model.UseVariants)
            return (model.VariantPaths.Count == 0
                    ? "no variant files set"
                    : $"{model.VariantPaths.Count} variant file(s): {string.Join(", ", model.VariantPaths.Select(Path.GetFileName))}",
                model.VariantPaths.Count == 0 ? Theme.Faint : Theme.Muted,
                "A switch group of models.\n\n" + edit);

        if (string.IsNullOrWhiteSpace(model.SourcePath))
            return ("no model file set", Theme.Faint, edit);

        return (Path.GetFileName(model.SourcePath), Theme.Muted, model.SourcePath + "\n\n" + edit);
    }

    /// <summary>The drop rule for a race model: one .mdl sets File mode, several (or a folder) make a variant group.</summary>
    public FileDropRule ModelDropRule(PortItem item, RaceModelEntry model) => new(FileDrop.RejectForModel, paths =>
    {
        var files = FileDrop.ModelFiles(paths);
        if (files.Count == 1 && paths.Folders.Count == 0)
        {
            model.SourcePath  = files[0];
            model.UseVariants = false;
        }
        else
        {
            // A dropped set replaces the list outright, so the old per-variant material
            // choices no longer belong to any file.
            model.VariantPaths = files;
            model.VariantMaterialLinks.Clear();
            model.UseVariants  = true;
        }
        model.IsVanillaDummy = false;
        _session.MarkDirty();
        _session.Notify($"{item.Subject.DisplayName} / {model.RaceGender.DisplayName} ← {paths.Describe()}", Severity.Info);
    });

    // ── Material ─────────────────────────────────────────────────────────────

    private void DrawMaterial(PortItem item, int index)
    {
        var mat = item.Materials[index];
        var target = new Target(TargetKind.Material, item.Key, index);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        bool open = TreeNode("##mat", target, FontAwesomeIcon.Palette, $"Material {index + 1}", null, ImGuiTreeNodeFlags.DefaultOpen);
        Ui.Tooltip($"Mesh slot {index + 1}: the model's material slot {index + 1} references this material by its name.");
        DrawMaterialContextMenu(item, index);

        ImGui.TableNextColumn();
        float shaderW = Theme.S(150);
        float presetW = Theme.S(96);
        float spacing = ImGui.GetStyle().ItemSpacing.X;

        string name = mat.Name;
        ImGui.SetNextItemWidth(Math.Max(Theme.S(80), ImGui.GetContentRegionAvail().X - shaderW - presetW - spacing * 2));
        if (ImGui.InputTextWithHint("##name", item.Subject.DefaultMaterialName(index + 1, _session.NamingRace(item)), ref name, 128))
        {
            mat.Name = name;
            _session.MarkDirty();
        }
        Ui.Tooltip("The .mtrl name — also the prefix of every texture name in this material.");

        ImGui.SameLine();
        int shaderIdx = (int)mat.ShaderType;
        ImGui.SetNextItemWidth(shaderW);
        if (ImGui.Combo("##shader", ref shaderIdx, ShaderInfo.Labels, ShaderInfo.Labels.Length))
        {
            mat.ShaderType = (ShaderType)shaderIdx;
            _session.MarkDirty();
        }
        Ui.Tooltip($"Shader pack: {ShaderInfo.ShaderPackName(mat.ShaderType)}");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(presetW);
        if (ImGui.BeginCombo("##preset", "Preset…"))
        {
            for (int p = 0; p < MaterialPresets.All.Count; p++)
            {
                if (ImGui.Selectable(MaterialPresets.Names[p]))
                {
                    if (mat.Textures.Count == 0) _session.ApplyPreset(mat, MaterialPresets.All[p]);
                    else Ask(ref _askPreset, (item.Key, index, p), ConfirmPresetId);
                }
                Ui.Tooltip($"{ShaderInfo.DisplayName(MaterialPresets.All[p].Shader)}: " +
                           string.Join(", ", MaterialPresets.All[p].Textures.Select(TextureTypeInfo.DisplayName)));
            }
            ImGui.EndCombo();
        }
        Ui.Tooltip("Set the shader and texture slots from a preset.");

        ImGui.TableNextColumn();
        MarkerUnder(target);

        if (!open)
            return;

        for (int t = 0; t < mat.Textures.Count; t++)
        {
            ImGui.PushID(t);
            DrawTexture(item, index, t);
            ImGui.PopID();
        }
        DrawAddTextureRow(item, index);
        ImGui.TreePop();
    }

    private void DrawMaterialContextMenu(PortItem item, int index)
    {
        if (!ImGui.BeginPopupContextItem("##matctx"))
            return;

        _session.Selection = new Target(TargetKind.Material, item.Key, index);
        if (ImGui.MenuItem("Copy"))
            _session.CopyMaterial(item, index);
        PasteMenuItem(item, index);
        ImGui.Separator();
        if (ImGui.MenuItem("Move up", "", false, index > 0))
            _session.MoveMaterial(item, index, -1);
        if (ImGui.MenuItem("Move down", "", false, index < item.Materials.Count - 1))
            _session.MoveMaterial(item, index, +1);
        ImGui.Separator();
        if (ImGui.MenuItem("Remove…"))
            Ask(ref _askRemoveMaterial, (item.Key, index), ConfirmRemoveMaterialId);
        ImGui.EndPopup();
    }

    private void DrawAddTextureRow(PortItem item, int materialIndex)
    {
        var mat = item.Materials[materialIndex];
        var used = mat.Textures.Select(t => t.Type).ToHashSet();
        var usual = ShaderInfo.SupportedTextures(mat.ShaderType);
        var common = usual.Where(t => !used.Contains(t)).ToList();
        var unusual = Enum.GetValues<TextureType>().Where(t => !used.Contains(t) && !usual.Contains(t)).ToList();
        var shader = ShaderInfo.DisplayName(mat.ShaderType);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (Ui.Disabled(common.Count + unusual.Count == 0))
        {
            if (AddRowButton("Texture slot", common.Count + unusual.Count == 0
                    ? "Every texture type is already here."
                    : $"Add a texture slot. Types {shader} normally uses are listed first."))
                ImGui.OpenPopup("##addtex");
        }

        if (ImGui.BeginPopup("##addtex"))
        {
            foreach (var type in common)
                if (ImGui.Selectable(TextureTypeInfo.DisplayName(type)))
                    _session.AddTexture(item, materialIndex, type);

            if (unusual.Count > 0)
            {
                if (common.Count > 0) ImGui.Separator();
                ImGui.TextColored(Theme.Muted, $"Not normally used by {shader}");
                foreach (var type in unusual)
                {
                    if (ImGui.Selectable($"{TextureTypeInfo.DisplayName(type)}##unusual"))
                        _session.AddTexture(item, materialIndex, type);
                    Ui.Tooltip("Added to the material with its own sampler, for special effects — the shader may ignore it unless it is set up to read it.");
                }
            }
            ImGui.EndPopup();
        }
    }

    // ── Texture ──────────────────────────────────────────────────────────────

    private void DrawTexture(PortItem item, int materialIndex, int index)
    {
        var tex = item.Materials[materialIndex].Textures[index];
        var target = new Target(TargetKind.Texture, item.Key, materialIndex, index);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        TreeNode("##tex", target, FontAwesomeIcon.None, "", null, ImGuiTreeNodeFlags.Leaf, () =>
        {
            _textures.DrawThumb(tex, ShowThumbs);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(TextureTypeInfo.DisplayName(tex.Type));
        });

        ImGui.TableNextColumn();
        float removeW = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        if (_textures.DrawCompact(tex, removeW))
        {
            _session.Selection = target;
            _session.MarkDirty();
        }
        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Trash, "removetex", "Remove this texture slot", danger: true))
            _session.RemoveTexture(item, materialIndex, index);

        ImGui.TableNextColumn();
        IssueList.Marker(_session, target);
    }

    // ── + material ───────────────────────────────────────────────────────────

    private void DrawAddMaterialRow(PortItem item)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (AddRowButton("Material", "Add a material — one more mesh slot on this model."))
            ImGui.OpenPopup("##addmat");

        if (!ImGui.BeginPopup("##addmat"))
            return;

        if (_session.CopiedMaterial is { } copied)
        {
            if (ImGui.Selectable($"Paste \"{copied.Name}\""))
                RequestPaste(item, -1);
            Ui.Tooltip($"Copied from {_session.CopiedFrom?.DisplayName}.");
            ImGui.Separator();
        }

        int suggested = SuggestedPreset(item.Subject);
        ImGui.TextColored(Theme.Muted, "From a preset");
        for (int p = 0; p < MaterialPresets.All.Count; p++)
        {
            var preset = MaterialPresets.All[p];
            if (ImGui.Selectable($"{preset.Name}{(p == suggested ? "   (suggested)" : "")}##preset{p}"))
                _session.AddMaterial(item, preset);
            Ui.Tooltip($"{ShaderInfo.DisplayName(preset.Shader)}: {string.Join(", ", preset.Textures.Select(TextureTypeInfo.DisplayName))}");
        }

        ImGui.Separator();
        var emptyShader = suggested >= 0 ? MaterialPresets.All[suggested].Shader : ShaderType.Character;
        if (ImGui.Selectable($"Empty ({ShaderInfo.DisplayName(emptyShader)}, no textures)"))
            _session.AddMaterial(item, null, emptyShader);
        Ui.Tooltip("Just a shader — add the texture slots you need with + Texture slot.");

        if (ImGui.Selectable("Match vanilla materials"))
        {
            if (item.Materials.Count == 0) _session.MatchVanillaMaterials(item);
            else Ask(ref _askMatch, item.Key, ConfirmMatchId);
        }
        Ui.Tooltip("Replace this model's materials with the vanilla model's own: their names, shaders and texture slots.");
        ImGui.EndPopup();
    }

    /// <summary>Index of the preset that suits the subject (hair → Hair, face → Skin, tails and ears → their vanilla shader), or -1.</summary>
    internal int SuggestedPreset(PortSubject subject)
    {
        if (_suggestedPreset.TryGetValue(subject.Key, out var cached))
            return cached;

        ShaderType? shader = subject.Kind switch
        {
            SubjectKind.Hair => ShaderType.Hair,
            SubjectKind.Face => ShaderType.Skin,
            SubjectKind.Gear => ShaderType.Character,
            _ => subject.BaseRace is { } race
                ? _plugin.GameData.ReadVanillaMaterialLayout(subject, race)?.FirstOrDefault()?.ShaderType
                : null,
        };

        int index = -1;
        for (int i = 0; shader != null && i < MaterialPresets.All.Count; i++)
        {
            if (MaterialPresets.All[i].Shader == shader)
            {
                index = i;
                break;
            }
        }
        _suggestedPreset[subject.Key] = index;
        return index;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Row helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tree node that selects its target on click (but not when the arrow is toggled), opens
    /// when a focus request lies beneath it, and scrolls into view when it is the focus target.
    /// Returns whether it is open; open non-leaf nodes must be closed with <see cref="ImGui.TreePop"/>.
    /// </summary>
    private bool TreeNode(string id, Target target, FontAwesomeIcon icon, string label, Vector4? color, ImGuiTreeNodeFlags extra,
        Action? drawLabel = null)
    {
        bool leaf = (extra & ImGuiTreeNodeFlags.Leaf) != 0;
        var flags = extra | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding
                  | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (leaf) flags |= ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (_session.IsSelected(target)) flags |= ImGuiTreeNodeFlags.Selected;

        if (!leaf && _session.WantsOpen(target))
            ImGui.SetNextItemOpen(true);

        // The node spans the cell, so its label is drawn over it by hand: SameLine after a
        // full-width item would land at the cell's right edge.
        float labelX = ImGui.GetCursorPosX() + ImGui.GetTreeNodeToLabelSpacing();
        bool open = ImGui.TreeNodeEx(id, flags, "");
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
            _session.Selection = target;
        if (_session.TakeScrollRequest(target))
            ImGui.SetScrollHereY(0.3f);

        ImGui.SameLine();
        ImGui.SetCursorPosX(labelX);
        if (icon != FontAwesomeIcon.None)
        {
            Ui.Icon(icon, color ?? Theme.Muted);
            ImGui.SameLine();
        }
        ImGui.AlignTextToFramePadding();
        if (drawLabel != null) drawLabel();
        else                   ImGui.TextUnformatted(label);
        return open;
    }

    /// <summary>A muted "+ label" button in the name column, indented like a child row.</summary>
    private static bool AddRowButton(string label, string tooltip)
    {
        ImGui.Indent(ImGui.GetTreeNodeToLabelSpacing());
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        bool clicked = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, label);
        ImGui.PopStyleColor(2);
        Ui.Tooltip(tooltip);
        ImGui.Unindent(ImGui.GetTreeNodeToLabelSpacing());
        return clicked;
    }

    /// <summary>Issue marker for a node that has children: the worst problem on it or anywhere under it.</summary>
    private void MarkerUnder(Target node)
    {
        var worst = PortValidator.WorstUnder(_session.Issues, node);
        if (worst == null)
            return;
        ImGui.AlignTextToFramePadding();
        Ui.Icon(IssueList.IconFor(worst.Value), Ui.SeverityColor(worst.Value));
        Ui.Tooltip(string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(node)).Select(i => i.Message)));
    }

    private void Ask<T>(ref T field, T value, string popupId)
    {
        field = value;
        _openPopup = popupId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector and confirmations
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector() => _inspector.Draw();

    /// <summary>A "Paste" menu entry, disabled until a material has been copied.</summary>
    private void PasteMenuItem(PortItem item, int afterIndex)
    {
        var copied = _session.CopiedMaterial;
        var label = copied == null ? "Paste material" : $"Paste \"{copied.Name}\"{(afterIndex >= 0 ? " below" : "")}";
        if (ImGui.MenuItem(label, "", false, copied != null))
            RequestPaste(item, afterIndex);
    }

    /// <summary>
    /// Pastes the copied material, first asking whether its textures should stay shared with the
    /// original or be written again for this model. A material without textures just pastes.
    /// </summary>
    internal void RequestPaste(PortItem item, int afterIndex)
    {
        if (_session.CopiedMaterial is not { } copied) return;
        if (copied.Textures.Count == 0) _session.PasteMaterial(item, keepTexturePaths: false, afterIndex);
        else Ask(ref _askPaste, (item.Key, afterIndex), ConfirmPasteId);
    }

    /// <summary>Asks before removing an item from the modpack.</summary>
    internal void AskRemoveItem(PortItem item) => Ask(ref _askRemoveItem, item.Key, ConfirmRemoveItemId);

    /// <summary>Asks before removing a material; called from the inspector.</summary>
    internal void AskRemoveMaterial(PortItem item, int index) => Ask(ref _askRemoveMaterial, (item.Key, index), ConfirmRemoveMaterialId);

    /// <summary>Applies a preset, asking first when the material already has texture slots; called from the inspector.</summary>
    internal void AskPreset(PortItem item, int index, int preset)
    {
        if (index < 0 || index >= item.Materials.Count) return;
        if (item.Materials[index].Textures.Count == 0) _session.ApplyPreset(item.Materials[index], MaterialPresets.All[preset]);
        else Ask(ref _askPreset, (item.Key, index, preset), ConfirmPresetId);
    }

    /// <summary>Asks before replacing an item's materials with the vanilla layout; called from the inspector.</summary>
    internal void AskMatchVanilla(PortItem item)
    {
        if (item.Materials.Count == 0) _session.MatchVanillaMaterials(item);
        else Ask(ref _askMatch, item.Key, ConfirmMatchId);
    }

    public void DrawPopups()
    {
        if (_openPopup != null)
        {
            ImGui.OpenPopup(_openPopup);
            _openPopup = null;
        }

        Confirm(ConfirmRemoveItemId, () =>
        {
            var item = _session.FindItem(_askRemoveItem);
            ImGui.TextUnformatted($"Remove \"{item?.Subject.DisplayName}\" from the modpack?");
            ImGui.TextColored(Theme.Muted, "Its set-up is kept — adding it again from the browser brings it back.");
        }, "Remove", () => _session.RemoveItem(_askRemoveItem));

        Confirm(ConfirmRemoveMaterialId, () =>
        {
            var mat = _askRemoveMaterial is { } a ? MaterialAt(a.Item, a.Material) : null;
            ImGui.TextUnformatted($"Remove \"{mat?.Name}\" and its texture set-up?");
            ImGui.TextColored(Theme.Muted, "Files on disk are not touched.");
        }, "Remove", () =>
        {
            if (_askRemoveMaterial is { } a && _session.FindItem(a.Item) is { } item)
                _session.RemoveMaterial(item, a.Material);
        });

        Confirm(ConfirmMatchId, () =>
        {
            var item = _session.FindItem(_askMatch);
            ImGui.TextUnformatted($"Replace all {item?.Materials.Count} material(s) of \"{item?.Subject.DisplayName}\" with the vanilla model's materials?");
            ImGui.TextColored(Theme.Muted, "Texture source paths set on the current materials are cleared.");
        }, "Replace", () =>
        {
            if (_session.FindItem(_askMatch) is { } item)
                _session.MatchVanillaMaterials(item);
        });

        DrawPastePopup();

        Confirm(ConfirmPresetId, () =>
        {
            var p = _askPreset;
            var mat = p is { } x ? MaterialAt(x.Item, x.Material) : null;
            ImGui.TextUnformatted($"Replace the shader and all {mat?.Textures.Count} texture slot(s) of \"{mat?.Name}\"");
            ImGui.TextUnformatted($"with the \"{(p is { } y ? MaterialPresets.Names[y.Preset] : "")}\" preset?");
            ImGui.TextColored(Theme.Muted, "Texture source paths set on this material are cleared.");
        }, "Apply", () =>
        {
            if (_askPreset is { } x && MaterialAt(x.Item, x.Material) is { } mat)
                _session.ApplyPreset(mat, MaterialPresets.All[x.Preset]);
        });
    }

    private void DrawPastePopup()
    {
        if (!ImGui.BeginPopupModal(ConfirmPasteId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var copied = _session.CopiedMaterial;
        var from = _session.CopiedFrom;
        var target = _askPaste is { } a ? _session.FindItem(a.Item) : null;
        if (copied == null || from == null || target == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        int after = _askPaste!.Value.After;
        ImGui.TextUnformatted($"Paste \"{copied.Name}\" from {from.DisplayName} onto {target.Subject.DisplayName}.");
        ImGui.TextUnformatted($"What should its {copied.Textures.Count} texture(s) use?");
        ImGui.Spacing();

        float w = Theme.S(210);
        if (Ui.IconTextButton(FontAwesomeIcon.Link, "Keep texture paths", null, w))
        {
            _session.PasteMaterial(target, keepTexturePaths: true, after);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Theme.S(320));
        Ui.HintWrapped($"Shared: this material points at the textures {from.DisplayName} writes. One set of files, no duplicates.");
        ImGui.PopTextWrapPos();

        if (Ui.IconTextButton(FontAwesomeIcon.Unlink, "Retarget to this model", null, w))
        {
            _session.PasteMaterial(target, keepTexturePaths: false, after);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Theme.S(320));
        Ui.HintWrapped($"Separate: textures are written again under {target.Subject.DisplayName}'s folder — from the same source files, so they still match.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private MaterialSetup? MaterialAt(uint key, int index)
    {
        var item = _session.FindItem(key);
        return item != null && index >= 0 && index < item.Materials.Count ? item.Materials[index] : null;
    }

    private static void Confirm(string id, Action body, string confirmLabel, Action onConfirm)
    {
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        body();
        ImGui.Spacing();
        if (ImGui.Button(confirmLabel, new Vector2(Theme.S(100), 0)))
        {
            onConfirm();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
