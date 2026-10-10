using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Core.Campaign.Rivers;

/// <summary>
/// WH3's river bake (BOB "Terry file": bob_terrain → QTU::to_warscape_spline → TOOLDATABUILDER::process_river_spline →
/// WARSCAPE::tessellate_spline, MODEL_PROCESSOR), in float32 as BOB does it. On IEE (60 rivers) and Old World (127) every
/// models\river_&lt;id&gt;.wsmodel.rigid_model_v2 is identical to BOB's apart from the two bytes BOB leaves uninitialised
/// (research/rivers/wh3_tessellate.py; docs/native_campaign_build.md "Rivers (WH3)"). warscape.modder.x64.dll RVAs:
///  - the spline is in the river entity's frame, its heights taken relative to the stored first point's (the prop is
///    raised by it; x and z stay as stored: 15 IEE rivers have first points 1e-6 off in x/z); reverse_direction walks
///    the points backwards, each point's tangents swapped
///  - 0x7109a0: one 4-D Bézier (x, y, z, width) per point pair: p_i, p_i + tangent_out_i, p_i+1 + tangent_in_i+1,
///    p_i+1; widths clamped to ≥ 0.01, inner controls 0.75·w0 + 0.25·w1 and 0.25·w0 + 0.75·w1
///  - 0x736b60: a segment ending where it starts is dropped; a degenerate end takes a midpoint and the straight
///    length; else the length is a 1001-step float32 rectangle sum of |B′(u)| over xyz (3K's)
///  - 0x741410: power-basis weights, w0 = (d − 3c) + (−a + 3b); each channel (P0·w0 + P1·w1) + (P2·w2 + P3·w3)
///  - 0x7111b0: columns = ⌈clamp(width(0) / 1.0, 1, 10)⌉ + 1, n = ⌈max(L / 0.2, 1)⌉ steps, row k at t = k·(1/n)
///  - 0x733340: P = eval(t), D = deriv(t), (dx, dz) normalised; off = (j / (cols − 1))·w − w/2;
///    vertex (dz·off + Px, Py, −(off·dx) + Pz), u = 0.1·off, v = 0.1·t·L
///  - 0x733980: per quad (A, B, C), (C, B, D); 0x710be0: normalised face normals summed per vertex, tangent n × X
///    (below 0.001: (ny, −nx, 0)), bitangent n × t; 0x71fc60: bytes trunc((v + 1)·127.5) in z, y, x order
///  - MODEL_PROCESSOR (3K's): vertices renumbered by first use, winding flipped, positions relative to the box centre,
///    normals decoded and re-encoded, the w bytes and colour 0
/// </summary>
public static class Wh3River
{
    /// <summary>DYNAMIC_SEGMENTED_SPLINE +0x10, +0x14, +0x18 as to_warscape_spline sets them for campaign rivers (bob_terrain
    /// passes 0.2): column step 5 × 0.2, row step 0.2, uv scale 0.1.</summary>
    public const float ColumnStep = 1f, RowStep = 0.2f, UvScale = 0.1f;

    /// <summary>tessellate_spline fails (BOB writes no model) from this many vertices on.</summary>
    public const int MaxVertices = 0xFFFF;

    public readonly record struct Point(float X, float Y, float Z, float InX, float InY, float InZ, float OutX, float OutY, float OutZ, float Width);

    /// <summary>One ECRiverSpline, in the river entity's frame as stored.</summary>
    public sealed record Spline(bool Reverse, string Material, IReadOnlyList<Point> Points)
    {
        /// <summary>The stored first point's height: BOB bakes the heights relative to it and raises the prop by it
        /// (Old World's river 19261f91ac8e913, first point at y -0.757).</summary>
        public float BaseHeight => Points.Count == 0 ? 0 : Points[0].Y;
    }

