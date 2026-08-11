using System;
using System.Collections.Generic;

namespace XIVPortStudio.Models;

/// <summary>
/// A user-defined material for a ported item: which shader it uses, and the
/// list of textures that belong to it.
/// </summary>
[Serializable]
public class MaterialSetup
{
    /// <summary>Display name of the material.</summary>
    public string Name { get; set; } = "Material";

    /// <summary>Shader / material type used by this material.</summary>
    public ShaderType ShaderType { get; set; } = ShaderType.Default;

    /// <summary>
    /// Material-variant index (1-based, matching the game's v000N material
    /// folder convention). Multiple materials may share the same variant to
    /// represent different texture sets for one material slot.
    /// </summary>
    public int MaterialVariant { get; set; } = 1;

    /// <summary>Textures assigned to this material, in order.</summary>
    public List<TextureSlot> Textures { get; set; } = new();

    public MaterialSetup Clone()
    {
        var copy = new MaterialSetup
        {
            Name            = Name,
            ShaderType      = ShaderType,
            MaterialVariant = MaterialVariant,
        };
        foreach (var t in Textures)
            copy.Textures.Add(t.Clone());
        return copy;
    }
}

/// <summary>One texture assignment inside a material.</summary>
[Serializable]
public class TextureSlot
{
    /// <summary>What the texture is used for (diffuse, normal, …).</summary>
    public TextureType Type { get; set; } = TextureType.Diffuse;

    /// <summary>
    /// Game path (e.g. "chara/equipment/e0164/material/v0001/mt_c0101b0001_d.tex")
    /// or a path to a texture file the user wants to import.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    public TextureSlot Clone() => new() { Type = Type, Path = Path };
}
