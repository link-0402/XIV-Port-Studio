using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

internal enum BuildOutcome { Ok, Skipped, Failed }

internal enum BuildCategory { Model, Texture, Variant, Material, Meta }

/// <summary>One file (or one attempted file) the build produced.</summary>
internal sealed record BuildEntry(
    BuildOutcome  Outcome,
    BuildCategory Category,
    string        Label,
    string?       GamePath,
    string?       Detail,
    Target?       Source = null);

/// <summary>Everything the build worker needs, deep-copied from the session on the render thread.</summary>
internal sealed class BuildRequest
{
    public required PortSubject               Subject            { get; init; }
    public required string                    ModRoot            { get; init; }
    public required string                    ModName            { get; init; }
    public required List<RaceModelEntry>      Models             { get; init; }
    public required List<MaterialSetup>       Materials          { get; init; }
    public required List<ModelMaterialSlot>   ModelMaterialSlots { get; init; }
    public required string                    PluginDirectory    { get; init; }
    public required ModMetaInfo               Meta               { get; init; }

    // Game data the build needs, read on the render thread before the worker starts, so
    // the worker only touches files on disk and never the game's data files.
    public required HashSet<RaceGender>       NativeRaces        { get; init; }
    public List<ModImcOverride>?              ImcOverrides       { get; init; }

    /// <summary>
    /// Vanilla materials to clone for materials whose shader has no bundled preset, keyed by
    /// <see cref="TemplateKey"/>. A missing or null entry means "none found".
    /// </summary>
    public IReadOnlyDictionary<string, (string GamePath, MtrlInfo Material)?> VanillaTemplates { get; init; }
        = new Dictionary<string, (string GamePath, MtrlInfo Material)?>();

    public static string TemplateKey(string materialName, RaceGender? race) => $"{materialName}|{race?.RaceCode}";
}

/// <summary>An immutable progress snapshot; the worker swaps in a new one per step.</summary>
internal sealed record BuildProgress(int Done, int Total, string Current)
{
    public float Fraction => Total <= 0 ? 0f : Math.Clamp(Done / (float)Total, 0f, 1f);
}

/// <summary>The structured result of one build, replacing the old single summary string.</summary>
internal sealed class BuildReport
{
    public required string ModName  { get; init; }
    public required string ModPath  { get; init; }
    public required string ItemName { get; init; }

    public List<BuildEntry> Entries { get; } = new();

    public int VariantGroups  { get; set; }
    public int EqdpOverrides  { get; set; }
    public int ImcOverrides   { get; set; }

    public TimeSpan Duration  { get; set; }
    public bool     Cancelled { get; set; }

    /// <summary>Set when the build stopped on an exception rather than a per-file failure.</summary>
    public string? FatalError { get; set; }

    /// <summary>Filled in on the render thread after the worker finishes.</summary>
    public PenumbraApiEc? AddModResult { get; set; }

    public int Ok      => Entries.Count(e => e.Outcome == BuildOutcome.Ok);
    public int Skipped => Entries.Count(e => e.Outcome == BuildOutcome.Skipped);
    public int Failed  => Entries.Count(e => e.Outcome == BuildOutcome.Failed);

    public bool Succeeded => FatalError == null && !Cancelled && Failed == 0;

    public bool RegisteredWithPenumbra => AddModResult is PenumbraApiEc.Success or PenumbraApiEc.NothingDone;

    public void Add(BuildOutcome outcome, BuildCategory category, string label, string? gamePath = null,
        string? detail = null, Target? source = null)
        => Entries.Add(new BuildEntry(outcome, category, label, gamePath, detail, source));

    /// <summary>One-line result, as shown at the top of the report.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        if (FatalError != null)
            sb.Append("Build stopped: ").Append(FatalError).Append(". ");
        else if (Cancelled)
            sb.Append("Build cancelled. ");

        sb.Append($"{Ok} written, {Skipped} skipped, {Failed} failed");
        if (VariantGroups > 0) sb.Append($" · {VariantGroups} variant group(s)");
        if (EqdpOverrides > 0) sb.Append($" · {EqdpOverrides} race override(s)");
        if (ImcOverrides  > 0) sb.Append($" · {ImcOverrides} dye-variant override(s)");
        sb.Append($" · {Duration.TotalSeconds:0.0}s");
        return sb.ToString();
    }

    /// <summary>The whole report as plain text, for the copy button and the log.</summary>
    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"XIV Port Studio build — {ItemName}");
        sb.AppendLine($"Mod: {ModName}");
        sb.AppendLine($"Folder: {ModPath}");
        sb.AppendLine(Summary());
        if (AddModResult != null)
            sb.AppendLine(RegisteredWithPenumbra
                ? "Registered with Penumbra."
                : $"Penumbra did not pick it up automatically ({AddModResult}) — run a mod rediscovery.");
        sb.AppendLine();

        foreach (var group in Entries.GroupBy(e => e.Category))
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var e in group)
            {
                var tag = e.Outcome switch
                {
                    BuildOutcome.Ok      => "ok     ",
                    BuildOutcome.Skipped => "skipped",
                    _                    => "FAILED ",
                };
                sb.Append("  ").Append(tag).Append("  ").Append(e.GamePath ?? e.Label);
                if (!string.IsNullOrEmpty(e.Detail))
                    sb.Append("  — ").Append(e.Detail);
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
