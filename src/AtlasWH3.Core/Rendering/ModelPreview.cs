using SkiaSharp;
using AtlasWH3.Core.Assets;

namespace AtlasWH3.Core.Rendering;

/// <summary>
/// Software preview of a model: orthographic camera turned by yaw (about y) and pitched down, fitted to the model's
/// bounds; z-buffered triangles with the base colour texture (alpha-tested materials discard texels below half
/// alpha) and simple Lambert light. For thumbnails, the MCP tools and the inspector; the viewport renders on the GPU.
/// </summary>
public static class ModelPreview
{
    public sealed record Options(int Size = 384, double Yaw = 35, double Pitch = 30, int Lod = 0, bool Textured = true,
                                 uint Background = 0xFF262626, int TextureSize = 256);

    /// <summary>RGBA pixels (row-major, Size × Size).</summary>
    public static byte[] Render(RenderModel model, ModelLibrary library, Options? options = null)
    {
        var o = options ?? new Options();
        var n = o.Size;
        var rgba = new byte[n * n * 4];
        for (var i = 0; i < n * n; i++)
        {
            rgba[i * 4] = (byte)(o.Background >> 16);
            rgba[i * 4 + 1] = (byte)(o.Background >> 8);
            rgba[i * 4 + 2] = (byte)o.Background;
            rgba[i * 4 + 3] = (byte)(o.Background >> 24);
        }
        var depth = new float[n * n];
        Array.Fill(depth, float.MaxValue);
        var lod = model.Lods.ElementAtOrDefault(Math.Clamp(o.Lod, 0, Math.Max(0, model.Lods.Count - 1)));
        if (lod is null || lod.Count == 0) return rgba;

        // Camera turned by yaw about y, looking along forward = (0, -sin p, cos p) (down at the model, towards +z),
        // screen up = (0, cos p, sin p). View = (screen x, screen y up, depth; smaller depth is nearer).
        double cy = Math.Cos(o.Yaw * Math.PI / 180), sy = Math.Sin(o.Yaw * Math.PI / 180);
        double cp = Math.Cos(o.Pitch * Math.PI / 180), sp = Math.Sin(o.Pitch * Math.PI / 180);
        (double X, double Y, double Z) View(double x, double y, double z)
        {
            var x1 = x * cy - z * sy;
            var z1 = x * sy + z * cy;
            return (x1, y * cp + z1 * sp, -y * sp + z1 * cp);
        }

        // Fit: the projected extent of every vertex.
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var m in lod)
            for (var i = 0; i < m.Positions.Length; i += 3)
            {
                var v = View(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]);
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
            }
        if (minX > maxX) return rgba;
        var scale = n * 0.9 / Math.Max(1e-6, Math.Max(maxX - minX, maxY - minY));
        double ox = n / 2.0 - (minX + maxX) / 2 * scale, oy = n / 2.0 + (minY + maxY) / 2 * scale;

        var light = Normalize((-0.4, 0.8, -0.45));
        foreach (var m in lod)
        {
            var tex = o.Textured && m.BaseColour is { } path ? library.Texture(path, o.TextureSize) : null;
            var vc = m.Positions.Length / 3;
            var sx = new float[vc];
            var syv = new float[vc];
            var sz = new float[vc];
            var shade = new float[vc];
            for (var i = 0; i < vc; i++)
            {
                var v = View(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
                sx[i] = (float)(ox + v.X * scale);
                syv[i] = (float)(oy - v.Y * scale);
                sz[i] = (float)v.Z;
                var nrm = Normalize((m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]));
                // Two-sided (leaves, cards): use the facing side of the normal.
                var d = nrm.X * light.X + nrm.Y * light.Y + nrm.Z * light.Z;
                shade[i] = (float)(0.45 + 0.55 * Math.Abs(d));
            }
            for (var t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                int a = m.Indices[t], b = m.Indices[t + 1], c = m.Indices[t + 2];
                Triangle(n, rgba, depth, sx, syv, sz, shade, m, tex, a, b, c);
            }
        }
        return rgba;
    }

    private static void Triangle(int n, byte[] rgba, float[] depth, float[] sx, float[] sy, float[] sz, float[] shade,
                                 RenderMesh m, Formats.Dds.DdsTexture.Image? tex, int a, int b, int c)
    {
        float x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-9) return;
        int minx = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), maxx = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        int miny = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2)))), maxy = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        for (var py = miny; py <= maxy; py++)
            for (var px = minx; px <= maxx; px++)
            {
                float cx = px + 0.5f, cyy = py + 0.5f;
                var w0 = ((x1 - cx) * (y2 - cyy) - (x2 - cx) * (y1 - cyy)) / area;
                var w1 = ((x2 - cx) * (y0 - cyy) - (x0 - cx) * (y2 - cyy)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c];
                var idx = py * n + px;
                if (z >= depth[idx]) continue;
                byte r = 170, g = 170, bl = 170;
                if (tex is not null && m.Uvs.Length > 0)
                {
                    var u = w0 * m.Uvs[a * 2] + w1 * m.Uvs[b * 2] + w2 * m.Uvs[c * 2];
                    var v = w0 * m.Uvs[a * 2 + 1] + w1 * m.Uvs[b * 2 + 1] + w2 * m.Uvs[c * 2 + 1];
                    var tx = Wrap((int)Math.Floor(u * tex.Width), tex.Width);
                    var ty = Wrap((int)Math.Floor(v * tex.Height), tex.Height);
                    var ti = (ty * tex.Width + tx) * 4;
                    if (m.AlphaTest && tex.Rgba[ti + 3] < 128) continue;
                    (r, g, bl) = (tex.Rgba[ti], tex.Rgba[ti + 1], tex.Rgba[ti + 2]);
                }
                var s = w0 * shade[a] + w1 * shade[b] + w2 * shade[c];
                depth[idx] = z;
                rgba[idx * 4] = (byte)Math.Min(255, r * s);
                rgba[idx * 4 + 1] = (byte)Math.Min(255, g * s);
                rgba[idx * 4 + 2] = (byte)Math.Min(255, bl * s);
                rgba[idx * 4 + 3] = 255;
            }
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l < 1e-12 ? (0, 1, 0) : (v.X / l, v.Y / l, v.Z / l);
    }

    /// <summary>Renders and encodes a PNG.</summary>
    public static byte[] RenderPng(RenderModel model, ModelLibrary library, Options? options = null)
    {
        var o = options ?? new Options();
        var rgba = Render(model, library, o);
        using var bmp = new SKBitmap(new SKImageInfo(o.Size, o.Size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
