using AtlasWH3.Core.Campaign.AiPathfinding;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

/// <summary>
/// WH3's hlp_data.esf / spd_data.esf (campaign AI pathfinding data): format round trips and the native build, against
/// CA's files in the vanilla packs (campaign_maps/&lt;map&gt;/: pathfinding.ppd and map_data.esf in, hlp and spd out).
/// </summary>
public class HlpSpdTests
{
    private static readonly Lazy<PackSet> Vanilla = new(() => PackSet.OpenVanilla(TestKits.Wh3GameData));

    private static byte[] Read(string map, string file) =>
        Vanilla.Value.TryRead($"campaign_maps/{map}/{file}") ?? throw new FileNotFoundException($"campaign_maps/{map}/{file} not in the vanilla packs");

    private static (CampaignPathGrid Grid, PathfindingPpd Ppd, MapDataRegions Regions) Inputs(string map)
    {
        var ppd = PathfindingPpd.Read(Read(map, "pathfinding.ppd"));
        var regions = MapDataRegions.Read(EsfTree.Read(Read(map, "map_data.esf")));
        // vanilla's defaults: road 80, beaches 2100 (the old maps' playable-areas rows name *_old campaigns)
        return (new CampaignPathGrid(ppd, regions), ppd, regions);
    }

    [Theory]
    [InlineData("wh3_main_prologue_map", "hlp_data.esf")]
    [InlineData("wh3_main_prologue_map", "spd_data.esf")]
    [InlineData("wh3_main_chaos_map_1", "hlp_data.esf")]
    [InlineData("wh3_main_combi_map_7", "hlp_data.esf")] // CBAB
    public void RoundTripsByteIdentical(string map, string file)
    {
        var original = Read(map, file);
        var again = file.StartsWith("hlp") ? HlpData.Read(original).ToBytes() : SpdData.Read(original).ToBytes();
        Assert.True(original.AsSpan().SequenceEqual(again), $"{map}/{file} differs after a round trip");
    }

    /// <summary>The CA writer's padded sizes (5-byte uleb128 across a 1 MiB block, with the group count) through the
    /// generic tree writer: combi map 1's spd (98 MB) holds both kinds.</summary>
    [Fact]
    public void CaabWriter_RewritesTreeByteIdentical()
    {
        var original = Read("wh3_main_combi_map_1", "spd_data.esf");
        var tree = EsfTree.Read(original);
        var w = new CaabWriter(tree.Root.Name, (byte)tree.Root.Version, original.Length);
        foreach (var c in tree.Root.Children) w.Node(c);
        var again = w.ToFile(BitConverter.ToUInt32(original, 8), BitConverter.ToUInt32(original, 0));
        Assert.True(original.AsSpan().SequenceEqual(again));
    }

    [Fact]
    public void Prologue_NativeBuild_ByteIdentical()
    {
        var (grid, ppd, regions) = Inputs("wh3_main_prologue_map");
        var refSpd = Read("wh3_main_prologue_map", "spd_data.esf");
        var spd = SpdBuilder.Build(grid, regions, SpdData.Read(refSpd).Timestamp);
        spd.Magic = CaabWriter.MagicCaab;
        Assert.True(refSpd.AsSpan().SequenceEqual(spd.ToBytes()), "spd_data.esf differs");
        var refHlp = Read("wh3_main_prologue_map", "hlp_data.esf");
        var hlp = HlpBuilder.Build(ppd, regions, new CampaignPathGrid.Settings(), HlpData.Read(refHlp).Timestamp,
                                   options: new HlpBuilder.Options { LegacyCentrePath = true });
        hlp.Magic = CaabWriter.MagicCaab;
        Assert.True(refHlp.AsSpan().SequenceEqual(hlp.ToBytes()), "hlp_data.esf differs");
    }

    /// <summary>Guards the spd parity reached on combi map 1 (2026-10-10): every landmark, 99.96 % of the set costs and
    /// 99.99 % of the area costs.</summary>
    [Fact]
    public void Combi_NativeSpd_Parity()
    {
        var (grid, _, regions) = Inputs("wh3_main_combi_map_1");
        var reference = SpdData.Read(Read("wh3_main_combi_map_1", "spd_data.esf"));
        var spd = SpdBuilder.Build(grid, regions, reference.Timestamp);
        Assert.Equal((reference.X0, reference.Y0, reference.X1, reference.Y1), (spd.X0, spd.Y0, spd.X1, spd.Y1));
        Assert.Equal(reference.SetLandmarks, spd.SetLandmarks);
        Assert.Equal(reference.AreaLandmarks.Count, spd.AreaLandmarks.Count);
        for (var i = 0; i < spd.AreaLandmarks.Count; i++)
        {
            Assert.Equal(reference.AreaLandmarks[i].Area, spd.AreaLandmarks[i].Area);
            Assert.Equal(reference.AreaLandmarks[i].Landmarks, spd.AreaLandmarks[i].Landmarks);
        }
        Assert.Equal(reference.Sets, spd.Sets);
        long set = 0, area = 0, n = reference.Sets.LongLength;
        for (long c = 0; c < n; c++)
            for (var j = 0; j < SpdData.Stride; j++)
                if (spd.Values[c * SpdData.Stride + j] == reference.Values[c * SpdData.Stride + j])
                    if (j < 8) set++; else area++;
        Assert.True(set >= n * 8 * 0.9995, $"set costs {set}/{n * 8}");
        Assert.True(area >= n * 8 * 0.9998, $"area costs {area}/{n * 8}");
    }

