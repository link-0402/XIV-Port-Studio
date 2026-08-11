using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVPortStudio.Models;

/// <summary>
/// The shader / material types available for FFXIV materials.
/// Each maps to the shader pack path used by the game's .mtrl files.
/// </summary>
public enum ShaderType
{
    Default,
    Skin,
    Hair,
    Cloth,
    Leather,
    Metal,
    Glass,
    Gemstone,
    Glow,
    Accessory,
    Body,
    Water,
}

/// <summary>One shader definition: display name plus the game shader pack path.</summary>
public sealed record ShaderDef(ShaderType Type, string DisplayName, string ShaderPackPath);

/// <summary>Static metadata about the supported shader types.</summary>
public static class ShaderInfo
{
    public static readonly IReadOnlyList<ShaderDef> All = new List<ShaderDef>
    {
        new(ShaderType.Default,    "Default",                 "chara/shader/mtrl/ffxiv_character_default.mtrl"),
        new(ShaderType.Skin,       "Skin",                    "chara/shader/mtrl/ffxiv_character_skin.mtrl"),
        new(ShaderType.Hair,       "Hair",                    "chara/shader/mtrl/ffxiv_character_hair.mtrl"),
        new(ShaderType.Cloth,      "Cloth",                   "chara/shader/mtrl/ffxiv_character_cloth.mtrl"),
        new(ShaderType.Leather,    "Leather",                 "chara/shader/mtrl/ffxiv_character_leather.mtrl"),
        new(ShaderType.Metal,      "Metal",                   "chara/shader/mtrl/ffxiv_character_metal.mtrl"),
        new(ShaderType.Glass,      "Glass",                   "chara/shader/mtrl/ffxiv_character_glass.mtrl"),
        new(ShaderType.Gemstone,   "Gemstone",                "chara/shader/mtrl/ffxiv_character_gem.mtrl"),
        new(ShaderType.Glow,       "Glow / Emissive",         "chara/shader/mtrl/ffxiv_character_glow.mtrl"),
        new(ShaderType.Accessory,  "Accessory",               "chara/shader/mtrl/ffxiv_accessory.mtrl"),
        new(ShaderType.Body,       "Body",                    "chara/shader/mtrl/ffxiv_character_body.mtrl"),
        new(ShaderType.Water,      "Water",                   "water/water_default.mtrl"),
    };

    /// <summary>Display names in enum order, for ImGui combo boxes.</summary>
    public static readonly string[] Labels = All.Select(d => d.DisplayName).ToArray();

    public static ShaderDef Get(ShaderType type)
    {
        foreach (var def in All)
            if (def.Type == type)
                return def;
        return All[0];
    }

    public static string DisplayName(ShaderType type) => Get(type).DisplayName;

    public static string ShaderPackPath(ShaderType type) => Get(type).ShaderPackPath;
}

/// <summary>The role a texture plays inside a material.</summary>
public enum TextureType
{
    Diffuse,
    Normal,
    Specular,
    Emissive,
    Mask,
    Detail1,
    Detail2,
}

public static class TextureTypeInfo
{
    public static readonly string[] Labels =
    {
        "Diffuse (Color)",
        "Normal",
        "Specular / Multiply",
        "Emissive / Glow",
        "Mask",
        "Detail 1",
        "Detail 2",
    };

    public static string DisplayName(TextureType type) => Labels[(int)type];

    public static TextureType FromIndex(int index)
        => index >= 0 && index < Labels.Length ? (TextureType)index : TextureType.Diffuse;
}
