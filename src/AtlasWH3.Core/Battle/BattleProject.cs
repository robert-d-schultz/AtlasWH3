using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using AtlasWH3.Formats.Battle;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Battle;

/// <summary>Where a battle project finds the kit-wide data it needs besides its own folder.</summary>
public sealed record BattlePaths
{
    /// <summary>raw_data\terrain\battles\tile_placement_groups.xml of the kit (the 20 mixed placement groups).</summary>
    public string? PlacementGroupsXml { get; init; }
    /// <summary>terrain\tiles\battle\_tile_database extracted from the game.</summary>
    public string TileDatabaseDir { get; init; } =
        Path.Combine(Path.GetDirectoryName(Defaults.CompiledRoot.TrimEnd('\\'))!, "terrain", "tiles", "battle", "_tile_database");
    /// <summary>Assembly kit root, when the project sits inside one (…\raw_data\terrain\battles\&lt;map&gt;).</summary>
    public string? AssemblyKitRoot { get; init; }

    /// <summary>Derives the kit from a project folder inside raw_data\terrain\battles, falling back to <paramref name="defaults"/>.</summary>
    public static BattlePaths For(string projectDir, ProjectPaths defaults)
    {
        var dir = new DirectoryInfo(projectDir);
        string? kit = null;
        if (dir.Parent?.Name.Equals("battles", StringComparison.OrdinalIgnoreCase) == true
            && dir.Parent.Parent?.Name.Equals("terrain", StringComparison.OrdinalIgnoreCase) == true
            && dir.Parent.Parent.Parent?.Name.Equals("raw_data", StringComparison.OrdinalIgnoreCase) == true)
            kit = dir.Parent.Parent.Parent.Parent?.FullName;
        kit ??= defaults.AssemblyKitRoot;
        // A standalone copy of the sources (e.g. a shared package) may lack the kit-wide groups file.
        var groups = new[] { kit, defaults.AssemblyKitRoot }
            .Select(k => Path.Combine(k, "raw_data", "terrain", "battles", "tile_placement_groups.xml"))
            .FirstOrDefault(File.Exists) ?? "";
        var vanillaDb = Path.Combine(Path.GetDirectoryName(defaults.VanillaRoot) ?? defaults.VanillaRoot,
            "terrain", "tiles", "battle", "_tile_database");
        return new BattlePaths
        {
            AssemblyKitRoot = kit,
            PlacementGroupsXml = File.Exists(groups) ? groups : null,
            TileDatabaseDir = Directory.Exists(vanillaDb) ? vanillaDb : new BattlePaths().TileDatabaseDir,
        };
    }
}

/// <summary>
/// The BOB sources of one campaign-battle terrain (raw_data\terrain\battles\&lt;map&gt;): tile_map.png (1 px per cell,
/// row 0 = north), climate_map.png, explicit_tiles.txt, the land/sea height TIFs (Terry-named copy + lf_*.tif that BOB
/// reads, kept identical) and the catchment layer. Heights are held at lf resolution (8 px per cell for land,
/// 4 for sea). Height values are BOB source units: BOB stores them normalised by their own min..max, so land and sea
/// compare directly (water where sea &gt; land).
/// </summary>
public sealed class BattleProject
{
    public string Dir { get; }
    public string MapName { get; }
    public BattlePaths Paths { get; }
    public TerryProject Terry { get; }

    public Raster<uint> TileMap { get; }
    public Raster<uint>? Climate { get; }
    public Raster<uint>? LandMask { get; }
    public Raster<ushort> Land { get; }
    public Raster<ushort> Sea { get; }
    public List<ExplicitTile> ExplicitTiles { get; }
    public List<BattleCatchment> Catchments { get; }

    public BattleTileDatabase TileDatabase { get; }
    public BattlePalette Palette { get; }

    /// <summary>Land heights wrapped for the campaign height brushes.</summary>
    public TerrainData LandTerrain { get; }
    /// <summary>Sea heights wrapped the same way (the brush edits <see cref="TerrainData.Height"/>).</summary>
    public TerrainData SeaTerrain { get; }

    public int Width => TileMap.Width;
    public int Height => TileMap.Height;
    public int LandPerCell => Land.Width / TileMap.Width;
    public int SeaPerCell => Sea.Width / TileMap.Width;

    /// <summary>Messages from loading (missing files, mismatched height copies, ...).</summary>
    public List<string> Notes { get; } = new();

    private readonly Dictionary<string, ulong> _loadedHashes = new();
    private string _explicitAsLoaded;
    private string _catchmentsAsLoaded;
    private readonly XDocument? _layerDoc;

    /// <summary>The .layer file holding the catchment entities (null when the project has none).</summary>
    public string? LayerPath { get; }

    public string TileMapPath => Path.Combine(Dir, "tile_map.png");
    public string ClimatePath => Path.Combine(Dir, "climate_map.png");
    public string ExplicitPath => Path.Combine(Dir, "explicit_tiles.txt");
    public string LandPath => Path.Combine(Dir, "lf_heights.tif");
    public string SeaPath => Path.Combine(Dir, "lf_sea_heights.tif");

