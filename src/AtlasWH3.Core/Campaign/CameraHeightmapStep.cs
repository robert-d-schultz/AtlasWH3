using System.Diagnostics;
using System.Globalization;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// campaign_maps\&lt;map&gt;\camera_heightmap.png, BOB's "Generate Camera Height Map" (TOOLDATABUILDER::
/// generate_camera_height_map, tooldatabuilderdll 0x19de90; read off the WH3 kit, 2026-10-09). The generator is 3K's:
///  - settings from rules.bob [Terrain] cam_hmap_* (bob_terrain 0x33180). WH3's kit sets none, and BOB has no defaults,
///    so the resolution is 0 and the action fails on an empty grid; the defaults here are CA's resolution 0.25 (every
///    shipped vanilla combi map is 2880 × 1941 tiles × 0.25 = 720 × 486) and 8 samples per unit (the best fit to CA's
///    file);
///  - a ceil(tile map W · res) × ceil(tile map H · res) grid over the terrain bounds; cell (u, j) is centred at
///    (u · step x, j · step z) and keeps the max of the scene height over n × n points from its min corner (n = ceil(extent
///    · samples per unit), both loops run the z count) and its centre, starting from −1 (0x19fd40);
///  - pixel = ceil(max(h / highest, 0) · 65535), PNG row 0 = the north edge; tEXt height_scale = "%f" of highest / 65535.
/// The scene height is WS_TERRAIN_LOGIC::height, for a campaign max(height patches, tile terrain, lf terrain)
/// (warscape 0x5b20e0). Here it is <see cref="TreeHeightField"/>: the nearest full_logic_map texel raised by the layers'
/// height patches, for_camera_height_map_only ones included (the tile terrain is not modelled). So the mountains'
/// patches are in the map, unlike a map made from the raw heights. CA's own file can't be matched closely: it is
/// stale in places (vanilla combi's south-east was raised after it was made).
/// </summary>
public sealed class CameraHeightmapStep : ICampaignBuildStep
{
    /// <summary>Used when rules.bob has no cam_hmap_resolution_scale / cam_hmap_samples_per_wu.</summary>
    public const float DefaultResolutionScale = 0.25f;
    public const int DefaultSamplesPerUnit = 8;

    public string Name => "camera_heightmap";
    public string ReplacesBobAction => "Generate Camera Height Map";
    public IReadOnlyList<string> DependsOn => ["heightmaps", "tile_list"];