    private static int IdenticalAreas(HlpData hlp, HlpData reference)
    {
        var refAreas = reference.Nodes.SelectMany(n => n.Areas).ToDictionary(a => a.Area);
        return hlp.Nodes.SelectMany(n => n.Areas).Count(a => refAreas.TryGetValue(a.Area, out var r) &&
            a.Transitions.SequenceEqual(r.Transitions) && a.Costs.SequenceEqual(r.Costs) && (a.CentreX, a.CentreY, a.A, a.B) == (r.CentreX, r.CentreY, r.A, r.B));
    }

    /// <summary>Guards the hlp parity reached on combi map 1 (2026-10-10, an older game build's file: legacy centre
    /// paths): 694 of 695 areas identical, and from CA's own transitions the region tables at 99.95 % of costs and 98 %
    /// of hop counts.</summary>
    [Fact]
    public void Combi_NativeHlp_Parity()
    {
        var (_, ppd, regions) = Inputs("wh3_main_combi_map_1");
        var reference = HlpData.Read(Read("wh3_main_combi_map_1", "hlp_data.esf"));
        var hlp = HlpBuilder.Build(ppd, regions, new CampaignPathGrid.Settings(), reference.Timestamp,
                                   options: new HlpBuilder.Options { LegacyCentrePath = true });
        var same = IdenticalAreas(hlp, reference);
        Assert.True(same >= 694, $"{same} identical areas");

        var fromRef = HlpData.Read(Read("wh3_main_combi_map_1", "hlp_data.esf"));
        HlpRegionTables.Fill(fromRef, wrapLikeGame: true);
        int costs = 0, hops = 0;
        for (var i = 0; i < fromRef.RegionCosts.Length; i++)
        {
            if (fromRef.RegionCosts[i] == reference.RegionCosts[i]) costs++;
            if (fromRef.RegionHops[i] == reference.RegionHops[i]) hops++;
        }
        Assert.True(costs >= fromRef.RegionCosts.Length - 200, $"region costs {costs}");
        Assert.True(hops >= fromRef.RegionHops.Length - 8000, $"region hops {hops}");
        Assert.Equal(reference.MaxRegionCost, fromRef.MaxRegionCost);
    }

    /// <summary>Guards the current exe's transitions on combi map 7 (2026-10-10): every area identical (transitions,
    /// matrix, centre, a, b).</summary>
    [Fact]
    public void Combi7_NativeHlp_Parity()
    {
        var (_, ppd, regions) = Inputs("wh3_main_combi_map_7");
        var reference = HlpData.Read(Read("wh3_main_combi_map_7", "hlp_data.esf"));
        var hlp = HlpBuilder.Build(ppd, regions, new CampaignPathGrid.Settings(), reference.Timestamp);
        var same = IdenticalAreas(hlp, reference);
        Assert.Equal(reference.Nodes.Sum(n => n.Areas.Count), same);
    }

    /// <summary>The region tables sum in wrapping u32: Old World's 38 unreachable matrix pairs (0xFFFFFFFF) are steps of
    /// -1 (from CA's own transitions: 99.7 % of the costs, 87 % without the wrap).</summary>
    [Fact]
    public void OldWorld_RegionTables_FromCaTransitions()
    {
        var pack = new PackSet([PackFile.Open(TestKits.Pack(TestKits.OldWorldPack))]);
        var bytes = pack.TryRead($"campaign_maps/{TestKits.OldWorld}/hlp_data.esf") ?? throw new FileNotFoundException("Old World hlp_data.esf");
        var reference = HlpData.Read(bytes);
        var copy = HlpData.Read(bytes);
        HlpRegionTables.Fill(copy, wrapLikeGame: true);
        int costs = 0, all = 0;
        for (var i = 0; i < copy.RegionCosts.Length; i++)
        {
            if (reference.RegionCosts[i] == HlpData.NoRegionCost && copy.RegionCosts[i] == HlpData.NoRegionCost) continue;
            all++;
            if (copy.RegionCosts[i] == reference.RegionCosts[i]) costs++;
        }
        Assert.True(costs >= all * 0.996, $"region costs {costs}/{all}");
        Assert.Equal(reference.MaxRegionCost, copy.MaxRegionCost);
    }

    /// <summary>By default an area's unreachable pair is no step: no region cost comes out below the wrapped one, and
    /// Old World's 0 -> 409 is not the game's impossible 40,541.</summary>
    [Fact]
    public void OldWorld_RegionTables_NoWrap()
    {
        var bytes = new PackSet([PackFile.Open(TestKits.Pack(TestKits.OldWorldPack))]).TryRead($"campaign_maps/{TestKits.OldWorld}/hlp_data.esf")!;
        var reference = HlpData.Read(bytes);
        var wrapped = HlpData.Read(bytes);
        HlpRegionTables.Fill(wrapped, wrapLikeGame: true);
        var copy = HlpData.Read(bytes);
        HlpRegionTables.Fill(copy);
        Assert.Equal(40541u, reference.RegionCosts[409]);
        Assert.Equal(40541u, wrapped.RegionCosts[409]);
        Assert.True(copy.RegionCosts[409] > 40541u);
        var higher = 0;
        for (var i = 0; i < copy.RegionCosts.Length; i++)
        {
            if (copy.RegionCosts[i] == HlpData.NoRegionCost) continue;
            Assert.True(copy.RegionCosts[i] >= wrapped.RegionCosts[i], $"pair {i / 1024},{i % 1024}");
            if (copy.RegionCosts[i] > wrapped.RegionCosts[i]) higher++;
        }
        Assert.True(higher > 0);
    }
}
