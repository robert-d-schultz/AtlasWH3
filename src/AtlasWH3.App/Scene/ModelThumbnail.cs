using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtlasWH3.Core.Assets;

namespace AtlasWH3.App.Scene;

/// <summary>A quick flat-shaded preview of a model for the asset browser: the lowest-detail LOD, orthographic from
/// above at an angle, z-buffered on the CPU (no textures, so it costs milliseconds even for big meshes).</summary>
public static class ModelThumbnail
{
    /// <summary>BGRA pixels (frozen bitmap, usable from any thread) or null when the model has no triangles.</summary>
    public static BitmapSource? Render(RenderModel model, int size = 160, float yawDeg = 35, float pitchDeg = 30)
    {
        var lod = model.Lods.LastOrDefault(l => l.Sum(m => m.Indices.Length) > 0);
        if (lod is null) return null;
        var rot = Matrix4x4.CreateRotationY(yawDeg * MathF.PI / 180) * Matrix4x4.CreateRotationX(pitchDeg * MathF.PI / 180);
        var light = Vector3.Normalize(new Vector3(-0.4f, 0.8f, -0.45f));
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var m in lod)
            for (var i = 0; i + 2 < m.Positions.Length; i += 3)
            {
                var p = Vector3.Transform(new Vector3(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]), rot);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        var extent = Math.Max(max.X - min.X, max.Y - min.Y);
        if (!(extent > 0)) return null;
        var s = (size - 8) / extent;
        float ox = (min.X + max.X) / 2, oy = (min.Y + max.Y) / 2;
        var pixels = new uint[size * size];
        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);
        foreach (var m in lod)
        {
            var colour = m.Shader.Contains("water", StringComparison.OrdinalIgnoreCase) ? new Vector3(0.35f, 0.55f, 0.8f)
                : m.AlphaTest ? new Vector3(0.45f, 0.68f, 0.38f) : new Vector3(0.78f, 0.74f, 0.68f);
            for (var t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                Vector3 V(int k)
                {
                    var i = m.Indices[t + k] * 3;
                    return i + 2 < m.Positions.Length ? new Vector3(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]) : Vector3.Zero;
                }
                Vector3 a = V(0), b = V(1), c = V(2);
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-20f) continue;
                var shade = 0.35f + 0.65f * Math.Abs(Vector3.Dot(Vector3.Normalize(n), light));
                var col = colour * shade * 255;
                var bgra = 0xFF000000u | (uint)col.X << 16 | (uint)col.Y << 8 | (uint)col.Z;
                Vector3 P(Vector3 v)
                {
                    var r = Vector3.Transform(v, rot);
                    return new Vector3((r.X - ox) * s + size / 2f, size / 2f - (r.Y - oy) * s, r.Z);
                }
                Fill(P(a), P(b), P(c), bgra, pixels, depth, size);
            }
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        return bmp;
    }

    private static void Fill(Vector3 a, Vector3 b, Vector3 c, uint colour, uint[] pixels, float[] depth, int size)
    {
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                var w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                var i = y * size + x;
                if (z >= depth[i]) continue;
                depth[i] = z;
                pixels[i] = colour;
            }
    }

    /// <summary>"2.4 × 1.1 × 2.0 (w × h × d)" from model bounds.</summary>
    public static string SizeText(float[] b) =>
        b.Length < 6 ? "" : $"{b[3] - b[0]:0.##} × {b[4] - b[1]:0.##} × {b[5] - b[2]:0.##} (w × h × d), base y {b[1]:0.##}";
}
