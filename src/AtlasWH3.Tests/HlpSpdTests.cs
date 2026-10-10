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
        var hlp = HlpBuilder.Build(ppd, regions, new CampaignPathGrid.Settings(), HlpData.Read(refHlp).Timestamp);
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
}
