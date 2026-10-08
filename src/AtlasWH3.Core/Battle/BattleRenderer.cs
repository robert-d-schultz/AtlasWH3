using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.Core.Battle;

public enum BattleViewMode { TileMap, Climate, LandMask, Heights }

public sealed class BattleRenderOptions
{
    public BattleViewMode Mode { get; set; } = BattleViewMode.TileMap;
    public bool Hillshade { get; set; } = true;
    public bool Water { get; set; } = true;
    public bool Grid { get; set; } = true;
    public bool ExplicitTiles { get; set; } = true;
    public bool Catchments { get; set; } = false;
    /// <summary>Hillshade vertical exaggeration (height source units per view pixel).</summary>
    public double ShadeExaggeration { get; set; } = 0.02;
    /// <summary>Indices of explicit tiles BOB would reject (drawn red).</summary>
    public HashSet<int> ExplicitConflicts { get; set; } = new();
    /// <summary>Palette colour to highlight (0xRRGGBB) — cells of other colours are dimmed.</summary>
    public uint? Highlight { get; set; }
}

/// <summary>
/// Software renderer for a <see cref="BattleProject"/>. View pixels are land-height pixels; the tile map, climate and mask
/// are sampled per cell. Output is BGRA (0xAARRGGBB) for WPF.
/// </summary>
public sealed class BattleRenderer(BattleProject project)
{
    public BattleRenderOptions Options { get; } = new();

    private const uint Empty = 0xFF141E30;      // black tile-map cells: no tile (open sea)
    private const uint Offmap = 0xFF202020;

    public void Render(Viewport view, int width, int height, uint[] bgra)
    {
        var p = project;
        var opts = Options;
        var per = p.LandPerCell;
        var land = p.Land.Data;
        var lw = p.Land.Width;
        var lh = p.Land.Height;
        var step = Math.Max(1, (int)Math.Round(view.Scale));
        var slope = opts.ShadeExaggeration / step;
        const double lx = -0.5, ly = -0.5, lz = 0.7071;
        var highlight = opts.Highlight;

        Parallel.For(0, height, sy =>
        {
            var vy = view.OriginY + sy * view.Scale;
            var row = sy * width;
            if (vy < 0 || vy >= lh)
            {
                Array.Fill(bgra, Offmap, row, width);
                return;
            }
            var iy = (int)vy;
            var cy = Math.Min(iy / per, p.Height - 1);
            for (var sx = 0; sx < width; sx++)
            {
                var vx = view.OriginX + sx * view.Scale;
                if (vx < 0 || vx >= lw)
                {
                    bgra[row + sx] = Offmap;
                    continue;
                }
                var ix = (int)vx;
                var cx = Math.Min(ix / per, p.Width - 1);
                var h = land[iy * lw + ix];

                uint rgb;
                switch (opts.Mode)
                {
                    case BattleViewMode.Climate:
                        rgb = p.Climate != null ? BattleProject.RgbAt(p.Climate, cx, cy) : 0x808080;
                        break;
                    case BattleViewMode.LandMask:
                        rgb = p.LandMask != null && BattleProject.RgbAt(p.LandMask, cx, cy) == 0xFFFFFF ? 0x5A8A4Au : 0x1E2A44u;
                        break;
                    case BattleViewMode.Heights:
                    {
                        var g = (uint)Math.Clamp(h / 110, 0, 255);
                        rgb = (g << 16) | (g << 8) | g;
                        break;
                    }
                    default:
                    {
                        var tile = BattleProject.RgbAt(p.TileMap, cx, cy);
                        if (tile == 0) rgb = Empty & 0xFFFFFF;
                        else if (p.Palette.Find(tile) == null) rgb = ((ix / 4 + iy / 4) & 1) == 0 ? 0xFF00FFu : 0x400040u;
                        else rgb = tile;
                        if (highlight is { } hl && tile != hl) rgb = Dim(rgb);
                        break;
                    }
                }
                double r = (rgb >> 16) & 0xFF, g2 = (rgb >> 8) & 0xFF, b = rgb & 0xFF;

                if (opts.Hillshade)
                {
                    var xl = Math.Max(ix - step, 0);
                    var xr = Math.Min(ix + step, lw - 1);
                    var y0 = Math.Max(iy - step, 0);
                    var y1 = Math.Min(iy + step, lh - 1);
                    var dzdx = (land[iy * lw + xr] - land[iy * lw + xl]) * slope;
                    var dzdy = (land[y1 * lw + ix] - land[y0 * lw + ix]) * slope;
                    var inv = 1.0 / Math.Sqrt(dzdx * dzdx + dzdy * dzdy + 1);
                    var shade = 0.45 + 0.55 * Math.Max(0, (-dzdx * lx - dzdy * ly + lz) * inv) / lz;
                    r *= shade; g2 *= shade; b *= shade;
                }

                if (opts.Water)
                {
                    var sea = p.Sea[Math.Min(ix * p.Sea.Width / lw, p.Sea.Width - 1), Math.Min(iy * p.Sea.Height / lh, p.Sea.Height - 1)];
                    if (sea > h)
                    {
                        var t = Math.Clamp(0.35 + (sea - h) / 400.0, 0.35, 0.8);
                        r = r * (1 - t) + 30 * t;
                        g2 = g2 * (1 - t) + 80 * t;
                        b = b * (1 - t) + 140 * t;
                    }
                }

                if (opts.Grid && view.Scale < per / 6.0 && (ix % per == 0 || iy % per == 0))
                {
                    r *= 0.75; g2 *= 0.75; b *= 0.75;
                }

                bgra[row + sx] = 0xFF000000u | ((uint)Math.Clamp(r, 0, 255) << 16) | ((uint)Math.Clamp(g2, 0, 255) << 8)
                                 | (uint)Math.Clamp(b, 0, 255);
            }
        });

        if (opts.Catchments)
            foreach (var c in p.Catchments)
                DrawCellRect(view, width, height, bgra, c.Box.MinX, c.Box.MinY, c.Box.MaxX + 1, c.Box.MaxY + 1, CatchmentColour(c));
        if (opts.ExplicitTiles)
            for (var i = 0; i < p.ExplicitTiles.Count; i++)
                if (p.TileDatabase.TileAt(p.ExplicitTiles[i].Location) is { } tile)
                {
                    var t = p.ExplicitTiles[i];
                    var (w, h) = t.Size(tile);
                    DrawCellRect(view, width, height, bgra, t.X, t.Y, t.X + w, t.Y + h,
                        opts.ExplicitConflicts.Contains(i) ? 0xFFFF2020 : 0xFFFFFFFF);
                }
    }

