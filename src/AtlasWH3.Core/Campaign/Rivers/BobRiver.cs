using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Core.Campaign.Rivers;

/// <summary>
/// BOB's own river geometry ("Terry file"), reproduced in float32 from tooldatabuilder / warscape
/// (research/rivers/bob_spline.py, bob_mesh.py, bob_file.py, bob_patch.py; docs/native_campaign_build.md "Rivers vs BOB").
/// On main190 every river .rigid_model_v2 is identical to BOB's apart from the bytes BOB leaves uninitialised, and every
/// height patch raster, header and rectangle is identical.
///  - spline (FUN_18016e440, utilitydll SEGMENTED_SPLINE_3): Bézier segments (p_i, p_i + tangent_out, p_i+1 + tangent_in,
///    p_i+1) in float32 world space, degenerate ends replaced by midpoints, length = Σ|B′(u)|/1000 over 1000 steps;
///    samples = optimise_spline(density 20, tolerance 0.02, extras k/8) with BOB's in-place unique that never shrinks
///  - FUN_18015e9e0: 5 vertices per sample at -w/2 .. w/2 across the xz direction, quantised to half floats by BOB's
///    own float→half; index list per sample pair; the in-place snap pass (a vertex inside a triangle's xz takes the
///    nearest corner's position); face normals; triangles touching a vertex whose normal y byte is &lt; 0x82 are dropped
///  - MODEL_PROCESSOR: vertices renumbered by first use, winding flipped, positions relative to the bbox-centre pivot
///  - height patches (bob_terrain FUN_18005eb30): the model rasterised at 16 px per unit into a max-height field
///    (INVALID -50) on a 16-unit grid, cut into 512 x 512 patches
/// Verified on main190 (24 rivers, all yaw 0, terrain_relative false, no reverse_direction) and on vanilla scratch rivers
/// with yaw 30, terrain_relative="true" (no effect) and reverse_direction="true" (2026-10-05).
/// </summary>
public static class BobRiver
{
    // ---------------------------------------------------------------- spline

    private static readonly float[][] Basis = [[-1, 3, -3, 1], [3, -6, 3, 0], [-3, 3, 0, 0], [1, 0, 0, 0]];

    private static void Weights(float u3, float u2, float u1, float u0, Span<float> w)
    {
        for (var r = 0; r < 4; r++)
            w[r] = u3 * Basis[r][0] + u2 * Basis[r][1] + u1 * Basis[r][2] + u0 * Basis[r][3];
    }

    private static void Combine(float[][] p, ReadOnlySpan<float> w, Span<float> o)
    {
        for (var k = 0; k < 3; k++)
            o[k] = w[0] * p[0][k] + w[1] * p[1][k] + w[2] * p[2][k] + w[3] * p[3][k];
    }

    private static void Eval(float[][] p, float u, Span<float> o)
    {
        Span<float> w = stackalloc float[4];
        var u2 = u * u;
        Weights(u * u2, u2, u, 1f, w);
        Combine(p, w, o);
    }

    private static void Deriv(float[][] p, float u, Span<float> o)
    {
        Span<float> w = stackalloc float[4];
        Weights(u * 3f * u, u + u, 1f, 0f, w);
        Combine(p, w, o);
    }

    public sealed class Spline
    {
        public List<float[][]> Segments { get; } = [];
        public List<float> Lengths { get; } = [];
        public List<float> Starts { get; } = [];
        public float Total { get; private set; }

