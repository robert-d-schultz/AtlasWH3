using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Rivers;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Tests;

/// <summary>BOB's own river geometry (BobRiver) against BOB's main190 "Terry file" output. The data tests need the 190E
/// assembly kit and the saved BOB run (output/bob_runs/frida_rivers2_main190_bob_terrain, 2026-10-05) and skip
/// without them.</summary>
public class BobRiverTests
{
    private static readonly ProjectPaths Main190 = new()
    {
        AssemblyKitRoot = TestKits.Expanded,
        MapName = "3k_190e_expanded_map",
    };
    private static string BobRun => Path.Combine(Main190.OutputRoot, "bob_runs", "frida_rivers2_main190_bob_terrain");

    /// <summary>Bytes BOB leaves uninitialised: LOD padding and two material bytes; they differ between BOB runs.</summary>
    private static readonly int[] Uninitialised = [0xA5, 0xA6, 0xA7, 0x31A, 0x31B];

    [Fact]
    public void HalfBits_MatchesIeeeOnRepresentableValues_AndBobsRounding()
    {
        foreach (var v in new[] { 0f, 1f, -2.5f, 463.5f, 1.4150390625f, 0.0001220703125f, 65504f })
            Assert.Equal(BitConverter.HalfToUInt16Bits((Half)v), BobRiver.HalfBits(v));
        Assert.Equal(0x7c00, BobRiver.HalfBits(1e6f));                          // overflow → infinity
        // BOB adds ((v - 1) & v) & 0x1fff (the dropped bits without their lowest set bit) before dropping 13 bits, so
        // an exact tie truncates: 1 + 3·2^-11 gives 0x3c01 where IEEE ties-to-even gives 0x3c02
        var tie = BitConverter.UInt32BitsToSingle(0x3f803000);
        Assert.Equal(0x3c02, BitConverter.HalfToUInt16Bits((Half)tie));
        Assert.Equal(0x3c01, BobRiver.HalfBits(tie));
        Assert.Equal(0x3c02, BobRiver.HalfBits(BitConverter.UInt32BitsToSingle(0x3f803800)));   // above the tie: up
    }

    [Fact]
    public void Spline_DegenerateEndsUseMidpointsAndStraightLength()
    {
        var s = new BobRiver.Spline();
        s.Add([0, 0, 0], [0, 0, 0], [3, 0, 4], [6, 0, 8]);
        Assert.Equal([1.5f, 0f, 2f], s.Segments[0][1]);                          // p1 = (p2 + p0) / 2
        Assert.Equal(10f, s.Lengths[0]);
        var ts = s.Optimise();
        Assert.Equal(0f, ts[0]);
        Assert.Contains(1f, ts);
    }

    /// <summary>River entity options none of the shipped maps use, against BOB's "Terry file" on a scratch copy of the
    /// vanilla river layer (output/bob_runs/river_variants_vanilla_bob, 2026-10-05): river_7 turned 30° (yaw), river_6
    /// terrain_relative="true" (no effect in BOB), river_5 reverse_direction="true".</summary>
    [Fact]
    public void Vanilla_RotatedRelativeAndReversedRivers_MatchBob()
    {
        var vanilla = new ProjectPaths { MapName = "3k_dlc07_main_map", AssemblyKitRoot = TestKits.Vanilla };
        var run = Path.Combine(vanilla.OutputRoot, "bob_runs", "river_variants_vanilla_bob");
        var layer = Path.Combine(run, "scratch_river_layer.layer");
        if (!File.Exists(layer) || BobRiver.MapBounds(vanilla) is not { } bounds) return;
        var expected = new Dictionary<string, int> { ["river_7"] = 0, ["river_6"] = 1, ["river_5"] = 2 };
        var rivers = RiverBuilder.ReadLayer(layer).Where(r => expected.ContainsKey(r.Name)).ToList();
        Assert.Equal(3, rivers.Count);
        Assert.Contains(rivers, r => r.YawDegrees == 30);
        Assert.Contains(rivers, r => r.TerrainRelative);
        Assert.Contains(rivers, r => r.Reverse);
        foreach (var river in rivers)
        {
            var raw = BobRiver.BuildRaw(BobRiver.BuildSpline(river), BobRiver.RiverPointsInOrder(river).Select(p => (float)p.Width).ToList(), bounds);
            var ours = BobRiver.ToModel(raw).ToBytes();
            var bob = File.ReadAllBytes(Path.Combine(run, "models", $"river_{expected[river.Name]}.wsmodel.rigid_model_v2"));
            Assert.True(ours.Length == bob.Length, river.Name);
            for (var i = 0; i < bob.Length; i++)
                if (ours[i] != bob[i] && !Uninitialised.Contains(i)) Assert.Fail($"{river.Name} differs at 0x{i:X}");
        }
    }

    [Fact]
    public void Main190_RiverModels_MatchBobApartFromUninitialisedBytes()
    {
        if (!Directory.Exists(Path.Combine(BobRun, "models")) || !Directory.Exists(Main190.AkTerrainDir)) return;
        var outDir = Path.Combine(Path.GetTempPath(), "atlaswh3_bobriver_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ctx = new CampaignBuildContext(Main190, outDir);
            var result = new RiversStep().Run(ctx);
            Assert.Contains(result.Notes, n => n.StartsWith("river geometry: BOB's", StringComparison.Ordinal));
            var reference = Directory.GetFiles(Path.Combine(BobRun, "models"));
            Assert.Equal(48, reference.Length);
            foreach (var file in reference)
            {
                var bob = File.ReadAllBytes(file);
                var ours = File.ReadAllBytes(ctx.OutFile("models", Path.GetFileName(file)));
                if (file.EndsWith(".rigid_model_v2", StringComparison.Ordinal))
                    foreach (var i in Uninitialised) bob[i] = ours[i];
                Assert.True(bob.AsSpan().SequenceEqual(ours), Path.GetFileName(file));
            }
        }
        finally { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); }
    }
}
