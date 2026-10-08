using System.IO.Compression;
using AtlasWH3.Core.Campaign.Camera;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

public class CameraHeightmapTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100_000)]
    public void ZlibDeflate_RoundTrips(int length)
    {
        var rng = new Random(7);
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 97 < 60 ? i / 300 : rng.Next(256));   // runs and noise
        foreach (var filtered in new[] { false, true })
        {
            var z = ZlibDeflate.Compress(data, 6, filtered);
            Assert.Equal(0x78, z[0]);
            using var s = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress);
            var back = new MemoryStream();
            s.CopyTo(back);
            Assert.Equal(data, back.ToArray());
        }
    }

    [Fact]
    public void PngLib_WritesHeightScaleAndDecodableRows()
    {
        var raster = new Raster<ushort>(5, 3);
        for (var i = 0; i < raster.Data.Length; i++) raster.Data[i] = (ushort)(i * 4000);
        var png = PngLib.Encode16(raster, new Dictionary<string, string> { ["height_scale"] = "0.000298" });
        var text = System.Text.Encoding.Latin1.GetString(png);
        Assert.Contains("tEXtheight_scale\00.000298", text);
        Assert.True(text.IndexOf("tEXt", StringComparison.Ordinal) < text.IndexOf("IDAT", StringComparison.Ordinal));
    }

    [Fact]
    public void Inverse_UndoesAnAffineMatrix()
    {
        float[] m = [-0.37254524f, 0, 0.47032955f, 89.26875f, 0, 0.7f, 0, 7.10262f, -0.47032955f, 0, -0.37254524f, 259.06424f, 0, 0, 0, 1];
        var inv = CameraHeightField.Inverse(m);
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                double v = 0;
                for (var k = 0; k < 4; k++) v += m[r * 4 + k] * inv[k * 4 + c];
                Assert.Equal(r == c ? 1.0 : 0.0, v, 4);
            }
    }

    [Fact]
    public void SampleMesh_FillsInvalidCornersAndReturnsInvalidWhenAllAre()
    {
        // 2x2 map: raw 0 = lo (−50, invalid), raw 65535 = hi (10)
        var map = new CameraHeightField.HeightMap([0, 65535, 0, 0], 2, 2, -50f, 10f);
        Assert.Equal(10f, CameraHeightField.SampleMesh(map, 0.6f, 0.1f));    // the one valid corner fills the others
        var allInvalid = new CameraHeightField.HeightMap([0, 0, 0, 0], 2, 2, -50f, 10f);
        Assert.Equal(CameraHeightField.Invalid, CameraHeightField.SampleMesh(allInvalid, 0.5f, 0.5f));
    }
}
