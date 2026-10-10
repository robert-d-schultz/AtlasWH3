using System.Text;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

public class MapHexTests
{
    /// <summary>A minimal map.hex of version <paramref name="minor"/>, 3 × 2 hexes.</summary>
    private static byte[] File(int minor)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Str(string s) { w.Write(s.Length); w.Write(Encoding.ASCII.GetBytes(s)); }
        void List(params string[] items) { w.Write(items.Length); foreach (var s in items) Str(s); }
        w.Write(0); w.Write(minor);
        if (minor >= 0x12) w.Write(0L);
        Str("warhammer3"); Str("test_map");
        List("reg_a", "reg_b"); List("sea_x"); List("grass"); List("deep"); List("clim_1", "clim_2"); List("att");
        if (minor is 0x13 or 0x14) List("aoi");
        if (minor is 0x12 or 0x14) { w.Write(1); w.Write(0); w.Write(0); }
        w.Write(2); w.Write(0x11223344); w.Write(0x55667788);   // land colour pool
        w.Write(0);                                             // sea colour pool
        w.Write(3); w.Write(2);
        for (var i = 0; i < 6; i++)
        {
            var rec = new byte[16];
            var region = i == 0 ? 0 : i == 5 ? 3 : 1;            // none, reg_a ×4, sea_x
            rec[0] = (byte)(region << 3 | (i == 5 ? 1 : 0));
            rec[1] = (byte)(region >> 5);
            rec[2] = (byte)((i == 1 ? 1 : 0) << 4 | (i == 2 ? 8 : 0));   // slot 0 on hex 1, impassable hex 2
            rec[3] = (byte)(i == 3 ? 0b1000_0101 : 0);                 // hex 3: sprawl, road edge 2, bridge
            rec[5] = (byte)(1 << 4);                                   // ground type 0
            w.Write(rec);
        }
        if (minor is 0x12 or 0x14) { w.Write(1); w.Write(1); w.Write((byte)0b0010_0001); }
        w.Write(0);                                             // checksum
        return ms.ToArray();
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(0x14)]
    public void Read_V19AndV20(int minor)
    {
        var hex = MapHexFile.Read(File(minor));
        Assert.Equal(minor, hex.Version);
        Assert.Equal(("test_map", 3, 2), (hex.Name, hex.Width, hex.Height));
        Assert.Equal(["clim_1", "clim_2"], hex.Climates);
        Assert.Equal(["aoi"], hex.AreasOfInterest);
        Assert.Null(hex.RegionAt(0, 0));
        Assert.Equal("reg_a", hex.RegionAt(1, 0));
        Assert.Equal("sea_x", hex.RegionAt(2, 1));
        Assert.Equal(1, hex.TerrainAt(2, 1));
        Assert.Equal(0, hex.SlotAt(1, 0));
        Assert.Equal(-1, hex.SlotAt(2, 0));
        Assert.True(hex.ImpassableAt(2, 0));
        Assert.True(hex.SprawlAt(0, 1) && hex.BridgeAt(0, 1));
        Assert.Equal(2, hex.RoadAt(0, 1));
        Assert.Equal(0, hex.GroundTypeAt(0, 0));
        if (minor == 0x14)
        {
            Assert.True(hex.HexBitAt(0, 0));
            Assert.True(hex.HexBitAt(2, 1));
            Assert.False(hex.HexBitAt(1, 0));
        }
        else Assert.Null(hex.HexBits);
    }

    [Fact]
    public void Wh3_UserMapsRead()
    {
        foreach (var (map, w, h) in new[] { ("cr_combi_expanded_map_1", 1600, 970), ("cr_oldworld_map_1", 2048, 1774) })
        {
            var path = Path.Combine(TestKits.Kit(map), "raw_data", "EmpireDesignData", "campaign_maps", map, "map.hex");
            if (!System.IO.File.Exists(path)) continue;
            var hex = MapHexFile.Read(path);
            Assert.Equal((0x14, w, h), (hex.Version, hex.Width, hex.Height));
            Assert.Equal(40, hex.Climates.Count);
            Assert.Equal(w * h / 8, hex.HexBits!.Length);
            // every hex's region index is in range
            for (var r = 0; r < h; r += 13)
                for (var c = 0; c < w; c += 7)
                    Assert.True(hex.RegionIndexAt(c, r) < hex.LandRegions.Count + hex.SeaRegions.Count);
        }
    }
}
