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
/// the race models, and writes one meta.json into a mod folder under Penumbra's mod
/// directory for every item in the pack. Texture and model slots marked as variants become
/// single-select switch groups; everything else is written as default files.
///
/// Runs on a worker thread against a <see cref="BuildRequest"/> snapshot. Registering the
/// finished mod with Penumbra happens afterwards on the render thread.
/// </summary>
internal sealed class ModBuilder
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".dds" };

    private readonly BuildJob _job;
    private readonly BuildRequest _req;
    private readonly TextureCache _textures;

    private int _done;
    private int _total;

    /// <summary>This run's cancellation, so the steps that wait on something else can be cut short.</summary>
    private CancellationToken _ct;

    public ModBuilder(BuildJob job)
    {
        _job = job;
        _req = job.Request;
        _textures = new TextureCache(_req.CacheDirectory);
    }

    public BuildReport Run(CancellationToken ct)
    {
        _ct = ct;
        var modPath = Path.Combine(_req.ModRoot, _req.ModName);
        var itemsSummary = _req.Items.Count switch
        {
            0 => "(no items)",
            1 => _req.Items[0].Subject.DisplayName,
            _ => $"{_req.Items.Count} items ({string.Join(", ", _req.Items.Select(i => i.Subject.DisplayName))})",
        };
        var report = new BuildReport { ModName = _req.ModName, ModPath = modPath, ItemsSummary = itemsSummary };
        var clock  = Stopwatch.StartNew();

        try
        {
            _total = EstimateSteps();
            Directory.CreateDirectory(modPath);

            var defaultFiles     = new List<KeyValuePair<string, string>>();
            var groups           = new List<ModVariantGroup>();
            var eqdpOverrides    = new List<ModEqdpOverride>();
            var eqpOverrides     = _req.Items.Select(i => i.EqpOverride).OfType<ModEqpOverride>().ToList();
            var estOverrides     = _req.Items.SelectMany(i => i.EstOverrides ?? Enumerable.Empty<ModEstOverride>()).ToList();
            var imcOverrides     = new List<ModImcOverride>();
            // Shared across every item, not reset per item: a game path collision between two
            // different items should be unreachable in practice (paths derive from each item's
            // own model/slot id), but this is the safety net if it ever happens.
            var writtenMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _req.Items)
            {
                ct.ThrowIfCancellationRequested();
                report.CurrentSubjectKey = item.Subject.Key;
                report.CurrentItemName   = item.Subject.DisplayName;

                RunItem(item, modPath, report, defaultFiles, groups, eqdpOverrides, writtenMaterials, ct);

                if (item.Materials.Count > 0 && item.ImcOverrides != null)
                    imcOverrides.AddRange(item.ImcOverrides);
            }

            // ── Meta ─────────────────────────────────────────────────────────
            // One meta.json for the whole pack, covering every item's files/groups/overrides.
            ct.ThrowIfCancellationRequested();
            report.CurrentSubjectKey = 0;
            report.CurrentItemName   = string.Empty;
            Step("meta.json");

            ModMetaWriter.Write(modPath, _req.ModName, defaultFiles, groups, eqdpOverrides,
                imcOverrides.Count > 0 ? imcOverrides : null, _req.Meta, eqpOverrides, estOverrides);
            report.Add(BuildOutcome.Ok, BuildCategory.Meta, "meta.json", "meta.json",
                $"{defaultFiles.Count} default file(s), {groups.Count} group(s)");

            report.VariantGroups = groups.Count;
            report.EqdpOverrides = eqdpOverrides.Count;
            report.ImcOverrides  = imcOverrides.Count;
            report.EqpOverrides  = eqpOverrides.Count;
            report.EstOverrides  = estOverrides.Count;
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XPS] Mod build for {0} stopped", itemsSummary);
            report.FatalError = ex.Message;
        }

        _textures.Prune();
        report.Duration = clock.Elapsed;
        _job.Report(new BuildProgress(_total, _total, report.Cancelled ? "Cancelled" : "Done"));

        Plugin.Log.Information("[XPS] Mod build for {0}: {1} ok, {2} skipped, {3} failed, {4} variant groups, {5} eqdp, {6} imc. Path: {7}",
            itemsSummary, report.Ok, report.Skipped, report.Failed, report.VariantGroups, report.EqdpOverrides, report.ImcOverrides, modPath);
        return report;
    }

    /// <summary>
    /// One item's contribution to the pack: its race models, its materials' textures, and
    /// one .mtrl per mesh slot (per race, for hair). Appends to the accumulators the whole
    /// pack shares, so everything ends up in one meta.json.
    /// </summary>
    private void RunItem(ItemBuildRequest item, string modPath, BuildReport report,
        List<KeyValuePair<string, string>> defaultFiles, List<ModVariantGroup> groups,
        List<ModEqdpOverride> eqdpOverrides, HashSet<string> writtenMaterials, CancellationToken ct)
    {
        var subject = item.Subject;

        // What this item has to claim in the Eqdp tables, merged per race: the model bit for every
        // race a model is written for, and the material bit for the base race its materials are
        // named after. Emitted once, at the end, against what the game already grants.
        var eqdpClaims = new Dictionary<RaceGender, ushort>();

        // ── Models ───────────────────────────────────────────────────────
        var nativeRaces = item.NativeRaces;
        for (int i = 0; i < item.Models.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var model = item.Models[i];
            var gamePath = subject.ModelGamePath(model.RaceGender);
            var modelTarget = new Target(TargetKind.Model, subject.Key, i);
            bool modelOk;

            if (model.UseDummy)
            {
                Step($"{model.RaceGender.DisplayName} dummy model");
                modelOk = ProcessDummyModel(item, model, gamePath, modPath, report, modelTarget);
                if (modelOk) defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
            }
            else if (model.UseVariants)
            {
                modelOk = ProcessModelVariants(item, model, gamePath, modPath, report, groups, modelTarget, ct);
            }
            else
            {
                Step($"{model.RaceGender.DisplayName} model");
                modelOk = ProcessModel(item, model, gamePath, modPath, report, modelTarget);
                if (modelOk) defaultFiles.Add(new KeyValuePair<string, string>(gamePath, gamePath));
            }

            if (!modelOk)
                continue;

            if (subject is GearSubject gear && !nativeRaces.Contains(model.RaceGender))
                eqdpClaims[model.RaceGender] = (ushort)(eqdpClaims.GetValueOrDefault(model.RaceGender) | EqdpInfo.Model(gear.Item.Slot));
        }

        // ── Pass 1: textures, once per Material ──────────────────────────
        // Independent of how many mesh slots end up pointing at that Material.
        var replacementsByMaterial = new Dictionary<MaterialSetup, Dictionary<TextureType, string>>();
        for (int m = 0; m < item.Materials.Count; m++)
        {
            var mat = item.Materials[m];
            var replacements = new Dictionary<TextureType, string>();

            for (int t = 0; t < mat.Textures.Count; t++)
            {
                ct.ThrowIfCancellationRequested();
                var tex = mat.Textures[t];
                var textureName = MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix);
                var gamePath = subject.TextureGamePath(textureName);
                var source = new Target(TargetKind.Texture, subject.Key, m, t);
                bool ok;

                if (tex.IsShared)
                {
                    // Another material writes this texture; this one only points at it.
                    Step(textureName);
                    report.Add(BuildOutcome.Ok, BuildCategory.Texture, textureName, tex.SharedGamePath,
                        "shared — written by the material it was copied from", source);
                    replacements[tex.Type] = tex.SharedGamePath;
                    continue;
                }

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
        var materialRaces = item.MaterialRaces;
        for (int s = 0; s < item.ModelMaterialSlots.Count && item.Materials.Count > 0; s++)
        {
            var slot = item.ModelMaterialSlots[s];
            var mat = item.Materials[Math.Clamp(slot.MaterialIndex, 0, item.Materials.Count - 1)];
            var effectiveName = string.IsNullOrWhiteSpace(slot.NameOverride) ? mat.Name : slot.NameOverride;

            foreach (var race in materialRaces)
            {
                ct.ThrowIfCancellationRequested();
                var raceName = subject.MaterialNameFor(effectiveName, race);
                var matGamePath = subject.MaterialGamePath(raceName, race);
                Step($"{raceName}.mtrl");

                // Two copies resolving to the same file (a name that pins its own race) are
                // written once — shared across every item in the pack, not just this one.
                if (!writtenMaterials.Add(matGamePath))
                {
                    report.Add(BuildOutcome.Skipped, BuildCategory.Material, $"{raceName}.mtrl", matGamePath,
                        "already written by another material or item", new Target(TargetKind.Material, subject.Key, s));
                    continue;
                }

                if (subject is GearSubject materialGear && race is { } materialRace)
                    eqdpClaims[materialRace] = (ushort)(eqdpClaims.GetValueOrDefault(materialRace) | EqdpInfo.Material(materialGear.Item.Slot));

                item.VanillaTemplates.TryGetValue(BuildRequest.TemplateKey(effectiveName, race), out var template);
                var label = race != null && materialRaces.Count > 1 ? $"{raceName}.mtrl ({race.Value.DisplayName})" : $"{raceName}.mtrl";
                if (ProcessMaterial(mat, subject.DisplayName, matGamePath, label, template, replacementsByMaterial[mat], modPath, report, new Target(TargetKind.Material, subject.Key, s)))
                    defaultFiles.Add(new KeyValuePair<string, string>(matGamePath, matGamePath));
            }
        }

        // ── Eqdp ─────────────────────────────────────────────────────────
        AddEqdpOverrides(item, eqdpClaims, eqdpOverrides, report);
    }

    /// <summary>
    /// Turns this item's claims into Eqdp manipulations: for each race, what the game already grants
    /// for the slot plus what the build wrote. A race the game already covers needs no manipulation,
    /// which is why the entry is compared against the game's own before being added — and why
    /// anything the game grants for the slot is carried over rather than replaced.
    /// </summary>
    private static void AddEqdpOverrides(ItemBuildRequest item, Dictionary<RaceGender, ushort> claims,
        List<ModEqdpOverride> overrides, BuildReport report)
    {
        if (item.Subject is not GearSubject gear || claims.Count == 0)
            return;

        var slot = gear.Item.Slot;
        foreach (var (race, claimed) in claims.OrderBy(c => c.Key.RaceCode, StringComparer.Ordinal))
        {
            ushort vanilla = (ushort)(item.VanillaEqdp.GetValueOrDefault(race.RaceCode) & EqdpInfo.Mask(slot));
            ushort entry   = (ushort)(vanilla | claimed);
            if (entry == vanilla)
                continue;   // the game already says this race has them

            overrides.Add(new ModEqdpOverride
            {
                RaceGender = race,
                Slot       = slot,
                SetId      = gear.Item.ModelId,
                Entry      = entry,
            });
            report.Add(BuildOutcome.Ok, BuildCategory.Meta, $"Eqdp · {race.DisplayName}", null,
                $"{EqdpInfo.Describe(slot, (ushort)(entry & ~vanilla))} of its own for this item",
                new Target(TargetKind.Item, gear.Key));
        }
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
        int steps = 1; // meta.json
        foreach (var item in _req.Items)
        {
            steps += item.ModelMaterialSlots.Count * item.MaterialRaces.Count;
            foreach (var model in item.Models)
                steps += model.UseVariants && !model.UseDummy ? Math.Max(1, model.VariantPaths.Count) : 1;
            foreach (var tex in item.Materials.SelectMany(m => m.Textures))
                steps += tex.UseVariants && !tex.UseWhiteDummy && !tex.IsShared ? Math.Max(1, CountImages(tex.SourcePath)) : 1;
        }
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
    private static bool ProcessModel(ItemBuildRequest item, RaceModelEntry model, string gamePath, string modPath,
        BuildReport report, Target source)
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

        var (failed, detail) = WriteModel(model.SourcePath, LocalPath(modPath, gamePath), item, model, model.MaterialLinks);
        if (model.IsVanillaDummy)
            detail = detail == null ? "empty dummy — still needs modeling" : $"empty dummy — still needs modeling; {detail}";

        report.Add(failed ? BuildOutcome.Failed : BuildOutcome.Ok, BuildCategory.Model, label, gamePath, detail, source);
        return true;
    }

    /// <summary>
    /// Writes one .mdl into the mod with each of its mesh parts switched to the material the user
    /// assigned it. A model made anywhere else still references the material names it was exported
    /// with, so without this the .mtrl files the build writes would sit in the mod unused.
    ///
    /// The file on disk is never touched: it is copied through unchanged when nothing needs
    /// switching, and also when it cannot be read — which is reported as a failure, since the
    /// assignment the user made is then missing from the mod.
    /// </summary>
    private static (bool Failed, string? Detail) WriteModel(string sourcePath, string targetPath,
        ItemBuildRequest item, RaceModelEntry model, IReadOnlyList<int>? links)
    {
        var written = ModelMaterialLinks.WrittenNames(item.Subject, item.Materials, MaterialRaceOf(item, model));
        bool assigns = written.Count > 0 && (links == null || links.Any(l => l != ModelMaterialLinks.Keep));

        byte[] data;
        IReadOnlyList<string> referenced;
        try
        {
            data = File.ReadAllBytes(sourcePath);
            referenced = ModelMaterialRewriter.ReadMaterials(data);
        }
        catch (Exception ex)
        {
            File.Copy(sourcePath, targetPath, true);
            return assigns
                ? (true, $"copied as it is — its materials could not be read ({ex.Message})")
                : (false, null);
        }

        var plan = ModelMaterialLinks.Plan(referenced, links, written);
        int changes = ModelMaterialLinks.CountChanges(referenced, plan);
        if (changes == 0)
        {
            File.Copy(sourcePath, targetPath, true);
            return (false, assigns && referenced.Count > 0 ? "materials left as the file has them" : null);
        }

        try
        {
            var rewritten = ModelMaterialRewriter.Rewrite(data, plan);

            // Check the result the way the game reads it, not the way it was written: a material
            // name the string table cannot resolve looks fine in the bytes and loads as nothing.
            var resolved = ModelMaterialRewriter.ReadMaterialsStrict(rewritten);
            if (resolved.Any(string.IsNullOrEmpty))
            {
                File.Copy(sourcePath, targetPath, true);
                return (true, "copied as it is — the rewritten model would not resolve all of its material names");
            }

            File.WriteAllBytes(targetPath, rewritten);
        }
        catch (Exception ex)
        {
            File.Copy(sourcePath, targetPath, true);
            return (true, $"copied as it is — its materials could not be switched ({ex.Message})");
        }

        return (false, $"{changes} of {referenced.Count} mesh material(s) pointed at this mod's own");
    }

    /// <summary>
    /// Copies each of a race's picked .mdl variant files into the mod and registers a
    /// single-select switch group for them, the model counterpart of
    /// <see cref="ProcessVariantFolder"/>. Returns true when at least one variant was added.
    /// </summary>
    private bool ProcessModelVariants(ItemBuildRequest item, RaceModelEntry model, string gamePath, string modPath,
        BuildReport report, List<ModVariantGroup> groups, Target source, CancellationToken ct)
    {
        var label = $"{model.RaceGender.DisplayName} model";

        // Carry each file's own index: its material assignment is stored against it, and blank
        // entries would shift the rest.
        var files = model.VariantPaths
            .Select((path, index) => (Path: path, Index: index))
            .Where(v => !string.IsNullOrWhiteSpace(v.Path))
            .ToList();
        if (files.Count == 0)
        {
            Step(label);
            report.Add(BuildOutcome.Skipped, BuildCategory.Model, label, gamePath, "no variant files set", source);
            return false;
        }

        var group = new ModVariantGroup
        {
            Name     = label,
            GamePath = gamePath,
        };
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, variantIndex) in files)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            Step($"{label} ← {fileName}");

            if (!File.Exists(filePath))
            {
                report.Add(BuildOutcome.Failed, BuildCategory.Variant, $"{label} / {fileName}", gamePath, "file not found", source);
                continue;
            }

            var stem = MaterialNaming.SanitizeFileName(Path.GetFileNameWithoutExtension(filePath));
            var raceCode = model.RaceGender.RaceCode;
            var variantFileName = $"{raceCode}_{stem}.mdl";
            int counter = 2;
            while (!usedNames.Add(variantFileName))
                variantFileName = $"{raceCode}_{stem}_{counter++}.mdl";

            var variantRelPath = $"modelvariants/{variantFileName}";
            var variantPath = Path.Combine(modPath, "modelvariants", variantFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(variantPath)!);

            // Each variant carries its own material assignment: they start alike and can differ.
            var (failed, detail) = WriteModel(filePath, variantPath, item, model, model.VariantLinks(variantIndex));

            group.Options.Add((Path.GetFileNameWithoutExtension(filePath), variantRelPath));
            report.Add(failed ? BuildOutcome.Failed : BuildOutcome.Ok, BuildCategory.Variant, $"{label} / {fileName}", gamePath,
                detail == null ? $"→ {variantRelPath}" : $"→ {variantRelPath} · {detail}", source);
        }

        if (group.Options.Count == 0)
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Variant, label, gamePath, "no variants could be copied", source);
            return false;
        }

        groups.Add(group);
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
    private bool ProcessMaterial(MaterialSetup mat, string itemDisplayName, string gamePath, string label,
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
                    $"no bundled preset for {ShaderInfo.DisplayName(mat.ShaderType)}, and no vanilla material found to clone for {itemDisplayName}", source);
                return false;
            }

            template = found.Value.Material;
            sourceDescription = $"cloned from {found.Value.GamePath}";
        }

        if (!MaterialPatcher.TryBuildPatchedMaterial(template, replacements, presetPath != null, out var mtrlData, out var plan, out var error))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Material, label, gamePath, error, source);
            return false;
        }

        File.WriteAllBytes(LocalPath(modPath, gamePath), mtrlData);

        var changes = plan.Describe();
        var note = changes.Length > 0 ? $"{sourceDescription}; {changes}" : sourceDescription;
        report.Add(BuildOutcome.Ok, BuildCategory.Material, label, gamePath, note, source);
        return true;
    }

    /// <summary>
    /// Generates this race's model instead of copying one in: its vanilla skeleton with one empty
    /// mesh part per material, each pointing at that material, so the port loads in game and can be
    /// modelled over later. Returns true on success.
    /// </summary>
    private bool ProcessDummyModel(ItemBuildRequest item, RaceModelEntry model, string gamePath, string modPath,
        BuildReport report, Target source)
    {
        var label = $"{model.RaceGender.DisplayName} model";
        if (item.Materials.Count == 0)
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Model, label, gamePath,
                "a generated model needs at least one material - each becomes an empty mesh part", source);
            return false;
        }

        if (!item.DummyBases.TryGetValue(model.RaceGender.RaceCode, out var vanilla))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Model, label, gamePath,
                "the game has no model here to take the skeleton from", source);
            return false;
        }

        var materialNames = item.Materials
            .Select(m => $"{MaterialNaming.SanitizeFileName(item.Subject.MaterialNameFor(m.Name, MaterialRaceOf(item, model)))}.mtrl")
            .ToList();

        try
        {
            File.WriteAllBytes(LocalPath(modPath, gamePath), DummyModelBuilder.Build(vanilla, materialNames));
        }
        catch (Exception ex)
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Model, label, gamePath, $"{ex.GetType().Name}: {ex.Message}", source);
            return false;
        }

        report.Add(BuildOutcome.Ok, BuildCategory.Model, label, gamePath,
            $"generated: vanilla skeleton with {materialNames.Count} empty mesh part(s)", source);
        return true;
    }

    /// <summary>
    /// The race a model's materials are looked up under: not always its own, since the game shares
    /// materials per gender (gear) and per race group (features). Resolved before the build started.
    /// </summary>
    private static RaceGender MaterialRaceOf(ItemBuildRequest item, RaceModelEntry model)
        => item.MaterialRaceByModel.TryGetValue(model.RaceGender.RaceCode, out var race) ? race : model.RaceGender;

    /// <summary>Converts a single source image and writes it at its game path. Returns true on success.</summary>
    private bool ProcessSingleTexture(TextureSlot tex, string textureName, string gamePath, string modPath,
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

        if (!ConvertTexture(tex.SourcePath, tex.Compression, textureName, out var texData, out var error, out bool cached))
        {
            report.Add(BuildOutcome.Failed, BuildCategory.Texture, textureName, gamePath, error, source);
            return false;
        }

        File.WriteAllBytes(LocalPath(modPath, gamePath), texData);
        report.Add(BuildOutcome.Ok, BuildCategory.Texture, textureName, gamePath,
            Describe(Path.GetFileName(tex.SourcePath), tex.Compression, cached), source);
        return true;
    }

    /// <summary>
    /// Converts one image, reusing the result of a previous build where the file and the settings
    /// are unchanged. Compressing is the slowest thing a build does \u2014 BC7 on a 4096\u00d74096 image runs
    /// the better part of a minute across every core \u2014 so the progress line carries a percentage
    /// while it works, and the cache means only new or edited images pay for it again.
    /// </summary>
    private bool ConvertTexture(string sourcePath, TextureCompression compression, string label,
        out byte[] texData, out string error, out bool cached)
    {
        // Penumbra and this plugin's own encoder do not produce the same bytes, so which one made an
        // entry is part of what identifies it.
        bool native = _req.ConvertTexture != null && compression != TextureCompression.None;
        var key = TextureCache.KeyFor(sourcePath, compression, native ? "penumbra" : "managed");
        if (_textures.Get(key) is { } hit)
        {
            texData = hit;
            error   = string.Empty;
            cached  = true;
            return true;
        }

        cached = false;
        if (native)
        {
            _job.Report(new BuildProgress(_done - 1, _total, $"{label} \u2014 compressing"));
            var scratch = Path.Combine(Path.GetTempPath(), $"xps-{Guid.NewGuid():N}.tex");
            try
            {
                var failure = _req.ConvertTexture!(sourcePath, compression, scratch, _ct);
                if (failure == null)
                {
                    texData = File.ReadAllBytes(scratch);
                    error   = string.Empty;
                    _textures.Store(key, texData);
                    return true;
                }

                // Not fatal: the encoder bundled here can do the same job, only slower.
                Plugin.Log.Information("[XPS] Penumbra did not convert {0} ({1}); compressing it here instead", sourcePath, failure);
            }
            finally
            {
                try { File.Delete(scratch); } catch (Exception) { /* a temp file that outlives us is harmless */ }
            }
        }

        var progress = new Progress<float>(fraction =>
            _job.Report(new BuildProgress(_done - 1, _total, $"{label} \u2014 compressing {fraction:P0}")));

        if (!TextureConverter.Convert(sourcePath, compression, out texData, out error, compression == TextureCompression.None ? null : progress))
            return false;

        _textures.Store(key, texData);
        return true;
    }

    /// <summary>The report line for a converted texture: where it came from, how it was written.</summary>
    private static string Describe(string sourceName, TextureCompression compression, bool cached)
    {
        var format = compression switch
        {
            TextureCompression.Bc7 => ", BC7",
            TextureCompression.Bc3 => ", BC3",
            _                      => string.Empty,
        };
        return $"{sourceName}{format}{(cached ? " (unchanged, reused)" : string.Empty)}";
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
            var dummyKey = TextureCache.KeyForGenerated("white", tex.WhiteDummySize, tex.Compression);
            var texData = _textures.Get(dummyKey);
            string error = string.Empty;
            if (texData == null && TextureConverter.CreateWhiteDummy(tex.WhiteDummySize, tex.Compression, out texData, out error))
                _textures.Store(dummyKey, texData);

            if (texData == null)
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

            if (!ConvertTexture(imagePath, tex.Compression, $"{textureName} \u2190 {imageName}", out var texData, out var error, out _))
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
