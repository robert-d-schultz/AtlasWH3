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
            "terrain/campaigns/3k_190e_expanded_map/lf_height_map.dds",
            @"campaign_maps\other_map\display\trees\trees.campaign_tree_list",
            "terrain/campaigns/readme.txt",           // a file, not a map folder
            "terrain/tiles/campaign/x/y.bmd",
            "db/start_pos_tables/data__",
        ]);
        Assert.Equal(["3k_190e_expanded_map", "other_map"], maps);
    }

    [Fact]
    public void Expanded_kit_lists_the_custom_map()
    {
        if (!Directory.Exists(TestKits.Expanded)) return;
        var maps = MapCatalog.KitMaps(TestKits.Expanded);
        Assert.Contains("3k_190e_expanded_map", maps);
        Assert.Contains("3k_190e_expanded_map", MapCatalog.All(TestKits.Expanded, []).Select(m => m.Name));
    }

    [Fact]
    public void Vanilla_kit_lists_the_main_map()
    {
        if (!Directory.Exists(TestKits.Vanilla)) return;
        Assert.Contains(MapCatalog.DefaultMap, MapCatalog.KitMaps(TestKits.Vanilla));
    }

    [Fact]
    public void Linked_map_pack_is_found_read_only()
    {
        var pack = Path.Combine(Defaults.GameData, "!!190_expanded_region_test_main190.pack");
        if (!File.Exists(pack)) return;
        var before = (File.GetLastWriteTimeUtc(pack), new FileInfo(pack).Length);
        var all = MapCatalog.All(TestKits.Expanded, [pack]);
        var expanded = all.Single(m => m.Name == "3k_190e_expanded_map");
        Assert.Contains(MapOrigin.Pack, expanded.Origins);
        Assert.Equal([pack], expanded.Packs);
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
    public void Link_and_unlink_packs_without_duplicates()
    {
        var s = new AppSettings();
        var pack = Path.Combine(Path.GetTempPath(), "x", "mod.pack");
        Assert.True(s.LinkPack(pack));
        Assert.False(s.LinkPack(pack.ToUpperInvariant()));
        Assert.Equal([Path.GetFullPath(pack)], s.LinkedPacks);
        Assert.Throws<ArgumentException>(() => s.LinkPack(Path.Combine(Path.GetTempPath(), "mod.zip")));
        Assert.True(s.UnlinkPack(pack));
        Assert.Empty(s.LinkedPacks);
    }

    [Fact]
    public void WithSelection_applies_remembered_map_and_existing_linked_packs()
    {
        var dir = Temp();
        try
        {
            var pack = Path.Combine(dir, "mod.pack");
            File.WriteAllBytes(pack, []);
            var s = new AppSettings { MapName = "3k_190e_expanded_map", LinkedPacks = [pack, Path.Combine(dir, "gone.pack")] };
            var paths = MapCatalog.WithSelection(TestKits.VanillaPaths, s);
            Assert.Equal("3k_190e_expanded_map", paths.MapName);
            Assert.Equal([pack], paths.ModPacks);

            // a map or packs given on the command line win
            var cli = TestKits.VanillaPaths with { MapName = "cli_map", ModPacks = ["cli.pack"] };
            var kept = MapCatalog.WithSelection(cli, s);
            Assert.Equal("cli_map", kept.MapName);
            Assert.Equal(["cli.pack"], kept.ModPacks);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SourceGuardTests
{
    private const string Data = @"C:\Games\Three Kingdoms\data";

    [Theory]
    [InlineData(@"C:\Games\Three Kingdoms\data")]
    [InlineData(@"C:\Games\Three Kingdoms\data\")]
    [InlineData(@"C:\Games\Three Kingdoms\DATA\terrain\campaigns\x\lf_height_map.dds")]
    [InlineData(@"C:\Games\Three Kingdoms\data\..\data\db")]
    [InlineData(@"D:\anywhere\some.pack")]
    [InlineData(@"D:\mods\linked.PACK")]
    public void Refuses_game_data_and_packs(string path)
    {
        Assert.True(SourceGuard.IsProtected(path, Data, [@"D:\mods\linked.pack"]));
        Assert.Throws<UnauthorizedAccessException>(() => SourceGuard.EnsureWritable(path, Data, [@"D:\mods\linked.pack"]));
    }

    [Theory]
    [InlineData(@"C:\Games\Three Kingdoms\assembly_kit_190E\raw_data\terrain\campaigns\x\x.terry")]
    [InlineData(@"C:\Games\Three Kingdoms\data_backup\file.dds")]
    [InlineData(@"C:\Users\me\AppData\Local\AtlasWH3\vanilla")]
    [InlineData("")]
    public void Allows_kit_output_and_cache(string path)
    {
        Assert.False(SourceGuard.IsProtected(path, Data, [@"D:\mods\linked.pack"]));
        SourceGuard.EnsureWritable(path, Data);
    }

    [Fact]
    public void ProjectPaths_overload_uses_its_game_data_and_mod_packs()
    {
        var paths = TestKits.VanillaPaths with { GameDataDir = Data, ModPacks = [@"D:\mods\linked.pack"] };
        Assert.Throws<UnauthorizedAccessException>(() => SourceGuard.EnsureWritable(Path.Combine(Data, "x.txt"), paths));
        SourceGuard.EnsureWritable(paths.AkTerrainDir, paths);
    }

    [Fact]
    public void Prepare_refuses_a_cache_inside_the_game_data_folder()
    {
        var data = Path.Combine(Path.GetTempPath(), "atlaswh3_guard_" + Guid.NewGuid().ToString("N"), "data");
        Assert.Throws<UnauthorizedAccessException>(() =>
            GameSetup.ExtractCompiledMap(data, "any_map", Path.Combine(data, "cache"), []));
        Assert.Throws<UnauthorizedAccessException>(() => GameSetup.ExtractDbTables(data, Path.Combine(data, "db"), []));
        Assert.False(Directory.Exists(data));
    }
}
