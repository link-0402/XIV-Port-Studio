using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVPortStudio.Models;

/// <summary>
/// A pre-made material type: a shader pack plus the texture slots that are
/// pre-configured when the preset is applied, mirroring the texture sets the
/// game uses for each shader pack.
/// </summary>
public sealed record MaterialPreset(string Name, ShaderType Shader, IReadOnlyList<TextureType> Textures);

/// <summary>Static list of pre-made material presets.</summary>
public static class MaterialPresets
{
    public static readonly IReadOnlyList<MaterialPreset> All = new List<MaterialPreset>
    {
        new("Gear",               ShaderType.Character,             new[] { TextureType.Normal, TextureType.Mask, TextureType.Index }),
        new("Gear with Diffuse",        ShaderType.Character,             new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Mask, TextureType.Index }),
        new("Gear Legacy",             ShaderType.CharacterLegacy,       new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Mask, TextureType.Index }),
        new("Scroll",             ShaderType.CharacterScroll,       new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Mask, TextureType.Index }),
        new("Skin",               ShaderType.Skin,                  new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Mask }),
        new("Hair",               ShaderType.Hair,                  new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Mask, TextureType.Index }),
        new("Iris",               ShaderType.Iris,                  new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular }),
    };

    /// <summary>Preset names in list order, for ImGui combo boxes.</summary>
    public static readonly string[] Names = All.Select(p => p.Name).ToArray();
}
