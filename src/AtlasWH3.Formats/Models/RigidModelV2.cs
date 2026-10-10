using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Models;

/// <summary>
/// The single-LOD RMV2 (version 8) files BOB writes for campaign terrain:
/// global_meshes\land_mesh_N / sea_mesh_N (material 101 TerrainTiles, 16-byte vertices) and
/// models\river_N.wsmodel.rigid_model_v2 (material 68 default, 48-byte vertices).
///
/// Layout (little-endian):
///   0x00 "RMV2", u32 version 8, u32 lod count 1, 128-byte skeleton name (zero)
///   0x8C LOD header: u32 mesh count 1, u32 vertex bytes, u32 index bytes, u32 first mesh offset 0xA8,
///        f32 distance 1e6, u32 lod level 0, 4 bytes quality + padding (padding is uninitialised memory in BOB)
///   0xA8 mesh header: u16 material, u16 render flags, u32 section size, u32 vertex offset, u32 vertex count,
///        u32 index offset, u32 index count, 6 x f32 bounding box (min xyz, max xyz); offsets relative to 0xA8
///   0xD8 32-byte shader block ("rigid_default" + uninitialised bytes)
///   0xF8 material block (88 bytes for TerrainTiles, 860 for default), then vertices, then u16 indices.
/// A global mesh over BOB's MESH_SPLITTER limit has several meshes in LOD 0 (<see cref="MoreMeshes"/>): each mesh
/// section (header at 0x00..0x2F of the section, offsets relative to the section) follows the previous one, and the LOD
/// header holds the total vertex and index bytes.
/// </summary>
public sealed class RigidModelV2
{
    public const int MeshStart = 0xA8;
    private const int ShaderStart = 0xD8;
    private const int MaterialStart = 0xF8;

    public ushort Material { get; set; }
    public ushort RenderFlags { get; set; }
    /// <summary>Bytes 0xA4..0xA7: quality byte and three padding bytes.</summary>
    public byte[] LodQuality { get; set; } = new byte[4];
    /// <summary>Bytes 0xD8..0xF7.</summary>
    public byte[] Shader { get; set; } = new byte[32];
    /// <summary>Bytes from 0xF8 up to the vertices.</summary>
    public byte[] MaterialBlock { get; set; } = [];
    public int VertexStride { get; set; } = 16;
    public byte[] Vertices { get; set; } = [];
    public ushort[] Indices { get; set; } = [];
    /// <summary>min x, y, z, max x, y, z.</summary>
    public float[] Bounds { get; set; } = new float[6];
    /// <summary>The LOD's further meshes (only their mesh fields are used), written after this one.</summary>
    public List<RigidModelV2> MoreMeshes { get; } = [];

    public int VertexCount => Vertices.Length / VertexStride;

    public static RigidModelV2 Read(string path) => Read(File.ReadAllBytes(path));

    public static RigidModelV2 Read(ReadOnlySpan<byte> b)
    {
        if (!b[..4].SequenceEqual("RMV2"u8)) throw new InvalidDataException("Not an RMV2 file.");
        if (U32(b, 4) != 8 || U32(b, 8) != 1)
            throw new InvalidDataException("Only single-LOD RMV2 v8 terrain models are supported.");
        var meshes = (int)U32(b, 0x8C);
        if (meshes < 1) throw new InvalidDataException("RMV2 LOD without meshes.");
        RigidModelV2? model = null;
        var start = (int)U32(b, 0x98);
        for (var m = 0; m < meshes; m++)
        {
            var mesh = ReadMesh(b, start, b.Slice(0xA4, 4).ToArray());
            if (model is null) model = mesh;
            else model.MoreMeshes.Add(mesh);
            start += (int)U32(b, start + 4);
        }
        return model!;
    }

