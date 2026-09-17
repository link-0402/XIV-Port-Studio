using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lumina.Data.Files;
using XIVPortStudio.Models;

namespace XIVPortStudio.Services;

/// <summary>
/// Builds a mod's .mtrl by cloning a vanilla material and swapping in the game
/// paths of whichever textures the user configured, matched to vanilla's own
/// texture slots by the "_d"/"_n"/"_s"/"_m"/"_id"/"_r" suffix on their path —
/// the same convention this tool's own default postfixes follow. Slots the
/// vanilla material never had (e.g. a reflection map on an item that never used
/// one) are left alone: there's no shader/sampler wiring to safely fabricate for
/// them, so that texture is written but not referenced by the material.
///
/// Everything else (shader package, shader keys, constants, samplers, color
/// set) is copied byte-for-byte from vanilla, since none of it is understood or
/// touched here.
/// </summary>
public static class MaterialPatcher
{
    private const int HeaderSize = 16;

    public static bool TryBuildPatchedMaterial(
        byte[] vanillaRawBytes,
        MtrlFile vanilla,
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
            var texturePaths = new string[vanilla.TextureOffsets.Length];
            for (int i = 0; i < texturePaths.Length; i++)
            {
                var original = ReadCString(vanilla.Strings, vanilla.TextureOffsets[i].Offset);
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

            var uvNames = vanilla.UvColorSets.Select(u => ReadCString(vanilla.Strings, u.NameOffset)).ToArray();
            var colorNames = vanilla.ColorSets.Select(c => ReadCString(vanilla.Strings, c.NameOffset)).ToArray();
            var shaderPackageName = ReadCString(vanilla.Strings, vanilla.FileHeader.ShaderPackageNameOffset);

            // ── Rebuild the shared strings blob and every offset into it. ──
            var stringsBlob = new List<byte>();

            var textureOffsets = new uint[texturePaths.Length];
            for (int i = 0; i < texturePaths.Length; i++)
            {
                uint offset = (uint)stringsBlob.Count;
                AppendCString(stringsBlob, texturePaths[i]);
                textureOffsets[i] = offset | ((uint)vanilla.TextureOffsets[i].Flags << 16);
            }

            var uvColorSets = new (ushort NameOffset, byte Index, byte Unknown1)[uvNames.Length];
            for (int i = 0; i < uvNames.Length; i++)
            {
                var offset = (ushort)stringsBlob.Count;
                AppendCString(stringsBlob, uvNames[i]);
                uvColorSets[i] = (offset, vanilla.UvColorSets[i].Index, vanilla.UvColorSets[i].Unknown1);
            }

            var colorSets = new (ushort NameOffset, byte Index, byte Unknown1)[colorNames.Length];
            for (int i = 0; i < colorNames.Length; i++)
            {
                var offset = (ushort)stringsBlob.Count;
                AppendCString(stringsBlob, colorNames[i]);
                colorSets[i] = (offset, vanilla.ColorSets[i].Index, vanilla.ColorSets[i].Unknown1);
            }

            var shaderPackageOffset = (ushort)stringsBlob.Count;
            AppendCString(stringsBlob, shaderPackageName);

            while (stringsBlob.Count % 4 != 0)
                stringsBlob.Add(0);

            // ── Everything after the original strings block — additional data, the color
            //    set, and the shader key/constant/sampler/value lists — is untouched, so it's
            //    copied verbatim rather than re-derived. ──
            int tailStart = HeaderSize
                + vanilla.FileHeader.TextureCount * 4
                + vanilla.FileHeader.UvSetCount * 4
                + vanilla.FileHeader.ColorSetCount * 4
                + vanilla.FileHeader.StringTableSize;
            if (tailStart > vanillaRawBytes.Length)
            {
                error = "Vanilla material is smaller than its own declared header — cannot clone it.";
                return false;
            }
            var tail = vanillaRawBytes.AsSpan(tailStart);

            // ── Assemble the new file. ──
            var output = new List<byte>(HeaderSize + textureOffsets.Length * 4 + uvColorSets.Length * 4
                                        + colorSets.Length * 4 + stringsBlob.Count + tail.Length);

            uint packedFileSize = vanilla.FileHeader.FileSize | ((uint)vanilla.FileHeader.DataSetSize << 16);
            WriteU32(output, vanilla.FileHeader.Version);
            WriteU32(output, packedFileSize);
            WriteU16(output, (ushort)stringsBlob.Count);
            WriteU16(output, shaderPackageOffset);
            output.Add(vanilla.FileHeader.TextureCount);
            output.Add(vanilla.FileHeader.UvSetCount);
            output.Add(vanilla.FileHeader.ColorSetCount);
            output.Add(vanilla.FileHeader.AdditionalDataSize);

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
            output.AddRange(tail.ToArray());

            result = output.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Maps a vanilla texture's path suffix ("_d", "_n", "_s", "_m", "_id", "_r") to a <see cref="TextureType"/>.</summary>
    private static TextureType? ClassifyBySuffix(string texturePath)
    {
        var stem = Path.GetFileNameWithoutExtension(texturePath);
        int idx = stem.LastIndexOf('_');
        if (idx < 0 || idx == stem.Length - 1) return null;

        return stem[(idx + 1)..] switch
        {
            "d"  => TextureType.Diffuse,
            "n"  => TextureType.Normal,
            "s"  => TextureType.Specular,
            "m"  => TextureType.Mask,
            "id" => TextureType.Index,
            "r"  => TextureType.Reflection,
            _    => null,
        };
    }

    private static string ReadCString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = (int)offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }

    private static void AppendCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.UTF8.GetBytes(s ?? string.Empty));
        b.Add(0);
    }

    private static void WriteU16(List<byte> b, ushort v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
}
