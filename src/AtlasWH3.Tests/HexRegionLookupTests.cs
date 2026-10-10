using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

public class HexRegionLookupTests
{
    /// <summary>IEE's lookup (its map.hex over the map_data.esf bounds): every hex centre falls back in its own hex and
    /// region, and the quadtree root spans the map.</summary>
    [Fact]
    public void Lookup_HexCentresMapBackToTheirHexAndRegion()
    {
        var lookup = HexRegionLookup.ForMap(TestKits.Paths(TestKits.Iee), out var why);
        if (lookup is null) return;
        var hex = lookup.Hex;
        Assert.Equal((1600, 970), (hex.Width, hex.Height));
        for (var r = 0; r < hex.Height; r += 7)
            for (var c = 0; c < hex.Width; c += 5)
            {
                var (x, z) = lookup.HexCentre(c, r);
                Assert.Equal((c, r), lookup.HexAt(x, z));
                Assert.Equal(hex.RegionAt(c, r), lookup.RegionAt(x, z));
            }
        var (x0, z0, x1, z1) = lookup.QuadRoot;
        Assert.Equal((0f, 0f), (x0, z0));
        Assert.Equal(1068.1111f, x1, 3);
        Assert.Equal(748.5708f, z1, 3);
    }
}
