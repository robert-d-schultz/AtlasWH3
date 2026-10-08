using SkiaSharp;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>
/// PNG of a hex area of a tile map for checking tile edits: each hex drawn as a flat-top hexagon in its tile-set
/// colour, a labelled col/row grid, a legend of the sets in view, changed hexes outlined in white and new issues
/// marked (red = blocking, orange = other warnings).
/// </summary>
public static class TileMapPreview
{
    public sealed record Mark(int Col, int Row, bool Blocking);

    public static string Render(HexTileMap map, Func<uint, string?> setOf, int c0, int r0, int c1, int r1, string outPath,
                                int width = 1024, IEnumerable<(int Col, int Row)>? changed = null, IEnumerable<Mark>? marks = null)
    {
        (c0, c1) = (Math.Clamp(Math.Min(c0, c1), 0, map.Width - 1), Math.Clamp(Math.Max(c0, c1), 0, map.Width - 1));
        (r0, r1) = (Math.Clamp(Math.Min(r0, r1), 0, map.Height - 1), Math.Clamp(Math.Max(r0, r1), 0, map.Height - 1));
        // flat-top hexes: x = col·1.5·R, y = (row + (col & 1) / 2)·√3·R, row 0 at the bottom
        var cols = c1 - c0 + 1;
        var rows = r1 - r0 + 1;
        const float legendW = 190;
        var rad = (float)Math.Max(2.0, (width - legendW) / (1.5 * cols + 0.5));
        var hexH = (float)(Math.Sqrt(3) * rad);
        var mapH = hexH * (rows + 0.5f);
        var height = (int)Math.Ceiling(Math.Max(mapH + 20, 120));
        float Cx(int c) => rad + (c - c0) * 1.5f * rad;
        float Cy(int c, int r) => mapH - ((r - r0) + (c & 1) * 0.5f + 0.5f) * hexH + 10;   // row 0 = south = bottom

        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(30, 30, 30));
        using var font = new SKFont(SKTypeface.Default, 11);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 60), StrokeWidth = 1, IsAntialias = true };

        SKPath Hex(int c, int r, float scale = 1f)
        {
            var p = new SKPath();
            float cx = Cx(c), cy = Cy(c, r), rr = rad * scale;
            for (var k = 0; k < 6; k++)
            {
                var a = Math.PI / 3 * k;
                var (x, y) = ((float)(cx + rr * Math.Cos(a)), (float)(cy + rr * Math.Sin(a)));
                if (k == 0) p.MoveTo(x, y); else p.LineTo(x, y);
            }
            p.Close();
            return p;
        }

        var inView = new Dictionary<uint, int>();
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
            {
                var (x, y) = map.HexPixel(c, r);
                var v = map.Pixel(x, y);
                inView[v] = inView.GetValueOrDefault(v) + 1;
                fill.Color = new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
                using var path = Hex(c, r);
                canvas.DrawPath(path, fill);
                if (rad >= 5) canvas.DrawPath(path, edge);
            }

        if (changed is not null)
        {
            using var outline = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = Math.Max(1.5f, rad / 5), IsAntialias = true };
            foreach (var (c, r) in changed)
                if (c >= c0 && c <= c1 && r >= r0 && r <= r1) { using var p = Hex(c, r, 0.85f); canvas.DrawPath(p, outline); }
        }
        if (marks is not null)
        {
            using var bad = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(255, 30, 30), StrokeWidth = Math.Max(2f, rad / 3), IsAntialias = true };
            using var warn = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(255, 160, 0), StrokeWidth = Math.Max(2f, rad / 3), IsAntialias = true };
            foreach (var m in marks)
                if (m.Col >= c0 && m.Col <= c1 && m.Row >= r0 && m.Row <= r1)
                    canvas.DrawCircle(Cx(m.Col), Cy(m.Col, m.Row), Math.Max(3, rad * 0.55f), m.Blocking ? bad : warn);
        }

        // grid labels every ~6th column / row
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(cols, rows) / 8.0));
        using var tick = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 1 };
        for (var c = c0 - c0 % step; c <= c1; c += step)
        {
            if (c < c0) continue;
            canvas.DrawLine(Cx(c), 10, Cx(c), mapH + 10, tick);
            canvas.DrawText($"c{c}", Cx(c) + 2, 10, font, text);
        }
        for (var r = r0 - r0 % step; r <= r1; r += step)
        {
            if (r < r0) continue;
            canvas.DrawText($"r{r}", 2, Cy(c0 & ~1, r) + 4, font, text);
        }

        var lx = width - legendW + 10;
        var ly = 20f;
        foreach (var (rgb, n) in inView.OrderByDescending(k => k.Value))
        {
            fill.Color = new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            canvas.DrawRect(lx, ly - 9, 12, 12, fill);
            canvas.DrawText($"{setOf(rgb) ?? $"#{rgb:x6} ?"} ({n})", lx + 16, ly + 1, font, text);
            ly += 16;
            if (ly > height - 40) break;
        }
        canvas.DrawText($"cols {c0}-{c1}, rows {r0}-{r1}", lx, height - 24, font, text);
        canvas.DrawText("white = changed, red/orange = new issue", lx, height - 8, font, text);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
        using (var fs = File.Create(outPath)) data.SaveTo(fs);
        return outPath;
    }
}
