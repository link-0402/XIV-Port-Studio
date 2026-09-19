using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using XIVPortStudio.Services;
using XIVPortStudio.Services.Sims;
using XIVPortStudio.Windows.UI;

namespace XIVPortStudio.Windows;

/// <summary>
/// Imports a Sims 4 .package: lists the meshes and textures it carries, lets the user
/// choose which detail levels to take and untick anything unwanted, then extracts the
/// rest into the folder layout the material editor consumes — a folder of diffuse
/// swatches to point a variant slot at, and single files for the other roles.
///
/// Scanning and extraction both run off the render thread. A package can run to
/// hundreds of megabytes, and decoding its textures on the draw call would stall the
/// game. The worker only returns its result and publishes a progress line; everything
/// else is applied here on the render thread once the task has finished.
/// </summary>
public sealed class SimsImportWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly TextureThumbnailCache _thumbnails;

    private string _packagePath = string.Empty;
    private string _outputDir   = string.Empty;

    private SimsScanResult? _scan;
    private SimsPackageInspector.ExtractionResult? _extraction;

    /// <summary>Whether to halve textures whose used content sits in one half.</summary>
    private bool _cropHalf = true;

    /// <summary>Index into <see cref="_lodOptions"/>; 0 is "All LODs".</summary>
    private int _lodChoice;
    private string[] _lodOptions = { AllLodsLabel };
    private const string AllLodsLabel = "All LODs";

    private Task<SimsScanResult>? _scanTask;
    private Task<SimsPackageInspector.ExtractionResult>? _extractTask;

    /// <summary>The one field the worker writes: its latest progress line.</summary>
    private volatile string _progress = string.Empty;

    private string _status       = string.Empty;
    private string _errorMessage = string.Empty;
    private string _applyMessage = string.Empty;

    private static readonly string[] RoleLabels =
    {
        "Unknown", "Diffuse", "Normal", "Specular", "Shadow", "Emission", "Region map",
    };

    internal SimsImportWindow(Plugin plugin, TextureThumbnailCache thumbnails) : base("Import Sims 4 Package###XPSSimsImport")
    {
        _plugin     = plugin;
        _thumbnails = thumbnails;
        Size          = new Vector2(900, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(620, 420) };

        var cfg = plugin.Configuration;
        _packagePath = cfg.LastSimsPackagePath;
        _outputDir   = cfg.LastSimsOutputDir;

        if (string.IsNullOrWhiteSpace(_outputDir))
            _outputDir = Path.Combine(Path.GetTempPath(), "XIVPortStudio", "sims-import");
    }

    public void Dispose()
    {
        _thumbnails.ForgetMemoryEntries();
    }

    private bool Busy => _scanTask is { IsCompleted: false } || _extractTask is { IsCompleted: false };

    // ─────────────────────────────────────────────────────────────────────────
    // Draw
    // ─────────────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        CollectFinishedWork();
        bool busy = Busy;

        DrawSource(busy);
        DrawStatus(busy);
        ImGui.Spacing();

        DrawOptions(busy);

        float footerH = _extraction != null
            ? ImGui.GetFrameHeightWithSpacing() * 6
            : ImGui.GetFrameHeightWithSpacing() * 1.5f;
        if (ImGui.BeginChild("##SimsAssets", new Vector2(-1, -footerH), true))
            DrawAssetList(busy);
        ImGui.EndChild();

        DrawActions(busy);
    }

    private void DrawSource(bool busy)
    {
        Ui.SectionHeader("1  Package", "Pick a .package and scan it. Nothing is written until you extract.");

        using (Ui.Disabled(busy))
        {
            // Typed paths are only kept in memory; they are persisted when a scan starts,
            // so an in-progress path does not write the config file on every keystroke.
            Ui.Label("Package");
            float scanW = Theme.S(110);
            Ui.PathPicker("SimsPackage", ref _packagePath, "path to a .package file", PathKind.File, "Sims 4 packages{.package}",
                picked => { _packagePath = picked; SaveSettings(); }, reserveRight: scanW + ImGui.GetStyle().ItemSpacing.X);

            ImGui.SameLine();
            if (Ui.PrimaryButton(FontAwesomeIcon.Search, "Scan", scanW, "Reads the package and lists its meshes and textures."))
                StartScan();

            Ui.Label("Output", "A subfolder named after the package is created here, holding model/ and textures/.");
            Ui.PathPicker("SimsOutput", ref _outputDir, "folder the extracted files go into", PathKind.Folder, "",
                picked => { _outputDir = picked; SaveSettings(); },
                "A subfolder named after the package is created here, holding model/ and textures/.");
        }
    }

    private void DrawStatus(bool busy)
    {
        if (busy)
        {
            Ui.Icon(FontAwesomeIcon.Spinner, Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Accent, _progress);
        }
        else if (!string.IsNullOrEmpty(_errorMessage))
        {
            Ui.Icon(FontAwesomeIcon.TimesCircle, Theme.Bad);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, _errorMessage);
        }
        else if (!string.IsNullOrEmpty(_status))
        {
            Ui.Hint(_status);
        }
    }

    /// <summary>
    /// Detail-level picker and half-crop toggle. The LOD dropdown only re-ticks the mesh
    /// rows, so an individual tick changed afterwards is still respected — it is a bulk
    /// selection, not a filter that hides anything.
    /// </summary>
    private void DrawOptions(bool busy)
    {
        Ui.SectionHeader("2  Choose what to extract");
        if (_scan == null)
            return;

        using (Ui.Disabled(busy))
        {
            if (_scan.AvailableLods.Count > 0)
            {
                if (Ui.LabeledCombo("Detail", "##SimsLod", ref _lodChoice, _lodOptions,
                        "Which detail levels to export. LOD 0 is the full-quality mesh and the only one a port needs, so it is " +
                        "selected by default; the lower levels are the game's reduced-detail copies. Individual meshes can still be " +
                        "ticked or unticked below.", Theme.S(220)))
                    ApplyLodChoice(_scan);
                ImGui.SameLine();
                Ui.Hint($"{_scan.Meshes.Count()} mesh(es) in this package");
            }

            DrawCropRow(_scan);
        }

        if (!_scan.HasCasPartData)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, "No CAS part could be read, so texture roles are guesses from the resource type — check them.");
        }
        ImGui.Spacing();
    }

    /// <summary>
    /// The half-crop toggle. It is only offered when the meshes actually fit in one half
    /// of the sheet, since that is what makes rescaling UV0 safe; otherwise the reason is
    /// shown instead of a control that could not do anything.
    /// </summary>
    private void DrawCropRow(SimsScanResult scan)
    {
        var crop = scan.SuggestedCrop;
        Ui.Label("Half crop");
        if (!crop.Crops)
        {
            Ui.Hint("not available — the meshes use both halves of the texture sheet");
            Ui.Tooltip("Sims sheets are often twice as tall as they need to be, with one half empty. " +
                       "That can only be cropped away when every mesh sits inside a single half.");
            return;
        }

        ImGui.Checkbox($"Crop textures to the {crop.Describe()}##SimsCrop", ref _cropHalf);
        Ui.Tooltip($"The meshes only use the {crop.Describe()} of the sheet, so the rest is dead space. " +
                   "Halving it turns a 2048x4096 diffuse into a square 2048x2048, and UV0 on the " +
                   "exported model is rescaled by the same amount so the two still line up.");
        ImGui.SameLine();
        Ui.Hint("(UV0 is rescaled to match)");
    }

    /// <summary>
    /// Rebuilds the picker for whatever levels the scan actually found and points it at
    /// the selection the scan already made — LOD 0 where there is one, everything
    /// otherwise. The scan owns that decision; this only reflects it, so the dropdown
    /// and the ticks cannot disagree on the first frame.
    /// </summary>
    private void BuildLodOptions(SimsScanResult scan)
    {
        var options = new List<string> { AllLodsLabel };
        foreach (var level in scan.AvailableLods)
        {
            options.Add(level switch
            {
                SimsAsset.UnknownLod => "Unknown level",
                0                    => "LOD 0 (highest detail)",
                _                    => $"LOD {level}",
            });
        }

        _lodOptions = options.ToArray();

        int lod0 = scan.AvailableLods.IndexOf(0);
        _lodChoice = lod0 >= 0 ? lod0 + 1 : 0;      // +1 to step over the "All LODs" entry
    }

    private void ApplyLodChoice(SimsScanResult scan)
    {
        // Choice 0 is "All"; the rest line up with AvailableLods, offset by that entry.
        int? wanted = _lodChoice > 0 && _lodChoice - 1 < scan.AvailableLods.Count
            ? scan.AvailableLods[_lodChoice - 1]
            : null;

        foreach (var asset in scan.Meshes)
            asset.Selected = wanted == null || asset.LodLevel == wanted;
    }

    private void DrawAssetList(bool busy)
    {
        if (_scan == null)
        {
            ImGui.Spacing();
            Ui.Icon(FontAwesomeIcon.Box, Theme.Muted);
            ImGui.SameLine();
            Ui.Hint("Pick a .package file and hit Scan.");
            return;
        }

        if (_scan.Assets.Count == 0)
        {
            Ui.Hint("(nothing extractable was found in this package)");
            return;
        }

        using (Ui.Disabled(busy))
        {
            if (!ImGui.BeginTable("##SimsAssetTable", 6,
                    ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX,
                    new Vector2(-1, -1)))
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("",         ImGuiTableColumnFlags.WidthFixed, Theme.S(26));
            ImGui.TableSetupColumn("Preview",  ImGuiTableColumnFlags.WidthFixed, Theme.S(64));
            ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthFixed, Theme.S(120));
            ImGui.TableSetupColumn("Role",     ImGuiTableColumnFlags.WidthFixed, Theme.S(130));
            ImGui.TableSetupColumn("Size",     ImGuiTableColumnFlags.WidthFixed, Theme.S(140));
            ImGui.TableSetupColumn("CAS part", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _scan.Assets.Count; i++)
                DrawAssetRow(_scan.Assets[i], i);

            ImGui.EndTable();
        }
    }

    private void DrawAssetRow(SimsAsset asset, int index)
    {
        ImGui.TableNextRow();
        ImGui.PushID(index);

        // Tick
        ImGui.TableNextColumn();
        bool selected = asset.Selected;
        using (Ui.Disabled(asset.Error != null))
        {
            if (ImGui.Checkbox("##Sel", ref selected))
                asset.Selected = selected;
        }

        // Preview
        ImGui.TableNextColumn();
        if (asset.ThumbnailPng != null)
            _thumbnails.DrawPng(asset.Entry.KeyString, asset.ThumbnailPng, Theme.S(56));
        else if (asset.Kind == SimsAssetKind.Mesh)
            Ui.Icon(FontAwesomeIcon.Cube, Theme.Muted);
        else
            Ui.Hint("—");

        // Resource type
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(asset.Kind == SimsAssetKind.Mesh
            ? $"GEOM ({asset.LodLabel})"
            : SimsResourceType.Name(asset.Entry.Type));
        Ui.Tooltip(asset.Entry.KeyString);

        // Role
        ImGui.TableNextColumn();
        if (asset.Kind == SimsAssetKind.Mesh)
        {
            Ui.Hint("model");
            if (asset.LodLevel == SimsAsset.UnknownLod)
                Ui.Tooltip("No CAS part lists this mesh, so its detail level could not be established.");
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            int roleIndex = (int)asset.Role;
            if (ImGui.Combo("##Role", ref roleIndex, RoleLabels, RoleLabels.Length))
                asset.Role = (SimsTextureRole)roleIndex;
            Ui.Tooltip(asset.RoleFromCasPart
                ? "Role taken from the CAS part that references this texture."
                : "No CAS part referenced this texture — the role was guessed from its resource type. Change it if the preview says otherwise.");
        }

        // Size / counts
        ImGui.TableNextColumn();
        if (asset.Error != null)
        {
            ImGui.TextColored(Theme.Bad, "failed");
            Ui.Tooltip(asset.Error);
        }
        else
        {
            Ui.Hint(asset.Describe());
            if (asset.Kind == SimsAssetKind.Texture)
                Ui.Tooltip(asset.SourceFormat);
        }

        // CAS part
        ImGui.TableNextColumn();
        Ui.Hint(string.IsNullOrWhiteSpace(asset.PartName) ? "—" : asset.PartName);

        ImGui.PopID();
    }

    private void DrawActions(bool busy)
    {
        int selected = _scan?.Assets.Count(a => a.Selected) ?? 0;

        using (Ui.Disabled(busy || _scan == null || selected == 0))
        {
            if (Ui.PrimaryButton(FontAwesomeIcon.FileExport, "Extract", Theme.S(140), "Writes the ticked assets into the output folder."))
                StartExtract();
        }

        if (_scan != null)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Ui.Hint($"{selected} of {_scan.Assets.Count} selected");
        }

        if (_extraction == null)
            return;

        ImGui.Spacing();
        Ui.SectionHeader("3  Use the result");

        bool clean = _extraction.Failed == 0;
        Ui.Icon(clean ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.ExclamationTriangle, clean ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.TextColored(clean ? Theme.Good : Theme.Bad, $"{_extraction.Extracted} file(s) written, {_extraction.Failed} failed.");
        if (_extraction.Crop.Crops)
        {
            ImGui.SameLine();
            Ui.Hint($"· cropped to the {_extraction.Crop.Describe()}, UV0 rescaled to match");
        }

        Ui.LabeledValue("Folder", _extraction.RootFolder, "Also holds extract-report.txt with the full log of this run.");
        if (_extraction.ModelFile != null)
        {
            Ui.LabeledValue("Model", _extraction.ModelFile,
                "The .glb is an intermediate, not a finished FFXIV model — open it in Blender, fit it to the target body, " +
                "and export a .mdl before setting it as a race model in the main window.");
        }

        bool canApply = _extraction.DiffuseCount > 0 || _extraction.NormalFile != null || _extraction.SpecularFile != null;
        var target = _plugin.Session.CurrentMaterial;
        using (Ui.Disabled(!canApply || target == null))
        {
            var label = target != null ? $"Apply to \"{target.Name}\"" : "Apply to selected material";
            if (Ui.IconTextButton(FontAwesomeIcon.ArrowRight, label,
                    target == null
                        ? "Select a material on the main window's Materials stage first."
                        : "Points that material at these files: the diffuse folder as a variant group, and the normal / specular images as single textures."))
            {
                _applyMessage = _plugin.MainWindow.ApplySimsExtraction(_extraction);
                _plugin.Session.GoToStage(Models.StageId.Materials);
            }
        }

        if (!string.IsNullOrEmpty(_applyMessage))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Ui.Hint(_applyMessage);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Work
    // ─────────────────────────────────────────────────────────────────────────

    private void SaveSettings()
    {
        var cfg = _plugin.Configuration;
        cfg.LastSimsPackagePath = _packagePath;
        cfg.LastSimsOutputDir   = _outputDir;
        cfg.Save();
    }

    private void StartScan()
    {
        if (string.IsNullOrWhiteSpace(_packagePath) || !File.Exists(_packagePath))
        {
            _errorMessage = "Pick a .package file first.";
            return;
        }

        SaveSettings();

        _thumbnails.ForgetMemoryEntries();
        _scan         = null;
        _extraction   = null;
        _errorMessage = string.Empty;
        _applyMessage = string.Empty;
        _status       = string.Empty;
        _progress     = "Opening package…";

        var path = _packagePath;
        _scanTask = Task.Run(() => SimsPackageInspector.Scan(path, s => _progress = s));
    }

    private void StartExtract()
    {
        var scan = _scan;
        if (scan == null)
            return;

        if (string.IsNullOrWhiteSpace(_outputDir))
        {
            _errorMessage = "Pick an output folder first.";
            return;
        }

        SaveSettings();

        _extraction   = null;
        _errorMessage = string.Empty;
        _applyMessage = string.Empty;
        _status       = string.Empty;
        _progress     = "Extracting…";

        var outputDir = _outputDir;
        var crop = _cropHalf ? scan.SuggestedCrop : SimsHalfCrop.None;
        _extractTask = Task.Run(() => SimsPackageInspector.Extract(scan, outputDir, crop, s => _progress = s));
    }

    /// <summary>Applies a finished scan or extraction on the render thread.</summary>
    private void CollectFinishedWork()
    {
        if (_scanTask is { IsCompleted: true } scanTask)
        {
            _scanTask = null;
            if (scanTask.IsCompletedSuccessfully)
            {
                var scan = scanTask.Result;
                BuildLodOptions(scan);
                _scan   = scan;
                _status = $"Found {scan.Meshes.Count()} mesh(es) and {scan.Textures.Count()} texture(s).";
                if (scan.Warnings.Count > 0)
                    Plugin.Log.Information("[XPS] Sims scan warnings for {0}:\n  {1}", scan.PackagePath, string.Join("\n  ", scan.Warnings));
            }
            else
            {
                var ex = scanTask.Exception?.GetBaseException();
                Plugin.Log.Warning(ex, "[XPS] Failed to scan Sims package {0}", _packagePath);
                _errorMessage = $"Could not read this package: {ex?.Message}";
            }
        }

        if (_extractTask is { IsCompleted: true } extractTask)
        {
            _extractTask = null;
            if (extractTask.IsCompletedSuccessfully)
            {
                var result = extractTask.Result;
                _extraction = result;
                _status     = $"Done — {result.Extracted} file(s) written to {result.RootFolder}";
                Plugin.Log.Information("[XPS] Sims extraction: {0} written, {1} failed, into {2}",
                    result.Extracted, result.Failed, result.RootFolder);
            }
            else
            {
                var ex = extractTask.Exception?.GetBaseException();
                Plugin.Log.Warning(ex, "[XPS] Failed to extract Sims package into {0}", _outputDir);
                _errorMessage = $"Extraction failed: {ex?.Message}";
            }
        }
    }
}
