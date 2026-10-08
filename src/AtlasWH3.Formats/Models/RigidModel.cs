using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Models;

/// <summary>
/// Full read-only RMV2 (.rigid_model_v2, versions 6-8): every LOD and mesh with decoded vertices (position, normal,
/// uv), triangles, and the mesh material's name, shader, textures, attachment points and alpha mode. Ported from
/// Z:\Claude\Animations\tw3k_animforge\crates\af-formats\src\rmv2.rs (verified on 3K data). Layout, little-endian:
/// <code>
/// file header 140: "RMV2", u32 version, u32 lod count, [u8;128] skeleton
/// lod header 28 (v7/8) | 20 (v6): u32 mesh count, u32 vertex bytes, u32 index bytes, u32 first mesh offset (abs),
///                                 f32 distance, [v7+: u32 lod level, u8 quality, 3 pad]
/// mesh 80: u16 material id, u16 render flags, u32 section size, u32 vertex offset, u32 vertex count,
///          u32 index offset, u32 index count, f32x6 bbox, [u8;12] shader, [u8;20]   (offsets from the mesh start)
/// material 860 (ids other than 49 / 101): u16 vertex format, [u8;32] name, [u8;256] texture dir, [u8;256] filters,
///          2 pad, f32x3 pivot, f32x36, i32 x2, u32x6 counts (attachments, textures, strings, floats, ints, vec4s),
///          [u8;124]; then attachments (84), textures (260: i32 type, [u8;256] path), params
///          (id 49: 256-byte path only; id 101 TerrainTiles: 88 bytes)
/// vertices: stride = (index offset - vertex offset) / vertex count; indices u16.
/// </code>
/// Vertex positions are pivot-relative in the file; the game renders raw + pivot, which is what is returned here.
/// </summary>
public sealed class RigidModel
{
    public const ushort VfStatic = 0, VfCollision = 1, VfWeighted = 3, VfCinematic = 4, VfPosition16 = 5, VfRiver = 13;
    /// <summary>Material 49 custom tile meshes (campaign cliffs, lakes, mines): f32 xyz + w, packed normal at 16, half uv at 28 (36 bytes).
    /// Not a format id in the file; assigned here.</summary>
    public const ushort VfTerrainCustom = 0xF049;
    /// <summary>Material 96 (TerrainBase0) tile base meshes: 4 × half (x, y, z, w), 8 bytes. Assigned here.</summary>
    public const ushort VfTerrainBase = 0xF096;
    public const int TexDiffuse = 0, TexNormal = 1, TexMask = 3, TexBaseColour = 27, TexMaterialMap = 29;

    public sealed class Mesh
    {
        public string Name { get; init; } = "";
        public ushort MaterialId { get; init; }
        public ushort RenderFlags { get; init; }
        public ushort VertexFormat { get; init; }
        public string Shader { get; init; } = "";
        /// <summary>Int param 0: 0 opaque, 1 alpha-tested (null when absent).</summary>
        public int? AlphaMode { get; init; }
        public IReadOnlyList<(int Type, string Path)> Textures { get; init; } = [];
        public IReadOnlyList<(string Name, int Bone)> Attachments { get; init; } = [];
        public float[] Pivot { get; init; } = new float[3];
        /// <summary>Material ids 49 / 100 carry only a 256-byte asset path (100: campaign/battle decals, whose textures
        /// are &lt;path&gt;_base_colour.dds etc. and whose quad the game builds itself: such meshes have no vertices).</summary>
        public string? MaterialPath { get; init; }
        /// <summary>True when the vertex layout was not known and only positions were recovered (no normals/uvs).</summary>
        public bool PositionsOnly { get; init; }
        public float[] BoundsMin { get; init; } = new float[3];
        public float[] BoundsMax { get; init; } = new float[3];
        /// <summary>x, y, z per vertex (pivot applied).</summary>
        public float[] Positions { get; init; } = [];
        /// <summary>x, y, z per vertex.</summary>
        public float[] Normals { get; init; } = [];
        /// <summary>u, v per vertex.</summary>
        public float[] Uvs { get; init; } = [];
        public ushort[] Indices { get; init; } = [];

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;

