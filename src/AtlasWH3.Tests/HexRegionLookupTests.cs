using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

public class HexRegionLookupTests
{
    /// <summary>IEE's lookup (the map_data.esf its mod pack ships): every hex centre falls back in its own hex and
    /// region, and the quadtree root spans the map.</summary>
    [Fact]
    public void Lookup_HexCentresMapBackToTheirHexAndRegion()
    {
        var lookup = HexRegionLookup.ForMap(TestKits.Paths(TestKits.Iee, TestKits.IeePack), out var why);
        if (lookup is null) return;
        Assert.Equal((1600, 970), (lookup.Width, lookup.Height));
        for (var r = 0; r < lookup.Height; r += 7)
            for (var c = 0; c < lookup.Width; c += 5)
            {
                var (x, z) = lookup.HexCentre(c, r);
                Assert.Equal((c, r), lookup.HexAt(x, z));
                Assert.Equal(lookup.RegionOfHex(c, r), lookup.RegionAt(x, z));
            }
        var (x0, z0, x1, z1) = lookup.QuadRoot;
        Assert.Equal((0f, 0f), (x0, z0));
        Assert.Equal(1068.1111f, x1, 3);
        Assert.Equal(748.5708f, z1, 3);
    }

    /// <summary>map_data.esf's per-hex regions (MASKED_REGIONS_DATA, what the build reads) against map.hex's (what BOB
    /// read). They agree when both come from one CAIME export: IEE on every hex. Old World's map.hex was saved 2026-10-03
    /// 21:12, after its map_data.esf (18:41, the pack's): 12 hexes were repainted since, (991, 1029) and 11 in column 2047.</summary>
    [Theory]
    [InlineData(TestKits.Iee, TestKits.IeePack, 0)]
    [InlineData(TestKits.OldWorld, TestKits.OldWorldPack, 12)]
    public void MapDataRegions_AreMapHexs(string map, string pack, int edited)
    {
        var paths = TestKits.Paths(map, pack);
        var esf = HexRegionLookup.ForMap(paths, out _);
        var hex = HexRegionLookup.ForMapHex(paths, out _);
        if (esf is null || hex is null) return;
        Assert.Equal((hex.Width, hex.Height), (esf.Width, esf.Height));
        var diffs = new List<string>();
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++)
                if (hex.RegionOfHex(c, r) != esf.RegionOfHex(c, r)) diffs.Add($"({c},{r}) {hex.RegionOfHex(c, r)} / {esf.RegionOfHex(c, r)}");
        Assert.True(diffs.Count <= edited, $"{map}: {diffs.Count} hexes differ: {string.Join("; ", diffs.Take(12))}");
    }
}