    private static uint Dim(uint rgb) =>
        ((((rgb >> 16) & 0xFF) / 4) << 16) | ((((rgb >> 8) & 0xFF) / 4) << 8) | ((rgb & 0xFF) / 4);

    private static uint CatchmentColour(BattleCatchment c) =>
        c.Types.Contains("settlement_standard") ? 0xFFFF4040
        : c.Types.Contains("settlement_unfortified") ? 0xFFFFA040
        : c.Types.Contains("gate_battle") ? 0xFFFF40FF
        : 0xFF40E0FF;

    /// <summary>Outline of cells [x0,x1) × [y0,y1) (image orientation).</summary>
    private void DrawCellRect(Viewport view, int width, int height, uint[] bgra, int x0, int y0, int x1, int y1, uint colour)
    {
        var per = project.LandPerCell;
        var (sx0, sy0) = view.MapToScreen(x0 * per, y0 * per);
        var (sx1, sy1) = view.MapToScreen(x1 * per, y1 * per);
        int ax = (int)Math.Round(sx0), ay = (int)Math.Round(sy0), bx = (int)Math.Round(sx1) - 1, by = (int)Math.Round(sy1) - 1;
        if (bx < 0 || by < 0 || ax >= width || ay >= height || bx < ax || by < ay) return;
        for (var x = Math.Max(ax, 0); x <= Math.Min(bx, width - 1); x++)
        {
            if (ay >= 0) bgra[ay * width + x] = colour;
            if (by < height) bgra[by * width + x] = colour;
        }
        for (var y = Math.Max(ay, 0); y <= Math.Min(by, height - 1); y++)
        {
            if (ax >= 0) bgra[y * width + ax] = colour;
            if (bx < width) bgra[y * width + bx] = colour;
        }
    }
}
