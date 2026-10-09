using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Tests;

/// <summary>BOB's campaign tree placement and heights, against the tree lists BOB made for the user's maps.</summary>
public class CampaignTreeGeneratorTests
{
    /// <summary>The fixture maps and the mod pack that carries each one's own tree ids.</summary>
    public static readonly (string Map, string ModPack)[] Fixtures =
        [("cr_combi_expanded_map_1", "!cr_immortal_empires_expanded.pack"), ("cr_oldworld_map_1", "!cr_oldworld_campaign.pack")];

    private static string Terry(string map) => Path.Combine(TestKits.Wh3Kit, "raw_data", "terrain", "campaigns", map, map + ".terry");
    private static string TreeList(string map) =>
        Path.Combine(TestKits.Wh3Kit, "working_data", "campaign_maps", map, "display", "trees", "trees.campaign_tree_list");
    private static string MapData(string map) => Path.Combine(TestKits.Wh3Kit, "working_data", "campaign_maps", map, "map_data.esf");
    private static string ModPack(string pack) => Path.Combine(TestKits.Wh3GameData, pack);

    private static bool Have(string map, string pack) =>
        File.Exists(Terry(map)) && File.Exists(TreeList(map)) && File.Exists(MapData(map)) && File.Exists(ModPack(pack));

    [Fact]
    public void CaHash_MatchesCalibsMurmur()
    {
        // Values from calling ?murmur_hash@CA@@YAIPEBEI@Z in calibs.modder.x64.dll.
        Assert.Equal(0x7195E3E8u, CaHash.Murmur("abc"));
        Assert.Equal(0x83BF88BCu, CaHash.Murmur("bamboo_1"));
        Assert.Equal(0xB45F22F3u, CaHash.Murmur("general_rock_medium_1"));
    }

    [Fact]
    public void HexGrid_FromMapData_MatchesTreeListHeaders()
    {
        foreach (var (map, pack) in Fixtures)
        {
            if (!Have(map, pack)) continue;
            var bounds = MapDataBounds.Read(MapData(map));
            var size = TerryProject.Load(Terry(map)).Find("CampaignTree")!.Size;
            var grid = HexGrid.ForTreeMap(size.Width, size.Height, bounds.Width);
            var list = CampaignTreeList.Load(TreeList(map));
            Assert.Equal((list.WorldWidth, list.WorldHeight), (grid.WorldWidth, grid.WorldHeight));
            Assert.Equal(bounds.Height, grid.WorldHeight);
        }
    }

    [Fact]
    public void Fixtures_EveryTreeIdPositionAndRotation()
    {
        foreach (var (map, pack) in Fixtures)
        {
            if (!Have(map, pack)) continue;
            var reference = CampaignTreeList.Load(TreeList(map));
            var rebuilt = Generate(map, pack, (_, _, _, _) => 0f);
            Assert.Equal(reference.Types.Select(t => t.Name), rebuilt.Types.Select(t => t.Name));
            for (var i = 0; i < reference.Types.Count; i++)
            {
                var (a, b) = (reference.Types[i].Instances, rebuilt.Types[i].Instances);
                Assert.Equal(a.Count, b.Count);
                for (var j = 0; j < a.Count; j++)
                    Assert.Equal((a[j].X, a[j].Z, a[j].Variant, a[j].Flag, a[j].Tag), (b[j].X, b[j].Z, b[j].Variant, b[j].Flag, b[j].Tag));
            }
        }
    }

    /// <summary>
    /// TreeHeightField against BOB's IEE list (2026-10-08): 238,143 of 253,903 heights bit-exact, 253,446 within 1e-3.
    /// Most of the rest are ulps under height patches (BOB's prop matrix differs from QtuTransform's by a little).
    /// </summary>
    [Fact]
    public void Iee_Heights_CloseToBob()
    {
        var (map, pack) = Fixtures[0];
        var logic = CompiledFormatTests.Built(map, "full_logic_map.compressed_map");
        if (!Have(map, pack) || !File.Exists(logic)) return;
        var packs = GameSetup.OpenWithLinked(TestKits.Wh3GameData, [ModPack(pack)]);
        var notes = new List<string>();
        var project = TerryProject.Load(Terry(map));
        var field = new TreeHeightField(CompressedMap.Read(logic), project.WorldWidth!.Value, TreeHeightField.LoadPatches(project, packs, notes));
        var trees = CampaignTreeList.Load(TreeList(map)).Types.SelectMany(t => t.Instances).ToList();
        int exact = 0, close = 0;
        foreach (var t in trees)
        {
            var y = field.Height(t.X, t.Z);
            if (y == t.Y) exact++;
            if (Math.Abs(y - t.Y) < 1e-3f) close++;
        }
        Console.WriteLine($"IEE tree heights: {exact} of {trees.Count} bit-exact, {close} within 1e-3; {string.Join("; ", notes)}");
        Assert.True(exact >= 238_100, $"{exact} of {trees.Count} bit-exact");
        Assert.True(close >= 253_400, $"{close} of {trees.Count} within 1e-3");
    }

    /// <summary>Old World (no height-patched props): the whole list byte for byte (2026-10-08).</summary>
    [Fact]
    public void OldWorld_ByteIdenticalToBob()
    {
        var (map, pack) = Fixtures[1];
        var logic = CompiledFormatTests.Built(map, "full_logic_map.compressed_map");
        if (!Have(map, pack) || !File.Exists(logic)) return;
        var project = TerryProject.Load(Terry(map));
        var packs = GameSetup.OpenWithLinked(TestKits.Wh3GameData, [ModPack(pack)]);
        var field = new TreeHeightField(CompressedMap.Read(logic), project.WorldWidth!.Value, TreeHeightField.LoadPatches(project, packs, []));
        var list = Generate(map, pack, (_, _, x, z) => field.Height(x, z));
        Assert.Equal(File.ReadAllBytes(TreeList(map)), list.ToBytes());
    }

    [Fact]
    public void MapDataBounds_OldWorld_IsNotThePlayableAreaRow()
    {
        var path = MapData("cr_oldworld_map_1");
        if (!File.Exists(path)) return;
        // the playable-area row (1068.1111 x 748.1) is IEE's; BOB's grid and the tree list use map_data.esf's
        Assert.Equal(new MapDataBounds(0, 0, 1367.396f, 1368.7428f), MapDataBounds.Read(path));
    }

    private static CampaignTreeList Generate(string map, string pack, CampaignTreeGenerator.HeightSource height)
    {
        var project = TerryProject.Load(Terry(map));
        var (treeMap, palette) = TerrainComposite.Indexed(project, "CampaignTree");
        var grid = HexGrid.ForTreeMap(treeMap.Width, treeMap.Height, MapDataBounds.Read(MapData(map)).Width);
        var db = TreeDatabase.FromPacks(GameSetup.OpenWithLinked(TestKits.Wh3GameData, [ModPack(pack)],
            n => n.StartsWith("db", StringComparison.OrdinalIgnoreCase)));
        return CampaignTreeGenerator.Generate(CampaignTreeGenerator.ReadTreeMap(treeMap, palette!, grid, TreesStep.EmptyIndex), grid, db, height);
    }
}
