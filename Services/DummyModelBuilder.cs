using System;
using System.Collections.Generic;
using System.Text;

namespace XIVPortStudio.Services;

/// <summary>
/// Builds a placeholder .mdl for editing in Blender (e.g. via InstantEdit): one
/// empty mesh group per configured material, each with a single empty part
/// already assigned to that material, so the mesh/material wiring is correct
/// before any real geometry exists. The skeleton (bones, bone tables, element
/// IDs, bounding boxes) is carried over unchanged from the vanilla model so the
/// result still binds to the right rig.
///
/// File layout is [FileHeader(68)][VertexDeclarations][StringBlock][ModelHeader
/// onward][vertex/index buffers]. The vertex-declarations block's size is what
/// the file header calls "StackSize"; the model-data block (ModelHeader through
/// the per-bone bounding boxes) is "RuntimeSize". The string block sits between
/// the two but is counted in neither — it has its own length prefix instead.
/// </summary>
public static class DummyModelBuilder
{
    private const int FileHeaderSize = 68;
    private const int ModelHeaderSize = 56;
    private const int LodStructSize = 60;
    private const int ElementIdStructSize = 32;
    private const int MeshStructSize = 36;
    private const int SubmeshStructSize = 16;
    private const int BoneTableStructSize = 132;
    private const int BoundingBoxStructSize = 32;

