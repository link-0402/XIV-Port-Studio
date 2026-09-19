using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

/// <summary>
/// One running (or finished) build. The worker only ever writes <see cref="Progress"/>
/// (an immutable snapshot swapped atomically) and the task result; the render thread
/// only reads them. Nothing else is shared, so there is no field for the two threads
/// to race on.
/// </summary>
internal sealed class BuildJob
{
    private readonly CancellationTokenSource _cts = new();
    private volatile BuildProgress _progress = new(0, 0, "Starting…");

    public BuildJob(BuildRequest request)
    {
        Request = request;
        Task = System.Threading.Tasks.Task.Run(() => new ModBuilder(this).Run(_cts.Token));
    }

    public BuildRequest       Request  { get; }
    public Task<BuildReport>  Task     { get; }
    public BuildProgress      Progress => _progress;
    public bool               IsDone   => Task.IsCompleted;

    public void Cancel() => _cts.Cancel();

    internal void Report(BuildProgress progress) => _progress = progress;
}

/// <summary>
/// Converts every configured local texture to .tex, writes one .mtrl per mesh slot, copies
/// the race models, and writes meta.json into a mod folder under Penumbra's mod directory.
/// Texture slots marked as variants become single-select switch groups; everything else
/// is written as default files.
///
/// Runs on a worker thread against a <see cref="BuildRequest"/> snapshot. Registering the
/// finished mod with Penumbra happens afterwards on the render thread.
/// </summary>
internal sealed class ModBuilder
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".dds" };

    private readonly BuildJob _job;
    private readonly BuildRequest _req;

    private int _done;
    private int _total;

    public ModBuilder(BuildJob job)
    {
        _job = job;
        _req = job.Request;
    }

    public BuildReport Run(CancellationToken ct)
    {
        var subject = _req.Subject;
        var modPath = Path.Combine(_req.ModRoot, _req.ModName);
        var report  = new BuildReport { ModName = _req.ModName, ModPath = modPath, ItemName = subject.DisplayName };
        var clock   = Stopwatch.StartNew();

        try
        {
            _total = EstimateSteps();
            Directory.CreateDirectory(modPath);

            var defaultFiles  = new List<KeyValuePair<string, string>>();
            var groups        = new List<ModVariantGroup>();
            var eqdpOverrides = new List<ModEqdpOverride>();

            // ── Models ───────────────────────────────────────────────────────
            var nativeRaces = _req.NativeRaces;
            for (int i = 0; i < _req.Models.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var model = _req.Models[i];
                var gamePath = subject.ModelGamePath(model.RaceGender);
                Step($"{model.RaceGender.DisplayName} model");

                if (!ProcessModel(model, gamePath, modPath, report, new Target(TargetKind.Model, i)))
                    continue;

                defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
                if (subject is GearSubject gear && !nativeRaces.Contains(model.RaceGender))
                    eqdpOverrides.Add(new ModEqdpOverride { RaceGender = model.RaceGender, Slot = gear.Item.Slot, SetId = gear.Item.ModelId });
            }

            // ── Pass 1: textures, once per Material ──────────────────────────
            // Independent of how many mesh slots end up pointing at that Material.
            var replacementsByMaterial = new Dictionary<MaterialSetup, Dictionary<TextureType, string>>();
            for (int m = 0; m < _req.Materials.Count; m++)
            {
                var mat = _req.Materials[m];
                var replacements = new Dictionary<TextureType, string>();

                for (int t = 0; t < mat.Textures.Count; t++)
                {
                    ct.ThrowIfCancellationRequested();
                    var tex = mat.Textures[t];
                    var textureName = MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix);
                    var gamePath = subject.TextureGamePath(textureName);
                    var source = new Target(TargetKind.Texture, m, t);
                    bool ok;

                    if (tex.UseWhiteDummy)
                    {
                        Step(textureName);
                        ok = ProcessDummyTexture(tex, textureName, gamePath, modPath, report, source);
                        if (ok) defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
                    }
                    else if (tex.UseVariants)
                    {
                        ok = ProcessVariantFolder(mat, tex, textureName, gamePath, modPath, report, groups, source, ct);
                    }
                    else
                    {
                        Step(textureName);
                        ok = ProcessSingleTexture(tex, textureName, gamePath, modPath, report, source);
                        if (ok) defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
                    }

                    if (ok) replacements[tex.Type] = gamePath;
                }

                replacementsByMaterial[mat] = replacements;
            }

            // ── Pass 2: one .mtrl per mesh slot (and per race, for hair) ─────
            // Each slot uses its own effective name (its override, or the assigned
            // Material's name) — a Material assigned to several slots gets one
            // differently-named .mtrl per slot, all from the same textures. Gear writes
            // one race-shared copy; a hair port writes a copy into each race's folder,
            // named for that race.
            var materialRaces = subject.MaterialRaces(_req.Models);
            var writtenMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < _req.ModelMaterialSlots.Count && _req.Materials.Count > 0; s++)
            {
                var slot = _req.ModelMaterialSlots[s];
                var mat = _req.Materials[Math.Clamp(slot.MaterialIndex, 0, _req.Materials.Count - 1)];
                var effectiveName = string.IsNullOrWhiteSpace(slot.NameOverride) ? mat.Name : slot.NameOverride;

                foreach (var race in materialRaces)
                {
                    ct.ThrowIfCancellationRequested();
                    var raceName = subject.MaterialNameFor(effectiveName, race);
                    var matGamePath = subject.MaterialGamePath(raceName, race);
                    Step($"{raceName}.mtrl");

                    // Two copies resolving to the same file (a name that pins its own race) are written once.
                    if (!writtenMaterials.Add(matGamePath))
                        continue;

                    _req.VanillaTemplates.TryGetValue(BuildRequest.TemplateKey(effectiveName, race), out var template);
                    var label = race != null && materialRaces.Count > 1 ? $"{raceName}.mtrl ({race.Value.DisplayName})" : $"{raceName}.mtrl";
                    if (ProcessMaterial(mat, matGamePath, label, template, replacementsByMaterial[mat], modPath, report, new Target(TargetKind.MeshSlot, s)))
                        defaultFiles.Add(new KeyValuePair<string, string>(matGamePath, matGamePath));
                }
            }

            // ── Meta ─────────────────────────────────────────────────────────
            // Force every dye/recolor variant of this item back to the v0001 material
            // folder this tool always writes to, so the mod's material shows regardless
            // of which specific variant the equipped item instance uses.
            ct.ThrowIfCancellationRequested();
            Step("meta.json");
            var imcOverrides = _req.Materials.Count > 0 ? _req.ImcOverrides : null;

            ModMetaWriter.Write(modPath, _req.ModName, defaultFiles, groups, eqdpOverrides, imcOverrides, _req.Meta);
            report.Add(BuildOutcome.Ok, BuildCategory.Meta, "meta.json", "meta.json",
                $"{defaultFiles.Count} default file(s), {groups.Count} group(s)");

            report.VariantGroups = groups.Count;
            report.EqdpOverrides = eqdpOverrides.Count;
            report.ImcOverrides  = imcOverrides?.Count ?? 0;
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XPS] Mod build for {0} stopped", subject.DisplayName);
            report.FatalError = ex.Message;
        }

        report.Duration = clock.Elapsed;
        _job.Report(new BuildProgress(_total, _total, report.Cancelled ? "Cancelled" : "Done"));

        Plugin.Log.Information("[XPS] Mod build for {0}: {1} ok, {2} skipped, {3} failed, {4} variant groups, {5} eqdp, {6} imc. Path: {7}",
            subject.DisplayName, report.Ok, report.Skipped, report.Failed, report.VariantGroups, report.EqdpOverrides, report.ImcOverrides, modPath);
        return report;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Progress
    // ─────────────────────────────────────────────────────────────────────────

    private void Step(string current)
    {
        _job.Report(new BuildProgress(_done, _total, current));
        _done++;
    }

    /// <summary>Counts the files the build will attempt, so the progress bar has a real denominator.</summary>
    private int EstimateSteps()
    {
        int steps = _req.Models.Count + _req.ModelMaterialSlots.Count * _req.Subject.MaterialRaces(_req.Models).Count + 1;
        foreach (var tex in _req.Materials.SelectMany(m => m.Textures))
            steps += tex.UseVariants && !tex.UseWhiteDummy ? Math.Max(1, CountImages(tex.SourcePath)) : 1;
        return steps;
    }

    private static int CountImages(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? EnumerateImages(folder).Count() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static IEnumerable<string> EnumerateImages(string folder)
        => Directory.EnumerateFiles(folder)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    private static string LocalPath(string modPath, string gamePath)
    {
        var localPath = Path.Combine(modPath, gamePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        return localPath;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Steps
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Copies a race's local .mdl source file into the mod at its game path. Returns true on success.</summary>
    private static bool ProcessModel(RaceModelEntry model, string gamePath, string modPath, BuildReport report, Target source)
    {
        var label = $"{model.RaceGender.DisplayName} model";
        if (string.IsNullOrWhiteSpace(model.SourcePath))
        {
            report.Add(BuildOutcome.Skipped, BuildCategory.Model, label, gamePath, "no local file set", source);
            return false;
        }

        if (!File.Exists(model.SourcePath))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Model, label, gamePath, $"file not found ({model.SourcePath})", source);
            return false;
        }

        File.Copy(model.SourcePath, LocalPath(modPath, gamePath), true);
        report.Add(BuildOutcome.Ok, BuildCategory.Model, label, gamePath,
            model.IsVanillaDummy ? "empty dummy — still needs modeling" : null, source);
        return true;
    }

    /// <summary>
    /// Builds one .mtrl for a mesh slot: starts from a bundled shader preset matching the
    /// Material's ShaderType (falling back to cloning a vanilla material when no preset is
    /// bundled for that shader — see <see cref="MaterialPresetLibrary"/>), then rewires it to
    /// the textures that were successfully converted for this Material, matched by
    /// <see cref="TextureType"/>. Written at <paramref name="gamePath"/> rather than under the
    /// Material's own name, since one Material can back several differently-named slots (and,
    /// for hair, one copy per race).
    /// </summary>
    private bool ProcessMaterial(MaterialSetup mat, string gamePath, string label,
        (string GamePath, MtrlInfo Material)? vanillaTemplate, Dictionary<TextureType, string> replacements,
        string modPath, BuildReport report, Target source)
    {
        if (replacements.Count == 0)
        {
            if (mat.Textures.Count > 0)
                report.Add(BuildOutcome.Skipped, BuildCategory.Material, label, gamePath, "no textures converted", source);
            return false;
        }

        MtrlInfo template;
        string sourceDescription;

        var configuredTypes = mat.Textures.Select(t => t.Type).ToHashSet();
        var presetPath = MaterialPresetLibrary.FindPresetPath(mat.ShaderType, configuredTypes);
        if (presetPath != null)
        {
            var fullPresetPath = Path.Combine(_req.PluginDirectory, presetPath);
            if (!File.Exists(fullPresetPath))
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Material, label, gamePath, $"bundled preset missing ({fullPresetPath})", source);
                return false;
            }

            template = MtrlReader.Read(File.ReadAllBytes(fullPresetPath));
            sourceDescription = $"preset ({ShaderInfo.DisplayName(mat.ShaderType)})";
        }
        else
        {
            var found = vanillaTemplate;
            if (found == null)
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Material, label, gamePath,
                    $"no bundled preset for {ShaderInfo.DisplayName(mat.ShaderType)}, and no vanilla material found to clone for {_req.Subject.DisplayName}", source);
                return false;
            }

            template = found.Value.Material;
            sourceDescription = $"cloned from {found.Value.GamePath}";
        }

        if (!MaterialPatcher.TryBuildPatchedMaterial(template, replacements, out var mtrlData, out var appliedTypes, out var error))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Material, label, gamePath, error, source);
            return false;
        }

        File.WriteAllBytes(LocalPath(modPath, gamePath), mtrlData);

        var unapplied = replacements.Keys.Except(appliedTypes).ToList();
        var note = unapplied.Count > 0
            ? $"{sourceDescription}; has no slot for: {string.Join(", ", unapplied)}"
            : sourceDescription;
        report.Add(BuildOutcome.Ok, BuildCategory.Material, label, gamePath, note, source);
        return true;
    }

    /// <summary>Converts a single source image and writes it at its game path. Returns true on success.</summary>
    private static bool ProcessSingleTexture(TextureSlot tex, string textureName, string gamePath, string modPath,
        BuildReport report, Target source)
    {
        if (string.IsNullOrWhiteSpace(tex.SourcePath))
        {
            report.Add(BuildOutcome.Skipped, BuildCategory.Texture, textureName, gamePath, "no local file set", source);
            return false;
        }

        if (!File.Exists(tex.SourcePath))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Texture, textureName, gamePath, $"file not found ({tex.SourcePath})", source);
            return false;
        }

        if (!TextureConverter.Convert(tex.SourcePath, tex.CompressBc7, out var texData, out var error))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Texture, textureName, gamePath, error, source);
            return false;
        }

        File.WriteAllBytes(LocalPath(modPath, gamePath), texData);
        report.Add(BuildOutcome.Ok, BuildCategory.Texture, textureName, gamePath,
            $"{Path.GetFileName(tex.SourcePath)}{(tex.CompressBc7 ? ", BC7" : "")}", source);
        return true;
    }

    /// <summary>
    /// Fills a texture slot's placeholder: the generated white square (existing behaviour), or
    /// a bundled ready-made .tex copied in as-is — see <see cref="DummyTextureLibrary"/>.
    /// Returns true on success.
    /// </summary>
    private bool ProcessDummyTexture(TextureSlot tex, string textureName, string gamePath, string modPath,
        BuildReport report, Target source)
    {
        var option = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);

        if (option.IsGenerated)
        {
            if (!TextureConverter.CreateWhiteDummy(tex.WhiteDummySize, tex.CompressBc7, out var texData, out var error))
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Texture, textureName, gamePath, error, source);
                return false;
            }

            File.WriteAllBytes(LocalPath(modPath, gamePath), texData);
            report.Add(BuildOutcome.Ok, BuildCategory.Texture, textureName, gamePath,
                $"white {tex.WhiteDummySize}x{tex.WhiteDummySize} dummy", source);
            return true;
        }

        var fullPath = Path.Combine(_req.PluginDirectory, option.ResourcePath!);
        if (!File.Exists(fullPath))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Texture, textureName, gamePath, $"bundled dummy texture missing ({fullPath})", source);
            return false;
        }

        File.Copy(fullPath, LocalPath(modPath, gamePath), true);
        report.Add(BuildOutcome.Ok, BuildCategory.Texture, textureName, gamePath, $"{option.Label} (bundled dummy)", source);
        return true;
    }

    /// <summary>
    /// Converts every image in the slot's folder into a variant of the texture and
    /// registers a single-select switch group for it. Returns true when at least one
    /// variant was added.
    /// </summary>
    private bool ProcessVariantFolder(MaterialSetup mat, TextureSlot tex, string textureName, string gamePath,
        string modPath, BuildReport report, List<ModVariantGroup> groups, Target source, CancellationToken ct)
    {
        var folder = tex.SourcePath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Step(textureName);
            report.Add(BuildOutcome.Failed, BuildCategory.Variant, textureName, gamePath, $"variant folder not found ({folder})", source);
            return false;
        }

        var images = EnumerateImages(folder).ToList();
        if (images.Count == 0)
        {
            Step(textureName);
            report.Add(BuildOutcome.Failed, BuildCategory.Variant, textureName, gamePath, "no image files found in variant folder", source);
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
            ct.ThrowIfCancellationRequested();
            var imageName = Path.GetFileName(imagePath);
            Step($"{textureName} ← {imageName}");

            if (!TextureConverter.Convert(imagePath, tex.CompressBc7, out var texData, out var error))
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Variant, $"{textureName} / {imageName}", gamePath, error, source);
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
            report.Add(BuildOutcome.Ok, BuildCategory.Variant, $"{textureName} / {imageName}", gamePath,
                $"→ {variantRelPath}", source);
        }

        if (group.Options.Count == 0)
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Variant, textureName, gamePath, "no variants could be converted", source);
            return false;
        }

        groups.Add(group);
        return true;
    }
}
