using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Dds.Bc6hVendor;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public sealed class Bc6hTests
{
    private static readonly Bc6Mode[] Modes = Enum.GetValues<Bc6Mode>().Where(m => m != Bc6Mode.Unknown).ToArray();

    /// <summary>Every endpoint bit the vendored layouts store comes back from the matching extract, in its own place, and
    /// leaves the mode, partition and index bits alone (mode 18 once stored bit 4 of its red deltas where bit 5 goes).</summary>
    [Fact]
    public void Layouts_StoreAndExtractAgree()
    {
        var rng = new Random(1);
        foreach (var mode in Modes)
        {
            var bits = mode.EndpointBits();
            var (dr, dg, db) = mode.DeltaBits();
            var transformed = mode.HasTransformedEndpoints();
            var two = mode.HasSubsets();
            for (var trial = 0; trial < 200; trial++)
            {
                (int, int, int) Rand(int r, int g, int b) => (rng.Next(1 << r), rng.Next(1 << g), rng.Next(1 << b));
                var ep = new (int, int, int)[4];
                ep[0] = Rand(bits, bits, bits);
                for (var e = 1; e < (two ? 4 : 2); e++)
                    ep[e] = transformed ? Rand(dr, dg, db) : Rand(bits, bits, bits);
                var block = new Bc6Block { lowBits = (ulong)(uint)mode };
                block.StoreEp0(ep[0]);
                block.StoreEp1(ep[1]);
                if (two) { block.StoreEp2(ep[2]); block.StoreEp3(ep[3]); }
                Assert.Equal(mode, block.Type);
                Assert.Equal(ep[0], block.ExtractEp0());
                Assert.Equal(ep[1], block.ExtractEp1());
                if (two) { Assert.Equal(ep[2], block.ExtractEp2()); Assert.Equal(ep[3], block.ExtractEp3()); }
                // one region: indices from bit 65; two regions: partition from bit 77, then indices
                var firstFree = two ? 77 : 65;
                Assert.Equal(0ul, block.highBits >> (firstFree - 64));
            }
        }
    }

    private static float MaxError(ReadOnlySpan<float> rgb)
    {
        var bytes = new byte[Bc6h.BlockBytes];
        Bc6h.EncodeBlock(rgb, bytes);
        var decoded = new float[48];
        Bc6h.DecodeBlock(bytes, decoded);
        var max = 0f;
        for (var p = 0; p < 16; p++)
            for (var c = 0; c < 2; c++) max = Math.Max(max, Math.Abs(decoded[p * 3 + c] - rgb[p * 3 + c]));
        return max;
    }

    private static float[] Block(float[] red, float green)
    {
        var rgb = new float[48];
        for (var p = 0; p < 16; p++) { rgb[p * 3] = red[p]; rgb[p * 3 + 1] = green; }
        return rgb;
    }

    /// <summary>IEE blocks that once decoded to hundreds: a refit whose deltas did not fit was kept, and mode 18's red
    /// deltas lost their top bit.</summary>
    [Fact]
    public void Encode_KnownBadBlocks()
    {
        Assert.True(MaxError(Block([12.664f, 10.887f, 9.200f, 7.637f, 8.808f, 7.190f, 5.741f, 4.481f,
                                    5.071f, 3.847f, 2.843f, 2.044f, 2.303f, 1.602f, 1.078f, 0.700f], -1)) < 0.5f);
        Assert.True(MaxError(Block([9.367f, 7.165f, 4.555f, 2.350f, 7.922f, 5.585f, 3.102f, 1.443f,
                                    6.479f, 4.056f, 2.013f, 0.833f, 4.885f, 2.772f, 1.241f, 0.450f], -1)) < 0.5f);
    }

    /// <summary>A NaN texel (Old World's Height has one) does not drag its neighbours: before, it entered the fit as
    /// 65504 and its neighbours were off by 5.</summary>
    [Fact]
    public void Encode_NaNTexelIsIgnored()
    {
        var red = Enumerable.Range(0, 16).Select(p => 3f + 0.1f * p).ToArray();
        float ErrorOthers(float[] rgb, float[] decoded)
        {
            var bytes = new byte[Bc6h.BlockBytes];
            Bc6h.EncodeBlock(rgb, bytes);
            Bc6h.DecodeBlock(bytes, decoded);
            return Enumerable.Range(0, 16).Where(p => p != 5).Max(p => Math.Abs(decoded[p * 3] - red[p]));
        }
        var clean = ErrorOthers(Block(red, -1), new float[48]);
        var withNaN = Block(red, -1);
        withNaN[5 * 3] = float.NaN;
        var decoded = new float[48];
        Assert.True(ErrorOthers(withNaN, decoded) <= clean * 1.5f + 1e-3f, $"{ErrorOthers(withNaN, decoded)} against {clean} without the NaN");
        Assert.InRange(decoded[5 * 3], 3f, 4.5f);
    }

    /// <summary>Random land blocks (planes and steps, 0.01 .. 100) over a flat sea bed. Bounds measured on this encoder:
    /// red within 15% of its range when it keeps one sign, within 54% of its magnitude when it crosses zero (BC6H
    /// interpolates the half-float bits, so a gradient through 0 bends); before the two fixes some blocks were off by
    /// thousands of times their magnitude.</summary>
    [Fact]
    public void Encode_RandomHeightBlocks()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 20000; trial++)
        {
            var scale = MathF.Pow(10, (float)rng.NextDouble() * 4 - 2);
            float a = (float)(rng.NextDouble() * 2 - 1) * scale, gx = (float)(rng.NextDouble() * 2 - 1) * scale / 4,
                gy = (float)(rng.NextDouble() * 2 - 1) * scale / 4, step = rng.Next(3) == 0 ? (float)(rng.NextDouble() * 2 - 1) * scale : 0;
            var edge = rng.Next(4);
            var rgb = new float[48];
            float lo = float.MaxValue, hi = float.MinValue;
            for (var p = 0; p < 16; p++)
            {
                int x = p % 4, y = p / 4;
                rgb[p * 3] = a + gx * x + gy * y + (x + y > edge + 1 ? step : 0);
                rgb[p * 3 + 1] = -Math.Abs(a) / 2;
                lo = Math.Min(lo, rgb[p * 3]); hi = Math.Max(hi, rgb[p * 3]);
            }
            var err = MaxError(rgb);
            var bound = lo < 0 && hi > 0 ? 0.6f * Math.Max(-lo, hi) : 0.2f * (hi - lo) + 1e-3f * Math.Max(-lo, hi);
            Assert.True(err <= bound, $"trial {trial}: max error {err}, red {lo} .. {hi}");
        }
    }

    /// <summary>full_logic_map.compressed_map and shroud_heights.dds are BOB's bytes, on the IEE fixture.</summary>
    [Fact]
    public void Heightmaps_LogicMapAndShroudMatchBob()
    {
        const string map = "cr_combi_expanded_map_1";
        var terry = Path.Combine(TestKits.Kit(map), "raw_data", "terrain", "campaigns", map, map + ".terry");
        var logic = CompiledFormatTests.Built(map, "full_logic_map.compressed_map");
        var shroud = CompiledFormatTests.Built(map, "shroud_heights.dds");
        if (!File.Exists(terry) || !File.Exists(logic) || !File.Exists(shroud)) return;
        var project = TerryProject.Load(terry);
        var dir = Directory.CreateTempSubdirectory("atlaswh3_heightmaps").FullName;
        try
        {
            HeightmapsStep.WriteLogicMap(TerrainComposite.Heights(project, "Height"), Path.Combine(dir, "logic"));
            Assert.True(File.ReadAllBytes(logic).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "logic"))));
            HeightmapsStep.WriteShroud(TerrainComposite.Heights(project, "HeightShroud"), Path.Combine(dir, "shroud"));
            Assert.True(File.ReadAllBytes(shroud).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "shroud"))));
        }
        finally { Directory.Delete(dir, true); }
    }
}
