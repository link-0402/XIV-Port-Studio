using System;
using System.IO;
using System.Text;

namespace XIVPortStudio.Services;

/// <summary>Bone/skeleton metadata read directly from a vanilla .mdl's raw bytes.</summary>
public sealed class VanillaModelInfo
{
    public uint Version;
    public float Radius;
    public float ModelClipOutDistance;
    public float ShadowClipOutDistance;
    public VanillaElementId[] ElementIds = Array.Empty<VanillaElementId>();
    public VanillaBoneTable[] BoneTables = Array.Empty<VanillaBoneTable>();
    public string[] BoneNames = Array.Empty<string>();
    public VanillaBoundingBox BoundingBoxes;
    public VanillaBoundingBox ModelBoundingBoxes;
    public VanillaBoundingBox WaterBoundingBoxes;
    public VanillaBoundingBox VerticalFogBoundingBoxes;
    public VanillaBoundingBox[] BoneBoundingBoxes = Array.Empty<VanillaBoundingBox>();
}

public readonly struct VanillaElementId
{
    public readonly uint ElementId;
    public readonly uint ParentBoneName;
    public readonly float[] Translate;
    public readonly float[] Rotate;
    public VanillaElementId(uint elementId, uint parentBoneName, float[] translate, float[] rotate)
    { ElementId = elementId; ParentBoneName = parentBoneName; Translate = translate; Rotate = rotate; }
}

public readonly struct VanillaBoneTable
{
    public readonly ushort[] BoneIndex;
    public readonly byte BoneCount;
    public VanillaBoneTable(ushort[] boneIndex, byte boneCount) { BoneIndex = boneIndex; BoneCount = boneCount; }
}

public readonly struct VanillaBoundingBox
{
    public readonly float[] Min;
    public readonly float[] Max;
    public VanillaBoundingBox(float[] min, float[] max) { Min = min; Max = max; }
}

/// <summary>
/// Reads just enough of a vanilla .mdl's header to rebuild a dummy model from it —
/// bypassing Lumina's typed <c>MdlFile</c> reader entirely.
///
/// That reader throws <c>EndOfStreamException</c> on every single retail model
/// tested against the currently installed Dalamud/Lumina build, in
/// <c>VertexDeclarationStruct.Read</c>: it always adds the first structure it reads
/// to the element list before checking whether it's the Stream==255 terminator, so
/// a declaration whose very first slot is already the terminator (any declaration
/// with zero real vertex streams) makes it read one structure too many and drifts
/// out of alignment for the rest of the file. This reader never touches vertex
/// declarations at all — it jumps straight past them using the file header's own
/// declared size — so it isn't affected.
///
/// It also does not parse Shapes/ShapeMeshes/ShapeValues/SubmeshBoneMap: those sit
/// between the bone tables and the bounding boxes with a byte layout this reader
/// could not reproduce from the documented struct sizes alone (something between
/// them still doesn't add up against real files — possibly another one-off
/// somewhere in that stretch, possibly a genuine per-patch format change). Since
/// none of that is needed to build a placeholder model, this reader skips directly
/// from the bone tables to the bounding boxes using the file's own declared
/// model-data block size instead of parsing through the gap.
/// </summary>
public static class VanillaModelReader
{
    private const int FileHeaderSize = 68;
    private const int ModelHeaderSize = 56;
    private const int LodStructSize = 60;
    private const int ExtraLodStructSize = 38;
    private const int ElementIdStructSize = 32;
    private const int MeshStructSize = 36;
    private const int TerrainShadowMeshStructSize = 20;
    private const int SubmeshStructSize = 16;
    private const int TerrainShadowSubmeshStructSize = 12;
    private const int BoneTableStructSize = 132;
    private const int BoundingBoxStructSize = 32;

    public static VanillaModelInfo Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var br = new BinaryReader(ms);

        // ── File header ──────────────────────────────────────────────────
        uint version = br.ReadUInt32();
        uint stackSize = br.ReadUInt32();   // size of the vertex-declarations block only.
        uint runtimeSize = br.ReadUInt32(); // size of the ModelHeader-onward model-data block.

        // ── String block: sits between vertex declarations and ModelHeader,
        //    and (unlike vertex declarations) is NOT counted in stackSize. ──
        long stringHeaderOffset = FileHeaderSize + stackSize;
        ms.Position = stringHeaderOffset;
        br.ReadUInt16(); // StringCount
        br.ReadUInt16(); // padding
        uint stringSize = br.ReadUInt32();
        var strings = br.ReadBytes((int)stringSize);
        long modelHeaderOffset = stringHeaderOffset + 8 + stringSize;

