using System.Collections.Generic;
using System.Linq;
using XIVPortStudio.Services;

namespace XIVPortStudio.Models;

/// <summary>What a port replaces.</summary>
public enum SubjectKind { Gear, Hair, Face, Tail, Ear }

/// <summary>
/// The thing a port replaces — a piece of gear, or a character feature — and the answer to
/// every "where does this file go?" question for it. Everything that builds paths asks the
/// subject, so the session, build and validation do not care which kind it is.
///
/// Saved set-ups are keyed by <see cref="Key"/>. Gear keys are the item's row id (as they
/// always were); feature keys set the top bit, see <see cref="FeatureSubject"/>.
/// </summary>
internal abstract class PortSubject
{
    public abstract uint        Key         { get; }
    public abstract SubjectKind Kind        { get; }
    public abstract string      DisplayName { get; }
    public abstract string      IdDisplay   { get; }

    /// <summary>Whether models can be added for several races (gear, hair).</summary>
    public abstract bool MultiRace { get; }

    /// <summary>The race a feature port was opened for; null for gear.</summary>
    public abstract RaceGender? BaseRace { get; }

    public abstract string ModelGamePath(RaceGender rg);

    /// <summary>Game path of a material. <paramref name="race"/> is ignored where materials are race-shared (gear).</summary>
    public abstract string MaterialGamePath(string materialName, RaceGender? race);

    public abstract string TextureGamePath(string textureName);

    public abstract string MaterialFolder(RaceGender? race);
    public abstract string TextureFolder { get; }

    public abstract string DefaultMaterialName(int letter);

    /// <summary>
    /// The races a copy of each material is written for. Gear yields a single null (one
    /// race-shared set); hair yields every configured race; other features their one race.
    /// </summary>
    public abstract IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models);

    /// <summary>The material's name in <paramref name="race"/>'s copy (hair rewrites the base race code).</summary>
    public virtual string MaterialNameFor(string name, RaceGender? race) => name;

    /// <summary>Where the vanilla game keeps the material a vanilla model references by this name.</summary>
    public abstract string VanillaMaterialPath(string referencedName, RaceGender race);

    public abstract bool UsesEqdp { get; }
    public abstract bool UsesImc  { get; }

    public string KindLabel => Kind == SubjectKind.Gear ? "Item" : FeatureNaming.KindLabel(Kind);

    /// <summary>Recreates a subject from a saved key, or null if it no longer resolves.</summary>
    public static PortSubject? FromKey(uint key, GameDataService gameData)
    {
        if (key == 0)
            return null;
        if (FeatureSubject.TryDecode(key, out var feature))
            return feature;
        var item = gameData.GetItemByRowId(key);
        return item != null ? new GearSubject(item) : null;
    }
}

/// <summary>A piece of gear: everything delegates to the existing equipment path helpers.</summary>
internal sealed class GearSubject : PortSubject
{
    public GearSubject(GameDataService.GameItem item) => Item = item;

    public GameDataService.GameItem Item { get; }

    public override uint        Key         => Item.RowId;
    public override SubjectKind Kind        => SubjectKind.Gear;
    public override string      DisplayName => Item.Name;
    public override string      IdDisplay   => Item.ModelIdDisplay;
    public override bool        MultiRace   => true;
    public override RaceGender? BaseRace    => null;
    public override bool        UsesEqdp    => true;
    public override bool        UsesImc     => true;

    public override string ModelGamePath(RaceGender rg)
        => ModelNaming.ModelGamePath(Item.Slot, Item.ModelId, rg.RaceCode);

    public override string MaterialGamePath(string materialName, RaceGender? race)
        => MaterialNaming.MaterialGamePath(Item.Slot, Item.ModelId, materialName);

    public override string TextureGamePath(string textureName)
        => MaterialNaming.TextureGamePath(Item.Slot, Item.ModelId, textureName);

    public override string MaterialFolder(RaceGender? race) => MaterialNaming.MaterialFolder(Item.Slot, Item.ModelId);
    public override string TextureFolder => MaterialNaming.TextureFolder(Item.Slot, Item.ModelId);

    public override string DefaultMaterialName(int letter) => MaterialNaming.DefaultName(Item.Slot, Item.ModelId, letter);

