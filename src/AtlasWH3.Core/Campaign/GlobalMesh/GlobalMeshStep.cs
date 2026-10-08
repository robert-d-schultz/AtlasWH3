using System.Collections.Concurrent;
using System.Diagnostics;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Campaign.GlobalMesh;

/// <summary>global_meshes\land_mesh_N (.rigid_model_v2 + .compressed_map) and sea_mesh_N (BOB "Global Mesh").</summary>
public sealed class GlobalMeshStep : ICampaignBuildStep
{
    /// <summary>World units per tile-map pixel on every 3K campaign map (vanilla 595.1 / 1784).</summary>
    public const float TileSize = 595.1f / 1784f;

    public string Name => "global_mesh";
    public string ReplacesBobAction => "Terrain / Global Mesh (land_mesh_N, sea_mesh_N)";
    public IReadOnlyList<string> DependsOn => ["rasters", "tile_list"];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        foreach (var f in new[] { "lf_height_map.compressed_map", "lf_sea_height_map.compressed_map" })
            if (!File.Exists(ctx.OutFile(f))) missing.Add($"missing {f} (run step 'rasters')");
        if (TileListSource(ctx) is null) missing.Add("no tile_list.bin (output, working_data or vanilla root)");
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir} (tile database)");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var tileListPath = TileListSource(ctx)!;
        ctx.Log($"tile list {tileListPath}");
        var tiles = TileList.Read(tileListPath);

        ctx.Log("tile database...");
        var packs = PackSet.OpenVanilla(ctx.Paths.GameDataDir);
        var prefix = PackFile.Normalize(TileDatabase.Folder);
        var entries = packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Distinct()
            .Select(k => packs.TryRead(k)).OfType<byte[]>();
        var db = TileDatabase.Load(entries);
        var coverage = new TileCoverage(tiles, db, TileSize, out var unknown);
        if (unknown.Count > 0) notes.Add($"{unknown.Count} tile paths not in the tile database (ignored): {string.Join(", ", unknown.Take(5))}");

        var land = CompressedMap.Read(ctx.OutFile("lf_height_map.compressed_map"));
        var sea = CompressedMap.Read(ctx.OutFile("lf_sea_height_map.compressed_map"));
        int tilesW = tiles.Ints[1], tilesH = tiles.Ints[2];
        if (land.Raster.Width != tilesW * 4 || land.Raster.Height != tilesH * 4)
            notes.Add($"lf {land.Raster.Width}x{land.Raster.Height} is not 4x the tile list's {tilesW}x{tilesH}");
        float worldW = tilesW * TileSize, worldH = tilesH * TileSize;
        BobGlobalHeight? bob = null;
        // research override: ATLASWH3_GMESH_BOB_HEIGHT=1 / 0 forces the BOB / native path
        var useBob = Environment.GetEnvironmentVariable("ATLASWH3_GMESH_BOB_HEIGHT") is { } force
            ? force == "1" : !ctx.GlobalMeshGeometry.Equals("native", StringComparison.OrdinalIgnoreCase);
        if (useBob)
        {
            // BOB reads the campaign tiles from the assembly kit's working_data (it can differ from the packs)
            byte[]? Read(string k)
            {
                var loose = Path.Combine(ctx.Paths.AkWorkingDir, k.Replace('\\', '/'));
                if ((BobGlobalHeight.Variant & 1024) != 0 && packs.TryRead(PackFile.Normalize(k)) is { } packed) return packed;
                return File.Exists(loose) ? File.ReadAllBytes(loose) : packs.TryRead(PackFile.Normalize(k));
            }
            var tileDbDir = Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "tiles", "campaign", "_tile_database", "tiles");
            var bobDb = Directory.Exists(tileDbDir) ? TileDatabase.Load(Directory.EnumerateFiles(tileDbDir, "*.bin").Select(File.ReadAllBytes)) : db;
            var settings = Read("terrain/tiles/campaign/_tile_database/_settings.bin");
            var excluded = settings is null ? new HashSet<string>() : BobGlobalHeight.ExcludedTileSets(settings);
            notes.Add($"BOB height query ({(bobDb == db ? "pack" : "working_data")} tiles); tile sets excluded from the global mesh: {string.Join(", ", excluded.Order())}");
            CompressedMap.Map FromTif(CompressedMap.Map map, string name)
            {
                var tif = Path.Combine(ctx.Paths.AkTerrainDir, name);
                if ((BobGlobalHeight.Variant & 2048) == 0 || !File.Exists(tif)) return map;
                var raster = TiffMap.ReadGray16(tif);
                notes.Add($"lf from {name} ({raster.Width}x{raster.Height})");
                return map with { Raster = raster, Header = [0, 0, 0, 0, 1, 0] };
            }
            bob = new BobGlobalHeight(tiles, bobDb, excluded, Read, FromTif(land, "lf_heights.tif"), FromTif(sea, "lf_sea_heights.tif"), TileSize);
        }
        var builder = new GlobalMeshBuilder(coverage, new LfSampler(land, worldW, worldH, TileSize),
            new LfSampler(sea, worldW, worldH, TileSize), tilesW, tilesH, TileSize, bob);

        var per = builder.MeshesPerAxis;
        ctx.Log($"building {per}x{per} land and sea meshes...");
        var results = new ConcurrentDictionary<(MeshKind, int), GlobalMeshBuilder.MeshResult?>();
        Parallel.For(0, per * per * 2, new ParallelOptions { CancellationToken = ctx.Cancel }, k =>
        {
            var kind = k < per * per ? MeshKind.Land : MeshKind.Sea;
            var index = k % (per * per);
            results[(kind, index)] = builder.Build(index / per, index % per, kind);
        });
        BobGlobalHeight.FlushTrace();

        var outDir = ctx.OutFile("global_meshes");
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.EnumerateFiles(outDir).Where(f => Path.GetFileName(f).StartsWith("land_mesh_", StringComparison.OrdinalIgnoreCase)
                                                                     || Path.GetFileName(f).StartsWith("sea_mesh_", StringComparison.OrdinalIgnoreCase)))
            File.Delete(old);
        var written = new List<string>();
        foreach (var kind in new[] { MeshKind.Land, MeshKind.Sea })
        {
            var number = 0;
            var stem = kind == MeshKind.Land ? "land_mesh_" : "sea_mesh_";
            for (var index = 0; index < per * per; index++)
            {
                if (results[(kind, index)] is not { } mesh) continue;
                if (bob is not null)   // header bytes BOB leaves from its string buffers (identical across its runs)
                {
                    if (kind == MeshKind.Land) mesh.Model.LodQuality = [0, 0, StaleLandByte(ctx, number), 0];
                    var shader = (byte[])mesh.Model.Shader.Clone();
                    Array.Clear(shader, 16, 16);
                    shader[24] = 0x9E;
                    shader[25] = 0xD4;
                    mesh.Model.Shader = shader;
                }
                var model = Path.Combine(outDir, $"{stem}{number}.rigid_model_v2");
                mesh.Model.Write(model);
                written.Add(model);
                if (mesh.HeightMap is not null)
                {
                    var cm = Path.Combine(outDir, $"{stem}{number}.compressed_map");
                    CompressedMap.Write(cm, mesh.HeightMap, mesh.HeightHeader!);
                    written.Add(cm);
                }
                number++;
            }
            notes.Add($"{number} {kind.ToString().ToLowerInvariant()} meshes");
        }
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>Byte 0xA6 of a land mesh: BOB leaves character 165 of the mesh's land_mesh_N.compressed_map path there
    /// (kit working folder\terrain\campaigns\map\global_meshes\...). On the Steam kits that is the first digit of N for
    /// assembly_kit_190E / 3k_190e_expanded_map and 's', 'e', 'r' for 1-, 2-, 3-digit N on assembly_kit /
    /// 3k_dlc07_main_map (BOB runs on both, 2026-10-05). 0 when the path is shorter.</summary>
    internal static byte StaleLandByte(CampaignBuildContext ctx, int number)
    {
        var path = Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "global_meshes",
            $"land_mesh_{number}.compressed_map");
        return path.Length > 165 ? (byte)path[165] : (byte)0;
    }

    private static string? TileListSource(CampaignBuildContext ctx) =>
        new[] { ctx.OutFile("tile_list.bin"),
                Path.Combine(ctx.Paths.AkWorkingDir, "terrain", "campaigns", ctx.MapName, "tile_list.bin"),
                Path.Combine(ctx.Paths.TerrainDir, "tile_list.bin") }
            .FirstOrDefault(File.Exists);
}
