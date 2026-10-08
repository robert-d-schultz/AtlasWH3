using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

public class TileMapCheckTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static string DatabaseDir =>
        Path.Combine(Path.GetDirectoryName(Paths.VanillaRoot)!, "terrain", "tiles", "campaign", "_tile_database");
    private static string VanillaMapDir => Path.Combine(Path.GetDirectoryName(Paths.VanillaRoot)!, "3k_dlc07_main_map");
    private static string VanillaTileMap => Path.Combine(VanillaMapDir, "tile_map.png");
    private static readonly Lazy<CampaignTileDatabase> Db = new(() => CampaignTileDatabase.LoadFolder(DatabaseDir));

    private static bool HaveVanilla => File.Exists(VanillaTileMap) && Directory.Exists(DatabaseDir);

    [Fact]
    public void CampaignTileDatabase_ParsesEveryVanillaTile()
    {
        if (!Directory.Exists(DatabaseDir)) return;
        var db = Db.Value;
        Assert.Empty(db.Errors);
        Assert.Equal(544, db.Tiles.Count);
        Assert.Equal(["cold", "arid", "temperate", "sub_tropical"], db.Climates.Select(c => c.Name));
        Assert.Equal(0x005555u, db.Climates[0].Rgb);
        Assert.Equal(27, db.TileSets.Count);
        Assert.Equal(0x96aa64u, db.TileSet("generic")!.Rgb);
        Assert.Equal(0x3971b7u, db.TileSet("generic_sea")!.Rgb);
        Assert.Equal(0x0000ffu, db.TileSet("river")!.Rgb);
        Assert.Equal("generic_sea", db.TileSet("river_mouth")!.AlsoPlaceTileSet);
        Assert.Equal(24, db.Tiles.Count(t => t.UseAltLf));
        Assert.All(db.Tiles, t => Assert.NotNull(db.TileSet(t.TileSet)));
    }

    [Fact]
    public void CampaignTileDatabase_ParsesCrossingLinks()
    {
        var file = Path.Combine(DatabaseDir, "tiles", "river_crossing_cross_1.bin");
        if (!File.Exists(file)) return;
        var tile = CampaignTileDatabase.ReadTile("river_crossing_cross_1.bin", File.ReadAllBytes(file));
        Assert.Equal("river_crossing", tile.TileSet);
        Assert.True(tile.RandomRotatable);
        Assert.Equal([("roads_paved", 0, 0), ("river", 0, 1), ("river", 1, 0), ("roads_paved", 1, 1)],
            tile.LinkTargets.Select(t => (t.TargetSet, t.X, t.Y)));
        Assert.Equal(4, tile.Links.Count);
        Assert.Equal(("river", -1, 1, true), (tile.Links[0].LinkSet, tile.Links[0].X, tile.Links[0].Y, tile.Links[0].TestEquals));
        Assert.Equal("arid_1", tile.Variations[0].Climate);
        Assert.StartsWith(@"terrain\tiles\campaign\river_crossing\cross_1", tile.Variations[0].Location);
    }

    private static TileMapReport Check(string tileMap) =>
        TileMapValidator.Run(Paths, new TileMapCheckOptions { TileMap = tileMap, ClimateDir = VanillaMapDir, TileMapOnly = true }, Db.Value);

    /// <summary>Copies vanilla tile_map.png with hex edits applied (set(col, row, rgb)) into a temp file.</summary>
    private static string EditedVanilla(Action<HexTileMap, Action<int, int, uint>> edit)
    {
        var map = HexTileMap.Read(VanillaTileMap);
        void Set(int c, int r, uint rgb)
        {
            for (var d = 0; d < 4; d++)
            {
                var (x, y) = map.HexPixel(c, r, d & 1, d >> 1);
                map.Pixels[y * map.PixelWidth + x] = rgb;
            }
        }
        edit(map, Set);
        var raster = new Raster<uint>(map.PixelWidth, map.PixelHeight);
        for (var i = 0; i < raster.Data.Length; i++)
        {
            var v = map.Pixels[i];
            raster.Data[i] = 0xff000000 | (v & 0xff) << 16 | (v & 0xff00) | (v >> 16 & 0xff);
        }
        var path = Path.Combine(Path.GetTempPath(), $"tilemap_check_{Guid.NewGuid():N}.png");
        PngMap.Write(path, raster);
        return path;
    }

    /// <summary>A hex well inside the map with only generic land within <paramref name="radius"/> columns / rows.</summary>
    private static (int C, int R) LandHex(HexTileMap map, int radius = 4)
    {
        var colours = map.HexColours();
        for (var r = 300; r < map.Height - radius; r++)
            for (var c = 300; c < map.Width - radius; c++)
            {
                var ok = true;
                for (var dr = -radius; dr <= radius && ok; dr++)
                    for (var dc = -radius; dc <= radius && ok; dc++)
                        ok = colours[map.Index(c + dc, r + dr)] == 0x96aa64;
                if (ok) return (c, r);
            }
        throw new InvalidOperationException("no open land");
    }

    [Fact]
    public void Validator_VanillaTileMapHasNoErrors()
    {
        if (!HaveVanilla) return;
        var report = Check(VanillaTileMap);
        Assert.Equal((892, 702), (report.HexWidth, report.HexHeight));
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Findings, f => f.Code is "pattern.unseen" or "palette.unused_set");
    }

    [Fact]
    public void Validator_FlagsWrongPaletteColours()
    {
        if (!HaveVanilla) return;
        var file = EditedVanilla((map, set) =>
        {
            var (c0, r0) = LandHex(map);
            for (var r = r0 - 3; r <= r0 + 3; r++)
                for (var c = c0 - 3; c <= c0 + 3; c++) set(c, r, 0x5a7647);     // CAIME's Attila land colour
        });
        try
        {
            var f = Assert.Single(Check(file).Findings, f => f.Code == "palette.unknown");
            Assert.Equal(TileMapFinding.Error, f.Severity);
            Assert.Equal(49, f.Count);
            Assert.Contains("generic 96aa64", f.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Validator_FlagsThickRoadAndCliffNextToBeach()
    {
        if (!HaveVanilla) return;
        (int, int) road = default;
        var file = EditedVanilla((map, set) =>
        {
            var (c, r) = LandHex(map);
            road = (c, r);
            set(c, r, 0x5d0018);                                                 // a road hex with 4 road neighbours
            for (var d = 0; d < 4; d++) { map.Neighbour(c, r, d, out var nc, out var nr); set(nc, nr, 0x5d0018); }
            set(c + 3, r, 0xf9ad69);                                             // a cliff next to a beach
            map.Neighbour(c + 3, r, 0, out var bc, out var br);
            set(bc, br, 0xffff00);
        });
        try
        {
            var report = Check(file);
            var thick = Assert.Single(report.Findings, f => f.Code == "line.thick");
            Assert.Contains(thick.AllHexes, h => (h[0], h[1]) == road);
            Assert.Contains(report.Findings, f => f.Code == "coast.cliff_end");
            Assert.Contains(report.Findings, f => f.Code == "pattern.unseen");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void CheckMap_ReportsOnlyTheEditedRegion()
    {
        if (!HaveVanilla) return;
        var map = HexTileMap.Read(VanillaTileMap);
        var (c, r) = LandHex(map);
        map.SetHex(c, r, 0x5d0018);
        for (var d = 0; d < 4; d++) { map.Neighbour(c, r, d, out var nc, out var nr); map.SetHex(nc, nr, 0x5d0018); }
        var findings = TileMapValidator.CheckMap(map, Db.Value, [(c, r)], VanillaTileMap);
        var thick = Assert.Single(findings, f => f.Code == "line.thick");
        Assert.Equal([(c, r)], thick.AllHexes.Select(h => (h[0], h[1])));
        Assert.DoesNotContain(findings, f => f.Code is "coast.missing_ring" or "palette.stray");   // vanilla's own issues are elsewhere
        Assert.Contains(TileMapValidator.CheckMap(map, Db.Value, vanillaTileMap: VanillaTileMap), f => f.Code == "coast.missing_ring");
    }

    [Fact]
    public void Simulator_TilesOpenLandWithoutHoles()
    {
        if (!Directory.Exists(DatabaseDir)) return;
        // 40 x 30 hexes of generic land: large tiles first, the rest filled in pass "all", nothing left uncovered
        var map = HexTileMap.FromHexColours(40, 30, Enumerable.Repeat(0x96aa64u, 40 * 30).ToArray());
        var result = new TileMatchSimulator(Db.Value).Run(map, new byte[map.Pixels.Length]);
        Assert.True(result.Summary.PlacedPerPass["large"] > 0);
        Assert.Empty(result.NoTile);
        Assert.Equal(result.Summary.Placed, result.Tiles.Count(t => t.Layer == 1));
    }

    [Fact]
    public void Simulator_FlagsAPointNoTileFits()
    {
        if (!Directory.Exists(DatabaseDir)) return;
        // a lone river_start hex in open land: no river_start tile links to nothing, so it stays a hole
        var colours = Enumerable.Repeat(0x96aa64u, 20 * 20).ToArray();
        colours[10 * 20 + 10] = 0xb4b4ff;
        var map = HexTileMap.FromHexColours(20, 20, colours);
        var result = new TileMatchSimulator(Db.Value).Run(map, new byte[map.Pixels.Length]);
        Assert.Contains(result.NoTileHexes(map), h => (h[0], h[1]) == (10, 10));
    }

    /// <summary>Against the BOB run in output/bob_runs/20260927_191115_step2 (vanilla tile map). ~70 s, so it runs
    /// only with TERRY_SLOW_TESTS=1.</summary>
    [Fact]
    public void Simulator_ReproducesBobOnVanilla()
    {
        if (Environment.GetEnvironmentVariable("TERRY_SLOW_TESTS") != "1" || !HaveVanilla) return;
        var map = HexTileMap.Read(VanillaTileMap);
        var result = new TileMatchSimulator(Db.Value).Run(map, new byte[map.Pixels.Length]);
        var p = result.Summary.PlacedPerPass;
        Assert.Equal((22029, 375, 416, 110122), (p["large"], p["transition"], p["junction"], p["link_target"]));
        Assert.InRange(p["linked"], 17032 - 20, 17032 + 20);
        Assert.InRange(p["all"], 32037 - 30, 32037 + 30);
        Assert.InRange(result.NoTile.Count, 170, 180);          // BOB's tile list leaves 176 points uncovered
    }

    [Fact]
    public void Validator_RejectsTgaAndBadLayout()
    {
        if (!Directory.Exists(DatabaseDir)) return;
        var tga = Path.Combine(Path.GetTempPath(), $"tilemap_check_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tga, [0, 1, 1, 0, 0, 0, 1, 32, 0, 0, 0, 0, 4, 0, 4, 0, 8, 0]);
        var even = Path.Combine(Path.GetTempPath(), $"tilemap_check_{Guid.NewGuid():N}.png");
        PngMap.Write(even, new Raster<uint>(64, 64, Enumerable.Repeat(0xff64aa96u, 64 * 64).ToArray()));
        try
        {
            Assert.Equal("file.format", Assert.Single(Check(tga).Findings, f => f.Severity == TileMapFinding.Error).Code);
            Assert.Contains(Check(even).Findings, f => f.Code == "layout.size" && f.Severity == TileMapFinding.Error);
        }
        finally { File.Delete(tga); File.Delete(even); }
    }
}
