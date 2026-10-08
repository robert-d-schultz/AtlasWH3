using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>One problem found in a tile map or its companion inputs. Hexes are [col, row] (row 0 = south).</summary>
public sealed record TileMapFinding(string Code, string Severity, string Message, int Count, IReadOnlyList<int[]> Hexes)
{
    public const string Error = "error", Warning = "warning", Info = "info";
    /// <summary>Every flagged hex (Hexes is capped), for the overlay.</summary>
    [JsonIgnore] public IReadOnlyList<int[]> AllHexes { get; init; } = Hexes;
}

public sealed record TileMapReport(string Map, string TileMap, int HexWidth, int HexHeight, IReadOnlyList<TileMapFinding> Findings,
                                   TileMatchSummary? Simulation = null)
{
    public int Errors => Findings.Count(f => f.Severity == TileMapFinding.Error);
    public int Warnings => Findings.Count(f => f.Severity == TileMapFinding.Warning);
}

public sealed record TileMapCheckOptions
{
    /// <summary>Tile map to check; default <c>&lt;AK terrain dir&gt;\tile_map.png</c>.</summary>
    public string? TileMap { get; init; }
    /// <summary>Folder with climate_map.png / climate_map_g.png; default the tile map's folder.</summary>
    public string? ClimateDir { get; init; }
    /// <summary>Loose _tile_database folder; default the Vanilla copy, else the game packs.</summary>
    public string? DatabaseDir { get; init; }
    /// <summary>CA's tile map, the reference for neighbourhood patterns BOB has tiles for.</summary>
    public string? VanillaTileMap { get; init; }
    /// <summary>Also run the BOB tile-matching simulation.</summary>
    public bool Simulate { get; init; }
    /// <summary>Progress messages from the simulation.</summary>
    public Action<string>? Log { get; init; }
    /// <summary>Skip the checks of kit files other than the tile map and climate maps.</summary>
    public bool TileMapOnly { get; init; }
    /// <summary>Write the simulated tile placements here (CSV: location,x,y,rotation,climate,layer).</summary>
    public string? SimulationCsv { get; init; }
}

/// <summary>
/// Pre-flight checks of a campaign tile map before BOB's Terrain / Tilemap action (or the native tile_list step):
/// file and layout, palette, climate map, the sizes and DB extents BOB reads alongside it, and the hex-level
/// line / coast / river / crossing rules CA's own map follows. Rule sources: research/guandu/caime_3k_tilemap_issue.md,
/// research/main190/tile_repair.py, docs/bob_campaign_build.md, docs/guandu_campaign_build.md.
/// </summary>
public static class TileMapValidator
{
    public const int MaxListedHexes = 200;
    private const double HexX = 0.668, HexZ = 0.772;

    // hex categories, as research/main190/tile_repair.py CAT
    internal const int Black = 0, Sea = 1, Beach = 2, Cliff = 3, CliffEnd = 4, River = 5, Start = 6, Mouth = 7, Crossing = 8,
        Road = 9, Area = 10, Canal = 11;

    public static int Category(string? tileSet) => tileSet switch
    {
        null => Area,
        "generic_sea" => Sea,
        "sea_coast" => Beach,
        "blockout_cliff" => Cliff,
        "blockout_cliff_ends" => CliffEnd,
        "river" => River,
        "river_start" => Start,
        "river_mouth" => Mouth,
        "river_crossing" or "river_crossing_imperial" or "river_crossing_track" => Crossing,
        "roads_paved" or "roads_imperial" or "roads_tracks" => Road,
        "canal" or "canal_links" => Canal,
        _ => Area,
    };

    public static string DefaultVanillaTileMap(ProjectPaths paths) =>
        Path.Combine(Path.GetDirectoryName(paths.VanillaRoot)!, "3k_dlc07_main_map", "tile_map.png");

    public static string DefaultDatabaseDir(ProjectPaths paths) =>
        Path.Combine(Path.GetDirectoryName(paths.VanillaRoot)!, "terrain", "tiles", "campaign", "_tile_database");

    /// <summary>The campaign tile database: a loose folder if there is one, else the game packs (what BOB reads).</summary>
    public static CampaignTileDatabase LoadDatabase(ProjectPaths paths, string? databaseDir = null)
    {
        var dir = databaseDir ?? DefaultDatabaseDir(paths);
        if (File.Exists(Path.Combine(dir, "_settings.bin"))) return CampaignTileDatabase.LoadFolder(dir);
        var packs = PackSet.OpenVanilla(paths.GameDataDir);
        var prefix = PackFile.Normalize(CampaignTileDatabase.PackFolder);
        var settings = packs.TryRead(prefix + "_settings.bin") ?? throw new FileNotFoundException("campaign _settings.bin not found in the game packs");
        var tilePrefix = PackFile.Normalize(CampaignTileDatabase.PackFolder + "tiles/");
        var tiles = packs.Packs.SelectMany(p => p.Entries.Keys)
            .Where(k => k.StartsWith(tilePrefix, StringComparison.Ordinal) && k.EndsWith(".bin", StringComparison.Ordinal))
            .Distinct()
            .Select(k => (k[(k.LastIndexOf('\\') + 1)..], packs.TryRead(k)))
            .Where(t => t.Item2 != null)
            .Select(t => (t.Item1, t.Item2!));
        return CampaignTileDatabase.Load(settings, tiles);
    }

