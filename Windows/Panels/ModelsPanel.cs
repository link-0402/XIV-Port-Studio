using System;
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
/// Stage 2 — which races get a model, and which material each of the model's mesh slots
/// uses. Mesh slots are the build's real output list: one .mtrl is written per slot, named
/// after the slot (its override, or its material's own name).
/// </summary>
internal sealed class ModelsPanel : IStagePanel
{
    private readonly Plugin _plugin;
    private readonly PortSession _session;
    private int _addRaceIndex;

    public ModelsPanel(Plugin plugin, PortSession session)
    {
        _plugin  = plugin;
        _session = session;
    }

    public StageId         Id      => StageId.Models;
    public string          Title   => "Models";
    public FontAwesomeIcon Icon    => FontAwesomeIcon.Cube;
    public string          Purpose => "The .mdl file for each race, and which material each mesh slot uses.";

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        var subject = _session.Subject;
        if (subject == null)
        {
            Ui.Hint("Select an item or feature on the left first.");
            return;
        }

        var nativeRaces = _plugin.GameData.GetNativeRaces(subject);

        if (subject is GearSubject)
            Ui.SectionHeader("Race models", "One model file per race/gender. Races without their own vanilla model get an Eqdp override automatically.");
        else if (subject.MultiRace)
            Ui.SectionHeader("Race models", $"One model per race. Each race gets its own copy of the materials, named for that race. " +
                                             $"Races that do not ship {subject.KindLabel.ToLowerInvariant()} {((FeatureSubject)subject).Id} may not load it.");
        else
            Ui.SectionHeader("Model", $"A {subject.KindLabel.ToLowerInvariant()} belongs to one race, so it has exactly one model.");

        if (subject.MultiRace)
        {
            DrawAddRace(nativeRaces);
            ImGui.Spacing();
        }
        DrawModelTable(subject, nativeRaces);