        // ── ModelHeader (56 bytes) ───────────────────────────────────────
        ms.Position = modelHeaderOffset;
        float radius = br.ReadSingle();
        ushort meshCount = br.ReadUInt16();
        ushort attributeCount = br.ReadUInt16();
        ushort submeshCount = br.ReadUInt16();
        ushort materialCount = br.ReadUInt16();
        ushort boneCount = br.ReadUInt16();
        ushort boneTableCount = br.ReadUInt16();
        br.ReadUInt16();                    // ShapeCount
        br.ReadUInt16();                    // ShapeMeshCount
        br.ReadUInt16();                    // ShapeValueCount
        br.ReadByte();                      // LodCount
        br.ReadByte();                      // Flags1 (unused)
        ushort elementIdCount = br.ReadUInt16();
        byte terrainShadowMeshCount = br.ReadByte();
        byte flags2 = br.ReadByte();
        float modelClipOutDistance = br.ReadSingle();
        float shadowClipOutDistance = br.ReadSingle();
        br.ReadUInt16();                    // Unknown4
        ushort terrainShadowSubmeshCount = br.ReadUInt16();
        br.ReadBytes(4);                    // Unknown5, BGChangeMaterialIndex, BGCrestChangeMaterialIndex, Unknown6
        br.ReadBytes(6);                    // Unknown7/8/9
        br.ReadBytes(6);                    // Padding

        bool extraLodEnabled = (flags2 & 0x10) != 0;

        // ── ElementIds ────────────────────────────────────────────────────
        var elementIds = new VanillaElementId[elementIdCount];
        for (int i = 0; i < elementIdCount; i++)
        {
            uint elementId = br.ReadUInt32();
            uint parentBoneName = br.ReadUInt32();
            var translate = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            var rotate = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            elementIds[i] = new VanillaElementId(elementId, parentBoneName, translate, rotate);
        }

        // ── Skip everything mesh-related we don't need, up to bone data. ─
        br.ReadBytes(3 * LodStructSize);
        if (extraLodEnabled)
            br.ReadBytes(3 * ExtraLodStructSize);
        br.ReadBytes(meshCount * MeshStructSize);
        br.ReadBytes(attributeCount * 4);
        br.ReadBytes(terrainShadowMeshCount * TerrainShadowMeshStructSize);
        br.ReadBytes(submeshCount * SubmeshStructSize);
        br.ReadBytes(terrainShadowSubmeshCount * TerrainShadowSubmeshStructSize);
        br.ReadBytes(materialCount * 4); // MaterialNameOffsets — not needed.

        // ── BoneNameOffsets + BoneTables ─────────────────────────────────
        var boneNameOffsets = new uint[boneCount];
        for (int i = 0; i < boneCount; i++)
            boneNameOffsets[i] = br.ReadUInt32();

        var boneTables = new VanillaBoneTable[boneTableCount];
        for (int i = 0; i < boneTableCount; i++)
        {
            var boneIndex = new ushort[64];
            for (int k = 0; k < 64; k++) boneIndex[k] = br.ReadUInt16();
            byte tableBoneCount = br.ReadByte();
            br.ReadBytes(3); // padding
            boneTables[i] = new VanillaBoneTable(boneIndex, tableBoneCount);
        }

        // ── Jump straight to the bounding boxes, skipping Shapes/ShapeMeshes/
        //    ShapeValues/SubmeshBoneMap — not needed, and not reliably skippable
        //    from here (see class doc). The model-data block's declared total
        //    size tells us exactly where they end regardless of their contents.
        long boundingBoxStart = modelHeaderOffset + runtimeSize - (4 + boneCount) * (long)BoundingBoxStructSize;
        ms.Position = boundingBoxStart;

        VanillaBoundingBox ReadBox()
        {
            var min = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            var max = new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
            return new VanillaBoundingBox(min, max);
        }

        var boundingBoxes = ReadBox();
        var modelBoundingBoxes = ReadBox();
        var waterBoundingBoxes = ReadBox();
        var verticalFogBoundingBoxes = ReadBox();
        var boneBoundingBoxes = new VanillaBoundingBox[boneCount];
        for (int i = 0; i < boneCount; i++)
            boneBoundingBoxes[i] = ReadBox();

        // ── Resolve bone name strings ────────────────────────────────────
        var boneNames = new string[boneCount];
        for (int i = 0; i < boneCount; i++)
            boneNames[i] = ReadCString(strings, boneNameOffsets[i]);

        return new VanillaModelInfo
        {
            Version = version,
            Radius = radius,
            ModelClipOutDistance = modelClipOutDistance,
            ShadowClipOutDistance = shadowClipOutDistance,
            ElementIds = elementIds,
            BoneTables = boneTables,
            BoneNames = boneNames,
            BoundingBoxes = boundingBoxes,
            ModelBoundingBoxes = modelBoundingBoxes,
            WaterBoundingBoxes = waterBoundingBoxes,
            VerticalFogBoundingBoxes = verticalFogBoundingBoxes,
            BoneBoundingBoxes = boneBoundingBoxes,
        };
    }

    private static string ReadCString(byte[] strings, uint offset)
    {
        if (offset >= strings.Length) return string.Empty;
        int end = (int)offset;
        while (end < strings.Length && strings[end] != 0) end++;
        return Encoding.UTF8.GetString(strings, (int)offset, end - (int)offset);
    }
}