    public static TileMapReport Run(ProjectPaths paths, TileMapCheckOptions? options = null, CampaignTileDatabase? db = null)
    {
        options ??= new TileMapCheckOptions();
        var tileMapPath = options.TileMap ?? Path.Combine(paths.AkTerrainDir, "tile_map.png");
        var findings = new List<TileMapFinding>();
        void Add(string code, string severity, string message, IReadOnlyList<int[]>? hexes = null, int? count = null) =>
            findings.Add(new TileMapFinding(code, severity, message, count ?? hexes?.Count ?? 1,
                hexes?.Take(MaxListedHexes).ToList() ?? []) { AllHexes = hexes ?? [] });

        if (!options.TileMapOnly) CheckKitFiles(paths, Add);

        if (!File.Exists(tileMapPath))
        {
            Add("file.missing", TileMapFinding.Error, $"tile map not found: {tileMapPath}");
            return new TileMapReport(paths.MapName, tileMapPath, 0, 0, findings);
        }
        if (FileFormat(tileMapPath) is { } format)
        {
            Add("file.format", TileMapFinding.Error, $"{Path.GetFileName(tileMapPath)} is {format}, not a PNG (BOB/CAIME TGA-named-png issue): re-save it as a real PNG");
            return new TileMapReport(paths.MapName, tileMapPath, 0, 0, findings);
        }
        HexTileMap map;
        try { map = HexTileMap.Read(tileMapPath); }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Add("file.format", TileMapFinding.Error, $"cannot decode {tileMapPath}: {e.Message}");
            return new TileMapReport(paths.MapName, tileMapPath, 0, 0, findings);
        }

        db ??= LoadDatabase(paths, options.DatabaseDir);
        if (db.Errors.Count > 0)
            Add("database.parse", TileMapFinding.Warning, $"{db.Errors.Count} tile database files did not parse: {string.Join("; ", db.Errors.Take(3))}");

        var climateDir = options.ClimateDir ?? ClimateDirFor(paths, tileMapPath);
        CheckClimate(map, climateDir, db, Add);
        if (!map.HasHexLayout)
        {
            Add("layout.size", TileMapFinding.Error,
                $"tile map is {map.PixelWidth}x{map.PixelHeight}: it must be 2W x (2H+1) for a W x H hex grid (even width, odd height)");
            CheckPalette(map, db, Add);
            return new TileMapReport(paths.MapName, tileMapPath, 0, 0, findings);
        }
        if (!options.TileMapOnly) CheckSizes(paths, map, Add);
        CheckLayout(map, Add);
        var setOf = CheckPalette(map, db, Add);
        CheckRules(map, setOf, options.VanillaTileMap ?? DefaultVanillaTileMap(paths), db, Add);

