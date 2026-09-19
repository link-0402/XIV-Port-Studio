using System;
using System.IO;
using System.Text;

namespace XIVPortStudio.Services;

/// <summary>One texture slot in an <see cref="MtrlInfo"/>: a resolved game path plus its raw flags bits.</summary>
public readonly struct MtrlTextureOffset
{
    public readonly string Path;
    public readonly ushort Flags;
    public MtrlTextureOffset(string path, ushort flags) { Path = path; Flags = flags; }
}

/// <summary>One UV/color set entry: a resolved name plus its raw index/unknown bytes.</summary>
public readonly struct MtrlNamedSet
{
    public readonly string Name;
    public readonly byte Index;
    public readonly byte Unknown1;
    public MtrlNamedSet(string name, byte index, byte unknown1) { Name = name; Index = index; Unknown1 = unknown1; }
}

/// <summary>
/// Plain, source-agnostic .mtrl contents: every field <see cref="MaterialPatcher"/> needs to
/// clone-and-rewire a material, plus the raw "tail" (everything after the strings block —
/// additional data, the color set, and the shader key/constant/sampler/value lists) which is
/// never inspected, only ever copied verbatim into the output.
/// </summary>
public sealed class MtrlInfo
{
    public uint Version;
    public ushort FileSize;
    public ushort DataSetSize;
    public byte TextureCount;
    public byte UvSetCount;
    public byte ColorSetCount;
    public byte AdditionalDataSize;
    public MtrlTextureOffset[] TextureOffsets = Array.Empty<MtrlTextureOffset>();
    public MtrlNamedSet[] UvColorSets = Array.Empty<MtrlNamedSet>();
    public MtrlNamedSet[] ColorSets = Array.Empty<MtrlNamedSet>();
    public string ShaderPackageName = string.Empty;

    /// <summary>Everything from just after the strings block to EOF, copied byte-for-byte on write.</summary>
    public byte[] Tail = Array.Empty<byte>();
}

/// <summary>
/// Reads raw .mtrl bytes into an <see cref="MtrlInfo"/> — from the live game (via
/// <see cref="GameDataService.GetVanillaFileBytes"/>) or from a bundled preset file on disk;
/// either way it's plain bytes, not something Lumina's <c>GameData.GetFile&lt;MtrlFile&gt;()</c>
/// can hand back a typed object for. The format has no risky variable-length loop (unlike
/// the .mdl vertex-declaration reader elsewhere in this project), so a straightforward
/// sequential read is safe.
/// </summary>
public static class MtrlReader
{
    private const int HeaderSize = 16;

    public static MtrlInfo Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        var info = new MtrlInfo();
        info.Version = br.ReadUInt32();

        uint packedFileSize = br.ReadUInt32();
        info.FileSize = (ushort)packedFileSize;
        info.DataSetSize = (ushort)(packedFileSize >> 16);

        ushort stringTableSize = br.ReadUInt16();
        ushort shaderPackageNameOffset = br.ReadUInt16();
        info.TextureCount = br.ReadByte();
        info.UvSetCount = br.ReadByte();
        info.ColorSetCount = br.ReadByte();
        info.AdditionalDataSize = br.ReadByte();

        var rawTextureOffsets = new uint[info.TextureCount];
        for (int i = 0; i < rawTextureOffsets.Length; i++)
            rawTextureOffsets[i] = br.ReadUInt32();

        var rawUvSets = new (ushort NameOffset, byte Index, byte Unknown1)[info.UvSetCount];
        for (int i = 0; i < rawUvSets.Length; i++)
            rawUvSets[i] = (br.ReadUInt16(), br.ReadByte(), br.ReadByte());

        var rawColorSets = new (ushort NameOffset, byte Index, byte Unknown1)[info.ColorSetCount];
        for (int i = 0; i < rawColorSets.Length; i++)
            rawColorSets[i] = (br.ReadUInt16(), br.ReadByte(), br.ReadByte());

        var strings = br.ReadBytes(stringTableSize);

        info.TextureOffsets = new MtrlTextureOffset[rawTextureOffsets.Length];
        for (int i = 0; i < rawTextureOffsets.Length; i++)
        {
            ushort offset = (ushort)rawTextureOffsets[i];
            ushort flags = (ushort)(rawTextureOffsets[i] >> 16);
            info.TextureOffsets[i] = new MtrlTextureOffset(ReadCString(strings, offset), flags);
        }

        info.UvColorSets = new MtrlNamedSet[rawUvSets.Length];
        for (int i = 0; i < rawUvSets.Length; i++)
            info.UvColorSets[i] = new MtrlNamedSet(ReadCString(strings, rawUvSets[i].NameOffset), rawUvSets[i].Index, rawUvSets[i].Unknown1);

        info.ColorSets = new MtrlNamedSet[rawColorSets.Length];
        for (int i = 0; i < rawColorSets.Length; i++)
            info.ColorSets[i] = new MtrlNamedSet(ReadCString(strings, rawColorSets[i].NameOffset), rawColorSets[i].Index, rawColorSets[i].Unknown1);

        info.ShaderPackageName = ReadCString(strings, shaderPackageNameOffset);

        int tailStart = HeaderSize + info.TextureCount * 4 + info.UvSetCount * 4 + info.ColorSetCount * 4 + stringTableSize;
        info.Tail = tailStart < data.Length ? data[tailStart..] : Array.Empty<byte>();

        return info;
    }

    private static string ReadCString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = (int)offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }
}
