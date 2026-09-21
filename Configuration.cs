using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Penumbra metadata overrides (EQP, EST) keyed by item row ID.</summary>
    public Dictionary<uint, ItemMeta> MetaByItem { get; set; } = new();

    /// <summary>Subject keys included in the modpack currently being built, in display/build order.</summary>
    public List<uint> ModpackItems { get; set; } = new();

    // ── Mod metadata written into meta.json (pack-level: one build makes one mod) ────

    /// <summary>User-edited mod name for the whole pack. Falls back to an auto-generated name when empty.</summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>Description of the whole pack; empty falls back to "Port of {mod name}".</summary>
    public string ModDescription { get; set; } = string.Empty;

    /// <summary>
    /// Penumbra mod identifier for the pack, so rebuilding keeps the same identity
    /// (and group/option ids) instead of minting new ones every time.
    /// </summary>
    public Guid? ModId { get; set; }

    public string ModAuthor { get; set; } = "XIV Port Studio";
    public string ModVersion { get; set; } = "1.0";
    public string ModWebsite { get; set; } = string.Empty;

    // ── Main window layout ───────────────────────────────────────────────────

    /// <summary>Width of the item browser pane, at 100% UI scale.</summary>
    public float BrowserWidth { get; set; } = 300;

    /// <summary>Width of the inspector pane, at 100% UI scale.</summary>
    public float InspectorWidth { get; set; } = 460;

    /// <summary>Stage tab last shown, from before Models and Materials were merged (0–3). Only read when <see cref="LastTab"/> is unset.</summary>
    public int LastStage { get; set; }

    /// <summary>Stage tab last shown before Browse became a tab of its own; only read when <see cref="LastTabV2"/> is unset.</summary>
    public int LastTab { get; set; } = -1;

    /// <summary>Stage tab last shown (Browse, Details, Mod Info, Build); -1 until first saved.</summary>
    public int LastTabV2 { get; set; } = -1;

    /// <summary>Height of the metadata section under the Details tree, at 100% UI scale.</summary>
    public float MetaPaneHeight { get; set; } = 220;

    // ── Defaults for new content ─────────────────────────────────────────────

    /// <summary>Whether newly added texture slots start with BC7 compression on. Read only until <see cref="DefaultCompression"/> is set.</summary>
    public bool DefaultCompressBc7 { get; set; }

    /// <summary>What a newly added texture slot is compressed to, or null while it still follows <see cref="DefaultCompressBc7"/>.</summary>
    public TextureCompression? DefaultCompressionMode { get; set; }

    /// <summary>The compression new texture slots start on.</summary>
    public TextureCompression DefaultCompression
    {
        get => DefaultCompressionMode ?? (DefaultCompressBc7 ? TextureCompression.Bc7 : TextureCompression.None);
        set
        {
            DefaultCompressionMode = value;
            DefaultCompressBc7     = value == TextureCompression.Bc7;
        }
    }

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

    /// <summary>Whether an item has any saved models or materials, regardless of modpack membership.</summary>
    public bool HasWork(uint key)
        => (MaterialsByItem.TryGetValue(key, out var m) && m.Count > 0)
        || (ModelsByItem.TryGetValue(key, out var r) && r.Any(x => !string.IsNullOrWhiteSpace(x.SourcePath)));
}
