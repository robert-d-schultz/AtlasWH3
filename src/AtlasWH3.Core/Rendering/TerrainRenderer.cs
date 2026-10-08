namespace AtlasWH3.Core.Rendering;

/// <summary>
/// A view onto the lf grid: map pixel (OriginX, OriginY) sits at screen (0,0); each screen pixel covers
/// <see cref="Scale"/> map pixels.
/// </summary>
public readonly record struct Viewport(double OriginX, double OriginY, double Scale)
{
    public (double X, double Y) ScreenToMap(double sx, double sy) => (OriginX + sx * Scale, OriginY + sy * Scale);
    public (double X, double Y) MapToScreen(double mx, double my) => ((mx - OriginX) / Scale, (my - OriginY) / Scale);
}

public sealed class TerrainRenderOptions
{
    /// <summary>Map pixels per texture repeat (48 lf px ≈ 4 world units).</summary>
    public double TextureRepeatPx { get; set; } = 48;
    /// <summary>Vertical exaggeration for the hillshade.</summary>
    public double ShadeExaggeration { get; set; } = 2.5;
    public double ShadeStrength { get; set; } = 0.6;
    public bool ShowTextures { get; set; } = true;
    public bool ShowWater { get; set; } = true;
}

/// <summary>
/// Software renderer for the campaign terrain: per pixel it looks up the blend group, samples that
/// group's base-colour texture (tiled in world space, mip chosen by zoom), applies a hillshade from the
/// heightmap, and tints water by depth. Output is packed RGBA (0xAABBGGRR) or BGRA for WPF.
/// </summary>
public sealed class TerrainRenderer
{
    private const double WorldUnitsPerLfPx = 595.1 / 7136.0;

    private readonly TerrainData _terrain;
    private readonly TerrainTextureSet? _textures;
    private readonly uint[] _fallbackColours;

    public TerrainRenderOptions Options { get; } = new();

    public TerrainRenderer(TerrainData terrain, TerrainTextureSet? textures)
    {
        _terrain = terrain;
        _textures = textures;
        _fallbackColours = Enumerable.Range(0, 256).Select(i =>
            textures != null && i < textures.Textures.Count && textures.Textures[i] != null
                ? textures.Textures[i]!.Average
                : FallbackColour(i)).ToArray();
    }

    /// <summary>Renders into <paramref name="bgra"/> (width*height, 0xAARRGGBB little-endian = BGRA bytes).</summary>
    public void Render(Viewport view, int width, int height, uint[] bgra)
    {
        var mapW = _terrain.Width;
        var mapH = _terrain.HeightPx;
        var heights = _terrain.Height.Data;
        var groups = _terrain.BlendGroup.Data;
        var opts = Options;

        // Texture mip: texels per screen pixel = scale * 512 / repeatPx.
        var texelsPerScreenPx = view.Scale * TerrainTextureSet.CacheSize / opts.TextureRepeatPx;
        var mip = texelsPerScreenPx <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(texelsPerScreenPx));
        var step = Math.Max(1, (int)Math.Round(view.Scale));           // gradient step in map px
        var slopeScale = HeightScale.UnitsPerStep * opts.ShadeExaggeration / (2 * step * WorldUnitsPerLfPx);
        // Light from the north-west, 45° elevation.
        const double lx = -0.5, ly = -0.5, lz = 0.7071;

        Parallel.For(0, height, sy =>
        {
            var my = view.OriginY + sy * view.Scale;
            var row = sy * width;
            if (my < 0 || my >= mapH)
            {
                Array.Fill(bgra, 0xFF202020u, row, width);
                return;
            }
            var iy = (int)my;
            var y0 = Math.Max(iy - step, 0) * mapW;
            var y1 = Math.Min(iy + step, mapH - 1) * mapW;
            var yc = iy * mapW;

            for (var sx = 0; sx < width; sx++)
            {
                var mx = view.OriginX + sx * view.Scale;
                if (mx < 0 || mx >= mapW)
                {
                    bgra[row + sx] = 0xFF202020u;
                    continue;
                }
                var ix = (int)mx;
                var i = yc + ix;
                var group = groups[i];

                uint rgba;
                if (opts.ShowTextures && _textures != null && group < _textures.Textures.Count && _textures.Textures[group] is { } tex)
                    rgba = tex.Sample(mx / opts.TextureRepeatPx, my / opts.TextureRepeatPx, mip);
                else
                    rgba = _fallbackColours[group];

                double r = rgba & 0xFF, g = (rgba >> 8) & 0xFF, b = (rgba >> 16) & 0xFF;

                // Hillshade from central differences.
                var xl = Math.Max(ix - step, 0);
                var xr = Math.Min(ix + step, mapW - 1);
                var dzdx = (heights[yc + xr] - heights[yc + xl]) * slopeScale;
                var dzdy = (heights[y1 + ix] - heights[y0 + ix]) * slopeScale;
                var inv = 1.0 / Math.Sqrt(dzdx * dzdx + dzdy * dzdy + 1);
                var lambert = Math.Max(0, (-dzdx * lx - dzdy * ly + lz) * inv);
                var shade = 1 - opts.ShadeStrength + opts.ShadeStrength * lambert / lz;
                r *= shade; g *= shade; b *= shade;

                if (opts.ShowWater)
                {
                    var sea = _terrain.SeaAt(ix, iy);
                    var land = heights[i];
                    if (sea > land)
                    {
                        // Depth in world units → blend toward deep water colour.
                        var depth = (sea - land) * HeightScale.UnitsPerStep;
                        var t = Math.Clamp(0.55 + depth * 0.25, 0.55, 0.92);
                        r = r * (1 - t) + 28 * t;
                        g = g * (1 - t) + 72 * t;
                        b = b * (1 - t) + 96 * t;
                    }
                }

                bgra[row + sx] = 0xFF000000u
                                 | ((uint)Math.Clamp(r, 0, 255) << 16)
                                 | ((uint)Math.Clamp(g, 0, 255) << 8)
                                 | (uint)Math.Clamp(b, 0, 255);
            }
        });
    }

    /// <summary>Distinct flat colours for groups without a loaded texture (packed RGBA).</summary>
    public static uint FallbackColour(int group)
    {
        var hue = group * 137.508 % 360;
        var (r, g, b) = HsvToRgb(hue, 0.45, 0.75);
        return 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
    }

    private static (byte, byte, byte) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
