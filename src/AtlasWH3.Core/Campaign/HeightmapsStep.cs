using System.Diagnostics;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// BOB's "Campaign Heightmap" and "Campaign Shroud Heights", from the .terry's composited height maps
/// (<see cref="TerrainComposite"/>; all three are stored bottom row first):
///  - full_logic_map.compressed_map: the Height map normalised to its own range, v = trunc((h − lo) · (1/(hi − lo)) ·
///    65535) in float32, header (0, lo, 0, 0, hi, 0), 16 × 16 tiles with WH3's 3 padding bytes after each bit-packed
///    tile. Byte-identical to BOB's on IEE and Old World.
///  - shroud_heights.dds: the HeightShroud map as R32_FLOAT. Byte-identical to BOB's on IEE and Old World.
///  - lf_normal.dds: the Height map's normals as Terry exports them to lf_normal.png (<see cref="LfNormalMap"/>), in the
///    DXT5nm layout BOB's NVTT writes; no Terry export or PNG in between.
///  - full_height_map.dds: Height in red, HeightSea in green, as BC6H_SF16 (<see cref="Bc6h"/>; BOB's AMD Compress
///    output is not reproduced bit for bit; the RMS error against the source is about half BOB's on both maps, see
///    docs/native_campaign_build.md).
/// </summary>
public sealed class HeightmapsStep : ICampaignBuildStep
{
    public string Name => "heightmaps";
    public string ReplacesBobAction => "Campaign Heightmap, Campaign Shroud Heights";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        if (!File.Exists(ctx.TerryFile)) return [$"missing {ctx.TerryFile}"];
        var project = TerryProject.Load(ctx.TerryFile);
        var missing = new List<string>();
        foreach (var type in new[] { "Height", "HeightSea", "HeightShroud" })
        {
            if (project.Find(type) is not { } map) { missing.Add($"no {type} map in the .terry"); continue; }
            try { TerrainComposite.Inputs(project, map); }
            catch (FileNotFoundException e) { missing.Add(e.Message); }
        }
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var project = TerryProject.Load(ctx.TerryFile);
        Directory.CreateDirectory(ctx.TerrainOutDir);
        var notes = new List<string>();

        ctx.Log("Height...");
        var land = TerrainComposite.Heights(project, "Height");
        var logicPath = ctx.OutFile("full_logic_map.compressed_map");
        var (lo, hi) = WriteLogicMap(land, logicPath);
        notes.Add($"logic map {land.Width}x{land.Height}, heights {lo} .. {hi}");
        ctx.Cancel.ThrowIfCancellationRequested();

        ctx.Log("lf_normal.dds...");
        var normalPath = ctx.OutFile("lf_normal.dds");
        File.WriteAllBytes(normalPath, LfNormalMap.ToDds(LfNormalMap.Compute(land, LfNormalMap.Spacing), land.Width, land.Height));
        ctx.Cancel.ThrowIfCancellationRequested();

        ctx.Log("HeightSea...");
        var sea = TerrainComposite.Heights(project, "HeightSea");
        if (sea.Width != land.Width || sea.Height != land.Height)
            throw new InvalidDataException($"HeightSea is {sea.Width}x{sea.Height}, Height is {land.Width}x{land.Height}");
        ctx.Log("full_height_map.dds (BC6H)...");
        var heightPath = ctx.OutFile("full_height_map.dds");
        WriteHeightMap(land, sea, heightPath, p => ctx.Log($"BC6H {p:P0}"), ctx.HeightMapBlocks);
        if (ctx.HeightMapBlocks is not null) notes.Add("full_height_map.dds: only the blocks the build asked for are encoded, the rest are zero");
        ctx.Cancel.ThrowIfCancellationRequested();

        ctx.Log("HeightShroud...");
        var shroud = TerrainComposite.Heights(project, "HeightShroud");
        var shroudPath = ctx.OutFile("shroud_heights.dds");
        WriteShroud(shroud, shroudPath);
        return new StepResult(Name, [logicPath, normalPath, heightPath, shroudPath], notes, sw.Elapsed);
    }

    /// <summary>full_logic_map.compressed_map from the composited Height map (row 0 = north); returns (lo, hi).</summary>
    public static (float Lo, float Hi) WriteLogicMap(Raster<float> land, string path)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in land.Data) { if (v < lo) lo = v; if (v > hi) hi = v; }
        var inv = hi > lo ? 1f / (hi - lo) : 0f;
        var values = new Raster<ushort>(land.Width, land.Height);
        Parallel.For(0, land.Height, y =>
        {
            var src = (land.Height - 1 - y) * land.Width;
            var dst = y * land.Width;
            for (var x = 0; x < land.Width; x++)
                values.Data[dst + x] = (ushort)(int)((land.Data[src + x] - lo) * inv * 65535f);
        });
        CompressedMap.Write(path, values, [0, lo, 0, 0, hi, 0], 3, 16, CompressedMap.Wh3BitTilePadding);
        return (lo, hi);
    }

    /// <summary>full_height_map.dds: BC6H_SF16, red = land, green = sea bed, bottom row first. <paramref name="blocks"/>
    /// (block column, block row in file order) encodes only some blocks.</summary>
    public static void WriteHeightMap(Raster<float> land, Raster<float> sea, string path, Action<double>? progress = null,
                                      Func<int, int, bool>? blocks = null)
    {
        int w = land.Width, h = land.Height;
        var red = new float[w * h];
        var green = new float[w * h];
        Parallel.For(0, h, y =>
        {
            Array.Copy(land.Data, (h - 1 - y) * w, red, y * w, w);
            Array.Copy(sea.Data, (h - 1 - y) * w, green, y * w, w);
        });
        var data = Bc6h.Encode(red, green, w, h, progress, blocks);
        using var f = File.Create(path);
        f.Write(DdsHeader.BuildDx10(w, h, DdsHeader.DxgiBc6hSf16, true, Bc6h.BlockBytes));
        f.Write(data);
    }

    /// <summary>shroud_heights.dds: R32_FLOAT, bottom row first.</summary>
    public static void WriteShroud(Raster<float> shroud, string path)
    {
        int w = shroud.Width, h = shroud.Height;
        using var f = File.Create(path);
        f.Write(DdsHeader.BuildDx10(w, h, DdsHeader.DxgiR32Float, false, 4));
        var row = new byte[w * 4];
        for (var y = h - 1; y >= 0; y--)
        {
            Buffer.BlockCopy(shroud.Data, y * w * 4, row, 0, row.Length);
            f.Write(row);
        }
    }
}