    /// <summary>The ECRiverSpline of an entity, or null.</summary>
    public static Spline? Read(XElement entity)
    {
        var spline = entity.Element("ECRiverSpline");
        if (spline is null) return null;
        var points = spline.Descendants("point").Select(p =>
        {
            var (x, y, z) = V3((string?)p.Attribute("position"));
            var (ix, iy, iz) = V3((string?)p.Attribute("tangent_in"));
            var (ox, oy, oz) = V3((string?)p.Attribute("tangent_out"));
            return new Point(x, y, z, ix, iy, iz, ox, oy, oz, F((string?)p.Attribute("width") ?? "1"));
        }).ToList();
        return new Spline((string?)spline.Attribute("reverse_direction") == "true", (string?)spline.Attribute("material") ?? "", points);
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static (float, float, float) V3(string? s)
    {
        var p = (s ?? "0,0,0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(F).ToArray();
        return (p[0], p[1], p[2]);
    }

    // ---------------------------------------------------------------- 4-D spline (x, y, z, width)

    private readonly record struct V4(float X, float Y, float Z, float W)
    {
        public bool SameXyz(V4 o) => X == o.X && Y == o.Y && Z == o.Z;
        public static V4 Mid(V4 a, V4 b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f, (a.W + b.W) * 0.5f);
    }

    private static (float, float, float, float) Weights(float a, float b, float c, float d) => (
        (d * 1f + c * -3f) + (a * -1f + b * 3f),
        (d * 0f + c * 3f) + (a * 3f + b * -6f),
        (d * 0f + c * 0f) + (a * -3f + b * 3f),
        (a * 1f + b * 0f) + (c * 0f + 0f * d));

    private static V4 Combine(V4[] p, (float W0, float W1, float W2, float W3) w) => new(
        (p[0].X * w.W0 + p[1].X * w.W1) + (p[2].X * w.W2 + p[3].X * w.W3),
        (p[0].Y * w.W0 + p[1].Y * w.W1) + (p[2].Y * w.W2 + p[3].Y * w.W3),
        (p[0].Z * w.W0 + p[1].Z * w.W1) + (p[2].Z * w.W2 + p[3].Z * w.W3),
        (p[0].W * w.W0 + p[1].W * w.W1) + (p[2].W * w.W2 + p[3].W * w.W3));

    private static V4 EvalU(V4[] p, float u)
    {
        var u2 = u * u;
        return Combine(p, Weights(u2 * u, u2, u, 1f));
    }

    private static V4 DerivU(V4[] p, float u) => Combine(p, Weights(u * 3f * u, u + u, 1f, 0f));

    private sealed class Spline4
    {
        public readonly List<V4[]> Segments = [];
        public readonly List<float> Lengths = [], Starts = [];
        public float Total;

        public void Add(V4 p0, V4 p1, V4 p2, V4 p3)
        {
            if (p3.SameXyz(p0)) return;
            bool e01 = p0.SameXyz(p1), e23 = p2.SameXyz(p3);
            if (e01 && !e23) p1 = V4.Mid(p2, p0);
            else if (e23 && !e01) p2 = V4.Mid(p1, p3);
            else if (e01 && e23) p1 = p2 = V4.Mid(p3, p0);
            V4[] seg = [p0, p1, p2, p3];
            Segments.Add(seg);
            float length;
            if (e01 || e23)
            {
                float dx = p0.X - p3.X, dy = p0.Y - p3.Y, dz = p0.Z - p3.Z;
                length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            else
            {
                float du = 1f / 1000f, u = 0, acc = 0;
                do
                {
                    var d = DerivU(seg, u);
                    u += du;
                    acc += MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                } while (u < 1f);
                length = acc * du;
            }
            Lengths.Add(length);
            Starts.Add(Total);
            Total = length + Total;
        }

        /// <summary>0x73d1e0 / 0x741300: t wrapped into [0, 1], the segment by its share of the length, local u.</summary>
        private (V4[] Segment, float U) Locate(float t)
        {
            if (MathF.Abs(t) > 1f) t %= 1f;
            if (t < 0) t += 1f;
            var i = 0;
            if (Total != 0)
            {
                var inv = 1f / Total;
                float acc = 0;
                i = Segments.Count - 1;
                for (var k = 0; k < Lengths.Count; k++)
                {
                    var ok = t >= acc;
                    acc = inv * Lengths[k] + acc;
                    if (ok && t <= acc) { i = k; break; }
                }
            }
            return (Segments[i], (Total * t - Starts[i]) / Lengths[i]);
        }

        public V4 Eval(float t) { var (s, u) = Locate(t); return EvalU(s, u); }
        public V4 Deriv(float t) { var (s, u) = Locate(t); return DerivU(s, u); }
    }

    private static Spline4 BuildSpline(Spline river)
    {
        var points = river.Points;
        var oy = river.BaseHeight;
        var walk = river.Reverse
            ? points.Reverse().Select(p => p with { InX = p.OutX, InY = p.OutY, InZ = p.OutZ, OutX = p.InX, OutY = p.InY, OutZ = p.InZ }).ToList()
            : points.ToList();
        static float Clamp(float w) => w > 0.01f ? w : 0.01f;
        var spline = new Spline4();
        for (var i = 0; i + 1 < walk.Count; i++)
        {
            Point a = walk[i], b = walk[i + 1];
            float ax = a.X, ay = a.Y - oy, az = a.Z, bx = b.X, by = b.Y - oy, bz = b.Z;
            float w0 = Clamp(a.Width), w1 = Clamp(b.Width);
            spline.Add(new V4(ax, ay, az, w0),
                       new V4(ax + a.OutX, ay + a.OutY, az + a.OutZ, w0 * 0.75f + w1 * 0.25f),
                       new V4(bx + b.InX, by + b.InY, bz + b.InZ, w0 * 0.25f + w1 * 0.75f),
                       new V4(bx, by, bz, w1));
        }
        return spline;
    }

    // ---------------------------------------------------------------- mesh

    private static int Ceil(float x)
    {
        var i = (int)x;
        return i == x ? i : i + (x > 0 ? 1 : 0);
    }

    /// <summary>A tessellated vertex before the model processor: position, offset across, distance along, n, t, b.</summary>
    private struct Vertex
    {
        public float X, Y, Z, Off, Dist;
        public float Nx, Ny, Nz, Tx, Ty, Tz, Bx, By, Bz;
    }

    /// <summary>The baked model, or null when the spline has no segment (fewer than 2 distinct points) or too many
    /// vertices for BOB (<see cref="MaxVertices"/>).</summary>
    public static RigidModelV2? Build(Spline river)
    {
        var spline = BuildSpline(river);
        if (spline.Segments.Count == 0 || spline.Total == 0) return null;
        var cols = Ceil(MathF.Min(MathF.Max(spline.Eval(0).W / ColumnStep, 1f), 10f)) + 1;
        var n = Ceil(MathF.Max(spline.Total / RowStep, 1f));
        if ((long)(n + 1) * cols >= MaxVertices) return null;
        var inv = 1f / n;
        var v = new Vertex[(n + 1) * cols];
        for (var k = 0; k <= n; k++)
        {
            var t = k == 0 ? 0f : k * inv;
            var p = spline.Eval(t);
            var d = spline.Deriv(t);
            float dx = d.X, dz = d.Z;
            var l2 = dz * dz + dx * dx;
            if (l2 > 0)
            {
                var r = 1f / MathF.Sqrt(l2);
                dz = r * dz;
                dx = r * dx;
            }
            var half = p.W * 0.5f;
            for (var j = 0; j < cols; j++)
            {
                var off = (float)j / (cols - 1) * p.W - half;
                v[k * cols + j] = new Vertex { X = dz * off + p.X, Y = p.Y, Z = -(off * dx) + p.Z, Off = off, Dist = t * spline.Total };
            }
        }
        var idx = new List<int>(n * (cols - 1) * 6);
        for (var k = 1; k <= n; k++)
            for (var j = 0; j + 1 < cols; j++)
            {
                int a = (k - 1) * cols + j, c = k * cols + j;
                idx.AddRange([a, a + 1, c, c, a + 1, c + 1]);
            }
        Normals(v, idx);
        return ToModel(v, idx);
    }

    private static void Normals(Vertex[] v, List<int> idx)
    {
        var acc = new float[v.Length * 3];
        for (var q = 0; q < idx.Count; q += 3)
        {
            int i0 = idx[q], i1 = idx[q + 1], i2 = idx[q + 2];
            float e1x = v[i1].X - v[i0].X, e1y = v[i1].Y - v[i0].Y, e1z = v[i1].Z - v[i0].Z;
            float e2x = v[i2].X - v[i0].X, e2y = v[i2].Y - v[i0].Y, e2z = v[i2].Z - v[i0].Z;
            float nx = 0, ny = 1, nz = 0;
            var l1 = e1y * e1y + e1x * e1x + e1z * e1z;
            var l2 = l1 > 0 ? e2y * e2y + e2x * e2x + e2z * e2z : 0;
            if (l1 > 0 && l2 > 0)
            {
                float r2 = 1f / MathF.Sqrt(l2), r1 = 1f / MathF.Sqrt(l1);
                float bx = r2 * e2x, by = r2 * e2y, bz = r2 * e2z;
                float ax = r1 * e1x, ay = r1 * e1y, az = r1 * e1z;
                var cx = az * by - ay * bz;
                var cy = ax * bz - az * bx;
                var cz = ay * bx - ax * by;
                var r = 1f / MathF.Sqrt(cy * cy + cx * cx + cz * cz);
                nx = r * cx; ny = r * cy; nz = r * cz;
            }
            foreach (var i in (ReadOnlySpan<int>)[i0, i1, i2])
            {
                acc[i * 3] = nx + acc[i * 3];
                acc[i * 3 + 1] = ny + acc[i * 3 + 1];
                acc[i * 3 + 2] = nz + acc[i * 3 + 2];
            }
        }
        for (var i = 0; i < v.Length; i++)
        {
            float ax = acc[i * 3], ay = acc[i * 3 + 1], az = acc[i * 3 + 2];
            var l = MathF.Sqrt(ax * ax + ay * ay + az * az);
            float nx = 0, ny = 1, nz = 0;
            if (l != 0)
            {
                var r = 1f / l;
                nx = ax * r; ny = ay * r; nz = az * r;
            }
            float tx = 0, ty = nz, tz = -ny;
            if (!(MathF.Sqrt(nz * nz + tz * tz) >= 0.001f)) { tx = ny; ty = -nx; tz = 0; }
            ref var o = ref v[i];
            o.Nx = nx; o.Ny = ny; o.Nz = nz;
            o.Tx = tx; o.Ty = ty; o.Tz = tz;
            o.Bx = tz * ny - ty * nz;
            o.By = tx * nz - tz * nx;
            o.Bz = ty * nx - tx * ny;
        }
    }

    /// <summary>0x71fc60's byte, then the model writer's decode (b/255·2 − 1) and re-encode.</summary>
    private static byte Unit(float v)
    {
        var b = (byte)(int)((v + 1f) * 127.5f);
        var t = b * (1f / 255f);
        return (byte)(int)((t + t - 1f + 1f) * 127.5f);
    }

    private static RigidModelV2 ToModel(Vertex[] v, List<int> idx)
    {
        var order = new List<int>(v.Length);
        var remap = new int[v.Length];
        Array.Fill(remap, -1);
        foreach (var i in idx)
            if (remap[i] < 0) { remap[i] = order.Count; order.Add(i); }
        float[] lo = [float.MaxValue, float.MaxValue, float.MaxValue], hi = [float.MinValue, float.MinValue, float.MinValue];
        foreach (var i in order)
        {
            lo[0] = MathF.Min(lo[0], v[i].X); lo[1] = MathF.Min(lo[1], v[i].Y); lo[2] = MathF.Min(lo[2], v[i].Z);
            hi[0] = MathF.Max(hi[0], v[i].X); hi[1] = MathF.Max(hi[1], v[i].Y); hi[2] = MathF.Max(hi[2], v[i].Z);
        }
        float px = (lo[0] + hi[0]) * 0.5f, py = (lo[1] + hi[1]) * 0.5f, pz = (lo[2] + hi[2]) * 0.5f;
        var bytes = new byte[order.Count * 48];
        for (var k = 0; k < order.Count; k++)
        {
            var s = bytes.AsSpan(k * 48, 48);
            var x = v[order[k]];
            BinaryPrimitives.WriteSingleLittleEndian(s, x.X - px);
            BinaryPrimitives.WriteSingleLittleEndian(s[4..], x.Y - py);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], x.Z - pz);
            BinaryPrimitives.WriteSingleLittleEndian(s[12..], 1f);
            BinaryPrimitives.WriteSingleLittleEndian(s[16..], UvScale * x.Off);
            BinaryPrimitives.WriteSingleLittleEndian(s[20..], UvScale * x.Dist);
            s[32] = Unit(x.Nz); s[33] = Unit(x.Ny); s[34] = Unit(x.Nx);
            s[36] = Unit(x.Tz); s[37] = Unit(x.Ty); s[38] = Unit(x.Tx);
            s[40] = Unit(x.Bz); s[41] = Unit(x.By); s[42] = Unit(x.Bx);
        }
        var indices = new ushort[idx.Count];
        for (var q = 0; q < idx.Count; q += 3)
        {
            indices[q] = (ushort)remap[idx[q]];
            indices[q + 1] = (ushort)remap[idx[q + 2]];
            indices[q + 2] = (ushort)remap[idx[q + 1]];
        }
        var model = RigidModelV2.NewRiver((px, py, pz));
        model.Vertices = bytes;
        model.Indices = indices;
        model.Bounds = [lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]];
        return model;
    }
}
