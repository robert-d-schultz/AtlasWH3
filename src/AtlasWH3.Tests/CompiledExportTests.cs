using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

public class CompiledExportTests
{
    [Fact]
    public void CompressedMap_RoundTrips_Synthetic_AllTileModes()
    {
        // 40x20: partial edge tiles; tiles that are constant, palette, base+delta and raw
        var r = new Raster<ushort>(40, 20);
        var rng = new Random(7);
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 40; x++)
            r[x, y] = x switch
            {
                < 16 => 1234,                                    // constant
                < 32 when y < 16 => (ushort)(100 + (x + y) % 5), // palette
                < 32 => (ushort)(5000 + rng.Next(3000)),         // base+delta (12 bits)
                _ => (ushort)rng.Next(65536),                    // raw
            };
        var bytes = CompressedMap.Encode(r, [0, 0, 0, 0, 1, 0]);
        var back = CompressedMap.Decode(bytes);
        Assert.Equal(r.Data, back.Raster.Data);
        Assert.Equal(bytes, CompressedMap.Encode(back.Raster, back.Header));
    }
}
