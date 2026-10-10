using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Dds;

namespace AtlasWH3.Tests;

/// <summary>The masks step (BOB's terrain-map actions) and the DirectXTex port it writes through.</summary>
public class MasksTests
{
    private const string OldWorld = "cr_oldworld_map_1", Iee = "cr_combi_expanded_map_1";

    private static ProjectPaths Paths(string map) =>
        new() { MapName = map, AssemblyKitRoot = TestKits.Kit(map), GameDataDir = TestKits.Wh3GameData };

    /// <summary>
    /// The whole step against BOB's output (2026-10-09). IEE: all five files byte-identical to the user's working_data,
    /// which a fresh BOB run reproduced byte for byte. Old World: snow_mask and corruption_mask (its working_data overlays
    /// were trimmed to 14 mips by campaign_tools' trim_mips.py, and its event area TIF is newer than its BOB run; a fresh
    /// BOB run in the scratch kit matched all five).
    /// </summary>
    [Theory]
    [InlineData(Iee, new[] { "colour_overlay.dds", "lf_sea_colour.dds", "snow_mask.dds", "corruption_mask.dds", "event_area_mask.dds" })]
    [InlineData(OldWorld, new[] { "snow_mask.dds", "corruption_mask.dds" })]
    public void Step_MatchesBob(string map, string[] files)
    {
        if (!files.All(f => File.Exists(CompiledFormatTests.Built(map, f)))) return;
        var target = Path.Combine(Path.GetTempPath(), "atlaswh3_masks_test_" + map);
        try
        {
            var ctx = new CampaignBuildContext(Paths(map), target);
            Assert.Empty(new MasksStep().CheckInputs(ctx));
            new MasksStep().Run(ctx);
            foreach (var file in files)
                Assert.True(File.ReadAllBytes(CompiledFormatTests.Built(map, file)).AsSpan().SequenceEqual(File.ReadAllBytes(ctx.OutFile(file))), file);
        }
        finally { if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    [Fact]
    public void SnowSize_IsOneBiggerRoundedToBlocks()
    {
        Assert.Equal((3204, 1944), MasksStep.SnowSize(3200, 1941));
        Assert.Equal((4100, 3552), MasksStep.SnowSize(4096, 3549));
        Assert.Equal((2220, 1448), MasksStep.SnowSize(2216, 1445));   // vanilla chaos map
    }

    [Fact]
    public void MipCount_IsTheFullChain()
    {
        Assert.Equal(14, DirectXTex.MipCount(12800, 7764));
        Assert.Equal(15, DirectXTex.MipCount(16384, 14196));
        Assert.Equal(12, DirectXTex.MipCount(3204, 1944));
        Assert.Equal(1, DirectXTex.MipCount(1, 1));
    }

    /// <summary>The linear filter's weights can sum to just under 1: a flat 95 area going from 1941 to 970 rows comes out
    /// 94 where the R8 store truncates (BOB's corruption mask has these), 95 where RGBA8 rounds.</summary>
    [Fact]
    public void Resize_TruncatesR8_RoundsRgba8()
    {
        var flat = Enumerable.Repeat((byte)95, 2 * 1941).ToArray();
        var r8 = DirectXTex.Resize(flat, 2, 1941, 1, 1, 970, round: false);
        Assert.Contains((byte)94, r8);
        Assert.All(r8, v => Assert.InRange(v, (byte)94, (byte)95));
        Assert.All(DirectXTex.Resize(flat, 2, 1941, 1, 1, 970, round: true), v => Assert.Equal(95, v));
        // an exact 2:1 reduction averages the 2 × 2 pixels
        Assert.Equal([(byte)((10 + 20 + 30 + 41) / 4)], DirectXTex.Resize([10, 20, 30, 41], 2, 2, 1, 1, 1, round: false));
        Assert.Equal([(byte)26], DirectXTex.Resize([10, 20, 30, 42], 2, 2, 1, 1, 1, round: true));   // 25.5 rounds up
    }

    /// <summary>Old World's ColorOverlay has no visible layer: BOB writes flat 127 grey, every block 7BEF 7BEF 00000000.</summary>
    [Fact]
    public void Bc1_FlatGrey_IsOneColour()
    {
        var rgba = new byte[5 * 3 * 4];
        for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = rgba[i + 1] = rgba[i + 2] = 127; rgba[i + 3] = 255; }
        var blocks = DirectXTex.CompressBc1(rgba, 5, 3);
        Assert.Equal(16, blocks.Length);
        Assert.All(Enumerable.Range(0, 2), b => Assert.Equal(0x000000007BEF7BEFul, BitConverter.ToUInt64(blocks, b * 8)));
    }

    [Fact]
    public void Bc1_TransparentBlock_IsColourKeyed()
    {
        Span<float> rgba = stackalloc float[64];
        Assert.Equal(0xFFFFFFFF_FFFF0000ul, DirectXTex.EncodeBc1(rgba, 0.5f));
    }

    /// <summary>A block holding 0 or 1 takes the 6-value mode (endpoint 0 ≤ endpoint 1, indices 6 and 7 are exact 0 and
    /// 1); a flat block decodes back to its value.</summary>
    [Fact]
    public void Bc4_Modes()
    {
        Span<float> t = stackalloc float[16];
        t.Fill(100 * (1f / 255f));
        var flat = DirectXTex.EncodeBc4U(t);
        Assert.Equal(100, (int)(flat & 0xFF));
        t[0] = 0f;
        t[1] = 1f;
        var six = DirectXTex.EncodeBc4U(t);
        Assert.Equal(0x0EFF64ul, six);       // endpoints 100, 255; texel 0 → index 6 (0), texel 1 → index 1 (255), the rest 0
    }

    /// <summary>A partial block repeats its pixels as DirectXTex does (columns 0, 0, 0, 1 for the missing ones), so a
    /// 2-wide image's block is built from columns 0, 1, 0, 1, not 0, 1, 1, 1.</summary>
    [Fact]
    public void Bc4_PartialBlock_RepeatsLikeDirectXTex()
    {
        byte[] r8 = [0, 200, 0, 200, 0, 200, 0, 200];   // 2 × 4
        var block = BitConverter.ToUInt64(DirectXTex.CompressBc4(r8, 2, 4));
        Span<float> t = stackalloc float[16];
        for (var j = 0; j < 4; j++) { t[j * 4] = t[j * 4 + 2] = 0f; t[j * 4 + 1] = t[j * 4 + 3] = 200 * (1f / 255f); }
        Assert.Equal(DirectXTex.EncodeBc4U(t), block);
    }

    [Fact]
    public void PatchGrid_FromTheTileMap()
    {
        Assert.Equal((128, 77), TileListWriter.PatchGrid(3200, 1941));
        Assert.Equal((128, 110), TileListWriter.PatchGrid(4096, 3549));
    }
}
