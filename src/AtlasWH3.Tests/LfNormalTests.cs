using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

/// <summary>lf_normal: Terry's normal export and BOB's NVTT DXT5nm layout.</summary>
public class LfNormalTests
{
    [Fact]
    public void Compute_FlatAndRamp()
    {
        var flat = LfNormalMap.Compute(new Raster<float>(8, 8), LfNormalMap.Spacing);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(new byte[] { 127, 127, 255, 255 }, flat[(i * 4)..(i * 4 + 4)]));

        // h = 0.5 · x: inside, gx = 4 · (h(x − 1) − h(x + 1)) = −4, gy = 0, n = (−4, 0, 1/4) normalised
        var ramp = new Raster<float>(8, 8);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++) ramp[x, y] = 0.5f * x;
        var n = LfNormalMap.Compute(ramp, LfNormalMap.Spacing);
        var o = (3 * 8 + 3) * 4;
        var inv = 1f / MathF.Sqrt(16f + 1f / 16f);
        Assert.Equal((byte)(int)((inv * -4f + 1f) * 127.5f), n[o]);
        Assert.Equal(127, n[o + 1]);
        Assert.Equal((byte)(int)((inv * 0.25f + 1f) * 127.5f), n[o + 2]);
    }

    [Fact]
    public void ToDds_HeaderMipsAndSwizzle()
    {
        const int w = 16, h = 8;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { rgba[i * 4] = 200; rgba[i * 4 + 1] = 60; rgba[i * 4 + 2] = 240; rgba[i * 4 + 3] = 255; }
        var dds = LfNormalMap.ToDds(rgba, w, h);
        Assert.Equal(0x20534444u, BitConverter.ToUInt32(dds, 0));
        Assert.Equal(5u, BitConverter.ToUInt32(dds, 28));                  // 16 → 1: five levels
        Assert.Equal(0x80000004u, BitConverter.ToUInt32(dds, 80));         // DDPF_FOURCC | DDPF_NORMAL
        Assert.Equal("DXT5"u8.ToArray(), dds[84..88]);
        Assert.Equal("NVTT"u8.ToArray(), dds[68..72]);
        var blocks = 4 * 2 + 2 * 1 + 1 + 1 + 1;
        Assert.Equal(128 + blocks * 16, dds.Length);
        // the first block: a constant alpha of 200 (x) and a constant colour of (255, 60, 0) (y in green)
        Assert.Equal(200, dds[128]);
        var c0 = BitConverter.ToUInt16(dds, 128 + 8);
        Assert.Equal((31, 0), (c0 >> 11, c0 & 31));
        Assert.InRange(((c0 >> 5) & 63) * 255 / 63, 56, 64);
    }
}