        public void Add(float[] p0, float[] p1, float[] p2, float[] p3)
        {
            var p = new[] { (float[])p0.Clone(), (float[])p1.Clone(), (float[])p2.Clone(), (float[])p3.Clone() };
            bool e01 = p[0].AsSpan().SequenceEqual(p[1]), e23 = p[2].AsSpan().SequenceEqual(p[3]);
            if (e01 && !e23) for (var k = 0; k < 3; k++) p[1][k] = (p[2][k] + p[0][k]) * 0.5f;
            else if (e23 && !e01) for (var k = 0; k < 3; k++) p[2][k] = (p[1][k] + p[3][k]) * 0.5f;
            else if (e01 && e23)
            {
                for (var k = 0; k < 3; k++) p[1][k] = (p[3][k] + p[0][k]) * 0.5f;
                p[2] = (float[])p[1].Clone();
            }
            Segments.Add(p);
            float length;
            if (e01 || e23)
            {
                float d0 = p[0][0] - p[3][0], d1 = p[0][1] - p[3][1], d2 = p[0][2] - p[3][2];
                length = MathF.Sqrt(d1 * d1 + d0 * d0 + d2 * d2);
            }
            else
            {
                Span<float> d = stackalloc float[3];
                float du = 1f / 1000f, u = 0, acc = 0;
                while (u < 1f)
                {
                    Deriv(p, u, d);
                    acc += MathF.Sqrt(d[1] * d[1] + d[0] * d[0] + d[2] * d[2]);
                    u += du;
                }
                length = acc * du;
            }
            Lengths.Add(length);
            Starts.Add(Total);
            Total += length;
        }

        /// <summary>Segment and local u for a spline parameter t (BOB's linear segment search).</summary>
        public (int Segment, float U) Locate(float t)
        {
            float acc = 0;
            var i = Segments.Count - 1;
            for (var k = 0; k < Lengths.Count; k++)
            {
                var ok = acc <= t;
                acc = 1f / Total * Lengths[k] + acc;
                if (ok && t <= acc) { i = k; break; }
            }
            return (i, (Total * t - Starts[i]) / Lengths[i]);
        }

        private void At(float t, Span<float> o)
        {
            float acc = 0;
            var i = Segments.Count - 1;
            for (var k = 0; k < Lengths.Count; k++)
            {
                var ok = acc <= t;
                acc = Lengths[k] / Total + acc;
                if (ok && t <= acc) { i = k; break; }
            }
            Eval(Segments[i], (Total * t - Starts[i]) / Lengths[i], o);
        }

        /// <summary>SEGMENTED_SPLINE_3::optimise_spline(density 20, extras k/8, tolerance 0.02).</summary>
        public List<float> Optimise(float density = 20f, float tolerance = 0.02f)
        {
            var n = density * Total;
            n = n > 0 ? n + 0.5f : n - 0.5f;
            var count = (int)n;
            var output = new List<float> { 0f };
            if (count > 3)
            {
                var step = 1f / (count - 1);
                var last = 1;
                var limit = 1f - tolerance;
                Span<float> a = stackalloc float[3], b = stackalloc float[3], c = stackalloc float[3], d = stackalloc float[3];
                for (var k = 2; k < count; k++)
                {
                    At(output[^1], a); At(last * step, b); At((k - 1) * step, c); At(k * step, d);
                    float v10 = b[0] - a[0], v11 = b[1] - a[1], v12 = b[2] - a[2];
                    float v20 = d[0] - c[0], v21 = d[1] - c[1], v22 = d[2] - c[2];
                    var i1 = 1f / MathF.Sqrt(v11 * v11 + v10 * v10 + v12 * v12);
                    var i2 = 1f / MathF.Sqrt(v21 * v21 + v20 * v20 + v22 * v22);
                    var dot = i2 * v20 * v10 * i1 + i2 * v21 * v11 * i1 + i2 * v22 * v12 * i1;
                    if (dot < limit) { output.Add((k - 1) * step); last = k; }
                }
            }
            output.Add(1f);
            float extra = 0;
            for (var k = 0; k < 7; k++) { output.Add(extra); extra += 1f / 8f; }
            output.Sort();
            // BOB's unique compacts in place but never shrinks the list, so the old tail values stay in
            var first = -1;
            for (var k = 1; k < output.Count; k++) if (output[k] == output[k - 1]) { first = k; break; }
            if (first >= 0)
            {
                var w = first - 1;
                for (var r = first + 1; r < output.Count; r++)
                    if (output[r] != output[w]) output[++w] = output[r];
            }
            return output;
        }
    }

