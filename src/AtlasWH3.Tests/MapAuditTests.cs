using AtlasWH3.Core.Audit;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

public class MapAuditTests
{
    /// <summary>IEE's map.hex fields (CAIME's v20): settlements, sprawl, roads, bridges and rivers, counted and spot-checked
    /// on the frozen fixture (2026-10-10).</summary>
    [Fact]
    public void MapHex_DecodesSettlementRoadRiverBridge()
    {
        var path = Path.Combine(TestKits.Paths(TestKits.Iee).AkDesignCampaignMapDir, "map.hex");
        if (!File.Exists(path)) return;
        var hex = MapHexFile.Read(path);
        int slots = 0, sprawl = 0, roads = 0, bridges = 0, rivers = 0;
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++)
            {
                if (hex.SlotAt(c, r) >= 0) slots++;
                if (hex.SprawlAt(c, r)) sprawl++;
                if (hex.RoadAt(c, r) > 0) roads++;
                if (hex.BridgeAt(c, r)) bridges++;
                if (hex.RiverAt(c, r) > 0) rivers++;
            }
        Assert.Equal((14_535, 14_535, 42_787, 1_484, 8_522), (slots, sprawl, roads, bridges, rivers));
        Assert.Equal(0, hex.SlotAt(663, 10));
        Assert.Equal("wh3_main_combi_region_the_skull_carvers_abode", hex.RegionAt(663, 10));
        Assert.Equal(1, hex.RoadAt(1328, 1));
        Assert.True(hex.BridgeAt(1451, 24));
        Assert.Equal(1, hex.TerrainAt(1451, 24));
        Assert.Equal(1, hex.RiverAt(643, 3));
    }

    /// <summary>IEE has no floating mountains, and one asset problem: a model path with a doubled slash
    /// (mudbanks//gen_mudbank_01), which the asset check does not resolve.</summary>
    [Fact]
    public void Iee_MountainAndAssetChecks()
    {
        var paths = TestKits.Paths(TestKits.Iee, TestKits.IeePack);
        if (!Directory.Exists(paths.AkTerrainDir) || !Directory.Exists(paths.GameDataDir)) return;
        var audit = new MapAudit(paths);
        audit.Run(["floating-mountain", "asset"]);
        Assert.DoesNotContain(audit.Findings, f => f.Check == "floating-mountain");
        var asset = Assert.Single(audit.Findings, f => f.Check == "asset");
        Assert.Equal(MapAudit.Error, asset.Severity);
        Assert.Contains("mudbanks//gen_mudbank_01", asset.Model);
    }
}