        TileMatchSummary? simulation = null;
        if (options.Simulate)
        {
            var sim = new TileMatchSimulator(db) { Log = options.Log }.Run(map, ClimateIndices(map, climateDir, db));
            simulation = sim.Summary;
            if (options.SimulationCsv != null)
                File.WriteAllLines(options.SimulationCsv, sim.Tiles.Select(t => $"{t.Location},{t.X},{t.Y},{t.Rotation},{t.Climate},{t.Layer}"));
            if (sim.NoTile.Count > 0)
                Add("match.no_tile", TileMapFinding.Error,
                    $"{sim.NoTile.Count} tile-map points get no tile (see-through holes in game), in {sim.NoTileHexes(map).Count} hexes",
                    sim.NoTileHexes(map), sim.NoTile.Count);
        }
        return new TileMapReport(paths.MapName, tileMapPath, map.Width, map.Height, findings, simulation);
    }

    /// <summary>The tile map's own folder when it holds a climate map, else the kit's map folder (a candidate tile map
    /// checked from a scratch folder).</summary>
    private static string ClimateDirFor(ProjectPaths paths, string tileMapPath)
    {
        var own = Path.GetDirectoryName(Path.GetFullPath(tileMapPath))!;
        return ClimateFiles(own).Length > 0 ? own : paths.AkTerrainDir;
    }

    /// <summary>
    /// The palette and hex-rule checks on an in-memory tile map (for editors: no file I/O except the cached vanilla
    /// pattern set). With <paramref name="region"/>, only those hexes and a 1-hex ring around them are reported.
    /// </summary>
    public static IReadOnlyList<TileMapFinding> CheckMap(HexTileMap map, CampaignTileDatabase db,
                                                         IEnumerable<(int Col, int Row)>? region = null, string? vanillaTileMap = null)
    {
        var findings = new List<TileMapFinding>();
        void Add(string code, string severity, string message, IReadOnlyList<int[]>? hexes = null, int? count = null) =>
            findings.Add(new TileMapFinding(code, severity, message, count ?? hexes?.Count ?? 1,
                hexes?.Take(MaxListedHexes).ToList() ?? []) { AllHexes = hexes ?? [] });
        if (!map.HasHexLayout)
        {
            Add("layout.size", TileMapFinding.Error, $"tile map is {map.PixelWidth}x{map.PixelHeight}: it must be 2W x (2H+1)");
            return findings;
        }
        bool[]? scope = null;
        if (region != null)
        {
            scope = new bool[map.Width * map.Height];
            foreach (var (c, r) in region)
            {
                if (c < 0 || r < 0 || c >= map.Width || r >= map.Height) continue;
                scope[map.Index(c, r)] = true;
                for (var d = 0; d < 6; d++)
                    if (map.Neighbour(c, r, d, out var nc, out var nr)) scope[map.Index(nc, nr)] = true;
            }
        }
        var setOf = CheckPalette(map, db, Add, scope);
        CheckRules(map, setOf, vanillaTileMap ?? DefaultVanillaTileMap(new ProjectPaths()), db, Add, scope);
        return findings;
    }

    /// <summary>"TGA" etc. when the file is not a PNG by its magic number; null for PNG.</summary>
    public static string? FileFormat(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using (var fs = File.OpenRead(path)) head = head[..fs.Read(head)];
        if (head.Length >= 8 && head.SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return null;
        if (head.Length >= 3 && head[1] <= 1 && head[2] is 1 or 2 or 3 or 9 or 10 or 11) return "a TGA";
        if (head.Length >= 2 && head[0] == 'B' && head[1] == 'M') return "a BMP";
        if (head.Length >= 2 && head[0] == 0xff && head[1] == 0xd8) return "a JPEG";
        return "not a PNG";
    }

    // ---------------------------------------------------------------- kit files

    private static void CheckKitFiles(ProjectPaths paths, Action<string, string, string, IReadOnlyList<int[]>?, int?> add)
    {
        var rules = Path.Combine(paths.AssemblyKitRoot, "raw_data", "terrain", "campaigns", "rules.bob");
        if (!File.Exists(rules))
            add("inputs.rules_bob", TileMapFinding.Error, $"{rules} is missing: BOB offers no Tilemap / climate / global mesh actions without it", null, null);
        else
        {
            var text = File.ReadAllText(rules);
            foreach (var flag in new[] { "save_final_tile_map", "generate_global_mesh" })
                if (!Regex.IsMatch(text, $@"{flag}\s*=\s*true", RegexOptions.IgnoreCase))
                    add("inputs.rules_bob", TileMapFinding.Error, $"rules.bob does not set {flag} = true", null, null);
        }
    }

    private static void CheckSizes(ProjectPaths paths, HexTileMap map, Action<string, string, string, IReadOnlyList<int[]>?, int?> add)
    {
        int w = map.Width, h = map.Height;
        (int, int) full = (8 * w, 8 * h + 4), sea = (4 * w, 4 * h + 2), quarter = (map.PixelWidth, map.PixelHeight);
        void Expect(string what, (int W, int H) actual, (int W, int H) expected)
        {
            if (actual != expected)
                add("inputs.sizes", TileMapFinding.Error,
                    $"{what} is {actual.W}x{actual.H}, expected {expected.W}x{expected.H} for a {w}x{h}-hex tile map (8 px/hex + 4 rows full, 4 + 2 sea, tile-map size quarter)", null, null);
        }

        if (w % 4 != 0)
            add("layout.mesh_columns", TileMapFinding.Error,
                $"hex width {w} is not a multiple of 4: BOB builds an extra global-mesh column and the game shows see-through notches beside road tiles", null, null);

        var terry = Path.Combine(paths.AkTerrainDir, paths.MapName + ".terry");
        if (File.Exists(terry))
        {
            var project = TerryProject.Load(terry);
            foreach (var (type, expected) in new[] { ("LowFrequencyHeight", full), ("LowFrequencyHeightSea", sea), ("BlendCampaign", full), ("CampaignTree", quarter) })
            {
                if (project.Find(type) is not { } m) continue;
                Expect($".terry {type} size", m.Size, expected);
                var tif = project.LayerTifPath(m);
                if (File.Exists(tif)) Expect(Path.GetFileName(tif), TiffMap.ReadSize(tif), expected);
            }
        }
        foreach (var (file, expected) in new[] { ("lf_heights.tif", full), ("lf_sea_heights.tif", sea) })
        {
            var p = Path.Combine(paths.AkTerrainDir, file);
            if (File.Exists(p)) Expect(file, TiffMap.ReadSize(p), expected);
        }

        // BOB bakes tile heights from the kit's working_data lf maps: stale ones give raised / sunk tiles
        var workingLf = Path.Combine(paths.AkWorkingDir, "terrain", "campaigns", paths.MapName, "lf_height_map.dds");
        if (File.Exists(workingLf))
        {
            var size = DdsSize(workingLf);
            if (size != full)
                add("inputs.stale_lf", TileMapFinding.Error,
                    $"working_data lf_height_map.dds is {size.W}x{size.H}, expected {full.Item1}x{full.Item2}: rebuild the lf maps (rasters step) before Tilemap", null, null);
            var source = new[] { Path.Combine(paths.AkTerrainDir, "lf_heights.tif") }
                .Concat(File.Exists(terry) && TerryProject.Load(terry) is var tp && tp.Find("LowFrequencyHeight") is { } lf ? [tp.LayerTifPath(lf)] : Array.Empty<string>())
                .Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            if (File.GetLastWriteTimeUtc(workingLf) < source)
                add("inputs.stale_lf", TileMapFinding.Warning,
                    "working_data lf_height_map.dds is older than the kit's height TIF: rebuild the lf maps before Tilemap", null, null);
        }
        else
            add("inputs.stale_lf", TileMapFinding.Warning, $"{workingLf} is missing: BOB needs the lf maps in working_data to bake tile heights", null, null);

        CheckExtents(paths, w, h, add);
    }

    private static (int W, int H) DdsSize(string path)
    {
        Span<byte> b = stackalloc byte[20];
        using var fs = File.OpenRead(path);
        if (fs.Read(b) < 20 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S') return (0, 0);
        return (BitConverter.ToInt32(b[16..20]), BitConverter.ToInt32(b[12..16]));
    }

    /// <summary>BOB reads the world size from campaign_map_playable_areas: a row left over from another grid size
    /// builds the tile list and meshes for the wrong width.</summary>
    private static void CheckExtents(ProjectPaths paths, int w, int h, Action<string, string, string, IReadOnlyList<int[]>?, int?> add)
    {
        var dir = Path.Combine(paths.AssemblyKitRoot, "raw_data", "EmpireDesignData");
        double hexX = (w - 1) * HexX, hexZ = (h - 1) * HexZ;
        foreach (var (table, tolerance) in new[] { ("campaign_map_playable_areas", 1.5), ("campaign_maps", 2.0) })
        {
            var file = Path.Combine(dir, table + ".xml");
            if (!File.Exists(file)) { add("inputs.extents", TileMapFinding.Error, $"{file} is missing (BOB reads its DB from EmpireDesignData)", null, null); continue; }
            var row = Regex.Matches(File.ReadAllText(file), $@"<{table}\b.*?</{table}>", RegexOptions.Singleline)
                .Select(m => m.Value)
                .FirstOrDefault(r => Regex.IsMatch(r, $@"<mapname>\s*{Regex.Escape(paths.MapName)}\s*</mapname>"));
            if (row is null) { add("inputs.extents", TileMapFinding.Error, $"EmpireDesignData/{table} has no row for {paths.MapName}", null, null); continue; }
            double V(string tag) => double.TryParse(Regex.Match(row, $@"<{tag}>\s*([-0-9.eE]+)\s*</{tag}>").Groups[1].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            double maxX = V("maxx"), maxY = V("maxy");
            if (!(Math.Abs(maxX - hexX) <= tolerance && Math.Abs(maxY - hexZ) <= tolerance))
                add("inputs.extents", TileMapFinding.Error,
                    $"EmpireDesignData/{table} max {maxX} x {maxY} does not fit a {w}x{h}-hex map (~{hexX:F1} x {hexZ:F1}): update the kit and the Movie pack rows before Tilemap", null, null);
        }
    }

    // ---------------------------------------------------------------- climate

    private static (string Path, int W, int H)[] ClimateFiles(string dir) =>
        new[] { "climate_map.png", "climate_map_g.png" }
            .Select(n => Path.Combine(dir, n))
            .Where(File.Exists)
            .Select(p => { using var codec = SkiaSharp.SKCodec.Create(p); return (p, codec?.Info.Width ?? 0, codec?.Info.Height ?? 0); })
            .ToArray();

    private static void CheckClimate(HexTileMap map, string dir, CampaignTileDatabase db, Action<string, string, string, IReadOnlyList<int[]>?, int?> add)
    {
        var files = ClimateFiles(dir);
        if (files.Length == 0)
        {
            add("climate.missing", TileMapFinding.Error, $"no climate_map.png / climate_map_g.png in {dir}", null, null);
            return;
        }
        var quarter = files.FirstOrDefault(f => f.W == map.PixelWidth && f.H == map.PixelHeight);
        if (quarter.Path is null)
        {
            add("climate.size", TileMapFinding.Error,
                $"no climate map matches the tile map's {map.PixelWidth}x{map.PixelHeight} ({string.Join(", ", files.Select(f => $"{Path.GetFileName(f.Path)} {f.W}x{f.H}"))}): BOB's load_map rejects a size mismatch", null, null);
            return;
        }
        var fullFile = files.FirstOrDefault(f => f.Path != quarter.Path);
        if (fullFile.Path != null && (fullFile.W != map.PixelWidth * 4 || fullFile.H != (map.PixelHeight - 1) * 4 + 4) && map.HasHexLayout)
            add("climate.size", TileMapFinding.Error,
                $"{Path.GetFileName(fullFile.Path)} is {fullFile.W}x{fullFile.H}, expected the full-resolution {map.Width * 8}x{map.Height * 8 + 4}", null, null);

        var q = HexTileMap.Read(quarter.Path);
        var known = db.Climates.Select(c => c.Rgb).ToHashSet();
        var unknown = new Dictionary<uint, int>();
        for (var y = 0; y < q.PixelHeight; y++)
            for (var x = 0; x < q.PixelWidth; x++)
            {
                var v = q.Pixel(x, y);
                if (!known.Contains(v) && !(q.HasHexLayout && q.IsFiller(x, y))) unknown[v] = unknown.GetValueOrDefault(v) + 1;
            }
        if (unknown.Count > 0)
            add("climate.unknown", TileMapFinding.Warning,
                $"{unknown.Values.Sum():N0} climate-map pixels match no climate (BOB uses climate 0 there): {string.Join(", ", unknown.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key:x6} x{k.Value:N0}"))}",
                null, unknown.Values.Sum());

        if (fullFile.Path != null && fullFile.W == q.PixelWidth * 4 && fullFile.H >= (q.PixelHeight - 1) * 4 + 1)
        {
            var f = HexTileMap.Read(fullFile.Path);
            var diff = 0;
            for (var y = 0; y < q.PixelHeight; y++)
                for (var x = 0; x < q.PixelWidth; x++)
                    if (q.Pixel(x, y) != f.Pixel(x * 4, y * 4)) diff++;
            if (diff > 0)
                add("climate.quarter", TileMapFinding.Info,
                    $"{Path.GetFileName(quarter.Path)} differs from {Path.GetFileName(fullFile.Path)}[::4, ::4] on {diff:N0} pixels ({100.0 * diff / q.Pixels.Length:F2}%): climate_map.cm is built from the full map, so this only matters for tools that read the quarter map", null, diff);
        }
    }

    /// <summary>Climate index per tile-map pixel, as BOB's get_climate_index (no match → 0).</summary>
    public static byte[] ClimateIndices(HexTileMap map, string dir, CampaignTileDatabase db)
    {
        var result = new byte[map.Pixels.Length];
        var quarter = ClimateFiles(dir).FirstOrDefault(f => f.W == map.PixelWidth && f.H == map.PixelHeight);
        if (quarter.Path is null) return result;
        var q = HexTileMap.Read(quarter.Path);
        var index = db.Climates.GroupBy(c => c.Rgb).ToDictionary(g => g.Key, g => (byte)g.First().Index);
        for (var i = 0; i < result.Length; i++) result[i] = index.GetValueOrDefault(q.Pixels[i]);
        return result;
    }

    // ---------------------------------------------------------------- layout and palette

    private static void CheckLayout(HexTileMap map, Action<string, string, string, IReadOnlyList<int[]>?, int?> add)
    {
        var filler = 0;
        for (var x = 0; x < map.PixelWidth; x++)
        {
            var y = (x / 2 & 1) == 0 ? 0 : map.PixelHeight - 1;
            if (map.Pixel(x, y) != 0) filler++;
        }
        if (filler > 0)
            add("layout.filler", TileMapFinding.Warning,
                $"{filler} filler pixels (top row of even columns, bottom row of odd columns) are not black: BOB treats them as tile points", null, filler);

        var mixed = new List<int[]>();
        for (var r = 0; r < map.Height; r++)
            for (var c = 0; c < map.Width; c++)
                if (!map.HexUniform(c, r)) mixed.Add([c, r]);
        if (mixed.Count > 0)
            add("layout.mixed_hex", TileMapFinding.Info,
                $"{mixed.Count} hexes are not one colour across their 2x2 pixels (BOB matches per pixel; the hex rules below use the first pixel)", mixed, null);
    }

    /// <summary>BOB's TILE_PLACEMENT_GROUPS: one group per tile set, per coloured tile and per coloured variation
    /// (exact RGB, first match wins). Returns the tile set of each hex colour.</summary>
    public static Dictionary<uint, string> PlacementColours(CampaignTileDatabase db)
    {
        var groups = new Dictionary<uint, string>();
        foreach (var s in db.TileSets) groups.TryAdd(s.Rgb, s.Name);
        foreach (var t in db.Tiles)
            if (t.Rgb != 0) groups.TryAdd(t.Rgb, t.TileSet);
        foreach (var t in db.Tiles)
            foreach (var v in t.Variations)
                if (v.Rgb != 0) groups.TryAdd(v.Rgb, t.TileSet);
        return groups;
    }

    private static Dictionary<uint, string> CheckPalette(HexTileMap map, CampaignTileDatabase db, Action<string, string, string, IReadOnlyList<int[]>?, int?> add,
                                                         bool[]? scope = null)
    {
        var groups = PlacementColours(db);
        var unknown = new Dictionary<uint, List<int[]>>();
        var black = new List<int[]>();
        if (map.HasHexLayout)
        {
            for (var r = 0; r < map.Height; r++)
                for (var c = 0; c < map.Width; c++)
                    for (var d = 0; d < 4; d++)
                    {
                        if (scope != null && !scope[map.Index(c, r)]) break;
                        var (x, y) = map.HexPixel(c, r, d & 1, d >> 1);
                        var v = map.Pixel(x, y);
                        if (groups.ContainsKey(v)) continue;
                        var list = v == 0 ? black : unknown.TryGetValue(v, out var l) ? l : unknown[v] = [];
                        if (list.Count == 0 || list[^1][0] != c || list[^1][1] != r) list.Add([c, r]);
                    }
        }
        else
        {
            foreach (var v in map.Pixels)
                if (v != 0 && !groups.ContainsKey(v))
                    (unknown.TryGetValue(v, out var l) ? l : unknown[v] = []).Add([]);
        }
        // a colour on many hexes is a wrong palette (CAIME's Attila colours: thousands of holes); a few pixels, or
        // near-misses of a real colour, are stray (CA's own map has ~200 such hexes, up to 44 of one sea shade)
        var stray = new List<int[]>();
        var strayColours = new List<string>();
        foreach (var (colour, hexes) in unknown.OrderByDescending(k => k.Value.Count))
        {
            var (nearest, distance) = Nearest(colour, groups);
            if (hexes.Count >= SystematicColourHexes && distance > NearColour || hexes.Count >= SystematicNearColourHexes)
                add("palette.unknown", TileMapFinding.Error,
                    $"colour {colour:x6} is no 3K tile set / tile / variation colour{AttilaHint(colour)}: {hexes.Count:N0} hexes get no tile (nearest: {nearest})",
                    map.HasHexLayout ? hexes : null, hexes.Count);
            else
            {
                stray.AddRange(hexes);
                if (strayColours.Count < 8) strayColours.Add($"{colour:x6}~{nearest}{(distance > 24 ? "?" : "")}");
            }
        }
        if (stray.Count > 0)
            add("palette.stray", TileMapFinding.Warning,
                $"{stray.Count} hexes hold stray off-palette pixels (rare or near-miss colours; no tile goes there): repaint with the nearest colour, e.g. {string.Join(", ", strayColours)}",
                map.HasHexLayout ? stray : null, stray.Count);
        if (black.Count > 0)
            add("palette.black", TileMapFinding.Error, $"{black.Count:N0} hexes are black (no tile set): they get no tile", black, null);
        return groups;
    }

    private const int SystematicColourHexes = 10, SystematicNearColourHexes = 500, NearColour = 8;

    /// <summary>Closest placement colour by largest channel difference, as "name rrggbb".</summary>
    internal static (string Name, int Distance) Nearest(uint colour, Dictionary<uint, string> groups)
    {
        var best = (Name: "", Distance: int.MaxValue);
        foreach (var (rgb, set) in groups)
        {
            var d = Math.Max(Math.Abs((int)(rgb >> 16) - (int)(colour >> 16)),
                Math.Max(Math.Abs((int)(rgb >> 8 & 0xff) - (int)(colour >> 8 & 0xff)), Math.Abs((int)(rgb & 0xff) - (int)(colour & 0xff))));
            if (d < best.Distance) best = ($"{set} {rgb:x6}", d);
        }
        return best;
    }

    private static string AttilaHint(uint colour) => colour switch
    {
        0x538dd5 => " (CAIME/Attila sea: use generic_sea 3971b7)",
        0x5a7647 => " (CAIME/Attila land: use generic 96aa64)",
        0x956826 => " (CAIME/Attila road: use roads_tracks 5d0018)",
        0xfe0000 => " (CAIME/Attila cliff: use blockout_cliff f9ad69)",
        _ => "",
    };

    // ---------------------------------------------------------------- hex rules

    /// <summary>What CA's own tile map uses: its 7-hex neighbourhood patterns and its tile sets.</summary>
    internal sealed record VanillaReference(HashSet<long> Patterns, HashSet<string> Sets);

    private static readonly ConcurrentDictionary<string, VanillaReference> VanillaReferences = new(StringComparer.OrdinalIgnoreCase);

    internal static int[] Categories(HexTileMap map, uint[] colours, Dictionary<uint, string> setOf)
    {
        var cats = new int[colours.Length];
        for (var i = 0; i < cats.Length; i++)
            cats[i] = colours[i] == 0 ? Black : Category(setOf.GetValueOrDefault(colours[i]));
        return cats;
    }

    /// <summary>Base-12 key of a hex's category and its 6 neighbours' (0 off the map), as tile_repair.py keys().</summary>
    internal static long PatternKey(HexTileMap map, int[] cats, int c, int r)
    {
        long k = cats[map.Index(c, r)];
        for (var d = 0; d < 6; d++)
            k = k * 12 + (map.Neighbour(c, r, d, out var nc, out var nr) ? cats[map.Index(nc, nr)] : 0);
        return k;
    }

    internal static VanillaReference? LoadVanilla(string path, CampaignTileDatabase db) =>
        !File.Exists(path) ? null : VanillaReferences.GetOrAdd(path, p =>
        {
            var map = HexTileMap.Read(p);
            var groups = PlacementColours(db);
            var colours = map.HexColours();
            var cats = Categories(map, colours, groups);
            var set = new HashSet<long>();
            for (var r = 0; r < map.Height; r++)
                for (var c = 0; c < map.Width; c++)
                    set.Add(PatternKey(map, cats, c, r));
            return new VanillaReference(set, colours.Distinct().Where(groups.ContainsKey).Select(v => groups[v]).ToHashSet());
        });

    internal static readonly int[] RiverFamily = [River, Start, Mouth, Crossing];
    private static readonly int[] RoadFamily = [Road, Crossing];

    // crossing layouts BOB tiles: (river sides, road sides) rotated to the smallest description (tile_repair.py GOOD_X)
    internal static readonly HashSet<string> GoodCrossings =
    [
        "0,3|1,4", "0,3|1,5", "0,3|2,5", "0,2|1,5", "0,2|1,3", "0,3|1,2,4", "0|1,4",
    ];

    private static void CheckRules(HexTileMap map, Dictionary<uint, string> setOf, string vanillaTileMap, CampaignTileDatabase db,
                                   Action<string, string, string, IReadOnlyList<int[]>?, int?> add, bool[]? scope = null)
    {
        var colours = map.HexColours();
        var cats = Categories(map, colours, setOf);
        int Cat(int c, int r) => cats[map.Index(c, r)];
        int CountAround(int c, int r, Func<int, bool> pred)
        {
            var n = 0;
            for (var d = 0; d < 6; d++)
                if (map.Neighbour(c, r, d, out var nc, out var nr) && pred(Cat(nc, nr))) n++;
            return n;
        }

        var thick = new List<int[]>();
        var thickSets = new SortedSet<string>();
        var ring = new List<int[]>();
        var cliffEnd = new List<int[]>();
        var riverEnds = new List<int[]>();
        var crossings = new List<int[]>();
        for (var r = 0; r < map.Height; r++)
            for (var c = 0; c < map.Width; c++)
            {
                if (scope != null && !scope[map.Index(c, r)]) continue;
                var cat = Cat(c, r);
                var colour = colours[map.Index(c, r)];
                if (cat is River or Start or Mouth or Crossing or Road or Cliff or CliffEnd or Canal)
                {
                    // line sets stay one hex wide: CA's map has 1-3 same-set neighbours per line hex, never 4+
                    var same = 0;
                    for (var d = 0; d < 6; d++)
                        if (map.Neighbour(c, r, d, out var nc, out var nr) && colours[map.Index(nc, nr)] == colour) same++;
                    if (same >= 4) { thick.Add([c, r]); thickSets.Add(setOf.GetValueOrDefault(colour, $"{colour:x6}")); }
                }
                if (cat == Area && CountAround(c, r, x => x == Sea) > 0) ring.Add([c, r]);
                if (cat == Cliff && CountAround(c, r, x => x == Beach) > 0) cliffEnd.Add([c, r]);
                if (cat == CliffEnd && CountAround(c, r, x => x == Beach) == 0) cliffEnd.Add([c, r]);
                var rivers = CountAround(c, r, x => RiverFamily.Contains(x));
                if (cat == Start && rivers != 1) riverEnds.Add([c, r]);
                if (cat == Mouth && (rivers != 1 || CountAround(c, r, x => x == Sea) == 0)) riverEnds.Add([c, r]);
                if (cat == River && rivers < 1) riverEnds.Add([c, r]);
                if (cat == Crossing && !GoodCrossings.Contains(CrossingLayout(map, cats, c, r))) crossings.Add([c, r]);
            }
        if (thick.Count > 0)
            add("line.thick", TileMapFinding.Warning,
                $"{thick.Count} line hexes ({string.Join(", ", thickSets)}) have 4+ neighbours of the same set: lines must stay one hex wide or no tile matches", thick, null);
        if (ring.Count > 0)
            add("coast.missing_ring", TileMapFinding.Warning,
                $"{ring.Count} land hexes touch the sea directly (no sea_coast / cliff hex between): likely coast holes", ring, null);
        if (cliffEnd.Count > 0)
            add("coast.cliff_end", TileMapFinding.Warning,
                $"{cliffEnd.Count} cliff hexes break the cliff-end rule (a cliff touching sea_coast must be blockout_cliff_ends 9f222a; a cliff end must touch sea_coast)", cliffEnd, null);
        if (riverEnds.Count > 0)
            add("river.ends", TileMapFinding.Warning,
                $"{riverEnds.Count} river hexes have a bad end: river_start needs exactly 1 river neighbour, river_mouth 1 river neighbour and the sea, a river hex at least 1 river neighbour", riverEnds, null);
        if (crossings.Count > 0)
            add("river.crossing", TileMapFinding.Warning,
                $"{crossings.Count} river crossings are in a layout CA's map never uses (BOB found no tile for those in earlier rounds): keep the river straight through and the road across it", crossings, null);

        if (LoadVanilla(vanillaTileMap, db) is not { } vanilla)
        {
            add("pattern.reference", TileMapFinding.Info, $"vanilla tile map not found ({vanillaTileMap}): neighbourhood pattern check skipped", null, null);
            return;
        }
        var unusedSets = new Dictionary<string, List<int[]>>();
        for (var r = 0; r < map.Height; r++)
            for (var c = 0; c < map.Width; c++)
                if ((scope == null || scope[map.Index(c, r)]) && setOf.TryGetValue(colours[map.Index(c, r)], out var set) && !vanilla.Sets.Contains(set))
                    (unusedSets.TryGetValue(set, out var l) ? l : unusedSets[set] = []).Add([c, r]);
        foreach (var (set, hexes) in unusedSets.OrderByDescending(k => k.Value.Count))
            add("palette.unused_set", TileMapFinding.Warning,
                $"{hexes.Count:N0} hexes use tile set {set} ({db.TileSet(set)?.Rgb:x6}), which CA's map never paints{(set == "sea" ? " (CAIME's sea colour: open sea is generic_sea 3971b7)" : "")}: its tiles and links are untested",
                hexes, null);
        var unseen = new List<int[]>();
        for (var r = 0; r < map.Height; r++)
            for (var c = 0; c < map.Width; c++)
                if ((scope == null || scope[map.Index(c, r)]) && Cat(c, r) is Beach or Cliff or CliffEnd or River or Start or Mouth or Crossing && !vanilla.Patterns.Contains(PatternKey(map, cats, c, r)))
                    unseen.Add([c, r]);
        if (unseen.Count > 0)
            add("pattern.unseen", TileMapFinding.Warning,
                $"{unseen.Count} coast / river / crossing hexes have a 7-hex neighbourhood CA's map never uses: likely holes (research/main190/tile_repair.py can recolour them)", unseen, null);
    }

    /// <summary>tile_repair.py x_sig: the crossing's river and road sides, rotated to the smallest description.</summary>
    internal static string CrossingLayout(HexTileMap map, int[] cats, int c, int r)
    {
        var rv = new List<int>();
        var rd = new List<int>();
        for (var d = 0; d < 6; d++)
        {
            if (!map.Neighbour(c, r, d, out var nc, out var nr)) continue;
            var k = cats[map.Index(nc, nr)];
            if (RiverFamily.Contains(k)) rv.Add(d);
            if (k == Road) rd.Add(d);
        }
        (int[] Rv, int[] Rd)? best = null;
        foreach (var b in rv.Count > 0 ? rv : [0])
        {
            var s = (rv.Select(d => (d - b + 6) % 6).Order().ToArray(), rd.Select(d => (d - b + 6) % 6).Order().ToArray());
            if (best is null || Compare(s, best.Value) < 0) best = s;
        }
        return $"{string.Join(",", best!.Value.Rv)}|{string.Join(",", best.Value.Rd)}";

        static int Compare((int[] Rv, int[] Rd) a, (int[] Rv, int[] Rd) b)
        {
            var x = Seq(a.Rv, b.Rv);
            return x != 0 ? x : Seq(a.Rd, b.Rd);
        }
        static int Seq(int[] a, int[] b)
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }
    }

    // ---------------------------------------------------------------- overlay

    /// <summary>Writes the tile map dimmed, with error hexes red, warning hexes yellow and info hexes cyan.</summary>
    public static void WriteOverlay(string tileMapPath, TileMapReport report, string outPath)
    {
        var map = HexTileMap.Read(tileMapPath);
        var raster = new Raster<uint>(map.PixelWidth, map.PixelHeight);
        for (var i = 0; i < map.Pixels.Length; i++)
        {
            var v = map.Pixels[i];
            uint r = (v >> 16 & 0xff) / 3 + 40, g = (v >> 8 & 0xff) / 3 + 40, b = (v & 0xff) / 3 + 40;
            raster.Data[i] = 0xff000000 | b << 16 | g << 8 | r;
        }
        if (map.HasHexLayout)
            foreach (var f in report.Findings.OrderBy(f => f.Severity == TileMapFinding.Error ? 2 : f.Severity == TileMapFinding.Warning ? 1 : 0))
            {
                uint colour = f.Severity switch { TileMapFinding.Error => 0xff0000ff, TileMapFinding.Warning => 0xff00ffff, _ => 0xffffff00 };
                foreach (var h in f.AllHexes)
                {
                    if (h.Length < 2 || h[0] >= map.Width || h[1] >= map.Height) continue;
                    for (var d = 0; d < 4; d++)
                    {
                        var (x, y) = map.HexPixel(h[0], h[1], d & 1, d >> 1);
                        raster.Data[y * map.PixelWidth + x] = colour;
                    }
                }
            }
        PngMap.Write(outPath, raster);
    }
}
