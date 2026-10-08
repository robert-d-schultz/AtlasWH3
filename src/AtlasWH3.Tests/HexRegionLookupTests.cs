using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

public class HexRegionLookupTests
{
    private static readonly ProjectPaths Kit190E = new ProjectPaths
    {
        MapName = "3k_190e_expanded_map",
        AssemblyKitRoot = TestKits.Expanded,
    };

    [Fact]
    public void MapHexFile_ReadsGridAndRegions()
    {
        var path = Path.Combine(Kit190E.AkDesignCampaignMapDir, "map.hex");
        if (!File.Exists(path)) return;
        var hex = MapHexFile.Read(path);
        Assert.Equal((1478, 1133), (hex.Width, hex.Height));
        Assert.Equal(340, hex.LandRegions.Count);
        Assert.Equal(96, hex.SeaRegions.Count);
    }

    [Fact]
    public void Lookup_PutsARiverEntityInItsSeaRegion()
    {
        var lookup = HexRegionLookup.ForMap(Kit190E, out var why);
        if (lookup is null) return;
        // river_0's ECRiver entity in the main190 rivers layer; BOB gives the river model this region
        Assert.StartsWith("3k_main_riv_sea_", lookup.RegionAt(289.0825, 635.8125));
    }
}
