using SkiaSharp;
using AtlasWH3.Core.Editing;

namespace AtlasWH3.Core.Rendering;

/// <summary>
/// Top-down PNG of a world rectangle for checking prop edits: hillshaded AK heightmap (blue below sea level 0), a
/// labelled world-coordinate grid, and every object as a dot coloured by kind (scaled props drawn larger). Highlighted
/// ids get a red ring and their id; when few objects are in view each is labelled with its model's short name.
/// </summary>
public static class PropPreview
{
    public static readonly IReadOnlyDictionary<string, SKColor> KindColours = new Dictionary<string, SKColor>
    {
        ["prop"] = new(40, 160, 60), ["decal"] = new(170, 120, 60), ["vfx"] = new(255, 140, 0), ["light"] = new(255, 230, 40),
        ["scene"] = new(200, 60, 200), ["sound"] = new(60, 200, 220), ["probe"] = new(150, 150, 150), ["poly"] = new(90, 90, 200),
        ["river"] = SKColors.White,
    };

    public static string Render(PropEditor editor, double x0, double z0, double x1, double z1, string outPath,
                                int width = 1024, ISet<string>? highlight = null, string? layer = null, int labelLimit = 150)
    {
        (x0, x1) = (Math.Min(x0, x1), Math.Max(x0, x1));
        (z0, z1) = (Math.Min(z0, z1), Math.Max(z0, z1));
        if (x1 - x0 < 1e-3 || z1 - z0 < 1e-3) throw new ArgumentException("empty preview rectangle");
        var height = Math.Max(16, (int)Math.Round(width * (z1 - z0) / (x1 - x0)));
        var ppu = width / (x1 - x0);
        float Px(double x) => (float)((x - x0) * ppu);
        float Pz(double z) => (float)((z1 - z) * ppu);

        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = new uint[width * height];
        Shade(editor, x0, z1, ppu, width, height, pixels);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bmp.GetPixelSpan());

        using var canvas = new SKCanvas(bmp);
        using var font = new SKFont(SKTypeface.Default, 11);
        using var grid = new SKPaint { Color = new SKColor(255, 255, 255, 70), StrokeWidth = 1, IsAntialias = true };
        using var gridText = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var step = NiceStep((x1 - x0) / 6);
        for (var gx = Math.Ceiling(x0 / step) * step; gx <= x1; gx += step)
        {
            canvas.DrawLine(Px(gx), 0, Px(gx), height, grid);
            canvas.DrawText($"x {gx:0.##}", Px(gx) + 2, 12, font, gridText);
        }
        for (var gz = Math.Ceiling(z0 / step) * step; gz <= z1; gz += step)
        {
            canvas.DrawLine(0, Pz(gz), width, Pz(gz), grid);
            canvas.DrawText($"z {gz:0.##}", 2, Pz(gz) - 2, font, gridText);
        }

        var margin = Math.Max(x1 - x0, z1 - z0) * 0.02;
        var objects = editor.Find(new PropEditor.Query(Layer: layer, Rect: [x0 - margin, z0 - margin, x1 + margin, z1 + margin])).ToList();
        using var dot = new SKPaint { IsAntialias = true };
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0, 0, 0, 160), StrokeWidth = 1 };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.Red, StrokeWidth = 2 };
        using var label = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var labelBack = new SKPaint { Color = new SKColor(0, 0, 0, 150) };
        var labelled = objects.Count <= labelLimit;
        foreach (var (_, e) in objects.OrderBy(o => highlight?.Contains(o.Entity.Id) == true))
        {
            var (px, pz) = (Px(e.Position[0]), Pz(e.Position[2]));
            var r = (float)Math.Clamp(2.5 * Math.Sqrt(Math.Max(0.05, e.Scale[0])) * Math.Sqrt(ppu / 8), 2, 9);
            dot.Color = KindColours.GetValueOrDefault(e.Kind, SKColors.White);
            canvas.DrawCircle(px, pz, r, dot);
            canvas.DrawCircle(px, pz, r, outline);
            var hot = highlight?.Contains(e.Id) == true;
            if (hot) canvas.DrawCircle(px, pz, r + 4, ring);
            if (labelled || hot)
            {
                var text = hot ? $"{e.Id} {ShortName(e.Asset)}" : ShortName(e.Asset);
                var w = font.MeasureText(text);
                canvas.DrawRect(px + r + 2, pz - 7, w + 4, 13, labelBack);
                canvas.DrawText(text, px + r + 4, pz + 4, font, label);
            }
        }

        // legend
        using (var panel = new SKPaint { Color = new SKColor(0, 0, 0, 140) })
            canvas.DrawRect(2, height - 22 - 14 * KindColours.Count, 230, 20 + 14 * KindColours.Count, panel);
        var ly = height - 8f;
        foreach (var (kind, colour) in KindColours.Reverse())
        {
            dot.Color = colour;
            canvas.DrawCircle(10, ly - 4, 4, dot);
            canvas.DrawText(kind, 18, ly, font, gridText);
            ly -= 14;
        }
        canvas.DrawText($"{objects.Count} objects, {x1 - x0:0.##} x {z1 - z0:0.##} world units", 10, ly - 2, font, gridText);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
        using (var fs = File.Create(outPath)) data.SaveTo(fs);
        return outPath;
    }

    public static string ShortName(string asset)
    {
        var name = Path.GetFileNameWithoutExtension(asset.Replace('\\', '/'));
        return name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static void Shade(PropEditor editor, double x0, double zTop, double ppu, int width, int height, uint[] pixels)
    {
        editor.Terrain(); // load once before the parallel loop
        double Sample(double x, double z) => editor.GroundY(x, z);
        var d = Math.Max(1 / ppu, Campaign.CameraHeightmapStep.PixelSizeX); // gradient over at least one lf pixel
        Parallel.For(0, height, py =>
        {
            for (var px = 0; px < width; px++)
            {
                double x = x0 + (px + 0.5) / ppu, z = zTop - (py + 0.5) / ppu;
                var y = Sample(x, z);
                var dx = (Sample(x + d, z) - Sample(x - d, z)) / (2 * d);
                var dz = (Sample(x, z + d) - Sample(x, z - d)) / (2 * d);
                var light = Math.Clamp(0.65 + (-dx * 0.6 + dz * 0.6) * 1.5, 0.25, 1.15); // lit from the north-west
                double r, g, b;
                if (y < 0) (r, g, b) = (60, 95, 150);
                else
                {
                    var t = Math.Clamp(y / 12, 0, 1); // lowland green → highland brown/grey
                    (r, g, b) = (110 + 70 * t, 140 + 10 * t, 90 + 50 * t);
                }
                pixels[py * width + px] = 0xFF000000u | (uint)Math.Clamp(r * light, 0, 255) << 16
                                          | (uint)Math.Clamp(g * light, 0, 255) << 8 | (uint)Math.Clamp(b * light, 0, 255);
            }
        });
    }

    private static double NiceStep(double raw)
    {
        var p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var m = raw / p;
        return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * p;
    }
}
