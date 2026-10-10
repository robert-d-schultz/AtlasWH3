using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Core.Campaign.Rivers;

/// <summary>One ECRiverSpline entity from a rivers .layer.</summary>
public sealed record RiverSpline(string Name, int Number, (double X, double Y, double Z) Position, double YawDegrees,
    double StepSize, bool TerrainRelative, bool Reverse, string Material, IReadOnlyList<RiverPoint> Points);

public sealed record RiverPoint((double X, double Y, double Z) Position, (double X, double Y, double Z) TangentIn,
    (double X, double Y, double Z) TangentOut, double Width, double TerrainOffset);

/// <summary>
/// Native river models and height patches (BOB "Terry file": models\river_N.wsmodel(.rigid_model_v2),
/// height_patches\river_N_patch_XxZ.compressed_map + rivers.height_patch_collection). Game-valid, modelled on the
/// vanilla/BOB output:
///  - the spline is a chain of cubic Béziers (p_i, p_i + tangent_out_i, p_i+1 + tangent_in_i+1, p_i+1) placed by the
///    entity transform, sampled every spline_step_size along its length; width and height interpolate per segment
///  - each cross-section has 5 vertices at -w/2, -w/4, 0, w/4, w/2; vertex = position relative to the pivot, 1,
///    v = 0.1 · lateral offset, u = 0.1 · arc length, world uv = (x / world width, z / world height),
///    packed normal (up), tangent (downstream), bitangent (across), 4 zero bytes (48 bytes)
///  - height patches: the water surface rasterised at 16 px per unit into 32-unit blocks (512 × 512, row 0 = south),
///    header (0, -50, 0, 0, max, 0), 0 = no water; named by pixel offset from the river's first block
/// River number = the entity name's trailing number (river_N), which is how vanilla numbers them.
/// </summary>
public static class RiverBuilder
{

    /// <summary>Extra width and end extension so the water covers the land-mesh river holes (which follow the river
    /// tiles, not the spline). Wider water is harmless: it lies under the higher banks.</summary>
    public const double WidthMargin = 1.15, EndExtension = 0.5;

