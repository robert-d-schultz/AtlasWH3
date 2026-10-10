using AtlasWH3.Core;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

/// <summary>lfnormal-* commands: research on Terry's lf_normal export.</summary>
static class NormalCommands
{
    public static readonly string[] Names = ["lfnormal-raw"];

    public static int Run(ProjectPaths paths, string command, string[] a) => command switch
    {
        "lfnormal-raw" => Raw(paths, a),
        _ => throw new ArgumentException($"unknown command {command}"),
    };

    /// <summary>lfnormal-raw &lt;out.raw&gt; [--spacing s] [--dds out.dds [--from-raw rgba]]: the map's lf_normal as raw RGBA (width × height × 4), from the
    /// composited Height map; the spacing defaults to the .terry's world width / map width.</summary>
    private static int Raw(ProjectPaths paths, string[] a)
    {
        var project = TerryProject.Load(Path.Combine(paths.AkTerrainDir, paths.MapName + ".terry"));
        var heights = TerrainComposite.Heights(project, "Height");
        var spacing = a.SkipWhile(s => s != "--spacing").Skip(1).Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault((project.WorldWidth ?? 1) / heights.Width);
        Console.WriteLine($"{heights.Width}x{heights.Height}, spacing {spacing:R}");
        if (a.Contains("--patches"))
        {
            var notes = new List<string>();
            var packs = GameSetup.OpenWithLinked(paths.GameDataDir, paths.ModPacks);
            var patches = AtlasWH3.Core.Campaign.Trees.TreeHeightField.LoadPatches(project, packs, notes, a.Contains("--camera"));
            notes.ForEach(Console.WriteLine);
            // pixel centres: x = (px + 0.5) · ww / W, z = (H − py − 0.5) · ww / W · 1.15476 (row 0 north); --corner: no 0.5
            var ww = project.WorldWidth ?? throw new InvalidDataException("no world_width");
            var off = a.Contains("--corner") ? 0f : 0.5f;
            float step = ww / heights.Width, zs = AtlasWH3.Core.Campaign.Trees.TreeHeightField.ZScale;
            var src = heights.Data;
            var patched = (float[])src.Clone();
            var raised = 0L;
            foreach (var p in patches)
            {
                int x0 = Math.Max(0, (int)(p.AabbMinX / step) - 1), x1 = Math.Min(heights.Width - 1, (int)(p.AabbMaxX / step) + 1);
                int r0 = Math.Max(0, (int)(heights.Height - p.AabbMaxZ / zs / step) - 2), r1 = Math.Min(heights.Height - 1, (int)(heights.Height - p.AabbMinZ / zs / step) + 1);
                for (var py = r0; py <= r1; py++)
                    for (var px = x0; px <= x1; px++)
                    {
                        var i = py * heights.Width + px;
                        if (p.Sample((px + off) * step, (heights.Height - py - off) * step * zs, src[i]) is { } h && h > patched[i]) { patched[i] = h; raised++; }
                    }
            }
            Console.WriteLine($"{patches.Count} patches raised {raised} pixel samples");
            heights = new Raster<float>(heights.Width, heights.Height, patched);
        }
        var rgba = LfNormalMap.Compute(heights, spacing);
        File.WriteAllBytes(a[0], rgba);
        if (a.SkipWhile(x => x != "--dds").Skip(1).FirstOrDefault() is { } dds)
        {
            // --from-raw: encode another RGBA image of the same size instead (BOB's input, to compare the encoders)
            if (a.SkipWhile(x => x != "--from-raw").Skip(1).FirstOrDefault() is { } other)
                rgba = File.ReadAllBytes(other);
            File.WriteAllBytes(dds, LfNormalMap.ToDds(rgba, heights.Width, heights.Height));
        }
        return 0;
    }
}