        public string? Texture(int type) => Textures.FirstOrDefault(t => t.Type == type).Path;
    }

    public sealed record Lod(float Distance, IReadOnlyList<Mesh> Meshes);

    public uint Version { get; init; }
    public string Skeleton { get; init; } = "";
    public IReadOnlyList<Lod> Lods { get; init; } = [];

    /// <summary>Bounds over every mesh of LOD 0: min x, y, z, max x, y, z (from the vertices).</summary>
    public float[] Bounds
    {
        get
        {
            float[] b = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
            foreach (var m in Lods.FirstOrDefault()?.Meshes ?? [])
                for (var i = 0; i < m.Positions.Length; i += 3)
                    for (var a = 0; a < 3; a++)
                    {
                        b[a] = Math.Min(b[a], m.Positions[i + a]);
                        b[a + 3] = Math.Max(b[a + 3], m.Positions[i + a]);
                    }
            return b[0] == float.MaxValue ? new float[6] : b;
        }
    }

    public static RigidModel Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 140 || !b[..4].SequenceEqual("RMV2"u8)) throw new InvalidDataException("not an RMV2 file");
        var version = U32(b, 4);
        if (version is < 6 or > 8) throw new InvalidDataException($"unsupported RMV2 version {version}");
        var lodCount = (int)U32(b, 8);
        // Battle vegetation stores flags in the high half (0x00010002 = 2 LODs).
        if (lodCount > 64 && (lodCount & 0xFFFF) <= 64) lodCount &= 0xFFFF;
        if (lodCount > 64) throw new InvalidDataException($"implausible LOD count {lodCount}");
        var skeleton = Str(b.Slice(12, 128));
        var lodSize = version >= 7 ? 28 : 20;
        var lods = new List<Lod>();
        for (var l = 0; l < lodCount; l++)
        {
            var h = Need(b, 140 + l * lodSize, lodSize, "LOD header");
            var meshCount = (int)U32(h, 0);
            var offset = (int)U32(h, 12);
            var distance = F32(h, 16);
            var meshes = new List<Mesh>();
            for (var m = 0; m < meshCount; m++)
            {
                var (mesh, size) = ReadMesh(b, offset, version);
                meshes.Add(mesh);
                offset += size;
            }
            lods.Add(new Lod(distance, meshes));
        }
        return new RigidModel { Version = version, Skeleton = skeleton, Lods = lods };
    }

    /// <summary>
    /// LOD 0 bounds from the mesh headers alone (min xyz, max xyz) — works for files whose vertices this reader cannot
    /// decode, so callers can still draw a placeholder box. Null when even the headers are unreadable.
    /// </summary>
    public static float[]? HeaderBounds(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length < 168 || !b[..4].SequenceEqual("RMV2"u8)) return null;
            var meshCount = (int)U32(b, 140);
            var offset = (int)U32(b, 152);
            float[] r = [float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue];
            for (var m = 0; m < meshCount && m < 256; m++)
            {
                var h = Need(b, offset, 80, "mesh header");
                for (var a = 0; a < 3; a++)
                {
                    r[a] = Math.Min(r[a], F32(h, 24 + a * 4));
                    r[a + 3] = Math.Max(r[a + 3], F32(h, 36 + a * 4));
                }
                var size = (int)U32(h, 4);
                if (size < 80) break;
                offset += size;
            }
            return r[0] <= r[3] && r.All(float.IsFinite) ? r : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Expected vertex stride for a format (null = not decodable).</summary>
    public static int? Stride(ushort format, uint version)
    {
        var v8 = version >= 8 ? 4 : 0;
        return format switch
        {
            VfStatic => 32,
            VfCollision => 24,
            VfWeighted => 28 + v8,
            VfCinematic => 32 + v8,
            VfPosition16 => 16,
            VfRiver => 48,
            VfTerrainCustom => 36,
            VfTerrainBase => 8,
            _ => null,
        };
    }

    private static int WeightCount(ushort format) => format switch { VfWeighted => 2, VfCinematic => 4, _ => 0 };

    private static (Mesh, int) ReadMesh(ReadOnlySpan<byte> b, int start, uint version)
    {
        var h = Need(b, start, 80, "mesh header");
        var materialId = U16(h, 0);
        var renderFlags = U16(h, 2);
        var sectionSize = (int)U32(h, 4);
        var vertexOffset = (int)U32(h, 8);
        var vertexCount = (int)U32(h, 12);
        var indexOffset = (int)U32(h, 16);
        var indexCount = (int)U32(h, 20);
        float[] bmin = [F32(h, 24), F32(h, 28), F32(h, 32)], bmax = [F32(h, 36), F32(h, 40), F32(h, 44)];
        var shader = Str(h.Slice(48, 12));
        if (sectionSize < 80) throw new InvalidDataException("RMV2 mesh section smaller than its header");

        var m0 = start + 80;
        var name = "";
        var format = VfCinematic;
        var textures = new List<(int, string)>();
        var attachments = new List<(string, int)>();
        int? alpha = null;
        var pivot = new float[3];
        string? materialPath = null;
        switch (materialId)
        {
            case 49 or 100:
                materialPath = Str(Need(b, m0, 256, "path material"));
                break;
            case 101:
                Need(b, m0, 88, "terrain tile material");
                format = VfPosition16;
                break;
            case 96:
                Need(b, m0, 88, "terrain base material");
                name = Str(b.Slice(m0, 32));
                format = VfTerrainBase;
                break;
            default:
                var m = Need(b, m0, 860, "material");
                format = U16(m, 0);
                name = Str(m.Slice(2, 32));
                pivot = [F32(m, 548), F32(m, 552), F32(m, 556)];
                var counts = new uint[6];
                for (var i = 0; i < 6; i++) counts[i] = U32(m, 712 + i * 4);
                var p = m0 + 860;
                for (var i = 0; i < counts[0]; i++, p += 84)
                {
                    var a = Need(b, p, 84, "attachment");
                    attachments.Add((Str(a[..32]), BinaryPrimitives.ReadInt32LittleEndian(a[80..])));
                }
                for (var i = 0; i < counts[1]; i++, p += 260)
                {
                    var t = Need(b, p, 260, "texture");
                    textures.Add((BinaryPrimitives.ReadInt32LittleEndian(t), Str(t[4..260])));
                }
                for (var i = 0; i < counts[2]; i++)
                {
                    var s = Need(b, p, 6, "string param");
                    p += 6 + U16(s, 4);
                }
                p += (int)counts[3] * 8;
                for (var i = 0; i < counts[4]; i++, p += 8)
                {
                    var ip = Need(b, p, 8, "int param");
                    if (BinaryPrimitives.ReadInt32LittleEndian(ip) == 0) alpha = BinaryPrimitives.ReadInt32LittleEndian(ip[4..]);
                }
                break;
        }

        float[] positions = [], normals = [], uvs = [];
        var positionsOnly = false;
        if (vertexCount > 0 && indexOffset == vertexOffset) vertexCount = 0; // decal stubs: no vertex data stored
        if (materialId == 49 && vertexCount > 0 && (indexOffset - vertexOffset) / vertexCount == 36) format = VfTerrainCustom;
        if (vertexCount > 0)
        {
            if (indexOffset < vertexOffset) throw new InvalidDataException("RMV2 index block before vertex block");
            var stride = (indexOffset - vertexOffset) / vertexCount;
            var available = Math.Max(0, Math.Min(vertexCount * stride, b.Length - start - vertexOffset));
            if (Stride(format, version) != stride
                && FallbackPositions(b.Slice(start + vertexOffset, available), vertexCount, stride, bmin, bmax) is { } recovered)
            {
                positions = recovered;
                normals = new float[vertexCount * 3];
                for (var i = 1; i < normals.Length; i += 3) normals[i] = 1;
                uvs = new float[vertexCount * 2];
                positionsOnly = true;
            }
            else if (Stride(format, version) != stride)
            {
                // The declared format does not match the stride: pick a layout that does (same weight count first).
                var want = WeightCount(format);
                ushort[] all = [VfStatic, VfWeighted, VfCinematic, VfCollision, VfPosition16];
                ushort? resolved = all.Where(c => Stride(c, version) == stride && WeightCount(c) == want).Select(c => (ushort?)c).FirstOrDefault()
                                   ?? new ushort[] { VfCinematic, VfWeighted, VfStatic, VfCollision, VfPosition16 }
                                       .Where(c => Stride(c, version) == stride).Select(c => (ushort?)c).FirstOrDefault();
                format = resolved ?? throw new InvalidDataException($"unknown RMV2 vertex layout (format {format}, stride {stride}, v{version})");
            }
            if (!positionsOnly)
            {
                var block = Need(b, start + vertexOffset, vertexCount * stride, "vertices");
                positions = new float[vertexCount * 3];
                normals = new float[vertexCount * 3];
                uvs = new float[vertexCount * 2];
                for (var i = 0; i < vertexCount; i++)
                    Decode(block.Slice(i * stride, stride), format, positions.AsSpan(i * 3, 3), normals.AsSpan(i * 3, 3), uvs.AsSpan(i * 2, 2));
            }
            if ((pivot[0] != 0 || pivot[1] != 0 || pivot[2] != 0) && pivot.All(float.IsFinite))
                for (var i = 0; i < positions.Length; i += 3)
                {
                    positions[i] += pivot[0];
                    positions[i + 1] += pivot[1];
                    positions[i + 2] += pivot[2];
                }
        }
        var usable = vertexCount == 0 ? 0 : indexCount / 3 * 3;
        var ib = Need(b, start + indexOffset, usable * 2, "indices");
        var indices = new ushort[usable];
        for (var i = 0; i < usable; i++) indices[i] = U16(ib, i * 2);
        // Drop triangles that point past the vertices (a few vanilla files have them).
        if (indices.Any(i => i >= vertexCount))
        {
            var kept = new List<ushort>(usable);
            for (var t = 0; t < usable; t += 3)
                if (indices[t] < vertexCount && indices[t + 1] < vertexCount && indices[t + 2] < vertexCount)
                    kept.AddRange([indices[t], indices[t + 1], indices[t + 2]]);
            indices = [.. kept];
        }

        return (new Mesh
        {
            Name = name, MaterialId = materialId, RenderFlags = renderFlags, VertexFormat = format, Shader = shader,
            AlphaMode = alpha, Textures = textures, Attachments = attachments, Pivot = pivot, BoundsMin = bmin, BoundsMax = bmax,
            MaterialPath = materialPath, PositionsOnly = positionsOnly,
            Positions = positions, Normals = normals, Uvs = uvs, Indices = indices,
        }, sectionSize);
    }

    private static void Decode(ReadOnlySpan<byte> v, ushort format, Span<float> pos, Span<float> normal, Span<float> uv)
    {
        switch (format)
        {
            case VfStatic:
                HalfPos(v, pos);
                ByteVec(v[16..20], normal);
                (normal[0], normal[2]) = (normal[2], normal[0]); // the static frame stores X/Z swapped
                uv[0] = H(v, 8);
                uv[1] = H(v, 10);
                break;
            case VfWeighted:
                HalfPos(v, pos);
                ByteVec(v[12..16], normal);
                uv[0] = H(v, 16);
                uv[1] = H(v, 18);
                break;
            case VfCinematic:
                HalfPos(v, pos);
                ByteVec(v[16..20], normal);
                uv[0] = H(v, 20);
                uv[1] = H(v, 22);
                break;
            case VfCollision:
                pos[0] = F32(v, 0); pos[1] = F32(v, 4); pos[2] = F32(v, 8);
                normal[0] = F32(v, 12); normal[1] = F32(v, 16); normal[2] = F32(v, 20);
                break;
            case VfTerrainBase:
                HalfPos(v, pos);
                normal[1] = 1;
                break;
            case VfTerrainCustom:
                pos[0] = F32(v, 0); pos[1] = F32(v, 4); pos[2] = F32(v, 8);
                ByteVec(v[16..20], normal);
                uv[0] = H(v, 28);
                uv[1] = H(v, 30);
                break;
            case VfRiver:
                // RiverBuilder layout: f32 xyz + w, uv, world uv, packed normal / tangent / bitangent, 4 zero bytes.
                pos[0] = F32(v, 0); pos[1] = F32(v, 4); pos[2] = F32(v, 8);
                uv[0] = F32(v, 16); uv[1] = F32(v, 20);
                ByteVec(v[32..36], normal);
                break;
            case VfPosition16:
                var w = F32(v, 12);
                var s = w > 0 ? w : 1;
                pos[0] = F32(v, 0) * s; pos[1] = F32(v, 4) * s; pos[2] = F32(v, 8) * s;
                normal[1] = 1;
                break;
        }
    }

    /// <summary>
    /// For vertex layouts this reader does not know (a few battle-only formats): positions as 4 x f16 or 3 x f32 at
    /// the start of each vertex, accepted only if every vertex lies inside the mesh header's bounding box (with a
    /// small margin). Returns null when neither fits.
    /// </summary>
    private static float[]? FallbackPositions(ReadOnlySpan<byte> block, int count, int stride, float[] bmin, float[] bmax)
    {
        if (stride < 8 || block.Length < count * stride) return null;
        var margin = Math.Max(0.05f, (bmax[0] - bmin[0] + bmax[1] - bmin[1] + bmax[2] - bmin[2]) * 0.02f);
        bool Inside(float x, float y, float z) =>
            float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            && x >= bmin[0] - margin && x <= bmax[0] + margin && y >= bmin[1] - margin && y <= bmax[1] + margin
            && z >= bmin[2] - margin && z <= bmax[2] + margin;
        var pos = new float[count * 3];
        var half = true;
        for (var i = 0; i < count && half; i++)
        {
            HalfPos(block.Slice(i * stride, 8), pos.AsSpan(i * 3, 3));
            half = Inside(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
        }
        if (half) return pos;
        if (stride < 12) return null;
        for (var i = 0; i < count; i++)
        {
            var v = block.Slice(i * stride, 12);
            pos[i * 3] = F32(v, 0); pos[i * 3 + 1] = F32(v, 4); pos[i * 3 + 2] = F32(v, 8);
            if (!Inside(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2])) return null;
        }
        return pos;
    }

    private static void HalfPos(ReadOnlySpan<byte> v, Span<float> pos)
    {
        var w = H(v, 6);
        var s = w != 0 ? w : 1;
        pos[0] = H(v, 0) * s;
        pos[1] = H(v, 2) * s;
        pos[2] = H(v, 4) * s;
    }

    private static void ByteVec(ReadOnlySpan<byte> b, Span<float> n)
    {
        static float F(byte c) => c / 255f * 2 - 1;
        float x = F(b[0]), y = F(b[1]), z = F(b[2]), w = F(b[3]);
        if (w > 0) { x *= w; y *= w; z *= w; }
        n[0] = x; n[1] = y; n[2] = z;
    }

    private static ReadOnlySpan<byte> Need(ReadOnlySpan<byte> b, int offset, int length, string what) =>
        offset >= 0 && length >= 0 && offset + (long)length <= b.Length ? b.Slice(offset, length)
            : throw new InvalidDataException($"RMV2 truncated {what} at {offset}+{length}");

    private static string Str(ReadOnlySpan<byte> b)
    {
        var end = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? b : b[..end]);
    }

    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static float F32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
    private static float H(ReadOnlySpan<byte> b, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(b[o..]));
}