    /// <summary>The spline BOB builds for a river entity: world = turn(float(local)) + float(position).</summary>
    public static Spline BuildSpline(RiverSpline river)
    {
        // yaw (ECTransform rotation y, degrees): float cos/sin, rotated in float, then the position added; the tangents
        // turn the same way (vanilla scratch river rotated 30°, byte-identical to BOB, 2026-10-05)
        var yaw = river.YawDegrees * Math.PI / 180;
        float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw);
        float[] Turn((double X, double Y, double Z) t)
        {
            float x = (float)t.X, y = (float)t.Y, z = (float)t.Z;
            return yaw == 0 ? [x, y, z] : [x * c + z * s, y, -x * s + z * c];
        }
        float[] World((double X, double Y, double Z) p)
        {
            var q = Turn(p);
            return [q[0] + (float)river.Position.X, q[1] + (float)river.Position.Y, q[2] + (float)river.Position.Z];
        }
        float[] Control(float[] w, (double X, double Y, double Z) t) { var q = Turn(t); return [w[0] + q[0], w[1] + q[1], w[2] + q[2]]; }
        var spline = new Spline();
        var points = RiverPointsInOrder(river);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var w0 = World(a.Position);
            var w3 = World(b.Position);
            spline.Add(w0, Control(w0, a.TangentOut), Control(w3, b.TangentIn), w3);
        }
        return spline;
    }

    /// <summary>The spline points in the order BOB walks them: reverse_direction="true" runs the spline backwards
    /// (last point first, each point's tangents swapped).</summary>
    public static IReadOnlyList<RiverPoint> RiverPointsInOrder(RiverSpline river) => !river.Reverse ? river.Points
        : river.Points.Reverse().Select(p => p with { TangentIn = p.TangentOut, TangentOut = p.TangentIn }).ToList();

    // ---------------------------------------------------------------- mesh (FUN_18015e9e0)

    /// <summary>tooldatabuilder FUN_1803811a0: float → half bits with BOB's own rounding.</summary>
    public static ushort HalfBits(float x)
    {
        var u = BitConverter.SingleToUInt32Bits(x);
        var sign = (u >> 16) & 0x8000;
        var e = (u >> 23) & 0xff;
        uint h;
        if (e > 0x8e) h = sign | 0x7c00;
        else if (e > 0x70)
        {
            var v = u & 0x7fffffff;
            v += ((v - 1) & v) & 0x1fff;
            h = (((v >> 13) & 0x3ff) | ((((v >> 23) + 0x10) * 0x400) & 0xffff) | sign) & 0xffff;
        }
        else if (e > 0x66)
        {
            var m = u & 0x7fffff;
            var sh = (int)((e + 0x99) & 0x1f);
            if (((m << sh) & 0x3fffff) > 0x200)
            {
                var r = (0x7fffffu >> sh) & m;
                m += (r - 1) & r;
            }
            m = (m + 0x800000) >> (int)((0x7e - e) & 0x1f);
            h = (m & 0xffff) | sign;
        }
        else h = sign | ((u & 0x7fffffff) > 0x33000400 ? 1u : 0u);
        return (ushort)h;
    }

    private static float HalfToFloat(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);

    /// <summary>The 32-byte vertices and the index list FUN_18015e9e0 emits for one river.</summary>
    public sealed record RawMesh(byte[] Vertices, List<int> Indices)
    {
        public int VertexCount => Vertices.Length / 32;
    }

    /// <summary>bounds = (min x, min z, max x, max z) of the map (map_data.esf header) for the world uv.</summary>
    public static RawMesh BuildRaw(Spline spline, IReadOnlyList<float> pointWidths, (float X0, float Z0, float X1, float Z1) bounds)
    {
        var ts = spline.Optimise();
        var n = ts.Count * 5;
        var x = new float[n]; var z = new float[n]; var y = new float[n]; var off = new float[n]; var t = new float[n];
        var tan = new float[n, 3];
        Span<float> p = stackalloc float[3], d = stackalloc float[3];
        for (var s = 0; s < ts.Count; s++)
        {
            var (i, u) = spline.Locate(ts[s]);
            var uc = Math.Clamp(u, 0f, 1f);
            float wa = pointWidths[i], wb = pointWidths[i + 1];
            var width = (wb - wa) * uc + wa;
            Eval(spline.Segments[i], u, p);
            Deriv(spline.Segments[i], u, d);
            var l2 = d[2] * d[2] + d[0] * d[0];
            float dx = d[0], dz = d[2];
            if (l2 > 0) { var inv = 1f / MathF.Sqrt(l2); dx = d[0] * inv; dz = inv * d[2]; }
            var o0 = width * -0.5f;
            var span = width * 0.5f - o0;
            for (var j = 0; j < 5; j++)
            {
                var v = s * 5 + j;
                off[v] = j * 0.25f * span + o0;
                x[v] = dz * off[v] + p[0];
                z[v] = -(dx * off[v]) + p[2];
                y[v] = p[1];
                t[v] = ts[s];
                tan[v, 0] = d[0]; tan[v, 1] = d[1]; tan[v, 2] = d[2];
            }
        }
        // position halves (x, y, z, w) as 4 ushorts per vertex
        var pos = new ushort[n, 4];
        for (var v = 0; v < n; v++) { pos[v, 0] = HalfBits(x[v]); pos[v, 1] = HalfBits(y[v]); pos[v, 2] = HalfBits(z[v]); pos[v, 3] = 2; }
        var idx = new List<int>();
        for (var s = 0; s + 1 < ts.Count; s++)
            for (var j = 0; j < 4; j++)
            {
                var c = 5 * s + 6 + j;
                idx.AddRange([c - 1, c - 6, c, c - 6, c - 5, c]);
            }
        float H(int v, int k) => HalfToFloat(pos[v, k]);

        // snap pass: in place, triangle by triangle
        for (var q = 0; q < idx.Count; q += 3)
        {
            int ia = idx[q], ib = idx[q + 1], ic = idx[q + 2];
            float ax = H(ia, 0), ay = H(ia, 1), az = H(ia, 2);
            float bx = H(ib, 0), by = H(ib, 1), bz = H(ib, 2);
            float cx = H(ic, 0), cy = H(ic, 1), cz = H(ic, 2);
            for (var v = 0; v < n; v++)
            {
                if (v == ia || v == ib || v == ic) continue;
                float px = H(v, 0), pz = H(v, 2);
                if (!Contains(px, pz, ax, az, bx, bz, cx, cz)) continue;
                var d0 = MathF.Sqrt((pz - az) * (pz - az) + (px - ax) * (px - ax));
                var sel = 0;
                if (float.MaxValue <= d0) { sel = -1; d0 = float.MaxValue; }
                var d1 = MathF.Sqrt((pz - bz) * (pz - bz) + (px - bx) * (px - bx));
                var m = d1; var s17 = 1;
                if (d0 <= d1) { m = d0; s17 = sel; }
                var k = 2;
                if (m <= MathF.Sqrt((pz - cz) * (pz - cz) + (px - cx) * (px - cx))) k = s17;
                var src = k == 0 ? ia : k == 1 ? ib : ic;
                for (var e = 0; e < 4; e++) pos[v, e] = pos[src, e];
            }
        }
        // face normals (only those with y != 0 accumulate)
        var acc = new float[n, 3];
        for (var q = 0; q < idx.Count; q += 3)
        {
            int ia = idx[q], ib = idx[q + 1], ic = idx[q + 2];
            float xa = H(ia, 0), ya = H(ia, 1), za = H(ia, 2);
            float xb = H(ib, 0), yb = H(ib, 1), zb = H(ib, 2);
            float xc = H(ic, 0), yc = H(ic, 1), zc = H(ic, 2);
            var nx = (zb - za) * (yc - ya) - (yb - ya) * (zc - za);
            var nz = (xc - xa) * (yb - ya) - (xb - xa) * (yc - ya);
            var ny = (xb - xa) * (zc - za) - (xc - xa) * (zb - za);
            var l = ny * ny + nx * nx + nz * nz;
            float fx = 0, fy = 1, fz = 0;
            if (l > 0) { var r = 1f / MathF.Sqrt(l); fx = r * nx; fy = r * ny; fz = r * nz; }
            if (fy == 0) continue;
            foreach (var i in (ReadOnlySpan<int>)[ia, ib, ic])
            {
                acc[i, 0] += fx; acc[i, 1] = fy + acc[i, 1]; acc[i, 2] = fz + acc[i, 2];
            }
        }
        var raw = new byte[n * 32];
        var tk = spline.Total * 0.1f;
        for (var v = 0; v < n; v++)
        {
            var o = raw.AsSpan(v * 32, 32);
            for (var e = 0; e < 4; e++) BinaryPrimitives.WriteUInt16LittleEndian(o[(e * 2)..], pos[v, e]);
            BinaryPrimitives.WriteUInt16LittleEndian(o[8..], HalfBits(off[v] * 0.1f));
            BinaryPrimitives.WriteUInt16LittleEndian(o[10..], HalfBits(tk * t[v]));
            BinaryPrimitives.WriteUInt16LittleEndian(o[12..], HalfBits((x[v] - bounds.X0) / (bounds.X1 - bounds.X0)));
            BinaryPrimitives.WriteUInt16LittleEndian(o[14..], HalfBits((z[v] - bounds.Z0) / (bounds.Z1 - bounds.Z0)));
            float ax = acc[v, 0], ay = acc[v, 1], az = acc[v, 2];
            var l = ax * ax + ay * ay + az * az;
            float nx = 0, ny = 1, nz = 0;
            if (l > 0) { var r = 1f / MathF.Sqrt(l); nx = ax * r; ny = ay * r; nz = az * r; }
            float tx = tan[v, 0], ty = tan[v, 1], tz = tan[v, 2];
            var rt = 1f / MathF.Sqrt(ty * ty + tx * tx + tz * tz);
            ty *= rt; tz *= rt; tx *= rt;
            o[16] = EncodeNormal(nz); o[17] = EncodeNormal(ny); o[18] = EncodeNormal(nx); o[19] = 0xff;
            o[20] = EncodeUnit(tz); o[21] = EncodeUnit(ty); o[22] = EncodeUnit(tx); o[23] = 0xff;
            float bx = ny * tz - nz * ty, by = nz * tx - nx * tz, bz = nx * ty - ny * tx;
            o[24] = EncodeUnit(bz); o[25] = EncodeUnit(by); o[26] = EncodeUnit(bx); o[27] = 0xff;
            o[28] = o[29] = o[30] = o[31] = 0xff;
        }
        // drop triangles touching a vertex whose normal y byte is < 0x82
        var kept = new List<int>(idx.Count);
        for (var q = 0; q < idx.Count; q += 3)
        {
            if (raw[idx[q] * 32 + 17] < 0x82 || raw[idx[q + 1] * 32 + 17] < 0x82 || raw[idx[q + 2] * 32 + 17] < 0x82) continue;
            kept.AddRange([idx[q], idx[q + 1], idx[q + 2]]);
        }
        return new RawMesh(raw, kept);
    }

    private static byte EncodeNormal(float v) => (byte)(int)(v * 0.5f * 255f + 127.5f);
    private static byte EncodeUnit(float v) => (byte)(int)((v + 1f) * 127.5f);

    /// <summary>WARSCAPE::contains(P, A, B, C) on (x, z): crossing-number test in BOB's operation order.</summary>
    private static bool Contains(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var r = false;
        if ((py <= ay) != (py <= cy)) r = px <= (py - ay) * (cx - ax) / (cy - ay) + ax;
        if ((py <= by) != (py <= ay) && px <= (py - by) * (ax - bx) / (ay - by) + bx) r = !r;
        if ((py <= cy) != (py <= by) && px <= (bx - cx) * (py - cy) / (by - cy) + cx) r = !r;
        return r;
    }

    // ---------------------------------------------------------------- file (FUN_180146460, cleaner, writer)

    public static RigidModelV2 ToModel(RawMesh mesh)
    {
        var order = new List<int>();
        var remap = new Dictionary<int, int>();
        foreach (var i in mesh.Indices)
            if (!remap.ContainsKey(i)) { remap[i] = order.Count; order.Add(i); }
        var n = order.Count;
        var pos = new float[n, 3];
        float[] lo = [float.MaxValue, float.MaxValue, float.MaxValue], hi = [float.MinValue, float.MinValue, float.MinValue];
        for (var v = 0; v < n; v++)
            for (var k = 0; k < 3; k++)
            {
                var f = HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(mesh.Vertices.AsSpan(order[v] * 32 + k * 2)));
                pos[v, k] = f;
                lo[k] = Math.Min(lo[k], f); hi[k] = Math.Max(hi[k], f);
            }
        var pivot = new float[3];
        for (var k = 0; k < 3; k++) pivot[k] = (lo[k] + hi[k]) * 0.5f;
        var vertices = new byte[n * 48];
        for (var v = 0; v < n; v++)
        {
            var o = vertices.AsSpan(v * 48, 48);
            var r = mesh.Vertices.AsSpan(order[v] * 32, 32);
            for (var k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(o[(k * 4)..], pos[v, k] - pivot[k]);
            BinaryPrimitives.WriteSingleLittleEndian(o[12..], 1f);
            for (var k = 0; k < 4; k++)
                BinaryPrimitives.WriteSingleLittleEndian(o[(16 + k * 4)..], HalfToFloat(BinaryPrimitives.ReadUInt16LittleEndian(r[(8 + k * 2)..])));
            for (var g = 0; g < 3; g++)
                for (var c = 0; c < 3; c++)
                    o[32 + 4 * g + c] = Reencode(r[16 + 4 * g + c]);
        }
        var indices = new ushort[mesh.Indices.Count];
        for (var q = 0; q < mesh.Indices.Count; q += 3)
        {
            indices[q] = (ushort)remap[mesh.Indices[q]];
            indices[q + 1] = (ushort)remap[mesh.Indices[q + 2]];
            indices[q + 2] = (ushort)remap[mesh.Indices[q + 1]];
        }
        var model = RigidModelV2.NewRiver((pivot[0], pivot[1], pivot[2]));
        // BOB's shader block tail (two BOB runs on main190; vanilla has other stale bytes here)
        model.Shader.AsSpan(16).Clear();
        model.Shader[24] = 0x9e; model.Shader[25] = 0xd4;
        model.Vertices = vertices;
        model.Indices = indices;
        model.Bounds = [lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]];
        return model;
    }

    /// <summary>FUN_180146460 decodes b/255·2−1, the writer re-encodes trunc((v+1)·127.5).</summary>
    private static byte Reencode(byte b)
    {
        var t = b * (1f / 255f);
        return EncodeUnit(t + t - 1f);
    }

    // ---------------------------------------------------------------- height patches (bob_terrain FUN_18005eb30)

    private const float InvalidHeight = -50f;

    public static List<(string Name, Raster<ushort> Raster, float[] Header, float MinX, float MinZ, float MaxX, float MaxZ)>
        HeightPatches(RigidModelV2 model, int riverNumber)
    {
        var pivot = (X: BitConverter.ToSingle(model.MaterialBlock, 0x224), Y: BitConverter.ToSingle(model.MaterialBlock, 0x228),
                     Z: BitConverter.ToSingle(model.MaterialBlock, 0x22C));
        var n = model.VertexCount;
        var vx = new float[n]; var vy = new float[n]; var vz = new float[n];
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (var i = 0; i < n; i++)
        {
            vx[i] = BitConverter.ToSingle(model.Vertices, i * 48) + pivot.X;
            vy[i] = BitConverter.ToSingle(model.Vertices, i * 48 + 4) + pivot.Y;
            vz[i] = BitConverter.ToSingle(model.Vertices, i * 48 + 8) + pivot.Z;
            minX = Math.Min(minX, vx[i]); maxX = Math.Max(maxX, vx[i]);
            minZ = Math.Min(minZ, vz[i]); maxZ = Math.Max(maxZ, vz[i]);
        }
        var ax = MathF.Floor(minX * 0.0625f); var az = MathF.Floor(minZ * 0.0625f);
        var bx = MathF.Ceiling(maxX * 0.0625f); var bz = MathF.Ceiling(maxZ * 0.0625f);
        float ox = ax * 16f, oz = az * 16f;
        var width = (int)(bx * 256f - ax * 256f);
        var height = (int)(bz * 256f - az * 256f);
        var field = new float[width * height];
        Array.Fill(field, InvalidHeight);
        float m3 = ox * -16f, m11 = oz * -16f;
        var px = new float[n]; var py = new float[n];
        for (var i = 0; i < n; i++)
        {
            px[i] = vy[i] * 0f + vx[i] * 16f + vz[i] * 0f + m3;
            py[i] = vy[i] * 0f + vx[i] * 0f + vz[i] * 16f + m11;
        }
        var idx = model.Indices;
        for (var q = 0; q + 2 < idx.Length; q += 3)
        {
            int ia = idx[q], ib = idx[q + 1], ic = idx[q + 2];
            float axp = px[ia], ayp = py[ia], bxp = px[ib], byp = py[ib], cxp = px[ic], cyp = py[ic];
            var x0 = Math.Min((int)axp, Math.Min((int)bxp, (int)cxp)) - 1;
            var x1 = Math.Max((int)axp, Math.Max((int)bxp, (int)cxp)) + 1;
            var y0 = Math.Min((int)ayp, Math.Min((int)byp, (int)cyp)) - 1;
            var y1 = Math.Max((int)ayp, Math.Max((int)byp, (int)cyp)) + 1;
            if (x1 < 0 || width < x0 || y1 < 0 || height < y0) continue;
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, width); y1 = Math.Min(y1, height);
            var area = MathF.Abs((cxp - axp) * (byp - ayp) - (cyp - ayp) * (bxp - axp));
            for (var y = y0; y < y1; y++)
            {
                float fy = y;
                for (var x = x0; x < x1; x++)
                {
                    float fx = x;
                    if (!Inside(fx, fy, axp, ayp, bxp, byp, cxp, cyp)) continue;
                    var inv = 2f / area;
                    var wc = MathF.Abs((fy - ayp) * (bxp - axp) - (fx - axp) * (byp - ayp)) * 0.5f * inv;
                    var wb = MathF.Abs((fy - ayp) * (cxp - axp) - (fx - axp) * (cyp - ayp)) * 0.5f * inv;
                    var wa = 1f - wb - wc;
                    var h = vy[ib] * wb + vy[ia] * wa + vy[ic] * wc;
                    ref var cell = ref field[y * width + x];
                    if (!(h <= cell)) cell = h;
                }
            }
        }
        var result = new List<(string, Raster<ushort>, float[], float, float, float, float)>();
        var block = new float[512 * 512];
        for (var z = 0; z < height; z += 512)
            for (var x = 0; x < width; x += 512)
            {
                Array.Fill(block, InvalidHeight);
                var any = false;
                for (var r = 0; r < 512 && z + r < height; r++)
                    for (var c = 0; c < 512 && x + c < width; c++)
                    {
                        var h = field[(z + r) * width + x + c];
                        block[r * 512 + c] = h;
                        any |= h != InvalidHeight;
                    }
                if (!any) continue;
                float lo = block.Min(), hi = block.Max();
                var range = lo == hi ? 1f : hi - lo;
                var scale = 1f / range;
                var raster = new Raster<ushort>(512, 512);
                for (var i = 0; i < block.Length; i++) raster.Data[i] = (ushort)(int)((block[i] - lo) * scale * 65535f);
                result.Add(($"river_{riverNumber}_patch_{x}x{z}", raster, [0, lo, 0, 0, hi, 0],
                    x * 0.0625f + ox, z * 0.0625f + oz, (x + 512) * 0.0625f + ox, (z + 512) * 0.0625f + oz));
            }
        return result;
    }

    /// <summary>warscape FUN_180476ea0: pixel (x, y) inside triangle (crossing test).</summary>
    internal static bool Inside(float X, float Y, float ax, float ay, float bx, float by, float cx, float cy)
    {
        var r = false;
        bool ra = ay > Y, rc = cy > Y, rb = by > Y;
        if (ra != rc) r = (Y - ay) * (cx - ax) / (cy - ay) + ax > X;
        if (rb != ra && X < (Y - by) * (ax - bx) / (ay - by) + bx) r = !r;
        if (rc != rb && X < (bx - cx) * (Y - cy) / (by - cy) + cx) r = !r;
        return r;
    }

    // ---------------------------------------------------------------- map bounds

    /// <summary>The map rectangle BOB passes to process_river_spline: map_data.esf's header (min x, min y, max x, max y),
    /// else the campaign_map_playable_areas row.</summary>
    public static (float X0, float Z0, float X1, float Z1)? MapBounds(ProjectPaths paths)
    {
        var areas = Path.Combine(paths.AssemblyKitRoot, "raw_data", "EmpireDesignData", "campaign_map_playable_areas.xml");
        if (!File.Exists(areas)) return null;
        var row = XDocument.Load(areas).Descendants("campaign_map_playable_areas")
            .FirstOrDefault(e => (string?)e.Element("mapname") == paths.MapName);
        if (row is null) return null;
        float F(string name) => float.Parse((string?)row.Element(name) ?? "0", CultureInfo.InvariantCulture);
        (float, float, float, float) xml = (F("minx"), F("miny"), F("maxx"), F("maxy"));
        var esf = Path.Combine(paths.AkWorkingCampaignMapDir, "map_data.esf");
        if (!File.Exists(esf)) return xml;
        var b = new byte[4096];
        int count;
        using (var s = File.OpenRead(esf)) count = s.Read(b, 0, b.Length);
        for (var i = 0; i + 18 <= count; i++)
        {
            if (b[i] != 0x0c || b[i + 9] != 0x0c) continue;
            float x0 = BitConverter.ToSingle(b, i + 1), y0 = BitConverter.ToSingle(b, i + 5);
            float x1 = BitConverter.ToSingle(b, i + 10), y1 = BitConverter.ToSingle(b, i + 14);
            // matched on min x, min y, max x as HexRegionLookup.EsfBounds: the header's max y can lag the row
            // (main190: 873.79541 in map_data.esf, which BOB uses, vs 874.185 in the row)
            if (Math.Abs(x0 - xml.Item1) <= 0.01f && Math.Abs(y0 - xml.Item2) <= 0.01f && Math.Abs(x1 - xml.Item3) <= 0.01f)
                return (x0, y0, x1, y1);
        }
        return xml;
    }
}