    private static RigidModelV2 ReadMesh(ReadOnlySpan<byte> b, int start, byte[] lodQuality)
    {
        var vertexOffset = (int)U32(b, start + 0x08);
        var vertexCount = (int)U32(b, start + 0x0C);
        var indexOffset = (int)U32(b, start + 0x10);
        var indexCount = (int)U32(b, start + 0x14);
        var vertexBytes = indexOffset - vertexOffset;
        var model = new RigidModelV2
        {
            Material = BinaryPrimitives.ReadUInt16LittleEndian(b[start..]),
            RenderFlags = BinaryPrimitives.ReadUInt16LittleEndian(b[(start + 2)..]),
            LodQuality = lodQuality,
            Shader = b.Slice(start + ShaderStart - MeshStart, 32).ToArray(),
            MaterialBlock = b[(start + MaterialStart - MeshStart)..(start + vertexOffset)].ToArray(),
            VertexStride = vertexCount == 0 ? 16 : vertexBytes / vertexCount,
            Vertices = b.Slice(start + vertexOffset, vertexBytes).ToArray(),
            Indices = new ushort[indexCount],
        };
        for (var i = 0; i < 6; i++) model.Bounds[i] = BitConverter.ToSingle(b.Slice(start + 0x18 + i * 4, 4));
        for (var i = 0; i < indexCount; i++)
            model.Indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(start + indexOffset + i * 2)..]);
        return model;
    }

    public byte[] ToBytes()
    {
        RigidModelV2[] meshes = [this, .. MoreMeshes];
        var sizes = meshes.Select(m => MaterialStart - MeshStart + m.MaterialBlock.Length + m.Vertices.Length + m.Indices.Length * 2).ToArray();
        var result = new byte[MeshStart + sizes.Sum()];
        var s = result.AsSpan();
        "RMV2"u8.CopyTo(s);
        W(s, 4, 8);
        W(s, 8, 1);
        W(s, 0x8C, (uint)meshes.Length);
        W(s, 0x90, (uint)meshes.Sum(m => m.Vertices.Length));
        W(s, 0x94, (uint)meshes.Sum(m => m.Indices.Length * 2));
        W(s, 0x98, MeshStart);
        BinaryPrimitives.WriteSingleLittleEndian(s[0x9C..], 1e6f);
        LodQuality.CopyTo(s[0xA4..]);
        var start = MeshStart;
        for (var k = 0; k < meshes.Length; k++)
        {
            meshes[k].WriteMesh(s[start..(start + sizes[k])]);
            start += sizes[k];
        }
        return result;
    }

    /// <summary>One mesh section (header, shader, material, vertices, indices; offsets relative to its start).</summary>
    private void WriteMesh(Span<byte> s)
    {
        var vertexOffset = MaterialStart - MeshStart + MaterialBlock.Length;
        var indexOffset = vertexOffset + Vertices.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(s, Material);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], RenderFlags);
        W(s, 0x04, (uint)s.Length);
        W(s, 0x08, (uint)vertexOffset);
        W(s, 0x0C, (uint)VertexCount);
        W(s, 0x10, (uint)indexOffset);
        W(s, 0x14, (uint)Indices.Length);
        for (var i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(s[(0x18 + i * 4)..], Bounds[i]);
        Shader.CopyTo(s[(ShaderStart - MeshStart)..]);
        MaterialBlock.CopyTo(s[(MaterialStart - MeshStart)..]);
        Vertices.CopyTo(s[vertexOffset..]);
        for (var i = 0; i < Indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(s[(indexOffset + i * 2)..], Indices[i]);
    }

    public void Write(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>Bounding box over the first three floats of every vertex.</summary>
    public void ComputeBounds()
    {
        float[] b = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
        for (var o = 0; o < Vertices.Length; o += VertexStride)
            for (var a = 0; a < 3; a++)
            {
                var v = BitConverter.ToSingle(Vertices, o + a * 4);
                b[a] = Math.Min(b[a], v);
                b[a + 3] = Math.Max(b[a + 3], v);
            }
        Bounds = VertexCount == 0 ? new float[6] : b;
    }

    /// <summary>Land/sea tiles: BOB's box is the whole tile rectangle in x/z (even where the mesh covers only part of
    /// it) and the vertex extent in y.</summary>
    public void SetTileBounds(float minX, float minZ, float maxX, float maxZ)
    {
        ComputeBounds();
        Bounds[0] = minX;
        Bounds[2] = minZ;
        Bounds[3] = maxX;
        Bounds[5] = maxZ;
    }

    /// <summary>16-byte TerrainTiles vertices (x, y, z, 1).</summary>
    public static byte[] PackPositions(IReadOnlyList<(float X, float Y, float Z)> positions)
    {
        var bytes = new byte[positions.Count * 16];
        for (var i = 0; i < positions.Count; i++)
        {
            var s = bytes.AsSpan(i * 16);
            BinaryPrimitives.WriteSingleLittleEndian(s, positions[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(s[4..], positions[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], positions[i].Z);
            BinaryPrimitives.WriteSingleLittleEndian(s[12..], 1f);
        }
        return bytes;
    }

    /// <summary>Land and sea tiles: the header bytes vanilla carries in every file of its kind.</summary>
    public static RigidModelV2 NewTerrainTile(bool sea) => new()
    {
        Material = 101,
        LodQuality = sea ? [0x00, 0x41, 0x0C, 0xC9] : [0, 0, 0, 0],
        Shader = ShaderBlock(sea ? [0xB0, 0xA0, 0x19, 0xC9, 0xFD, 0x7F, 0, 0] : new byte[8]),
        MaterialBlock = TerrainTileMaterial(),
        VertexStride = 16,
    };

    /// <summary>River meshes as WH3's BOB writes them (every IEE and Old World river: LOD quality 0, shader byte 24
    /// 0x44). The two bytes at material offset 0x222 (file 0x31A) are uninitialised memory in BOB and differ per river;
    /// they are written as zero.</summary>
    public static RigidModelV2 NewRiver((float X, float Y, float Z) pivot)
    {
        var shader = ShaderBlock(new byte[8]);
        shader[24] = 0x44;
        return new RigidModelV2
        {
            Material = 68,
            LodQuality = [0, 0, 0, 0],
            Shader = shader,
            MaterialBlock = RiverMaterial(pivot),
            VertexStride = 48,
        };
    }

    private static byte[] ShaderBlock(byte[] tail8)
    {
        var block = new byte[32];
        Encoding.ASCII.GetBytes("rigid_default").CopyTo(block, 0);
        block[15] = 0x8D;
        tail8.CopyTo(block, 16);
        return block;
    }

    private static byte[] TerrainTileMaterial()
    {
        var block = new byte[88];
        "Mesh"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x148 - MaterialStart), 0x1A0);
        return block;
    }

    /// <summary>860-byte default (weighted) material: u16 vertex format 13, 32-byte name "River", 256-byte texture
    /// directory "/", 256-byte filters, 2 padding bytes, pivot, three identity 3x4 matrices, matrix indices -1, -1,
    /// six zero counts, 124 bytes padding.</summary>
    private static byte[] RiverMaterial((float X, float Y, float Z) pivot)
    {
        var block = new byte[860];
        var s = block.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 13);
        "River"u8.CopyTo(s[2..]);
        s[34] = (byte)'/';
        var o = 0x224;
        BinaryPrimitives.WriteSingleLittleEndian(s[o..], pivot.X);
        BinaryPrimitives.WriteSingleLittleEndian(s[(o + 4)..], pivot.Y);
        BinaryPrimitives.WriteSingleLittleEndian(s[(o + 8)..], pivot.Z);
        o += 12;
        for (var m = 0; m < 3; m++, o += 48)
            for (var r = 0; r < 3; r++)
                BinaryPrimitives.WriteSingleLittleEndian(s[(o + (r * 4 + r) * 4)..], 1f);
        BinaryPrimitives.WriteInt32LittleEndian(s[o..], -1);
        BinaryPrimitives.WriteInt32LittleEndian(s[(o + 4)..], -1);
        return block;
    }

    private static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b[offset..]);
    private static void W(Span<byte> s, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(s[offset..], value);
}