    private BattleProject(string dir, BattlePaths paths)
    {
        Dir = Path.GetFullPath(dir);
        Paths = paths;
        var terryPath = Directory.EnumerateFiles(Dir, "*.terry").FirstOrDefault()
                        ?? throw new FileNotFoundException($"No .terry project in {Dir}");
        Terry = TerryProject.Load(terryPath);
        MapName = Terry.MapName;

        TileDatabase = BattleTileDatabase.Load(paths.TileDatabaseDir);
        Palette = BattlePalette.Build(TileDatabase, paths.PlacementGroupsXml);
        if (paths.PlacementGroupsXml == null)
            Notes.Add("tile_placement_groups.xml not found: the 20 mixed placement groups are missing from the palette.");

        TileMap = PngMap.Read(TileMapPath);
        Climate = File.Exists(ClimatePath) ? PngMap.Read(ClimatePath) : null;
        var mask = Path.Combine(Dir, "blm_land.png");
        LandMask = File.Exists(mask) ? PngMap.Read(mask) : null;
        Land = ReadHeight("LowFrequencyHeight", LandPath);
        Sea = ReadHeight("LowFrequencyHeightSea", SeaPath);
        if (Land.Width % TileMap.Width != 0 || Sea.Width % TileMap.Width != 0)
            Notes.Add($"Height rasters ({Land.Width}x{Land.Height}, {Sea.Width}x{Sea.Height}) are not a whole multiple of the tile map ({Width}x{Height}).");

        ExplicitTiles = File.Exists(ExplicitPath) ? ExplicitTilesFile.Read(ExplicitPath) : new List<ExplicitTile>();
        _explicitAsLoaded = ExplicitTilesFile.Format(ExplicitTiles);
        LayerPath = Terry.LayerFiles().Values.Select(Terry.LayerFilePath).FirstOrDefault(File.Exists);
        if (LayerPath != null)
        {
            _layerDoc = XDocument.Load(LayerPath);
            Catchments = BattleCatchmentLayer.Read(_layerDoc, Width, Height);
        }
        else
        {
            Catchments = new List<BattleCatchment>();
            Notes.Add("No catchment layer found: catchment areas can't be saved for this project.");
        }
        _catchmentsAsLoaded = CatchmentSignature();

        LandTerrain = new TerrainData(Land, Sea, new Raster<byte>(1, 1), new Raster<byte>(1, 1), TextureArrays.Empty, WorldCoords.Vanilla3K);
        SeaTerrain = new TerrainData(Sea, Sea, new Raster<byte>(1, 1), new Raster<byte>(1, 1), TextureArrays.Empty, WorldCoords.Vanilla3K);

        foreach (var (name, hash) in CurrentHashes()) _loadedHashes[name] = hash;
    }

    public static BattleProject Load(string dir, BattlePaths paths) => new(dir, paths);