    public static byte[] Build(VanillaModelInfo vanilla, IReadOnlyList<string> materialNames)
    {
        if (materialNames.Count == 0)
            throw new ArgumentException("At least one material is required.", nameof(materialNames));

        int meshCount = materialNames.Count;

        // ── Strings: our new material names, then the original bone names ──────
        var stringsBlob = new List<byte>();
        var materialOffsets = new uint[meshCount];
        for (int i = 0; i < meshCount; i++)
        {
            materialOffsets[i] = (uint)stringsBlob.Count;
            AppendCString(stringsBlob, materialNames[i]);
        }

        var boneOffsets = new uint[vanilla.BoneNames.Length];
        for (int i = 0; i < vanilla.BoneNames.Length; i++)
        {
            boneOffsets[i] = (uint)stringsBlob.Count;
            AppendCString(stringsBlob, vanilla.BoneNames[i]);
        }

        int stringCount = meshCount + vanilla.BoneNames.Length;

        // ── Vertex declarations: one minimal (terminator-only) block per mesh.
        //    This is the file header's "StackSize" section — strings are NOT
        //    part of it, despite Lumina's own reader treating them as if
        //    they were sequentially adjacent (they are adjacent, just not
        //    counted together). ──────────────────────────────────────────
        var vertexInfo = new List<byte>();
        for (int i = 0; i < meshCount; i++)
            AppendEmptyVertexDeclaration(vertexInfo);

        var stringSection = new List<byte>();
        WriteU16(stringSection, (ushort)stringCount);
        WriteU16(stringSection, 0); // padding
        WriteU32(stringSection, (uint)stringsBlob.Count);
        stringSection.AddRange(stringsBlob);

        // ── Model data block ─────────────────────────────────────────────────
        var model = new List<byte>();

        // ModelHeader
        WriteF32(model, vanilla.Radius);
        WriteU16(model, (ushort)meshCount);                 // MeshCount
        WriteU16(model, 0);                                 // AttributeCount
        WriteU16(model, (ushort)meshCount);                 // SubmeshCount
        WriteU16(model, (ushort)meshCount);                 // MaterialCount
        WriteU16(model, (ushort)vanilla.BoneNames.Length);  // BoneCount
        WriteU16(model, (ushort)vanilla.BoneTables.Length); // BoneTableCount
        WriteU16(model, 0);                                 // ShapeCount
        WriteU16(model, 0);                                 // ShapeMeshCount
        WriteU16(model, 0);                                 // ShapeValueCount
        model.Add(1);                                       // LodCount
        model.Add(0);                                       // Flags1
        WriteU16(model, (ushort)vanilla.ElementIds.Length); // ElementIdCount
        model.Add(0);                                       // TerrainShadowMeshCount
        model.Add(0);                                       // Flags2 (ExtraLodEnabled off, etc.)
        WriteF32(model, vanilla.ModelClipOutDistance);
        WriteF32(model, vanilla.ShadowClipOutDistance);
        WriteU16(model, 0);                                 // Unknown4
        WriteU16(model, 0);                                 // TerrainShadowSubmeshCount
        model.Add(0);                                       // Unknown5
        model.Add(0);                                       // BGChangeMaterialIndex
        model.Add(0);                                       // BGCrestChangeMaterialIndex
        model.Add(0);                                       // Unknown6
        WriteU16(model, 0);                                 // Unknown7
        WriteU16(model, 0);                                 // Unknown8
        WriteU16(model, 0);                                 // Unknown9
        model.AddRange(new byte[6]);                        // Padding

        // ElementIds — unchanged from vanilla (attach points aren't mesh-dependent).
        foreach (var e in vanilla.ElementIds)
        {
            WriteU32(model, e.ElementId);
            WriteU32(model, e.ParentBoneName);
            foreach (var v in e.Translate) WriteF32(model, v);
            foreach (var v in e.Rotate) WriteF32(model, v);
        }

        // The offset every "no geometry here" pointer converges on: right after
        // this whole model-data block, since the vertex/index buffers are empty.
        uint emptyBufferOffset = (uint)(FileHeaderSize + vertexInfo.Count + stringSection.Count + ModelDataBlockLength(vanilla, meshCount));

        // Lods (always exactly 3 slots; only the first is populated).
        WriteLod(model, meshIndex: 0, meshCount: (ushort)meshCount, dataOffset: emptyBufferOffset);
        WriteLod(model, meshIndex: 0, meshCount: 0, dataOffset: emptyBufferOffset);
        WriteLod(model, meshIndex: 0, meshCount: 0, dataOffset: emptyBufferOffset);

        // Meshes: one empty part per material, in order.
        for (int i = 0; i < meshCount; i++)
        {
            WriteU16(model, 0);           // VertexCount
            WriteU16(model, 0);           // Padding
            WriteU32(model, 0);           // IndexCount
            WriteU16(model, (ushort)i);   // MaterialIndex
            WriteU16(model, (ushort)i);   // SubMeshIndex
            WriteU16(model, 1);           // SubMeshCount
            WriteU16(model, 0);           // BoneTableIndex (table 0 always exists if there are any bones)
            WriteU32(model, 0);           // StartIndex
            WriteU32(model, 0); WriteU32(model, 0); WriteU32(model, 0); // VertexBufferOffset x3
            model.Add(0); model.Add(0); model.Add(0);                  // VertexBufferStride x3
            model.Add(0);                                              // VertexStreamCount
        }

        // No attributes, no terrain shadow meshes.

        // Submeshes: one per mesh, all empty.
        for (int i = 0; i < meshCount; i++)
        {
            WriteU32(model, 0); // IndexOffset
            WriteU32(model, 0); // IndexCount
            WriteU32(model, 0); // AttributeIndexMask
            WriteU16(model, 0); // BoneStartIndex
            WriteU16(model, 0); // BoneCount
        }

        // No terrain shadow submeshes.

        foreach (var o in materialOffsets) WriteU32(model, o);
        foreach (var o in boneOffsets) WriteU32(model, o);

        // Bone tables — unchanged from vanilla (bone list itself is unchanged).
        foreach (var bt in vanilla.BoneTables)
        {
            foreach (var idx in bt.BoneIndex) WriteU16(model, idx);
            model.Add(bt.BoneCount);
            model.AddRange(new byte[3]);
        }

        // No shapes.

        // SubmeshBoneMap: empty (byte-length prefix of 0, no data).
        WriteU32(model, 0);

        // No extra padding after the (empty) submesh bone map.
        model.Add(0);

        // Bounding boxes — unchanged from vanilla; harmless with no geometry and
        // keeps anything that eyeballs the model's extents from seeing a degenerate box.
        WriteBoundingBox(model, vanilla.BoundingBoxes);
        WriteBoundingBox(model, vanilla.ModelBoundingBoxes);
        WriteBoundingBox(model, vanilla.WaterBoundingBoxes);
        WriteBoundingBox(model, vanilla.VerticalFogBoundingBoxes);
        foreach (var bb in vanilla.BoneBoundingBoxes) WriteBoundingBox(model, bb);

        // ── File header ──────────────────────────────────────────────────────
        var header = new List<byte>();
        WriteU32(header, vanilla.Version);
        WriteU32(header, (uint)vertexInfo.Count);   // StackSize: vertex declarations only.
        WriteU32(header, (uint)model.Count);        // RuntimeSize: the model-data block.
        WriteU16(header, (ushort)meshCount);        // VertexDeclarationCount
        WriteU16(header, (ushort)meshCount);        // MaterialCount
        for (int i = 0; i < 3; i++) WriteU32(header, emptyBufferOffset); // VertexOffset
        for (int i = 0; i < 3; i++) WriteU32(header, emptyBufferOffset); // IndexOffset
        for (int i = 0; i < 3; i++) WriteU32(header, 0);                // VertexBufferSize
        for (int i = 0; i < 3; i++) WriteU32(header, 0);                // IndexBufferSize
        header.Add(1); // LodCount
        header.Add(1); // EnableIndexBufferStreaming
        header.Add(0); // EnableEdgeGeometry
        header.Add(0); // Padding

        var result = new byte[header.Count + vertexInfo.Count + stringSection.Count + model.Count];
        header.CopyTo(result, 0);
        vertexInfo.CopyTo(result, header.Count);
        stringSection.CopyTo(result, header.Count + vertexInfo.Count);
        model.CopyTo(result, header.Count + vertexInfo.Count + stringSection.Count);
        return result;
    }

