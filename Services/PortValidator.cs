using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

internal enum Severity { Info, Warning, Error }

/// <summary>One problem with the current set-up, pointing at the object that has it.</summary>
internal readonly record struct PortIssue(Severity Severity, Target Target, string Message);

/// <summary>
/// Checks the session for everything the build would otherwise only discover part-way
/// through: missing files, empty variant folders, shaders the build has no template for,
/// and two mesh slots writing the same .mtrl. Severities mirror what the build does —
/// something it would skip is a warning, something it would fail on is an error.
///
/// Pure over the session; the main window re-runs it when the session changes and on a
/// slow timer (files on disk can appear or vanish without an edit).
/// </summary>
internal static class PortValidator
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".dds" };

    public static List<PortIssue> Validate(PortSession session, GameDataService gameData, bool penumbraAvailable)
    {
        var issues = new List<PortIssue>();
        var subject = session.Subject;
        if (subject == null)
            return issues;

        var build = new Target(TargetKind.Build);

        if (!penumbraAvailable)
            issues.Add(new(Severity.Error, build, "Penumbra is not available — the mod folder cannot be resolved."));

        // A feature port replaces a vanilla file; an id the game does not have replaces nothing.
        if (subject is FeatureSubject feature && !gameData.GetNativeRaces(subject).Contains(feature.Race))
            issues.Add(new(Severity.Error, new Target(TargetKind.Item),
                $"The game has no {subject.DisplayName} — there is nothing for this port to replace."));

        bool hasModels = session.Models.Any(m => !string.IsNullOrWhiteSpace(m.SourcePath)) || subject.MultiRace && session.Models.Count > 0;
        if (!hasModels && session.Materials.Count == 0)
        {
            issues.Add(new(Severity.Info, new Target(TargetKind.Item), "Nothing set up yet — add a model or a material."));
            return issues;
        }

        if (session.Models.Count == 0)
            issues.Add(new(Severity.Info, new Target(TargetKind.Model),
                $"No race models — the mod will only replace this {subject.KindLabel.ToLowerInvariant()}'s materials and textures."));

        ValidateModels(session, gameData, subject, issues);
        ValidateMaterials(session, gameData, subject, issues);
        ValidateMeshSlots(session, gameData, subject, issues);

        issues.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return issues;
    }

    private static void ValidateModels(PortSession session, GameDataService gameData, PortSubject subject, List<PortIssue> issues)
    {
        // Hair ported to a race that has no vanilla hair of this id: the game may never ask for it.
        var native = subject.Kind == SubjectKind.Hair ? gameData.GetNativeRaces(subject) : null;

        for (int i = 0; i < session.Models.Count; i++)
        {
            var model = session.Models[i];
            var target = new Target(TargetKind.Model, i);
            var who = model.RaceGender.DisplayName;

            if (native != null && !native.Contains(model.RaceGender))
                issues.Add(new(Severity.Warning, target,
                    $"{who}: has no vanilla {subject.KindLabel.ToLowerInvariant()} {((FeatureSubject)subject).Id}, so the game may not load this model for that race — check in game."));

            if (string.IsNullOrWhiteSpace(model.SourcePath) && !subject.MultiRace)
                issues.Add(new(Severity.Info, target, $"{who}: no model file set — only materials and textures will be replaced."));
            else if (string.IsNullOrWhiteSpace(model.SourcePath))
                issues.Add(new(Severity.Warning, target, $"{who}: no model file set — this race will be skipped."));
            else if (!File.Exists(model.SourcePath))
                issues.Add(new(Severity.Error, target, $"{who}: model file not found."));
            else if (!model.SourcePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(Severity.Warning, target, $"{who}: source is not a .mdl file."));
            else if (model.IsVanillaDummy)
                issues.Add(new(Severity.Info, target, $"{who}: still the empty dummy — model it in Blender before release."));
        }
    }

    private static void ValidateMaterials(PortSession session, GameDataService gameData, PortSubject subject,
        List<PortIssue> issues)
    {
        for (int m = 0; m < session.Materials.Count; m++)
        {
            var mat = session.Materials[m];
            var target = new Target(TargetKind.Material, m);
            var who = string.IsNullOrWhiteSpace(mat.Name) ? $"Material {m + 1}" : mat.Name;

            if (string.IsNullOrWhiteSpace(mat.Name))
                issues.Add(new(Severity.Error, target, $"{who}: has no name, so it has no file name."));
            else if (!mat.Name.StartsWith("mt_", StringComparison.Ordinal))
                issues.Add(new(Severity.Warning, target, $"{who}: game materials are named \"mt_…\" — the model's material slots must match this name exactly."));

            if (mat.Textures.Count == 0)
                issues.Add(new(Severity.Warning, target, $"{who}: has no textures, so no .mtrl will be written for it."));

            var configuredTypes = mat.Textures.Select(t => t.Type).ToHashSet();
            if (MaterialPresetLibrary.FindPresetPath(mat.ShaderType, configuredTypes) == null)
            {
                var what = subject.KindLabel.ToLowerInvariant();
                issues.Add(gameData.HasVanillaMaterial(subject)
                    ? new(Severity.Info, target,
                        $"{who}: no bundled {ShaderInfo.DisplayName(mat.ShaderType)} preset — the {what}'s own vanilla material will be cloned instead.")
                    : new(Severity.Error, target,
                        $"{who}: no bundled {ShaderInfo.DisplayName(mat.ShaderType)} preset, and this {what} has no vanilla material to clone."));
            }

            foreach (var dup in mat.Textures.GroupBy(t => t.Type).Where(g => g.Count() > 1))
                issues.Add(new(Severity.Warning, target, $"{who}: more than one {TextureTypeInfo.DisplayName(dup.Key)} texture — only the last is used by the material."));

            foreach (var dup in mat.Textures.GroupBy(t => t.Postfix, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                issues.Add(new(Severity.Error, target, $"{who}: several textures use the postfix \"{dup.Key}\", so they overwrite each other."));

            for (int t = 0; t < mat.Textures.Count; t++)
                ValidateTexture(mat.Textures[t], new Target(TargetKind.Texture, m, t), $"{who} / {TextureTypeInfo.DisplayName(mat.Textures[t].Type)}", issues);
        }
    }

    private static void ValidateTexture(TextureSlot tex, Target target, string who, List<PortIssue> issues)
    {
        if (tex.UseWhiteDummy)
            return;

        if (tex.UseVariants)
        {
            if (string.IsNullOrWhiteSpace(tex.SourcePath) || !Directory.Exists(tex.SourcePath))
            {
                issues.Add(new(Severity.Error, target, $"{who}: variant folder not found."));
                return;
            }

            bool anyImage;
            try
            {
                anyImage = Directory.EnumerateFiles(tex.SourcePath).Any(f => ImageExtensions.Contains(Path.GetExtension(f)));
            }
            catch (Exception)
            {
                anyImage = false;
            }

            if (!anyImage)
                issues.Add(new(Severity.Error, target, $"{who}: variant folder has no png / jpeg / dds images."));
            return;
        }

        if (string.IsNullOrWhiteSpace(tex.SourcePath))
            issues.Add(new(Severity.Warning, target, $"{who}: no source file — this texture will be skipped."));
        else if (!File.Exists(tex.SourcePath))
            issues.Add(new(Severity.Error, target, $"{who}: source file not found."));
        else if (!ImageExtensions.Contains(Path.GetExtension(tex.SourcePath)))
            issues.Add(new(Severity.Error, target, $"{who}: unsupported file type (use png, jpeg or dds)."));
    }

    private static void ValidateMeshSlots(PortSession session, GameDataService gameData, PortSubject subject, List<PortIssue> issues)
    {
        // Names the vanilla model uses — a feature port's model must reference these exactly.
        HashSet<string>? vanillaNames = null;
        if (subject is FeatureSubject feature)
        {
            vanillaNames = gameData.GetVanillaMaterialNames(subject, feature.Race)
                .Select(n => MaterialNaming.SanitizeFileName(n.TrimStart('/')))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var races = subject.MaterialRaces(session.Models);
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int s = 0; s < session.ModelMaterialSlots.Count; s++)
        {
            var name = session.EffectiveMaterialName(session.ModelMaterialSlots[s]);
            var target = new Target(TargetKind.MeshSlot, s);

            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(new(Severity.Error, target, $"Mesh slot {s + 1}: resolves to an empty material name."));
                continue;
            }

            if (vanillaNames is { Count: > 0 } && !vanillaNames.Contains(MaterialNaming.SanitizeFileName(name)))
                issues.Add(new(Severity.Warning, target,
                    $"Mesh slot {s + 1} ({name}) is not one of the vanilla material names ({string.Join(", ", vanillaNames)}) — " +
                    "fine if your model references it, but a vanilla-named material is what the game looks for by default."));

            // Each race copy of each slot must land on its own file.
            foreach (var race in races)
            {
                var path = subject.MaterialGamePath(subject.MaterialNameFor(name, race), race);
                if (byPath.TryGetValue(path, out var first) && first != s)
                {
                    issues.Add(new(Severity.Error, target,
                        $"Mesh slot {s + 1} writes the same material as slot {first + 1} ({name}) — only one of them would survive."));
                    break;
                }
                byPath[path] = s;
            }
        }
    }

    /// <summary>Counts issues of a severity whose target lives on a stage.</summary>
    public static int Count(IReadOnlyList<PortIssue> issues, StageId stage, Severity severity)
        => issues.Count(i => i.Target.Stage == stage && i.Severity == severity);

    /// <summary>The most severe issue on exactly this target, if any.</summary>
    public static PortIssue? Worst(IReadOnlyList<PortIssue> issues, Target target)
    {
        PortIssue? worst = null;
        foreach (var issue in issues)
            if (issue.Target == target && (worst == null || issue.Severity > worst.Value.Severity))
                worst = issue;
        return worst;
    }
}