    /// <summary>BOB's CAMERA_HEIGHT_MAP_SETTINGS (rules.bob [Terrain] cam_hmap_*); <paramref name="FromRules"/> when
    /// both the resolution and the sample count were set.</summary>
    public sealed record Settings(float ResolutionScale, int SamplesPerUnit, bool ApplyBlur, int BlurKernel, float StandardDeviation,
                                  string? OutputTarget, bool FromRules);

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        else if (TerryProject.Load(ctx.TerryFile).WorldWidth is null && TreesStep.MapDataPath(ctx) is null)
            missing.Add("the .terry has no world_width and there is no map_data.esf (CAIME's output) to take the width from");
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir}");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var settings = ReadSettings(Path.Combine(ctx.Paths.AkTerrainDir, "..", "rules.bob"), Path.Combine(ctx.Paths.AkTerrainDir, "rules.bob"));
        if (!settings.FromRules)
            notes.Add($"rules.bob sets no cam_hmap_resolution_scale / cam_hmap_samples_per_wu (BOB then fails): using {settings.ResolutionScale} " +
                      $"and {settings.SamplesPerUnit}");
        if (settings.ApplyBlur) notes.Add("cam_hmap_apply_blur is on: BOB's blur is not ported, the map is written unblurred");
        if (!string.IsNullOrEmpty(settings.OutputTarget)) notes.Add($"cam_hmap_output_target '{settings.OutputTarget}' is ignored");

        var project = TerryProject.Load(ctx.TerryFile);
        var logicPath = TreesStep.LogicMapPath(ctx) ?? throw new FileNotFoundException("no full_logic_map.compressed_map (run step 'heightmaps')");
        var tileListPath = new[] { ctx.OutFile("tile_list.bin"), Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "tile_list.bin") }
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("no tile_list.bin (run step 'tile_list')");
        foreach (var p in new[] { logicPath, tileListPath })
            if (!p.StartsWith(ctx.TargetRoot, StringComparison.OrdinalIgnoreCase)) notes.Add($"read {p}");
        var tiles = TileList.Read(tileListPath);
        var worldWidth = project.WorldWidth ?? MapDataBounds.Read(TreesStep.MapDataPath(ctx)!).Width;

        ctx.Log("height patches (packs)...");
        var packs = GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks);
        var patches = TreeHeightField.LoadPatches(project, packs, notes, camera: true);
        var field = new TreeHeightField(CompressedMap.Read(logicPath), worldWidth, patches);
        ctx.Cancel.ThrowIfCancellationRequested();

        var (w, h) = GridSize(tiles.Ints[1], tiles.Ints[2], settings.ResolutionScale);
        ctx.Log($"sampling {w}x{h}...");
        var cells = Sample((x, z) => field.Height(x, z), w, h, field.WorldWidth, field.WorldDepth, settings.SamplesPerUnit);
        if (FillOffMap(cells, w, h) is > 0 and var filled)
            notes.Add($"{filled} cells off the terrain at the map's edges take the nearest terrain cell's height");
        var highest = cells.Max();
        var (raster, scale) = Encode(cells, w, h, highest);
        Directory.CreateDirectory(ctx.CampaignMapOutDir);
        var outPath = Path.Combine(ctx.CampaignMapOutDir, "camera_heightmap.png");
        File.WriteAllBytes(outPath, PngLib.Encode16(raster, new Dictionary<string, string> { ["height_scale"] = scale }));
        notes.Add($"{w}x{h} from the {tiles.Ints[1]}x{tiles.Ints[2]} tile map, {settings.SamplesPerUnit} samples per unit; " +
                  $"highest sampled height {highest:F6} (height_scale {scale})");
        return new StepResult(Name, [outPath], notes, sw.Elapsed);
    }

    /// <summary>The cell grid: ceil(terrain size in tiles · resolution scale), in float as BOB computes it.</summary>
    public static (int W, int H) GridSize(int tilesW, int tilesH, float resolutionScale) =>
        ((int)MathF.Ceiling(tilesW * resolutionScale), (int)MathF.Ceiling(tilesH * resolutionScale));

    /// <summary>The settings of the rules.bob files in order (later files win), as bob_terrain reads them.</summary>
    public static Settings ReadSettings(params string[] rulesBobs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rulesBob in rulesBobs.Where(File.Exists))
            foreach (var line in File.ReadAllLines(rulesBob))
                if (line.Split('=', 2) is [var k, var v] && k.Trim().StartsWith("cam_hmap_", StringComparison.OrdinalIgnoreCase))
                    values[k.Trim()] = v.Trim();
        float F(string k, float d) => values.TryGetValue(k, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : d;
        bool B(string k) => values.TryGetValue(k, out var v) && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
        var fromRules = values.ContainsKey("cam_hmap_resolution_scale") && values.ContainsKey("cam_hmap_samples_per_wu");
        return new Settings(F("cam_hmap_resolution_scale", DefaultResolutionScale), (int)F("cam_hmap_samples_per_wu", DefaultSamplesPerUnit),
            B("cam_hmap_apply_blur"), (int)F("cam_hmap_blur_kernel", 0f), F("cam_hmap_standard_drv", 0f),
            values.GetValueOrDefault("cam_hmap_output_target"), fromRules);
    }

    /// <summary>The per-cell maxima in BOB's order (row j = z index, south first).</summary>
    public static float[] Sample(Func<float, float, float> height, int w, int h, float sceneW, float sceneD, int samplesPerUnit)
    {
        var stepX = sceneW / w;
        var stepZ = sceneD / h;
        var halfX = stepX * 0.5f;
        var halfZ = stepZ * 0.5f;
        var cells = new float[w * h];
        Parallel.For(0, h, j =>
        {
            var cz = j * stepZ;
            var minZ = cz - halfZ;
            var maxZ = cz + halfZ;
            var dz = maxZ - minZ;
            var nz = (ushort)(int)MathF.Ceiling(dz * samplesPerUnit);      // BOB keeps the counts as u16
            var sz = dz / nz;
            for (var u = 0; u < w; u++)
            {
                var cx = u * stepX;
                var minX = cx - halfX;
                var maxX = cx + halfX;
                var dx = maxX - minX;
                var nx = (ushort)(int)MathF.Ceiling(dx * samplesPerUnit);
                var sx = dx / nx;
                var best = -1f;
                var z = minZ;
                for (var a = 0; a < nz; a++)
                {
                    var x = minX;
                    for (var b = 0; b < nz; b++)                // BOB: the inner loop also runs the z count
                    {
                        var v = height(x, z);
                        if (best < v) best = v;
                        x += sx;
                    }
                    z += sz;
                }
                var centre = height((maxX - minX) * 0.5f + minX, (maxZ - minZ) * 0.5f + minZ);
                if (best < centre) best = centre;
                cells[j * w + u] = best;
            }
        });
        return cells;
    }

    /// <summary>
    /// Cells off the terrain (no height above 0: the map's edges outside the tiled area, where full_logic_map is 0)
    /// that are connected to the map border take the value of the nearest cell with terrain, so the edge carries on the
    /// sea or coast next to it instead of dropping to 0, below the sea surface. CA's vanilla combi file does the same in
    /// effect (flat sea values out to the edge, 2026-10-10). Zero cells inside the map (lake bowls) are kept. Returns
    /// the number of cells filled.
    /// </summary>
    public static int FillOffMap(float[] cells, int w, int h)
    {
        var off = new bool[w * h];
        var queue = new Queue<int>();
        void Mark(int i) { if (!off[i] && cells[i] <= 0f) { off[i] = true; queue.Enqueue(i); } }
        for (var u = 0; u < w; u++) { Mark(u); Mark((h - 1) * w + u); }
        for (var j = 0; j < h; j++) { Mark(j * w); Mark(j * w + w - 1); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int u = i % w, j = i / w;
            if (u > 0) Mark(i - 1);
            if (u < w - 1) Mark(i + 1);
            if (j > 0) Mark(i - w);
            if (j < h - 1) Mark(i + w);
        }
        // breadth first from the terrain cells bordering the marked region: each marked cell takes its nearest one's value
        for (var i = 0; i < off.Length; i++)
        {
            if (off[i]) continue;
            int u = i % w, j = i / w;
            if (u > 0 && off[i - 1] || u < w - 1 && off[i + 1] || j > 0 && off[i - w] || j < h - 1 && off[i + w]) queue.Enqueue(i);
        }
        var filled = 0;
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int u = i % w, j = i / w;
            foreach (var n in new[] { u > 0 ? i - 1 : -1, u < w - 1 ? i + 1 : -1, j > 0 ? i - w : -1, j < h - 1 ? i + w : -1 })
            {
                if (n < 0 || !off[n]) continue;
                off[n] = false;
                cells[n] = cells[i];
                filled++;
                queue.Enqueue(n);
            }
        }
        return filled;
    }

    /// <summary>16-bit pixels (row 0 = north) and the height_scale text.</summary>
    public static (Raster<ushort> Raster, string Scale) Encode(float[] cells, int w, int h, float highest)
    {
        var raster = new Raster<ushort>(w, h);
        for (var j = 0; j < h; j++)
            for (var u = 0; u < w; u++)
            {
                var r = cells[j * w + u] / highest;
                raster.Data[(h - 1 - j) * w + u] = (ushort)MathF.Ceiling(MathF.Max(r, 0f) * 65535f);
            }
        var scale = ((double)(highest * (1f / 65535f))).ToString("F6", CultureInfo.InvariantCulture);
        return (raster, scale);
    }
}
