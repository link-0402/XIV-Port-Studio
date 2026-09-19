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
/// An Eqdp (Equipment Deformer Parameter) override: tells Penumbra that a given
/// race/gender has its own model for this item, instead of falling back to
/// another race's file. Needed whenever a model is set up for a race that
/// doesn't have a native model entry by default.
/// </summary>
public sealed class ModEqdpOverride
{
    public required RaceGender RaceGender { get; init; }
    public required EquipSlot  Slot       { get; init; }
    public required ushort     SetId      { get; init; }
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
        ModMetaInfo? info = null)
    {
        eqdpOverrides ??= Array.Empty<ModEqdpOverride>();
        imcOverrides  ??= Array.Empty<ModImcOverride>();
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

        if (defaultFiles.Count > 0 || eqdpOverrides.Count > 0 || imcOverrides.Count > 0)
        {
            j.WriteStartObject("DefaultData");
            if (defaultFiles.Count > 0)
                WriteFiles(j, defaultFiles);
            if (eqdpOverrides.Count > 0 || imcOverrides.Count > 0)
                WriteManipulations(j, eqdpOverrides, imcOverrides);
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
    /// Bit offset of a slot's 2-bit (Material, Model) pair within an Eqdp entry.
    /// Equipment and accessory slots each have their own file, so the two small
    /// offset ranges (0/2/4/6/8) don't collide. Mirrors Penumbra's Eqdp.Offset.
    /// </summary>
    private static int EqdpOffset(EquipSlot slot) => slot switch
    {
        EquipSlot.Head      => 0,
        EquipSlot.Body      => 2,
        EquipSlot.Hands     => 4,
        EquipSlot.Legs      => 6,
        EquipSlot.Feet      => 8,
        EquipSlot.Earring   => 0,
        EquipSlot.Neck      => 2,
        EquipSlot.Wrists    => 4,
        EquipSlot.RingRight => 6,
        EquipSlot.RingLeft  => 8,
        _                   => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private static void WriteManipulations(Utf8JsonWriter j, IReadOnlyList<ModEqdpOverride> eqdpOverrides,
        IReadOnlyList<ModImcOverride> imcOverrides)
    {
        j.WriteStartArray("Manipulations");

        foreach (var o in eqdpOverrides)
        {
            // Grant this race/gender its own Model for the slot, without claiming a
            // unique Material — texture lookups keep falling back to the shared
            // base-race material this tool always writes (see MaterialNaming).
            int entry = 1 << (EqdpOffset(o.Slot) + 1);

            j.WriteStartObject();
            j.WriteString("Type", "Eqdp");
            j.WriteStartObject("Manipulation");
            j.WriteNumber("Entry", entry);
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
