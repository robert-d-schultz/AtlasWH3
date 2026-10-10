using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

/// <summary>The tile map validator and BOB's tile matching on WH3's campaign tile database (the vanilla packs) and IEE's
/// tile map. The neighbourhood rules (thick lines, coasts, cliffs) and the reference-pattern check are still 3K's: their
/// tile set categories are 3K names, and the reference is 3K's vanilla tile map. They come with Phase 5's WH3 rules.</summary>
public class TileMapCheckTests
{
    private static ProjectPaths Paths => TestKits.Paths(TestKits.Iee);
    private static string IeeTileMap => Path.Combine(Paths.AkTerrainDir, "tile_map.png");
    private static readonly Lazy<CampaignTileDatabase> Db = new(() => TileMapValidator.LoadDatabase(Paths));
    private const uint Generic = 0xdfb491;

    private static bool HaveIee => File.Exists(IeeTileMap) && File.Exists(Path.Combine(TestKits.Wh3GameData, "tiles_campaign.pack"));
    private static bool HaveDb => File.Exists(Path.Combine(TestKits.Wh3GameData, "tiles_campaign.pack"));

    private static TileMapReport Check(string tileMap) =>
        TileMapValidator.Run(Paths, new TileMapCheckOptions { TileMap = tileMap, TileMapOnly = true }, Db.Value);

    /// <summary>Copies IEE's tile_map.png with hex edits applied (set(col, row, rgb)) into a temp file.</summary>
    private static string EditedIee(Action<HexTileMap, Action<int, int, uint>> edit)
    {
        var map = HexTileMap.Read(IeeTileMap);
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
                        ok = colours[map.Index(c + dc, r + dr)] == Generic;
                if (ok) return (c, r);
            }
        throw new InvalidOperationException("no open land");
    }

    [Fact]
    public void Validator_IeeTileMapHasNoErrors()
    {
        if (!HaveIee) return;
        var report = Check(IeeTileMap);
        Assert.Equal((1600, 970), (report.HexWidth, report.HexHeight));
        Assert.Equal(0, report.Errors);
        Assert.Equal(103_366, Assert.Single(report.Findings, f => f.Code == "palette.black").Count);   // hexes with no tile set
    }

    [Fact]
    public void Validator_FlagsWrongPaletteColours()
    {
        if (!HaveIee) return;
        var file = EditedIee((map, set) =>
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
            Assert.Contains("nearest: roads_grey_dark 625e56", f.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Simulator_TilesOpenLandWithoutHoles()
    {
        if (!HaveDb) return;
        // 40 x 30 hexes of generic land: large tiles first, the rest by link targets, nothing left uncovered
        var map = HexTileMap.FromHexColours(40, 30, Enumerable.Repeat(Generic, 40 * 30).ToArray());
        var result = new TileMatchSimulator(Db.Value).Run(map, new byte[map.Pixels.Length]);
        Assert.True(result.Summary.PlacedPerPass["large"] > 0);
        Assert.Empty(result.NoTile);
        Assert.Equal(result.Summary.Placed, result.Tiles.Count(t => t.Layer == 1));
    }

    [Fact]
    public void Simulator_FlagsAPointNoTileFits()
    {
        if (!HaveDb) return;
        // a lone river_start hex in open land: no river_start tile links to nothing, so it stays a hole
        var colours = Enumerable.Repeat(Generic, 20 * 20).ToArray();
        colours[10 * 20 + 10] = 0xb4b4ff;
        var map = HexTileMap.FromHexColours(20, 20, colours);
        var result = new TileMatchSimulator(Db.Value).Run(map, new byte[map.Pixels.Length]);
        Assert.Contains(result.NoTileHexes(map), h => (h[0], h[1]) == (10, 10));
    }

    [Fact]
    public void Validator_RejectsTgaAndBadLayout()
    {
        if (!HaveDb) return;
        var tga = Path.Combine(Path.GetTempPath(), $"tilemap_check_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tga, [0, 1, 1, 0, 0, 0, 1, 32, 0, 0, 0, 0, 4, 0, 4, 0, 8, 0]);
        var even = Path.Combine(Path.GetTempPath(), $"tilemap_check_{Guid.NewGuid():N}.png");
        PngMap.Write(even, new Raster<uint>(64, 64, Enumerable.Repeat(0xff91b4dfu, 64 * 64).ToArray()));
        try
        {
            Assert.Equal("file.format", Assert.Single(Check(tga).Findings, f => f.Severity == TileMapFinding.Error).Code);
            Assert.Contains(Check(even).Findings, f => f.Code == "layout.size" && f.Severity == TileMapFinding.Error);
        }
        finally { File.Delete(tga); File.Delete(even); }
    }
}