    public static List<RiverSpline> ReadLayer(string layerPath)
    {
        var doc = XDocument.Load(layerPath);
        var result = new List<RiverSpline>();
        var order = 0;
        foreach (var entity in doc.Descendants("entity"))
        {
            var spline = entity.Element("ECRiverSpline");
            if (spline is null) continue;
            var name = (string?)entity.Attribute("name") ?? $"river_{order}";
            var number = int.TryParse(name.Split('_').Last(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : order;
            order++;
            var transform = entity.Element("ECTransform");
            var position = V3((string?)transform?.Attribute("position") ?? "0 0 0", ' ');
            var rotation = V3((string?)transform?.Attribute("rotation") ?? "0 0 0", ' ');
            var points = spline.Descendants("point").Select(p => new RiverPoint(
                V3((string)p.Attribute("position")!, ','), V3((string)p.Attribute("tangent_in")!, ','),
                V3((string)p.Attribute("tangent_out")!, ','), D((string?)p.Attribute("width") ?? "1"),
                D((string?)p.Attribute("terrain_offset") ?? "0"))).ToList();
            result.Add(new RiverSpline(name, number, position, rotation.Y,
                D((string?)spline.Attribute("spline_step_size") ?? "1.5"),
                (string?)spline.Attribute("terrain_relative") == "true",
                (string?)spline.Attribute("reverse_direction") == "true",
                (string?)spline.Attribute("material") ?? "", points));
        }
        return result;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static (double X, double Y, double Z) V3(string s, char sep)
    {
        var p = s.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(D).ToArray();
        return (p[0], p[1], p[2]);
    }

    public sealed record Section(double X, double Y, double Z, double DirX, double DirZ, double Width, double Arc);

    /// <summary>Cross-sections every step along the spline, in world coordinates.</summary>
    public static List<Section> Sample(RiverSpline river, Func<double, double, double>? terrainHeight)
    {
        var yaw = river.YawDegrees * Math.PI / 180;
        (double X, double Y, double Z) World((double X, double Y, double Z) p) =>
            (river.Position.X + p.X * Math.Cos(yaw) + p.Z * Math.Sin(yaw), river.Position.Y + p.Y,
             river.Position.Z - p.X * Math.Sin(yaw) + p.Z * Math.Cos(yaw));

        // dense polyline with per-sample width/offset, then resample by arc length
        var dense = new List<(double X, double Y, double Z, double W, double O)>();
        var pts = river.Points;
        for (var s = 0; s + 1 < pts.Count; s++)
        {
            var a = pts[s];
            var b = pts[s + 1];
            var p0 = a.Position;
            var p1 = (a.Position.X + a.TangentOut.X, a.Position.Y + a.TangentOut.Y, a.Position.Z + a.TangentOut.Z);
            var p2 = (b.Position.X + b.TangentIn.X, b.Position.Y + b.TangentIn.Y, b.Position.Z + b.TangentIn.Z);
            var p3 = b.Position;
            const int steps = 32;
            for (var k = s == 0 ? 0 : 1; k <= steps; k++)
            {
                var t = (double)k / steps;
                var mt = 1 - t;
                double B(double c0, double c1, double c2, double c3) => mt * mt * mt * c0 + 3 * mt * mt * t * c1 + 3 * mt * t * t * c2 + t * t * t * c3;
                var w = World((B(p0.X, p1.Item1, p2.Item1, p3.X), B(p0.Y, p1.Item2, p2.Item2, p3.Y), B(p0.Z, p1.Item3, p2.Item3, p3.Z)));
                dense.Add((w.X, w.Y, w.Z, a.Width + (b.Width - a.Width) * t, a.TerrainOffset + (b.TerrainOffset - a.TerrainOffset) * t));
            }
        }
        if (pts.Count == 1) { var w = World(pts[0].Position); dense.Add((w.X, w.Y, w.Z, pts[0].Width, pts[0].TerrainOffset)); }
        if (river.Reverse) dense.Reverse();
        if (dense.Count < 2) return [];

        var arc = new double[dense.Count];
        for (var i = 1; i < dense.Count; i++)
            arc[i] = arc[i - 1] + Math.Sqrt(Math.Pow(dense[i].X - dense[i - 1].X, 2) + Math.Pow(dense[i].Z - dense[i - 1].Z, 2));
        var total = arc[^1];
        var count = Math.Max(1, (int)Math.Ceiling(total / Math.Max(river.StepSize, 0.01)));
        var sections = new List<Section>();
        var seg = 0;
        for (var k = 0; k <= count; k++)
        {
            var target = total * k / count;
            while (seg < dense.Count - 2 && arc[seg + 1] < target) seg++;
            var span = arc[seg + 1] - arc[seg];
            var f = span > 0 ? (target - arc[seg]) / span : 0;
            var a = dense[seg];
            var b = dense[seg + 1];
            double L(double x, double y) => x + (y - x) * f;
            double x = L(a.X, b.X), z = L(a.Z, b.Z);
            var y = river.TerrainRelative && terrainHeight is not null ? terrainHeight(x, z) + L(a.O, b.O) : L(a.Y, b.Y) + L(a.O, b.O);
            // direction from neighbouring dense samples
            var i0 = Math.Max(0, seg - 1);
            var i1 = Math.Min(dense.Count - 1, seg + 2);
            double dx = dense[i1].X - dense[i0].X, dz = dense[i1].Z - dense[i0].Z;
            var len = Math.Sqrt(dx * dx + dz * dz);
            if (len == 0) { dx = 1; dz = 0; len = 1; }
            sections.Add(new Section(x, y, z, dx / len, dz / len, L(a.W, b.W) * WidthMargin, target));
        }
        // push the first and last cross-sections outwards by half their width
        if (sections.Count >= 2)
        {
            var s0 = sections[0];
            var e = EndExtension * s0.Width;
            sections[0] = s0 with { X = s0.X - s0.DirX * e, Z = s0.Z - s0.DirZ * e };
            var s1 = sections[^1];
            e = EndExtension * s1.Width;
            sections[^1] = s1 with { X = s1.X + s1.DirX * e, Z = s1.Z + s1.DirZ * e, Arc = s1.Arc + e };
        }
        return sections;
    }

    /// <summary>River mesh in world coordinates: positions, triangles, per-vertex (v, u), across and along vectors.</summary>
    public static RigidModelV2 BuildModel(List<Section> sections, float worldWidth, float worldHeight)
    {
        float[] lateral = [-0.5f, -0.25f, 0f, 0.25f, 0.5f];
        var world = new List<(double X, double Y, double Z, double V, double U, double Dx, double Dz)>();
        foreach (var s in sections)
            foreach (var l in lateral)
            {
                // across = left of the flow direction
                double ax = -s.DirZ, az = s.DirX;
                var off = l * s.Width;
                world.Add((s.X + ax * off, s.Y, s.Z + az * off, 0.1 * off, 0.1 * s.Arc, s.DirX, s.DirZ));
            }
        double minX = world.Min(p => p.X), maxX = world.Max(p => p.X);
        double minY = world.Min(p => p.Y), maxY = world.Max(p => p.Y);
        double minZ = world.Min(p => p.Z), maxZ = world.Max(p => p.Z);
        var pivot = (X: Snap((minX + maxX) / 2, 8), Y: Snap((minY + maxY) / 2, 32), Z: Snap((minZ + maxZ) / 2, 8));

        var model = RigidModelV2.NewRiver(((float)pivot.X, (float)pivot.Y, (float)pivot.Z));
        var vertices = new byte[world.Count * 48];
        for (var i = 0; i < world.Count; i++)
        {
            var p = world[i];
            var s = vertices.AsSpan(i * 48);
            W(s, 0, (float)Snap(p.X - pivot.X, 64));
            W(s, 4, (float)Snap(p.Y - pivot.Y, 64));
            W(s, 8, (float)Snap(p.Z - pivot.Z, 64));
            W(s, 12, 1f);
            W(s, 16, (float)p.V);
            W(s, 20, (float)p.U);
            W(s, 24, (float)(p.X / worldWidth));
            W(s, 28, (float)(p.Z / worldHeight));
            Pack(s[32..], 0, 1, 0);            // normal: up
            Pack(s[36..], p.Dx, 0, p.Dz);      // tangent: downstream
            Pack(s[40..], p.Dz, 0, -p.Dx);     // bitangent: across (right of the flow)
        }
        var indices = new List<ushort>();
        for (var k = 0; k + 1 < sections.Count; k++)
            for (var l = 0; l < 4; l++)
            {
                var a = (ushort)(k * 5 + l);
                var b = (ushort)(a + 1);
                var c = (ushort)(a + 5);
                var d = (ushort)(c + 1);
                AddUp(world, indices, a, b, c);
                AddUp(world, indices, c, b, d);
            }
        model.Vertices = vertices;
        model.Indices = [.. indices];
        model.Bounds = [(float)minX, (float)minY, (float)minZ, (float)maxX, (float)maxY, (float)maxZ];
        return model;
    }

    /// <summary>Adds a triangle wound so its normal points up (+y), as vanilla's are.</summary>
    private static void AddUp(List<(double X, double Y, double Z, double V, double U, double Dx, double Dz)> w, List<ushort> idx, ushort a, ushort b, ushort c)
    {
        var cross = (w[b].Z - w[a].Z) * (w[c].X - w[a].X) - (w[b].X - w[a].X) * (w[c].Z - w[a].Z);
        if (cross >= 0) idx.AddRange([a, b, c]);
        else idx.AddRange([a, c, b]);
    }

    private static double Snap(double v, double per) => Math.Round(v * per) / per;
    private static void W(Span<byte> s, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(s[o..], v);

    private static void Pack(Span<byte> s, double x, double y, double z)
    {
        var len = Math.Sqrt(x * x + y * y + z * z);
        if (len > 0) { x /= len; y /= len; z /= len; }
        s[0] = B(x); s[1] = B(y); s[2] = B(z); s[3] = 0;
        static byte B(double c) => (byte)Math.Clamp(Math.Round((c + 1) * 127.5), 0, 255);
    }
}
