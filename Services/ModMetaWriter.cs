using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

/// <summary>One switch group in the mod: a target game path swapped between several variant files.</summary>
public sealed class ModVariantGroup
{
    public string Name { get; init; } = "Variants";

    /// <summary>Target game path this group replaces (e.g. "chara/equipment/e0164/.../top_d.tex").</summary>
    public string GamePath { get; init; } = string.Empty;

    /// <summary>(Option name, mod-relative file path) pairs, in display order.</summary>
    public List<(string OptionName, string FilePath)> Options { get; } = new();
}

/// <summary>
/// An Eqdp (Equipment Deformer Parameter) override: tells the game that a race has files of its
/// own for this item — a model, a material, or both (see <see cref="EqdpInfo"/>). Needed whenever a
/// port writes a model for a race the game gives none, and whenever it writes a material under a
/// base race the game has no material entry for.
/// </summary>
public sealed class ModEqdpOverride
{
    public required RaceGender RaceGender { get; init; }
    public required EquipSlot  Slot       { get; init; }
    public required ushort     SetId      { get; init; }

    /// <summary>The slot's bits as they should end up: what the game already grants, plus what this port needs.</summary>
    public required ushort     Entry      { get; init; }
}

/// <summary>
/// An Imc override: forces a single dye/recolor variant's MaterialId back to 1 (the
/// "v0001" material folder this tool always writes to), while preserving every other
/// field of that variant's own vanilla entry (decal, VFX, animation, attributes, sound).
/// One of these is written per variant an item has, so the mod's material shows up
/// regardless of which specific recolor variant the equipped item instance uses.
/// </summary>
public sealed class ModImcOverride
{
    public required EquipSlot Slot                { get; init; }
    public required ushort    SetId                { get; init; }
    public required byte      Variant              { get; init; }
    public required byte      DecalId              { get; init; }
    public required byte      VfxId                { get; init; }
    public required byte      MaterialAnimationId  { get; init; }
    public required ushort    AttributeMask        { get; init; }
    public required byte      SoundId              { get; init; }
}

/// <summary>
/// An Eqp (Equipment Parameter) override: what a piece hides or shows on the rest of the
/// character. <see cref="Entry"/> is the whole 64-bit entry, as Penumbra serialises it; only the
/// bytes of the item's own slot are meaningful, the rest are carried over from the game's entry.
/// </summary>
public sealed class ModEqpOverride
{
    public required EquipSlot Slot  { get; init; }
    public required ushort    SetId { get; init; }
    public required ulong     Entry { get; init; }
}

/// <summary>
/// An Est (Extra Skeleton Table) override: which skeleton a race uses for a hair id, which is
/// what lets a ported hairstyle keep its physics on a race the game has no entry for.
/// </summary>
public sealed class ModEstOverride
{
    public required RaceGender RaceGender { get; init; }
    public required ushort     SetId      { get; init; }
    public required ushort     SkeletonId { get; init; }

    /// <summary>Penumbra's EstType member name; only hair is offered for now.</summary>
    public string Slot { get; init; } = "Hair";
}

/// <summary>The mod's identity and descriptive fields as written into meta.json.</summary>
public sealed class ModMetaInfo
{
    /// <summary>Mod identifier. Reusing it across rebuilds keeps Penumbra's settings for the mod.</summary>
    public Guid   Identifier  { get; init; } = Guid.NewGuid();
    public string Author      { get; init; } = "XIV Port Studio";
    public string Description { get; init; } = string.Empty;
    public string Version     { get; init; } = "1.0";
    public string Website     { get; init; } = string.Empty;
}

/// <summary>
/// Writes the Penumbra <c>meta.json</c> (FileVersion 4, as of Penumbra 1.7+) for a
/// created mod: default files plus single-select groups that switch between texture
/// variants. The layout mirrors what Penumbra's ModSerialization/GroupSerialization
/// write. Since 1.7, groups and default options live in meta.json again (no more
/// separate group_*.json / default_mod.json files), and every mod/group/option gets
/// a stable GUID identifier.
/// </summary>
public static class ModMetaWriter
{
    /// <summary>
    /// Writes meta.json into <paramref name="modPath"/>.
    /// <paramref name="defaultFiles"/> maps game paths to mod-relative file paths
    /// (usually identical, as default textures are placed at their game paths).
    /// </summary>
    public static void Write(string modPath, string modName,
        IReadOnlyList<KeyValuePair<string, string>> defaultFiles,
        IReadOnlyList<ModVariantGroup> groups,
        IReadOnlyList<ModEqdpOverride>? eqdpOverrides = null,
        IReadOnlyList<ModImcOverride>? imcOverrides = null,
        ModMetaInfo? info = null,
        IReadOnlyList<ModEqpOverride>? eqpOverrides = null,
        IReadOnlyList<ModEstOverride>? estOverrides = null)
    {
        eqdpOverrides ??= Array.Empty<ModEqdpOverride>();
        imcOverrides  ??= Array.Empty<ModImcOverride>();
        eqpOverrides  ??= Array.Empty<ModEqpOverride>();
        estOverrides  ??= Array.Empty<ModEstOverride>();
        info          ??= new ModMetaInfo();

        using var stream = File.Create(Path.Combine(modPath, "meta.json"));
        using var j = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        j.WriteStartObject();
        j.WriteNumber("FileVersion", 4);
        j.WriteString("Identifier", info.Identifier);
        j.WriteString("LastWrite", DateTime.UtcNow);
        j.WriteString("Name", modName);
        j.WriteString("Author", info.Author);
        j.WriteString("Description", string.IsNullOrWhiteSpace(info.Description) ? $"Port of {modName}" : info.Description);
        j.WriteString("Version", info.Version);
        j.WriteString("Website", info.Website);

        bool anyManipulation = eqdpOverrides.Count > 0 || imcOverrides.Count > 0 || eqpOverrides.Count > 0 || estOverrides.Count > 0;
        if (defaultFiles.Count > 0 || anyManipulation)
        {
            j.WriteStartObject("DefaultData");
            if (defaultFiles.Count > 0)
                WriteFiles(j, defaultFiles);
            if (anyManipulation)
                WriteManipulations(j, eqdpOverrides, imcOverrides, eqpOverrides, estOverrides);
            j.WriteEndObject();
        }

        if (groups.Count > 0)
        {
            j.WriteStartArray("Groups");
            foreach (var group in groups)
                WriteGroup(j, group, info.Identifier);
            j.WriteEndArray();
        }

        j.WriteEndObject();
    }