    private static readonly IReadOnlyList<RaceGender?> Shared = new RaceGender?[] { null };
    public override IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models) => Shared;

    public override string VanillaMaterialPath(string referencedName, RaceGender race)
        => MaterialNaming.MaterialGamePath(Item.Slot, Item.ModelId, referencedName.TrimStart('/'));
}

/// <summary>
/// A character feature of one race: hair, face, tail or ears. Its files live in that race's
/// folder. Hair can additionally be ported to more races; each gets its own copy of the
/// materials, named for that race.
///
/// Key layout: <c>0x8000_0000 | kind &lt;&lt; 28 | raceCode &lt;&lt; 16 | id</c> — the top bit keeps
/// it clear of item row ids, so gear set-ups saved before features existed are untouched.
/// </summary>
internal sealed class FeatureSubject : PortSubject
{
    private const uint FeatureBit = 0x8000_0000;

    public FeatureSubject(SubjectKind kind, RaceGender race, ushort id)
    {
        Kind = kind;
        Race = race;
        Id   = id;
    }

    public override SubjectKind Kind { get; }
    public RaceGender Race { get; }
    public ushort     Id   { get; }

    public override uint Key => FeatureBit | ((uint)Kind << 28) | ((uint)int.Parse(Race.RaceCode) << 16) | Id;

    public override string DisplayName => $"{FeatureNaming.KindLabel(Kind)} {Id} · {Race.DisplayName}";
    public override string IdDisplay   => $"{FeatureNaming.Prefix(Kind)}{Id:D4}";
    public override bool   MultiRace   => Kind == SubjectKind.Hair;
    public override RaceGender? BaseRace => Race;
    public override bool   UsesEqdp    => false;
    public override bool   UsesImc     => false;

    public override string ModelGamePath(RaceGender rg) => FeatureNaming.ModelGamePath(Kind, rg.RaceCode, Id);

    /// <summary>
    /// A feature material lives where its name says: "mt_c1501h0005_hir_a" goes to Hrothgar's
    /// hair 5 folder whichever race is asking, since that is how the game resolves it. Names
    /// that carry no race/id go to <paramref name="race"/>'s own folder.
    /// </summary>
    public override string MaterialGamePath(string materialName, RaceGender? race)
        => FeatureNaming.ResolveMaterialPath(Kind, materialName, (race ?? Race).RaceCode, Id);

    public override string TextureGamePath(string textureName)
        => $"{TextureFolder}/{MaterialNaming.SanitizeFileName(textureName)}.tex";

    public override string MaterialFolder(RaceGender? race) => FeatureNaming.MaterialFolder(Kind, (race ?? Race).RaceCode, Id);

    /// <summary>Textures are written once, under the base race, and every race's material points at them.</summary>
    public override string TextureFolder => FeatureNaming.TextureFolder(Kind, Race.RaceCode, Id);

    public override string DefaultMaterialName(int letter) => FeatureNaming.DefaultMaterialName(Kind, Race.RaceCode, Id, letter);

    public override IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models)
    {
        if (!MultiRace || models.Count == 0)
            return new RaceGender?[] { Race };
        return models.Select(m => (RaceGender?)m.RaceGender).Distinct().ToList();
    }

    public override string MaterialNameFor(string name, RaceGender? race)
    {
        if (race == null || race.Value == Race)
            return name;
        var from = $"c{Race.RaceCode}";
        var to   = $"c{race.Value.RaceCode}";
        int at = name.IndexOf(from, System.StringComparison.OrdinalIgnoreCase);
        return at < 0 ? name : name[..at] + to + name[(at + from.Length)..];
    }

    public override string VanillaMaterialPath(string referencedName, RaceGender race)
        => FeatureNaming.ResolveMaterialPath(Kind, referencedName, race.RaceCode, Id);

    public static bool TryDecode(uint key, out FeatureSubject? subject)
    {
        subject = null;
        if ((key & FeatureBit) == 0)
            return false;

        var kind = (SubjectKind)((key >> 28) & 0x7);
        var code = ((key >> 16) & 0xFFF).ToString("D4");
        var id   = (ushort)(key & 0xFFFF);
        if (kind is SubjectKind.Gear or > SubjectKind.Ear)
            return true;   // a feature key we cannot read: treat as "nothing", not as an item

        foreach (var rg in RaceInfo.AllRaces)
        {
            if (rg.RaceCode == code)
            {
                subject = new FeatureSubject(kind, rg, id);
                return true;
            }
        }
        return true;
    }
}
