using System.Diagnostics;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// BOB's "Campaign Heightmap" and "Campaign Shroud Heights", from the .terry's composited Height and HeightSea maps
/// (<see cref="TerrainComposite"/>; all are stored bottom row first):
///  - full_logic_map.compressed_map: the Height map normalised to its own range, v = trunc((h − lo) · (1/(hi − lo)) ·
///    65535) in float32, header (0, lo, 0, 0, hi, 0), 16 × 16 tiles with WH3's 3 padding bytes after each bit-packed
///    tile. Byte-identical to BOB's on IEE and Old World.
///  - shroud_heights.dds: R32_FLOAT, computed from the Height map and the layers' height patches (read from the packs)
///    as Terry's Fill From Terrain makes it (<see cref="ShroudHeights"/>). The .terry's HeightShroud layers are not
///    read; BOB copied them.
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
        foreach (var type in new[] { "Height", "HeightSea" })
        {
            if (project.Find(type) is not { } map) { missing.Add($"no {type} map in the .terry"); continue; }
            try { TerrainComposite.Inputs(project, map); }
            catch (FileNotFoundException e) { missing.Add(e.Message); }
        }
        // the shroud's height patches: their models come from the packs, placed by the world width
        if (project.WorldWidth is null && !GameSetup.HasCampaignMapFile(ctx.Paths, ctx.CampaignMapName, "map_data.esf"))
            missing.Add("the .terry has no world_width, and " + GameSetup.CampaignMapFileMissing(ctx.Paths, ctx.CampaignMapName, "map_data.esf") +
                        " to take the width from");
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir}");
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
        var logic = WriteLogicMap(land, logicPath);
        notes.Add($"logic map {land.Width}x{land.Height}, heights {logic.Header[1]} .. {logic.Header[4]}");
        ctx.Cancel.ThrowIfCancellationRequested();

        ctx.Log("lf_normal.dds...");
        var normalPath = ctx.OutFile("lf_normal.dds");
        File.WriteAllBytes(normalPath, LfNormalMap.ToDds(LfNormalMap.Compute(land, LfNormalMap.Spacing), land.Width, land.Height));
        ctx.Cancel.ThrowIfCancellationRequested();

        ctx.Log("HeightSea...");
        var sea = TerrainComposite.Heights(project, "HeightSea");
        if (sea.Width != land.Width || sea.Height != land.Height)
            throw new InvalidDataException($"HeightSea is {sea.Width}x{sea.Height}, Height is {land.Width}x{land.Height}");
        var reference = Reference(ctx, land, notes);
        ctx.Log("full_height_map.dds (BC6H)...");
        var heightPath = ctx.OutFile("full_height_map.dds");
        var copied = WriteHeightMap(land, sea, heightPath, p => ctx.Log($"BC6H {p:P0}"), ctx.HeightMapBlocks, reference);
        if (ctx.HeightMapBlocks is not null) notes.Add("full_height_map.dds: only the blocks the build asked for are encoded, the rest are zero");
        if (reference is not null) notes.Add($"full_height_map.dds: {copied} blocks copied from {ctx.HeightMapReference!.Value.HeightMap}");
        ctx.Cancel.ThrowIfCancellationRequested();

        ctx.Log("shroud heights (height patches from the packs)...");
        var worldWidth = project.WorldWidth ?? MapDataBounds.Read(EsfTree.Read(TreesStep.MapData(ctx)!.Value.Data)).Width;
        var packs = GameSetup.OpenWithMods(ctx.Paths.GameDataDir, ctx.Paths.ModPacks);
        var field = new TreeHeightField(logic, worldWidth, TreeHeightField.LoadPatches(project, packs, notes));
        var zeroWestOf = ctx.ZeroShroudWestEdge ? ShroudHeights.WestEdgeZeroWidth : 0;
        var shroud = ShroudHeights.Compute(land, field, zeroWestOf);
        var shroudPath = ctx.OutFile("shroud_heights.dds");
        WriteShroud(shroud, shroudPath);
        notes.Add($"shroud {shroud.Width}x{shroud.Height}: the heights and height patches + {ShroudHeights.Offset}" +
                  (project.Find("HeightShroud") is { } painted && painted.Layers.Any(l => l.Composited)
                      ? "; the .terry's HeightShroud layers are not used" : ""));
        if (zeroWestOf > 0)
            notes.Add($"shroud: 0 west of x = {zeroWestOf:G9} (the first {(int)MathF.Ceiling(zeroWestOf / (2 * worldWidth / land.Width))} columns)");
        return new StepResult(Name, [logicPath, normalPath, heightPath, shroudPath], notes, sw.Elapsed);
    }

    /// <summary>full_logic_map.compressed_map from the composited Height map (row 0 = north); returns the map written
    /// (header [1] = lo, [4] = hi).</summary>
    public static CompressedMap.Map WriteLogicMap(Raster<float> land, string path)
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
        float[] header = [0, lo, 0, 0, hi, 0];
        CompressedMap.Write(path, values, header, 3, 16, CompressedMap.Wh3BitTilePadding);
        return new CompressedMap.Map(values, header, 3, 16, 16);
    }

    /// <summary>The reference land, sea and full_height_map.dds of <see cref="CampaignBuildContext.HeightMapReference"/>,
    /// or null (with a note) when it does not apply: a file of another size, or older than the reference's sources.</summary>
    private static HeightMapReference? Reference(CampaignBuildContext ctx, Raster<float> land, List<string> notes)
    {
        if (ctx.HeightMapReference is not { } r) return null;
        var (terry, dds) = r;
        if (!File.Exists(dds) || !File.Exists(terry)) { notes.Add($"full_height_map.dds: no reference {dds}, all blocks encoded"); return null; }
        var project = TerryProject.Load(terry);
        var sources = new[] { "Height", "HeightSea" }
            .SelectMany(t => project.Find(t) is { } m ? TerrainComposite.Inputs(project, m).Select(i => i.Tif) : [])
            .Append(terry);
        if (File.GetLastWriteTimeUtc(dds) <= sources.Max(File.GetLastWriteTimeUtc))
        {
            notes.Add($"full_height_map.dds: {dds} is older than its sources, all blocks encoded");
            return null;
        }
        var header = DdsHeader.BuildDx10(land.Width, land.Height, DdsHeader.DxgiBc6hSf16, true, Bc6h.BlockBytes);
        var bytes = File.ReadAllBytes(dds);
        if (bytes.Length != header.Length + (land.Width + 3) / 4 * ((land.Height + 3) / 4) * Bc6h.BlockBytes ||
            !bytes.AsSpan(0, header.Length).SequenceEqual(header))
        {
            notes.Add($"full_height_map.dds: {dds} is not a {land.Width}x{land.Height} BC6H map, all blocks encoded");
            return null;
        }
        ctx.Log("reference Height, HeightSea...");
        Raster<float>? refLand = null, refSea = null;
        Parallel.Invoke(() => refLand = TerrainComposite.Heights(project, "Height"),
                        () => refSea = TerrainComposite.Heights(project, "HeightSea"));
        if (refLand!.Width != land.Width || refLand.Height != land.Height || refSea!.Width != land.Width || refSea.Height != land.Height)
        {
            notes.Add($"full_height_map.dds: {terry}'s maps are another size, all blocks encoded");
            return null;
        }
        return new HeightMapReference(refLand, refSea, bytes.AsMemory(header.Length));
    }

    /// <summary>Another project's composited land and sea maps (the same size) and its full_height_map.dds blocks.</summary>
    public sealed record HeightMapReference(Raster<float> Land, Raster<float> Sea, ReadOnlyMemory<byte> Blocks);

    /// <summary>full_height_map.dds: BC6H_SF16, red = land, green = sea bed, bottom row first. <paramref name="blocks"/>
    /// (block column, block row in file order) encodes only some blocks. A block whose texels equal
    /// <paramref name="reference"/>'s is copied from it. Returns the number of blocks copied.</summary>
    public static long WriteHeightMap(Raster<float> land, Raster<float> sea, string path, Action<double>? progress = null,
                                      Func<int, int, bool>? blocks = null, HeightMapReference? reference = null)
    {
        int w = land.Width, h = land.Height;
        var red = new float[w * h];
        var green = new float[w * h];
        Parallel.For(0, h, y =>
        {
            Array.Copy(land.Data, (h - 1 - y) * w, red, y * w, w);
            Array.Copy(sea.Data, (h - 1 - y) * w, green, y * w, w);
        });
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        var same = reference is null ? null : SameBlocks(land, sea, reference);
        bool Wanted(int bx, int by) => blocks?.Invoke(bx, by) ?? true;
        var data = Bc6h.Encode(red, green, w, h, progress, same is null ? blocks : (bx, by) => !same[by * bw + bx] && Wanted(bx, by));
        long copied = 0;
        if (same is not null)
            for (var by = 0; by < bh; by++)
                for (var bx = 0; bx < bw; bx++)
                {
                    var i = by * bw + bx;
                    if (!same[i] || !Wanted(bx, by)) continue;
                    reference!.Blocks.Span.Slice(i * Bc6h.BlockBytes, Bc6h.BlockBytes).CopyTo(data.AsSpan(i * Bc6h.BlockBytes));
                    copied++;
                }
        using var f = File.Create(path);
        f.Write(DdsHeader.BuildDx10(w, h, DdsHeader.DxgiBc6hSf16, true, Bc6h.BlockBytes));
        f.Write(data);
        return copied;
    }

    /// <summary>Per block (file order), whether its 16 texels as <see cref="Bc6h.Encode"/> reads them (edges clamped)
    /// are bit for bit the reference's, in both maps.</summary>
    private static bool[] SameBlocks(Raster<float> land, Raster<float> sea, HeightMapReference reference)
    {
        int w = land.Width, h = land.Height, bw = (w + 3) / 4, bh = (h + 3) / 4;
        var same = new bool[bw * bh];
        Parallel.For(0, bh, by =>
        {
            for (var bx = 0; bx < bw; bx++)
            {
                var equal = true;
                for (var j = 0; j < 4 && equal; j++)
                {
                    var row = (h - 1 - Math.Min(by * 4 + j, h - 1)) * w;
                    for (var i = 0; i < 4 && equal; i++)
                    {
                        var k = row + Math.Min(bx * 4 + i, w - 1);
                        equal = BitConverter.SingleToInt32Bits(land.Data[k]) == BitConverter.SingleToInt32Bits(reference.Land.Data[k]) &&
                                BitConverter.SingleToInt32Bits(sea.Data[k]) == BitConverter.SingleToInt32Bits(reference.Sea.Data[k]);
                    }
                }
                same[by * bw + bx] = equal;
            }
        });
        return same;
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