    /// <summary>Reads a height raster, preferring the Terry-named copy; notes when the two copies differ.</summary>
    private Raster<ushort> ReadHeight(string terryType, string bobPath)
    {
        var map = Terry.Find(terryType);
        var terryPath = map != null ? Terry.LayerTifPath(map) : null;
        var hasTerry = terryPath != null && File.Exists(terryPath);
        if (!hasTerry && !File.Exists(bobPath))
            throw new FileNotFoundException($"Neither {Path.GetFileName(terryPath ?? "")} nor {Path.GetFileName(bobPath)} exists.");
        var raster = TiffMap.ReadGray16(hasTerry ? terryPath! : bobPath);
        if (hasTerry && File.Exists(bobPath) && !SameBytes(terryPath!, bobPath))
            Notes.Add($"{Path.GetFileName(terryPath)} and {Path.GetFileName(bobPath)} differ; editing the Terry copy, both are written on save.");
        return raster;
    }

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return XxHash64.HashToUInt64(File.ReadAllBytes(a)) == XxHash64.HashToUInt64(File.ReadAllBytes(b));
    }

    private IEnumerable<(string Name, ulong Hash)> CurrentHashes()
    {
        yield return ("tile_map", Hash(TileMap));
        yield return ("land", Hash(Land));
        yield return ("sea", Hash(Sea));
        if (Climate != null) yield return ("climate", Hash(Climate));
    }

    private static ulong Hash<T>(Raster<T> r) where T : unmanaged => XxHash64.HashToUInt64(MemoryMarshal.AsBytes(r.Data.AsSpan()));

    /// <summary>Names of the parts that differ from what was loaded or last saved.</summary>
    public IReadOnlyList<string> ChangedParts()
    {
        var changed = CurrentHashes().Where(h => _loadedHashes.GetValueOrDefault(h.Name) != h.Hash).Select(h => h.Name).ToList();
        if (ExplicitTilesFile.Format(ExplicitTiles) != _explicitAsLoaded) changed.Add("explicit_tiles");
        if (CatchmentSignature() != _catchmentsAsLoaded) changed.Add("catchments");
        return changed;
    }

    public sealed record SaveResult(IReadOnlyList<string> Written, string? BackupDir);

    /// <summary>
    /// Writes the changed parts only (an unedited project writes nothing). Every file about to be overwritten is first
    /// copied to &lt;backupRoot&gt;\battle_&lt;map&gt;_&lt;timestamp&gt;.
    /// </summary>
    public SaveResult Save(string backupRoot)
    {
        var changed = ChangedParts();
        var files = new List<(string Path, Action Write)>();
        if (changed.Contains("tile_map")) files.Add((TileMapPath, () => PngMap.Write(TileMapPath, TileMap)));
        if (changed.Contains("climate") && Climate != null) files.Add((ClimatePath, () => PngMap.Write(ClimatePath, Climate)));
        if (changed.Contains("land")) AddHeight(files, "LowFrequencyHeight", LandPath, Land);
        if (changed.Contains("sea")) AddHeight(files, "LowFrequencyHeightSea", SeaPath, Sea);
        if (changed.Contains("explicit_tiles")) files.Add((ExplicitPath, () => ExplicitTilesFile.Write(ExplicitPath, ExplicitTiles)));
        if (changed.Contains("catchments"))
        {
            if (LayerPath == null || _layerDoc == null) throw new InvalidOperationException("This project has no catchment layer to save into.");
            files.Add((LayerPath, () =>
            {
                BattleCatchmentLayer.Write(_layerDoc, Catchments, Width, Height);
                BattleCatchmentLayer.Save(_layerDoc, LayerPath);
            }));
        }
        if (files.Count == 0) return new SaveResult([], null);

        string? backup = null;
        foreach (var (path, _) in files.Where(f => File.Exists(f.Path)))
        {
            backup ??= Directory.CreateDirectory(Path.Combine(backupRoot, $"battle_{MapName}_{DateTime.Now:yyyyMMdd_HHmmss}")).FullName;
            File.Copy(path, Path.Combine(backup, Path.GetFileName(path)), overwrite: true);
        }
        foreach (var (_, write) in files) write();

        _loadedHashes.Clear();
        foreach (var (name, hash) in CurrentHashes()) _loadedHashes[name] = hash;
        _explicitAsLoaded = ExplicitTilesFile.Format(ExplicitTiles);
        _catchmentsAsLoaded = CatchmentSignature();
        return new SaveResult(files.Select(f => f.Path).ToList(), backup);
    }

    private void AddHeight(List<(string, Action)> files, string terryType, string bobPath, Raster<ushort> raster)
    {
        files.Add((bobPath, () => TiffMap.WriteGray16(bobPath, raster)));
        if (Terry.Find(terryType) is { } map)
        {
            var terryPath = Terry.LayerTifPath(map);
            files.Add((terryPath, () => TiffMap.WriteGray16(terryPath, raster)));
        }
    }

    private string CatchmentSignature() =>
        string.Join('\n', Catchments.Select(c =>
            $"{c.Id}|{string.Join(",", c.Types)}|{c.RedirectTo}|{c.Culture}|{c.Centre}|{c.Box}|{c.BoundaryId}"));

    /// <summary>A fresh catchment entity id (15 hex digits) not used by any catchment or boundary.</summary>
    public string NewCatchmentId()
    {
        var used = Catchments.Select(c => c.Id).Concat(Catchments.Where(c => c.BoundaryId != null).Select(c => c.BoundaryId!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_layerDoc != null)
            used.UnionWith(_layerDoc.Descendants("entity").Select(e => (string?)e.Attribute("id") ?? ""));
        return new BattleCatchmentLayer.IdSource(used).Next();
    }

    /// <summary>Palette entry (or null) of a tile-map cell; x, y in cells, row 0 = north.</summary>
    public PaletteEntry? EntryAt(int x, int y) => Palette.Find(RgbAt(TileMap, x, y));

    /// <summary>0xRRGGBB of a PngMap pixel (stored 0xAABBGGRR).</summary>
    public static uint RgbAt(Raster<uint> raster, int x, int y)
    {
        var p = raster[x, y];
        return ((p & 0xFF) << 16) | (p & 0xFF00) | ((p >> 16) & 0xFF);
    }

    /// <summary>0xRRGGBB → PngMap pixel (opaque 0xAABBGGRR).</summary>
    public static uint ToPixel(uint rgb) => 0xFF000000u | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    /// <summary>Climate name of a cell, from climate_map.png's colour.</summary>
    public string? ClimateAt(int x, int y)
    {
        if (Climate == null || !Climate.Contains(x, y)) return null;
        var rgb = RgbAt(Climate, x, y);
        return TileDatabase.Climates.FirstOrDefault(c => BattlePalette.Pack(c.R, c.G, c.B) == rgb)?.Name;
    }
}
