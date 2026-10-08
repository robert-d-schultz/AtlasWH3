using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

public class TileDatabaseTests
{
    private static CampaignTileDatabase? Wh3()
    {
        var path = Path.Combine(TestKits.Wh3GameData, "tiles_campaign.pack");
        if (!File.Exists(path)) return null;
        var pack = PackFile.Open(path);
        var prefix = PackFile.Normalize(CampaignTileDatabase.PackFolder);
        var tiles = pack.Entries.Values.Where(e => PackFile.Normalize(e.Path).StartsWith(prefix + "tiles\\", StringComparison.Ordinal))
            .Select(e => (Path.GetFileName(e.Path), pack.TryRead(e.Path)!));
        return CampaignTileDatabase.Load(pack.TryRead(prefix + "_settings.bin")!, tiles);
    }

    /// <summary>Every tile of WH3's campaign database (tile v5-v9, variation v8-v13) parses to its last byte.</summary>
    [Fact]
    public void Wh3_EveryTileParses()
    {
        if (Wh3() is not { } db) return;
        Assert.Empty(db.Errors);
        Assert.Equal(320, db.Tiles.Count);
        Assert.Equal(["default"], db.Climates.Select(c => c.Name));
        Assert.Equal(33, db.TileSets.Count);
        Assert.Equal(0xDFB491u, db.TileSet("generic")!.Rgb);
        Assert.Equal(0xFFFF00u, db.TileSet("sea_coast")!.Rgb);
        Assert.True(db.TileSet("sea_coast")!.ExcludeFromGlobalMesh);

        var road = db.Tiles.Single(t => t.TileSet == "roads_light" && t.Name == "4x2_straight_b0_b3");
        Assert.Equal((4, 2, 7), (road.Width, road.Height, road.Version));
        Assert.Equal(@"terrain\tiles\campaign\roads_light\4x2_straight_b0_b3\", road.Variations.Single().Location);
        // WH3 lists an empty name before 3K's "climate" slot
        Assert.Equal(["", "climate", "road0", "", "", "", "", ""], road.Variations[0].TextureLayers);
        Assert.Equal([(0, 1), (3, 1)], road.LinkTargets.Select(t => (t.X, t.Y)));
        Assert.Equal(2, road.Links.Count);
        Assert.All(road.Links, l => Assert.True(l.TestEquals));
    }

    [Fact]
    public void Wh3_LightReaderAgrees()
    {
        var path = Path.Combine(TestKits.Wh3GameData, "tiles_campaign.pack");
        if (!File.Exists(path)) return;
        var bytes = PackFile.Open(path).TryRead(@"terrain\tiles\campaign\_tile_database\tiles\cliff_gen_long_curve1.bin")!;
        var info = TileDatabase.Parse(bytes);
        var full = CampaignTileDatabase.ReadTile("x", bytes);
        Assert.Equal((full.Name, full.TileSet, full.Mask, full.Width, full.Height), (info.Name, info.Category, info.Mask, info.Width, info.Height));
        Assert.Equal(24, info.Mask.Length);
        Assert.Equal(@"terrain\tiles\campaign\cliff_gen\long_curve1\", info.Path);
    }
}
