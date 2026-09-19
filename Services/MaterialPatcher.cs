using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

/// <summary>
/// Builds a mod's .mtrl by cloning a template (a bundled shader preset, or — failing
/// that — a vanilla material) and swapping in the game paths of whichever textures the
/// user configured, matched to the template's own texture slots by the "_d"/"_n"/"_s"/
/// "_m"/"_id"/"_r" suffix on their path — the same convention this tool's own default
/// postfixes follow. Slots the template never had (e.g. a reflection map on a shader
/// that never used one) are left alone: there's no shader/sampler wiring to safely
/// fabricate for them, so that texture is written but not referenced by the material.
///
/// Everything else (shader package, shader keys, constants, samplers, color set) is
/// copied byte-for-byte from the template, since none of it is understood or touched here.
/// </summary>
public static class MaterialPatcher
{
    public static bool TryBuildPatchedMaterial(
        MtrlInfo template,
        IReadOnlyDictionary<TextureType, string> replacements,
        out byte[] result,
        out List<TextureType> appliedTypes,
        out string? error)
    {
        result = Array.Empty<byte>();
        appliedTypes = new List<TextureType>();
        error = null;

        try
        {
            // ── Resolve every existing texture slot, applying replacements where the type matches. ──
            var texturePaths = new string[template.TextureOffsets.Length];
            for (int i = 0; i < texturePaths.Length; i++)
            {
                var original = template.TextureOffsets[i].Path;
                var type = ClassifyBySuffix(original);
                if (type.HasValue && replacements.TryGetValue(type.Value, out var newPath))
                {
                    texturePaths[i] = newPath;
                    appliedTypes.Add(type.Value);
                }
                else
                {
                    texturePaths[i] = original;
                }
            }

            // ── Rebuild the shared strings blob and every offset into it. ──
            var stringsBlob = new List<byte>();

            var textureOffsets = new uint[texturePaths.Length];
            for (int i = 0; i < texturePaths.Length; i++)
            {
                uint offset = (uint)stringsBlob.Count;
                AppendCString(stringsBlob, texturePaths[i]);
                textureOffsets[i] = offset | ((uint)template.TextureOffsets[i].Flags << 16);
            }

            var uvColorSets = new (ushort NameOffset, byte Index, byte Unknown1)[template.UvColorSets.Length];
            for (int i = 0; i < uvColorSets.Length; i++)
            {
                var offset = (ushort)stringsBlob.Count;
                AppendCString(stringsBlob, template.UvColorSets[i].Name);
                uvColorSets[i] = (offset, template.UvColorSets[i].Index, template.UvColorSets[i].Unknown1);
            }

            var colorSets = new (ushort NameOffset, byte Index, byte Unknown1)[template.ColorSets.Length];
            for (int i = 0; i < colorSets.Length; i++)
            {
                var offset = (ushort)stringsBlob.Count;
                AppendCString(stringsBlob, template.ColorSets[i].Name);
                colorSets[i] = (offset, template.ColorSets[i].Index, template.ColorSets[i].Unknown1);
            }

            var shaderPackageOffset = (ushort)stringsBlob.Count;
            AppendCString(stringsBlob, template.ShaderPackageName);

            while (stringsBlob.Count % 4 != 0)
                stringsBlob.Add(0);

            // ── Assemble the new file. Everything after the strings block (additional data,
            //    the color set, and the shader key/constant/sampler/value lists) is copied
            //    verbatim from the template — never inspected, only ever passed through. ──
            var output = new List<byte>(16 + textureOffsets.Length * 4 + uvColorSets.Length * 4
                                        + colorSets.Length * 4 + stringsBlob.Count + template.Tail.Length);

            uint packedFileSize = template.FileSize | ((uint)template.DataSetSize << 16);
            WriteU32(output, template.Version);
            WriteU32(output, packedFileSize);
            WriteU16(output, (ushort)stringsBlob.Count);
            WriteU16(output, shaderPackageOffset);
            output.Add(template.TextureCount);
            output.Add(template.UvSetCount);
            output.Add(template.ColorSetCount);
            output.Add(template.AdditionalDataSize);

            foreach (var o in textureOffsets) WriteU32(output, o);
            foreach (var (nameOffset, index, unknown1) in uvColorSets)
            {
                WriteU16(output, nameOffset);
                output.Add(index);
                output.Add(unknown1);
            }
            foreach (var (nameOffset, index, unknown1) in colorSets)
            {
                WriteU16(output, nameOffset);
                output.Add(index);
                output.Add(unknown1);
            }

            output.AddRange(stringsBlob);
            output.AddRange(template.Tail);

            result = output.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Maps a template texture's path suffix to a <see cref="TextureType"/>. Recognizes both
    /// this tool's short convention ("_d"/"_n"/"_s"/"_m"/"_id"/"_r") and the longer spellings
    /// TexTools' own bundled material presets use ("_base"/"_norm"/"_mask", etc — confirmed by
    /// inspecting them directly; they're not consistent about which form they use).
    /// </summary>
    internal static TextureType? ClassifyBySuffix(string texturePath)
    {
        var stem = Path.GetFileNameWithoutExtension(texturePath);
        int idx = stem.LastIndexOf('_');
        if (idx < 0 || idx == stem.Length - 1) return null;

        return stem[(idx + 1)..] switch
        {
            "d" or "base" or "diffuse"    => TextureType.Diffuse,
            "n" or "norm" or "normal"     => TextureType.Normal,
            "s" or "spec" or "specular"   => TextureType.Specular,
            "m" or "mask"                 => TextureType.Mask,
            "id"                          => TextureType.Index,
            "r" or "refl" or "reflection" => TextureType.Reflection,
            _                             => null,
        };
    }

    private static void AppendCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.UTF8.GetBytes(s ?? string.Empty));
        b.Add(0);
    }

    private static void WriteU16(List<byte> b, ushort v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
}