    /// <summary>Computes the model-data block length up front, so Lod offsets can point past it.</summary>
    private static int ModelDataBlockLength(VanillaModelInfo vanilla, int meshCount)
        => ModelHeaderSize
         + vanilla.ElementIds.Length * ElementIdStructSize
         + 3 * LodStructSize
         + meshCount * MeshStructSize
         + meshCount * SubmeshStructSize
         + meshCount * 4                          // material name offsets
         + vanilla.BoneNames.Length * 4            // bone name offsets
         + vanilla.BoneTables.Length * BoneTableStructSize
         + 4  // empty submesh bone map length prefix
         + 1  // padding-amount byte
         + 4 * BoundingBoxStructSize
         + vanilla.BoneBoundingBoxes.Length * BoundingBoxStructSize;

    private static void WriteLod(List<byte> b, ushort meshIndex, ushort meshCount, uint dataOffset)
    {
        WriteU16(b, meshIndex);
        WriteU16(b, meshCount);
        WriteF32(b, 0f);   // ModelLodRange
        WriteF32(b, 100f); // TextureLodRange
        WriteU16(b, 0); WriteU16(b, 0); // WaterMeshIndex/Count
        WriteU16(b, 0); WriteU16(b, 0); // ShadowMeshIndex/Count
        WriteU16(b, 0); WriteU16(b, 0); // TerrainShadowMeshIndex/Count
        WriteU16(b, 0); WriteU16(b, 0); // VerticalFogMeshIndex/Count
        WriteU32(b, 0); // EdgeGeometrySize
        WriteU32(b, 0); // EdgeGeometryDataOffset
        WriteU32(b, 0); // PolygonCount
        WriteU32(b, 0); // Unknown1
        WriteU32(b, 0); // VertexBufferSize
        WriteU32(b, 0); // IndexBufferSize
        WriteU32(b, dataOffset); // VertexDataOffset
        WriteU32(b, dataOffset); // IndexDataOffset
    }

    private static void WriteBoundingBox(List<byte> b, VanillaBoundingBox box)
    {
        foreach (var v in box.Min) WriteF32(b, v);
        foreach (var v in box.Max) WriteF32(b, v);
    }

    private static void AppendEmptyVertexDeclaration(List<byte> b)
    {
        // 17 slots x 8-byte VertexElement, terminator first, rest padding.
        b.Add(255); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0); b.Add(0);
        for (int i = 1; i < 17; i++)
            for (int k = 0; k < 8; k++)
                b.Add(0);
    }

    private static void AppendCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.UTF8.GetBytes(s ?? string.Empty));
        b.Add(0);
    }

    private static void WriteU16(List<byte> b, ushort v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
    private static void WriteF32(List<byte> b, float v) => b.AddRange(BitConverter.GetBytes(v));
}
