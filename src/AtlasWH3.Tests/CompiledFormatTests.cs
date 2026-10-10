using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Tests;

/// <summary>WH3's compiled campaign files: read → write gives the same bytes, on BOB's output for the user's maps.</summary>
public class CompiledFormatTests
{
    public static readonly string[] Maps = ["cr_combi_expanded_map_1", "cr_oldworld_map_1"];

    public static string Built(string map, params string[] path) =>
        TestKits.Built(map, path);

    [Fact]
    public void TileList_V2_RoundTrips()
    {
        foreach (var map in Maps)
            foreach (var file in new[] { Built(map, "tile_list.bin"), Built(map, "global_map", "tile_list.bin") })
            {
                if (!File.Exists(file)) continue;
                var bytes = File.ReadAllBytes(file);
                var list = TileList.Read(bytes);
                Assert.Equal(2, list.Version);
                Assert.Equal(["default"], list.Climates);
                Assert.Equal(list.Ints[1] + 2, list.Ints[9]);
                Assert.Equal(bytes, list.ToBytes());
            }
    }

    [Fact]
    public void TreeList_V4_RoundTrips()
    {
        foreach (var map in Maps)
        {
            var file = Path.Combine(TestKits.Kit(map), "working_data", "campaign_maps", map, "display", "trees", "trees.campaign_tree_list");
            if (!File.Exists(file)) continue;
            var bytes = File.ReadAllBytes(file);
            var list = CampaignTreeList.Read(bytes);
            Assert.All(list.Types.SelectMany(t => t.Instances), t => Assert.Equal((1, 0xFF), (t.Flag, t.Tag)));
            Assert.Equal(bytes, list.ToBytes());
        }
    }
}