    private static void WriteGroup(Utf8JsonWriter j, ModVariantGroup group, Guid modId)
    {
        var groupId = DerivedId(modId, group.GamePath);
        j.WriteStartObject();
        j.WriteString("Type", "Single");
        j.WriteString("Id", groupId.ToString());
        j.WriteString("Name", group.Name);
        j.WriteString("Description", "");
        j.WriteNumber("Priority", 0);
        j.WriteNumber("Page", 0);
        j.WriteNumber("DefaultSettings", 0); // index of the option selected by default (first option)

        j.WriteStartArray("Options");
        for (int i = 0; i < group.Options.Count; i++)
        {
            j.WriteStartObject();
            j.WriteString("Id", DerivedId(groupId, group.Options[i].FilePath).ToString());
            j.WriteString("Name", group.Options[i].OptionName);
            j.WriteString("Description", "");
            j.WriteStartObject("Files");
            j.WriteString(group.GamePath, group.Options[i].FilePath);
            j.WriteEndObject();
            j.WriteEndObject();
        }
        j.WriteEndArray();

        j.WriteEndObject();
    }

    /// <summary>
    /// A stable id for a child of <paramref name="parent"/>, so a rebuilt mod gives the same
    /// group and option the same id and Penumbra keeps the user's chosen option.
    /// </summary>
    private static Guid DerivedId(Guid parent, string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{parent:N}/{key}");
        var hash  = System.Security.Cryptography.SHA256.HashData(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void WriteFiles(Utf8JsonWriter j, IReadOnlyList<KeyValuePair<string, string>> files)
    {
        j.WriteStartObject("Files");
        foreach (var (gamePath, relPath) in files)
            j.WriteString(gamePath, relPath);
        j.WriteEndObject();
    }

    /// <summary>
    private static void WriteManipulations(Utf8JsonWriter j, IReadOnlyList<ModEqdpOverride> eqdpOverrides,
        IReadOnlyList<ModImcOverride> imcOverrides, IReadOnlyList<ModEqpOverride> eqpOverrides,
        IReadOnlyList<ModEstOverride> estOverrides)
    {
        j.WriteStartArray("Manipulations");

        foreach (var o in eqpOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Eqp");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", o.Entry);
            j.WriteNumber("SetId", o.SetId);
            j.WriteString("Slot", SlotInfo.LabelMap[o.Slot]);
            j.WriteEndObject();
            j.WriteEndObject();
        }

        foreach (var o in estOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Est");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", o.SkeletonId);
            j.WriteString("Gender", o.RaceGender.PenumbraGender);
            j.WriteString("Race", o.RaceGender.PenumbraRace);
            j.WriteNumber("SetId", o.SetId);
            j.WriteString("Slot", o.Slot);
            j.WriteEndObject();
            j.WriteEndObject();
        }

        foreach (var o in eqdpOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Eqdp");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", o.Entry);
            j.WriteString("Gender", o.RaceGender.PenumbraGender);
            j.WriteString("Race", o.RaceGender.PenumbraRace);
            j.WriteNumber("SetId", o.SetId);
            j.WriteString("Slot", SlotInfo.LabelMap[o.Slot]);
            j.WriteEndObject();
            j.WriteEndObject();
        }

        foreach (var o in imcOverrides)
        {
            j.WriteStartObject();
            j.WriteString("Type", "Imc");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("PrimaryId", o.SetId);
            j.WriteNumber("SecondaryId", 0);
            j.WriteNumber("Variant", o.Variant);
            j.WriteString("ObjectType", SlotInfo.IsAccessory(o.Slot) ? "Accessory" : "Equipment");
            j.WriteString("EquipSlot", SlotInfo.LabelMap[o.Slot]);
            j.WriteString("BodySlot", "Unknown");
            j.WriteStartObject("Entry");
            j.WriteNumber("MaterialId", 1); // always force the v0001 folder this tool writes to
            j.WriteNumber("DecalId", o.DecalId);
            j.WriteNumber("VfxId", o.VfxId);
            j.WriteNumber("MaterialAnimationId", o.MaterialAnimationId);
            j.WriteNumber("AttributeMask", o.AttributeMask);
            j.WriteNumber("SoundId", o.SoundId);
            j.WriteEndObject();
            j.WriteEndObject();
            j.WriteEndObject();
        }

        j.WriteEndArray();
    }
}
