using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using XIVPortStudio.Models;

namespace XIVPortStudio;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Last-selected equipment slot (index into <see cref="SlotInfo.AllSlots"/>).</summary>
    public int LastSlotIndex { get; set; } = 1;

    /// <summary>Last-used item search text.</summary>
    public string LastItemSearch { get; set; } = string.Empty;

    /// <summary>
    /// Key of the last-selected port subject: an Item-sheet row id for gear, or an encoded
    /// character feature key (top bit set, see <c>FeatureSubject</c>).
    /// </summary>
    public uint LastItemRowId { get; set; }

    /// <summary>Kind shown in the browser (index into <c>SubjectKind</c>).</summary>
    public int LastBrowserKind { get; set; }

    /// <summary>Race code ("0101", …) last picked in the character-feature browser.</summary>
    public string LastFeatureRace { get; set; } = "0101";

    /// <summary>Material set-ups keyed by item row ID.</summary>
    public Dictionary<uint, List<MaterialSetup>> MaterialsByItem { get; set; } = new();

    /// <summary>Per-race model set-ups keyed by item row ID.</summary>
    public Dictionary<uint, List<RaceModelEntry>> ModelsByItem { get; set; } = new();

    /// <summary>Dummy-model material slot assignments keyed by item row ID.</summary>
    public Dictionary<uint, List<ModelMaterialSlot>> ModelMaterialSlotsByItem { get; set; } = new();

    /// <summary>User-edited mod name keyed by item row ID. Falls back to an auto-generated name when absent/empty.</summary>
    public Dictionary<uint, string> ModNameByItem { get; set; } = new();

    /// <summary>
    /// Penumbra mod identifier per item, so rebuilding an item's mod keeps the same
    /// identity (and group/option ids) instead of minting new ones every time.
    /// </summary>
    public Dictionary<uint, Guid> ModIdByItem { get; set; } = new();

    // ── Mod metadata written into meta.json ──────────────────────────────────

    public string ModAuthor { get; set; } = "XIV Port Studio";
    public string ModVersion { get; set; } = "1.0";
    public string ModWebsite { get; set; } = string.Empty;

    /// <summary>Description per item; empty falls back to "Port of {mod name}".</summary>
    public Dictionary<uint, string> ModDescriptionByItem { get; set; } = new();

    // ── Main window layout ───────────────────────────────────────────────────

    /// <summary>Width of the item browser pane, at 100% UI scale.</summary>
    public float BrowserWidth { get; set; } = 300;

    /// <summary>Width of the inspector pane, at 100% UI scale.</summary>
    public float InspectorWidth { get; set; } = 460;

    /// <summary>Stage tab last shown (index into the stage list).</summary>
    public int LastStage { get; set; }

    // ── Defaults for new content ─────────────────────────────────────────────

    /// <summary>Whether newly added texture slots start with BC7 compression on.</summary>
    public bool DefaultCompressBc7 { get; set; }

    /// <summary>Whether texture thumbnails are shown in the material inspector.</summary>
    public bool ShowThumbnails { get; set; } = true;

    /// <summary>Last .package file opened in the Sims 4 importer.</summary>
    public string LastSimsPackagePath { get; set; } = string.Empty;

    /// <summary>Last folder the Sims 4 importer extracted into.</summary>
    public string LastSimsOutputDir { get; set; } = string.Empty;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
