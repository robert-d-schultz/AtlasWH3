using AtlasWH3.Core;
using Xunit;

namespace AtlasWH3.Tests;

public class MapCatalogTests
{
    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atlaswh3_mapcatalog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void KitMaps_lists_campaign_folders_with_a_terry()
    {
        var kit = Temp();
        try
        {
            var campaigns = Path.Combine(kit, "raw_data", "terrain", "campaigns");
            Directory.CreateDirectory(Path.Combine(campaigns, "my_map"));
            File.WriteAllText(Path.Combine(campaigns, "my_map", "my_map.terry"), "<terry/>");
            Directory.CreateDirectory(Path.Combine(campaigns, "no_terry"));
            Assert.Equal(["my_map"], MapCatalog.KitMaps(kit));
            Assert.Empty(MapCatalog.KitMaps(Path.Combine(kit, "missing")));
        }
        finally { Directory.Delete(kit, true); }
    }

    [Fact]
    public void MapsIn_reads_terrain_and_campaign_maps_folders()
    {
        var maps = MapCatalog.MapsIn([
            "terrain/campaigns/cr_combi_expanded_map_1/full_height_map.dds",
            @"campaign_maps\other_map\display\trees\trees.campaign_tree_list",
            "terrain/campaigns/readme.txt",           // a file, not a map folder
            "terrain/tiles/campaign/x/y.bmd",
            "db/start_pos_tables/data__",
        ]);
        Assert.Equal(["cr_combi_expanded_map_1", "other_map"], maps);
    }

    [Fact]
    public void Wh3_kit_lists_the_fixture_maps()
    {
        if (!Directory.Exists(TestKits.Wh3Kit)) return;
        var maps = MapCatalog.KitMaps(TestKits.Wh3Kit);
        Assert.Contains(TestKits.Iee, maps);
        Assert.Contains(TestKits.OldWorld, maps);
        Assert.Contains(TestKits.Iee, MapCatalog.All(TestKits.Wh3Kit, []).Select(m => m.Name));
    }

    [Fact]
    public void Linked_map_pack_is_found_read_only()
    {
        var pack = TestKits.Pack(TestKits.IeePack);
        if (!File.Exists(pack)) return;
        var before = (File.GetLastWriteTimeUtc(pack), new FileInfo(pack).Length);
        var all = MapCatalog.All(TestKits.Kit(TestKits.Iee), [pack]);
        var iee = all.Single(m => m.Name == TestKits.Iee);
        Assert.Equal([MapOrigin.Kit, MapOrigin.Pack], iee.Origins.Order());
        Assert.Equal([pack], iee.Packs);
        Assert.DoesNotContain(MapOrigin.Pack, all.Single(m => m.Name == TestKits.OldWorld).Origins);
        Assert.Equal(before, (File.GetLastWriteTimeUtc(pack), new FileInfo(pack).Length));
    }

    [Fact]
    public void Unreadable_pack_is_reported_and_skipped()
    {
        var dir = Temp();
        try
        {
            var bad = Path.Combine(dir, "bad.pack");
            File.WriteAllText(bad, "not a pack");
            var log = new List<string>();
            Assert.Empty(MapCatalog.All(null, [bad], null, log.Add));
            Assert.Single(log);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WithSelection_applies_the_remembered_map()
    {
        var s = new AppSettings { MapName = TestKits.Iee };
        var paths = MapCatalog.WithSelection(new ProjectPaths(), s);
        Assert.Equal(TestKits.Iee, paths.MapName);
        Assert.Empty(paths.ModPacks);

        // a map given on the command line wins
        var cli = new ProjectPaths { MapName = "cli_map", ModPacks = ["cli.pack"] };
        var kept = MapCatalog.WithSelection(cli, s);
        Assert.Equal("cli_map", kept.MapName);
        Assert.Equal(["cli.pack"], kept.ModPacks);
    }
}

public class SourceGuardTests
{
    private const string Data = @"C:\Games\Warhammer III\data";

    [Theory]
    [InlineData(@"C:\Games\Warhammer III\data")]
    [InlineData(@"C:\Games\Warhammer III\data\")]
    [InlineData(@"C:\Games\Warhammer III\DATA\terrain\campaigns\x\full_height_map.dds")]
    [InlineData(@"C:\Games\Warhammer III\data\..\data\db")]
    [InlineData(@"D:\anywhere\some.pack")]
    [InlineData(@"D:\mods\linked.PACK")]
    public void Refuses_game_data_and_packs(string path)
    {
        Assert.True(SourceGuard.IsProtected(path, Data, [@"D:\mods\linked.pack"]));
        Assert.Throws<UnauthorizedAccessException>(() => SourceGuard.EnsureWritable(path, Data, [@"D:\mods\linked.pack"]));
    }

    [Theory]
    [InlineData(@"C:\Games\Warhammer III\assembly_kit\raw_data\terrain\campaigns\x\x.terry")]
    [InlineData(@"C:\Games\Warhammer III\data_backup\file.dds")]
    [InlineData(@"C:\Users\me\AppData\Local\AtlasWH3\output")]
    [InlineData("")]
    public void Allows_kit_output_and_cache(string path)
    {
        Assert.False(SourceGuard.IsProtected(path, Data, [@"D:\mods\linked.pack"]));
        SourceGuard.EnsureWritable(path, Data);
    }

    [Fact]
    public void ProjectPaths_overload_uses_its_game_data_and_mod_packs()
    {
        var paths = new ProjectPaths { GameDataDir = Data, ModPacks = [@"D:\mods\linked.pack"] };
        Assert.Throws<UnauthorizedAccessException>(() => SourceGuard.EnsureWritable(Path.Combine(Data, "x.txt"), paths));
        SourceGuard.EnsureWritable(paths.AkTerrainDir, paths);
    }
}
