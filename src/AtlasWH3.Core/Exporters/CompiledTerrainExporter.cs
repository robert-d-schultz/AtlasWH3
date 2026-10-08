using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Exporters;

/// <summary>
/// Writes compiled campaign-terrain files straight from the assembly-kit sources, without BOB, into a pack-shaped
/// folder (<c>&lt;target&gt;\terrain\campaigns\&lt;map&gt;\…</c>) that can be dropped into RPFM.
///
/// Phase 1 (rasters), each reproducing BOB byte for byte on the vanilla sources:
///  - lf_height_map.compressed_map / .dds      from the LowFrequencyHeight TIFF
///  - lf_sea_height_map.compressed_map / .dds  from the LowFrequencyHeightSea TIFF
///  - climate_map.cm                           from climate_map.png (quarter resolution, or full resolution sampled 4x)
/// BOB normalises each u16 source to its own range: v = trunc(f32(src - lo) / f32(hi - lo) * 65535), and stores
/// lo/65535 and hi/65535 in header floats 1 and 4. The .dds holds the same normalised values.
/// </summary>
public sealed class CompiledTerrainExporter
{
    public sealed record Result(string TargetDir, IReadOnlyList<string> Written, IReadOnlyList<string> Notes);

    /// <summary>climate_map.png colour (0xRRGGBB) → climate index: 0 cold, 1 arid, 2 temperate, 3 sub_tropical (the
    /// same indices global_blend.dds byte 1 carries). Verified exactly against vanilla climate_map.cm.</summary>
    public static readonly IReadOnlyDictionary<uint, ushort> ClimateColours = new Dictionary<uint, ushort>
    {
        [0x005555] = 0,
        [0xFFAA00] = 1,
        [0x005500] = 2,
        [0xAAAA55] = 3,
    };

    private readonly ProjectPaths _paths;

    public CompiledTerrainExporter(ProjectPaths paths) => _paths = paths;

    public string TerrainOutDir(string targetRoot) => Path.Combine(targetRoot, "terrain", "campaigns", _paths.MapName);

    public Result ExportRasters(string? targetRoot = null, Action<string>? log = null)
    {
        targetRoot ??= Path.Combine(_paths.OutputRoot, "compiled", _paths.MapName);
        var outDir = TerrainOutDir(targetRoot);
        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        var notes = new List<string>();

        var project = TerryProject.Load(Path.Combine(_paths.AkTerrainDir, _paths.MapName + ".terry"));
        string LayerTif(string type) =>
            project.LayerTifPath(project.Find(type) ?? throw new InvalidDataException($"No {type} map in the .terry"));

        log?.Invoke("height map...");
        var height = TiffMap.ReadGray16(LayerTif("LowFrequencyHeight"));
        written.AddRange(WriteNormalised(height, outDir, "lf_height_map"));
        log?.Invoke("sea height map...");
        written.AddRange(WriteNormalised(TiffMap.ReadGray16(LayerTif("LowFrequencyHeightSea")), outDir, "lf_sea_height_map"));

        // climate_map.cm is a quarter of the lf grid: the largest climate map, sampled every step-th pixel
        log?.Invoke("climate map...");
        var (climatePath, climateRgba) = LargestClimateMap();
        var step = Math.Max(1, climateRgba.Width / (height.Width / 4));
        if (step > 1) notes.Add($"climate: sampled {Path.GetFileName(climatePath)} every {step}th pixel");
        var climate = ClimateIndices(climateRgba, step, out var unknown);
        if (unknown > 0) notes.Add($"climate: {unknown:N0} pixels had a colour outside the climate table (written as 0)");
        var cm = Path.Combine(outDir, "climate_map.cm");
        CompressedMap.Write(cm, climate, [0, 0, 0, 0, 65535, 0]);
        written.Add(cm);

        return new Result(targetRoot, written, notes);
    }

    /// <summary>BOB's normalisation of a u16 source to its own min–max range.</summary>
    public static (Raster<ushort> Values, float[] Header) Normalise(Raster<ushort> source)
    {
        ushort lo = ushort.MaxValue, hi = 0;
        foreach (var v in source.Data) { if (v < lo) lo = v; if (v > hi) hi = v; }
        var result = new Raster<ushort>(source.Width, source.Height);
        if (hi > lo)
        {
            var range = (float)(hi - lo);
            for (var i = 0; i < source.Data.Length; i++)
                result.Data[i] = (ushort)(float)((float)(source.Data[i] - lo) / range * 65535f);
        }
        return (result, [0, lo / 65535f, 0, 0, hi / 65535f, 0]);
    }

    private static IEnumerable<string> WriteNormalised(Raster<ushort> source, string outDir, string name)
    {
        var (values, header) = Normalise(source);
        var cmPath = Path.Combine(outDir, name + ".compressed_map");
        CompressedMap.Write(cmPath, values, header);
        var ddsPath = Path.Combine(outDir, name + ".dds");
        TerrainDds.WriteL16(ddsPath, values);
        return [cmPath, ddsPath];
    }

    /// <summary>The full-resolution climate map is the master (climate_map.png and climate_map_g.png get swapped
    /// between resolutions); sampling it avoids drift from a hand-downscaled quarter map.</summary>
    private (string Path, Raster<uint> Rgba) LargestClimateMap()
    {
        var maps = new[] { "climate_map.png", "climate_map_g.png" }
            .Select(n => Path.Combine(_paths.AkTerrainDir, n)).Where(File.Exists)
            .Select(p => (Path: p, Rgba: PngMap.Read(p))).ToList();
        if (maps.Count == 0) throw new FileNotFoundException("No climate_map.png in the AK map folder.");
        return maps.MaxBy(m => m.Rgba.Width);
    }

    private static Raster<ushort> ClimateIndices(Raster<uint> rgba, int step, out int unknown)
    {
        var w = rgba.Width / step;
        var h = rgba.Height / step;
        var result = new Raster<ushort>(w, h);
        unknown = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = rgba[x * step, y * step]; // 0xAABBGGRR
            var rgb = ((p & 0xFF) << 16) | (p & 0xFF00) | ((p >> 16) & 0xFF);
            if (ClimateColours.TryGetValue(rgb, out var index)) result[x, y] = index;
            else unknown++;
        }
        return result;
    }
}
