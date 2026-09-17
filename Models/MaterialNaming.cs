using System;

namespace XIVPortStudio.Models;

/// <summary>
/// Material and texture file naming following the TexTools convention (see
/// xivModdingFramework's <c>Mtrl.GetMtrlNameByRootRaceSlotSuffix</c>):
/// <c>mt_{primaryType}{id}_{slot}</c>, e.g. <c>mt_c0101e0164_top</c> for a
/// body-slot item with model 0164.  Gear materials are race-shared, so the
/// canonical base race code <c>c0101</c> is always used.
/// </summary>
public static class MaterialNaming
{
    /// <summary>Base race code used by gear material names (all races share one set).</summary>
    private const string BaseRaceCode = "0101";

    /// <summary>
    /// Default material name for an item, e.g. <c>mt_c0101e0164_top_a</c>.
    /// <paramref name="variant"/> is the 1-based material slot, mapped to the
    /// game's letter-suffix convention (1 → <c>_a</c>, 2 → <c>_b</c>, …) — real
    /// game materials always carry this suffix, even the first one.
    /// </summary>
    public static string DefaultName(EquipSlot slot, ushort modelId, int variant = 1)
    {
        var itemPrefix = SlotInfo.ItemPrefix(slot);   // "e" for equipment, "a" for accessories
        var slotKey    = SlotInfo.KeyMap[slot];       // e.g. "top"
        var suffix     = (char)('a' + Math.Max(variant, 1) - 1);
        return $"mt_c{BaseRaceCode}{itemPrefix}{modelId:D4}_{slotKey}_{suffix}";
    }

    /// <summary>Default postfix for a texture role (game convention: d/n/s/m/id/r), before the user edits it.</summary>
    public static string DefaultPostfix(TextureType type)
        => type switch
        {
            TextureType.Diffuse    => "d",
            TextureType.Normal     => "n",
            TextureType.Specular   => "s",
            TextureType.Mask       => "m",
            TextureType.Index      => "id",
            TextureType.Reflection => "r",
            _                      => "o",
        };

    /// <summary>
    /// Combines a material name with a texture's user-editable postfix, e.g.
    /// material "mt_c0201e0025_top_b" + postfix "normal" → "mt_c0201e0025_top_b_normal".
    /// </summary>
    public static string ComposeTextureName(string materialName, string postfix)
        => $"{SanitizeFileName(materialName)}_{SanitizeFileName(postfix)}";

    /// <summary>
    /// Item folder, e.g. "chara/equipment/e0164" or "chara/accessory/a0101".
    /// </summary>
    public static string ItemFolder(EquipSlot slot, ushort modelId)
        => $"chara/{(SlotInfo.IsAccessory(slot) ? "accessory" : "equipment")}/{SlotInfo.ItemPrefix(slot)}{modelId:D4}";

    /// <summary>Material folder, e.g. "chara/equipment/e0164/material/v0001". Materials live here — textures do not.</summary>
    public static string MaterialFolder(EquipSlot slot, ushort modelId, int materialVariant)
        => $"{ItemFolder(slot, modelId)}/material/v{materialVariant:D4}";

    /// <summary>
    /// Full game path of a material, e.g. "chara/equipment/e0164/material/v0001/mt_c0101e0164_top_a.mtrl".
    /// </summary>
    public static string MaterialGamePath(EquipSlot slot, ushort modelId, int materialVariant, string materialName)
        => $"{MaterialFolder(slot, modelId, materialVariant)}/{SanitizeFileName(materialName)}.mtrl";

    /// <summary>
    /// Texture folder, e.g. "chara/equipment/e0164/texture". Flat per item — not nested under a
    /// material variant, since multiple material variants commonly share the same textures.
    /// </summary>
    public static string TextureFolder(EquipSlot slot, ushort modelId)
        => $"{ItemFolder(slot, modelId)}/texture";

    /// <summary>
    /// Full game path of a texture, e.g. "chara/equipment/e0164/texture/mt_c0101e0164_top_a_d.tex".
    /// </summary>
    public static string TextureGamePath(EquipSlot slot, ushort modelId, string textureName)
        => $"{TextureFolder(slot, modelId)}/{SanitizeFileName(textureName)}.tex";

    /// <summary>
    /// Lower-cases a file name, strips an extension if present, and removes
    /// characters that are invalid in game paths.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "texture";
        name = name.Trim().ToLowerInvariant();

        var ext = System.IO.Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext)) name = name[..^ext.Length];

        var chars = new char[name.Length];
        int len = 0;
        foreach (var c in name)
        {
            bool ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' || c is '_' or '-';
            if (ok) chars[len++] = c;
        }
        return len == 0 ? "texture" : new string(chars, 0, len);
    }
}
