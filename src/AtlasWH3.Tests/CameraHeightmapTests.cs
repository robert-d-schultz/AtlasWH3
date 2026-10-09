using System.IO.Compression;
using AtlasWH3.Core.Campaign;
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

    [Theory]
    [InlineData(2880, 1941, 720, 486)]     // vanilla combi: CA's shipped 720 x 486
    [InlineData(3200, 1941, 800, 486)]     // IEE
    [InlineData(4096, 3549, 1024, 888)]    // Old World
    public void GridSize_IsTheTileMapTimesTheResolutionRoundedUp(int tilesW, int tilesH, int w, int h) =>
        Assert.Equal((w, h), CameraHeightmapStep.GridSize(tilesW, tilesH, CameraHeightmapStep.DefaultResolutionScale));

    [Fact]
    public void ReadSettings_DefaultsWithoutRulesAndLaterFilesWin()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var none = CameraHeightmapStep.ReadSettings(Path.Combine(dir, "missing.bob"));
            Assert.False(none.FromRules);
            Assert.Equal((0.25f, 8), (none.ResolutionScale, none.SamplesPerUnit));

            File.WriteAllText(Path.Combine(dir, "a.bob"), "[Terrain]\n\tcam_hmap_resolution_scale = 1\n\tcam_hmap_samples_per_wu = 4\n");
            File.WriteAllText(Path.Combine(dir, "b.bob"), "[Terrain]\n\tcam_hmap_samples_per_wu = 2\n\tcam_hmap_apply_blur = true\n");
            var s = CameraHeightmapStep.ReadSettings(Path.Combine(dir, "a.bob"), Path.Combine(dir, "b.bob"));
            Assert.True(s.FromRules);
            Assert.Equal((1f, 2, true), (s.ResolutionScale, s.SamplesPerUnit, s.ApplyBlur));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Sample_KeepsTheHighestSampleOfEachCell()
    {
        // a 4 x 2 grid over 8 x 4 world units: cell (u, j) is centred at (2u, 2j); one spike at (4.4, 2.1) lands in
        // cell (2, 1) only, and the ground is 0 below z 1 and 1 above
        float H(float x, float z) => MathF.Abs(x - 4.4f) < 0.3f && MathF.Abs(z - 2.1f) < 0.3f ? 5f : z < 1f ? 0f : 1f;
        var cells = CameraHeightmapStep.Sample(H, 4, 2, 8, 4, 4);
        Assert.Equal([0, 0, 0, 0, 1, 1, 5, 1], cells);
        var (raster, scale) = CameraHeightmapStep.Encode(cells, 4, 2, 5f);
        Assert.Equal("0.000076", scale);
        Assert.Equal((ushort)65535, raster.Data[2]);          // row 0 is the north (last) row of cells
        Assert.Equal((ushort)13107, raster.Data[0]);
        Assert.Equal((ushort)0, raster.Data[4]);              // the south row
    }
}