        ImGui.Spacing();
        ImGui.Spacing();
        Ui.SectionHeader("Mesh slots → material",
            "The material slots on your model, in order. One .mtrl is written per slot, named after the slot. " +
            "There is one slot per material you set up; reassign or rename a slot here without touching the material itself.");
        DrawMeshSlotTable(subject);
    }

    private void DrawAddRace(System.Collections.Generic.HashSet<RaceGender> nativeRaces)
    {
        var configured = _session.Models.Select(m => m.RaceGender).ToHashSet();
        var addable = RaceInfo.AllRaces
            .Where(rg => !configured.Contains(rg))
            .OrderByDescending(rg => nativeRaces.Contains(rg))
            .ThenBy(rg => rg.RaceCode, StringComparer.Ordinal)
            .ToList();

        if (addable.Count == 0)
        {
            Ui.Hint("Every race has a model set up.");
            return;
        }

        var labels = addable
            .Select(rg => nativeRaces.Contains(rg) ? rg.DisplayName
                        : _session.Subject is GearSubject ? $"{rg.DisplayName}  (needs override)"
                        : $"{rg.DisplayName}  (no vanilla)")
            .ToArray();
        _addRaceIndex = Math.Clamp(_addRaceIndex, 0, labels.Length - 1);

        ImGui.SetNextItemWidth(Theme.S(240));
        ImGui.Combo("##AddRace", ref _addRaceIndex, labels, labels.Length);
        ImGui.SameLine();
        if (Ui.IconTextButton(FontAwesomeIcon.Plus, "Add race", "Add a model slot for this race/gender."))
        {
            _session.AddModel(addable[_addRaceIndex]);
            _session.SelectedMeshSlot = -1;
        }
    }

    private void DrawModelTable(PortSubject subject, System.Collections.Generic.HashSet<RaceGender> nativeRaces)
    {
        if (_session.Models.Count == 0)
        {
            Ui.Hint("(no race models yet — without one, the mod only replaces materials and textures)");
            return;
        }

        if (!ImGui.BeginTable("##Models", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("##state", ImGuiTableColumnFlags.WidthFixed, Theme.S(20));
        ImGui.TableSetupColumn("Race",    ImGuiTableColumnFlags.WidthFixed, Theme.S(190));
        ImGui.TableSetupColumn("Model file", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        for (int i = 0; i < _session.Models.Count; i++)
        {
            var model = _session.Models[i];
            var target = new Target(TargetKind.Model, i);
            ImGui.TableNextRow();
            ImGui.PushID(i);

            ImGui.TableNextColumn();
            IssueList.Marker(_session, target);

            ImGui.TableNextColumn();
            bool selected = _session.SelectedModel == i;
            if (ImGui.Selectable($"{model.RaceGender.DisplayName}##row", selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                _session.SelectedModel = i;
                _session.SelectedMeshSlot = -1;
            }
            if (_session.TakeScrollRequest(target))
                ImGui.SetScrollHereY(0.3f);

            ImGui.TableNextColumn();
            if (model.IsVanillaDummy)
                ImGui.TextColored(Theme.Warn, "empty dummy");
            else if (string.IsNullOrWhiteSpace(model.SourcePath))
                ImGui.TextColored(Theme.Faint, "not set");
            else
                ImGui.TextColored(Theme.Muted, System.IO.Path.GetFileName(model.SourcePath));

            if (!nativeRaces.Contains(model.RaceGender))
            {
                ImGui.SameLine();
                if (subject is GearSubject)
                {
                    ImGui.TextColored(Theme.Accent, " · override");
                    Ui.Tooltip("This race has no model by default — an Eqdp override is added so the game uses this model instead of another race's.");
                }
                else
                {
                    ImGui.TextColored(Theme.Warn, " · no vanilla");
                    Ui.Tooltip($"This race does not ship this {subject.KindLabel.ToLowerInvariant()}, so the game may never ask for the file.");
                }
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawMeshSlotTable(PortSubject subject)
    {
        if (_session.Materials.Count == 0)
        {
            Ui.Hint("(add a material on the Materials stage — each one creates a mesh slot here)");
            return;
        }

        var matLabels = _session.Materials
            .Select((m, i) => string.IsNullOrWhiteSpace(m.Name) ? $"(unnamed) #{i + 1}" : m.Name)
            .ToArray();

        if (!ImGui.BeginTable("##MeshSlots", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("##state", ImGuiTableColumnFlags.WidthFixed, Theme.S(20));
        ImGui.TableSetupColumn("Slot",     ImGuiTableColumnFlags.WidthFixed, Theme.S(44));
        ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Written as", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        for (int s = 0; s < _session.ModelMaterialSlots.Count; s++)
        {
            var slot = _session.ModelMaterialSlots[s];
            var target = new Target(TargetKind.MeshSlot, s);
            ImGui.TableNextRow();
            ImGui.PushID(s);

            ImGui.TableNextColumn();
            IssueList.Marker(_session, target);

            ImGui.TableNextColumn();
            bool selected = _session.SelectedMeshSlot == s;
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{s + 1}##slot", selected))
            {
                _session.SelectedMeshSlot = s;
                _session.SelectedModel = -1;
            }
            if (_session.TakeScrollRequest(target))
                ImGui.SetScrollHereY(0.3f);

            ImGui.TableNextColumn();
            int matIdx = Math.Clamp(slot.MaterialIndex, 0, _session.Materials.Count - 1);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##Assigned", ref matIdx, matLabels, matLabels.Length))
            {
                slot.MaterialIndex = matIdx;
                _session.MarkDirty();
            }

            ImGui.TableNextColumn();
            string nameOverride = slot.NameOverride;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##NameOverride", _session.Materials[matIdx].Name, ref nameOverride, 128))
            {
                slot.NameOverride = nameOverride;
                _session.MarkDirty();
            }
            Ui.Tooltip("The .mtrl file name for this slot. Leave blank to use the material's own name.\n\n" +
                       string.Join("\n", WrittenPaths(subject, slot)));

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        var subject = _session.Subject;
        if (subject == null)
        {
            Ui.Hint("Nothing selected.");
            return;
        }

        if (_session.SelectedMeshSlot >= 0 && _session.SelectedMeshSlot < _session.ModelMaterialSlots.Count)
        {
            DrawMeshSlotInspector(subject, _session.SelectedMeshSlot);
            return;
        }

        var model = _session.CurrentModel;
        if (model == null)
        {
            Ui.Hint("Select a race model or a mesh slot to edit it.");
            return;
        }

        DrawModelInspector(subject, model, _session.SelectedModel);
    }

    private void DrawModelInspector(PortSubject subject, RaceModelEntry model, int index)
    {
        Ui.SectionHeader(model.RaceGender.DisplayName);

        var gamePath = subject.ModelGamePath(model.RaceGender);
        Ui.LabeledValue("Game path", gamePath, "Where this model is placed in the mod.");

        bool native = _plugin.GameData.GetNativeRaces(subject).Contains(model.RaceGender);
        Ui.Label("Vanilla model");
        if (native)
            ImGui.TextColored(Theme.Muted, "yes — replaced directly");
        else if (subject is GearSubject)
            ImGui.TextColored(Theme.Accent, "no — Eqdp override added");
        else
            ImGui.TextColored(Theme.Warn, "no — the game may not load this");

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Model file");
        string src = model.SourcePath;
        if (Ui.PathPicker("ModelSrc", ref src, "path to a .mdl file", PathKind.File, "Model files{.mdl}",
                picked => { model.SourcePath = picked; model.IsVanillaDummy = false; _session.MarkDirty(); },
                "Local .mdl copied into the mod at the game path above."))
        {
            model.SourcePath = src;
            model.IsVanillaDummy = false;
            _session.MarkDirty();
        }

        if (model.IsVanillaDummy)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.TextWrapped("This is an empty dummy — open it in Blender and model it before releasing the mod.");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Spacing();
        Ui.SectionHeader("Start from a dummy");
        Ui.HintWrapped($"Builds a placeholder on this race's vanilla skeleton with one empty mesh part per mesh slot " +
                       $"({_session.ModelMaterialSlots.Count} now), each already assigned its material — ready to model in Blender via InstantEdit.");
        ImGui.Spacing();
        using (Ui.Disabled(_session.Materials.Count == 0))
        {
            if (Ui.IconTextButton(FontAwesomeIcon.Magic, "Generate dummy model",
                    _session.Materials.Count == 0 ? "Add a material first — each becomes a mesh part." : null))
                _session.BuildDummyModel(model);
        }

        if (!subject.MultiRace)
            return;

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Ui.IconTextButton(FontAwesomeIcon.Trash, "Remove race", "Removes this race from the mod. The file on disk is not touched."))
            _session.RemoveModel(index);
        ImGui.PopStyleColor();
    }

    private void DrawMeshSlotInspector(PortSubject subject, int index)
    {
        var slot = _session.ModelMaterialSlots[index];
        Ui.SectionHeader($"Mesh slot {index + 1}");

        var name = _session.EffectiveMaterialName(slot);
        var races = subject.MaterialRaces(_session.Models);
        if (races.Count == 1)
        {
            Ui.LabeledValue("Written as", subject.MaterialGamePath(subject.MaterialNameFor(name, races[0]), races[0]),
                "The .mtrl this slot produces. Your model's material slot must use this name.");
            Ui.LabeledValue("In the model", $"/{MaterialNaming.SanitizeFileName(subject.MaterialNameFor(name, races[0]))}.mtrl",
                "The material path to set on this mesh part in Blender / TexTools.");
        }
        else
        {
            // Hair ported to several races: each race's model references its own copy.
            ImGui.TextColored(Theme.Muted, "Written once per race — each race's model references its own name:");
            if (ImGui.BeginTable("##PerRace", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                foreach (var race in races)
                {
                    var raceName = subject.MaterialNameFor(name, race);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Muted, race?.DisplayName ?? "");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"/{MaterialNaming.SanitizeFileName(raceName)}.mtrl");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{subject.MaterialGamePath(raceName, race)}\n\nClick to copy.");
                    if (ImGui.IsItemClicked()) ImGui.SetClipboardText($"/{MaterialNaming.SanitizeFileName(raceName)}.mtrl");
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        var matIdx = Math.Clamp(slot.MaterialIndex, 0, Math.Max(0, _session.Materials.Count - 1));
        Ui.Label("Material");
        if (_session.Materials.Count > 0 && ImGui.SmallButton($"Edit \"{_session.Materials[matIdx].Name}\""))
            _session.Focus(new Target(TargetKind.Material, matIdx));

        ImGui.Spacing();
        Ui.HintWrapped("Several slots may use the same material (each gets its own .mtrl built from the same textures), " +
                       "but no two slots may be written under the same name.");
    }

    /// <summary>Every .mtrl game path a mesh slot is written to (one per race for multi-race hair).</summary>
    private IEnumerable<string> WrittenPaths(PortSubject subject, ModelMaterialSlot slot)
    {
        var name = _session.EffectiveMaterialName(slot);
        foreach (var race in subject.MaterialRaces(_session.Models))
            yield return subject.MaterialGamePath(subject.MaterialNameFor(name, race), race);
    }

}
