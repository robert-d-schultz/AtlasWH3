using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Models;

/// <summary>
/// Positions and triangles of one LOD of any RMV2 (versions 6-8), for rasterising props. Follows the layout in
/// Z:\Claude\Animations\tw3k_animforge\crates\af-formats\src\rmv2.rs:
///   file header 140, LOD headers (28 bytes v7/8, 20 v6: mesh count, vertex bytes, index bytes, first mesh offset, ...)
///   mesh header 80 (material, render flags, section size, vertex offset, vertex count, index offset, index count,
///   bbox, shader), material (256 bytes for id 49, 88 for 101, otherwise 860 with vertex format at 0 and pivot at 548)
///   vertex positions: 4 x f16 (x, y, z, w; scaled by w when non-zero) for the half-float formats, 3 x f32 for
///   collision (stride 24) and TerrainTiles (stride 16); indices are u16.
/// </summary>
public sealed class RigidModelGeometry
{
    public float[] Positions { get; }   // x, y, z per vertex
    public int[] Indices { get; }       // triangle list over Positions

    private RigidModelGeometry(float[] positions, int[] indices)
    {
        Positions = positions;
        Indices = indices;
    }

    public int TriangleCount => Indices.Length / 3;

    /// <summary>Reads LOD <paramref name="lod"/> (clamped to the last LOD), all meshes merged.</summary>
    public static RigidModelGeometry Read(ReadOnlySpan<byte> b, int lod = 0)
    {
        if (b.Length < 140 || !b[..4].SequenceEqual("RMV2"u8)) throw new InvalidDataException("Not an RMV2 file.");
        var version = U32(b, 4);
        var lodCount = (int)U32(b, 8);
        if (version is < 6 or > 8 || lodCount is < 1 or > 64) throw new InvalidDataException($"Unsupported RMV2 (v{version}, {lodCount} LODs).");
        lod = Math.Clamp(lod, 0, lodCount - 1);
        var lodSize = version >= 7 ? 28 : 20;
        var lh = 140 + lod * lodSize;
        var meshCount = (int)U32(b, lh);
        var offset = (int)U32(b, lh + 12);

        var positions = new List<float>();
        var indices = new List<int>();
        for (var m = 0; m < meshCount; m++)
        {
            var material = BinaryPrimitives.ReadUInt16LittleEndian(b[offset..]);
            var sectionSize = (int)U32(b, offset + 4);
            var vertexOffset = (int)U32(b, offset + 8);
            var vertexCount = (int)U32(b, offset + 12);
            var indexOffset = (int)U32(b, offset + 16);
            var indexCount = (int)U32(b, offset + 20);
            if (sectionSize < 80) throw new InvalidDataException("RMV2 mesh section smaller than its header.");

            float px = 0, py = 0, pz = 0;
            if (material is not 49 and not 101)
            {
                var mat = offset + 80;
                px = F32(b, mat + 548);
                py = F32(b, mat + 552);
                pz = F32(b, mat + 556);
                if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz)) px = py = pz = 0;
            }

            var baseVertex = positions.Count / 3;
            if (vertexCount > 0)
            {
                var stride = (indexOffset - vertexOffset) / vertexCount;
                var v0 = offset + vertexOffset;
                for (var i = 0; i < vertexCount; i++)
                {
                    var v = b.Slice(v0 + i * stride, stride);
                    float x, y, z;
                    if (stride == 24 || material == 101)
                    {
                        x = F32(v, 0); y = F32(v, 4); z = F32(v, 8);
                        if (stride == 16) { var w = F32(v, 12); if (w > 0) { x *= w; y *= w; z *= w; } }
                    }
                    else
                    {
                        x = H(v, 0); y = H(v, 2); z = H(v, 4);
                        var w = H(v, 6);
                        if (w != 0) { x *= w; y *= w; z *= w; }
                    }
                    positions.Add(x + px);
                    positions.Add(y + py);
                    positions.Add(z + pz);
                }
            }
            var usable = indexCount / 3 * 3;
            var i0 = offset + indexOffset;
            for (var t = 0; t < usable; t += 3)
            {
                int a = BinaryPrimitives.ReadUInt16LittleEndian(b[(i0 + t * 2)..]);
                int c = BinaryPrimitives.ReadUInt16LittleEndian(b[(i0 + t * 2 + 2)..]);
                int d = BinaryPrimitives.ReadUInt16LittleEndian(b[(i0 + t * 2 + 4)..]);
                if (a >= vertexCount || c >= vertexCount || d >= vertexCount) continue; // corrupt triangle
                indices.AddRange([baseVertex + a, baseVertex + c, baseVertex + d]);
            }
            offset += sectionSize;
        }
        return new RigidModelGeometry([.. positions], [.. indices]);
    }

    /// <summary>The geometry path inside a .wsmodel.</summary>
    public static string? WsModelGeometryPath(ReadOnlySpan<byte> wsmodel)
    {
        var text = Encoding.UTF8.GetString(wsmodel);
        var start = text.IndexOf("<geometry>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += "<geometry>".Length;
        var end = text.IndexOf("</geometry>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : text[start..end].Trim();
    }

    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static float F32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
    private static float H(ReadOnlySpan<byte> b, int o) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(b[o..]));
}
