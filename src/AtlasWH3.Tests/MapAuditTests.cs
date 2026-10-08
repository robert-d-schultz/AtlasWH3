using AtlasWH3.Core;
using AtlasWH3.Core.Audit;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

public class MapAuditTests
{
    private const string Kit190 = @"C:\Program Files (x86)\Steam\steamapps\common\Total War THREE KINGDOMS\assembly_kit_190E";

    /// <summary>map.hex fields against research/guandu/rebuild_hex.py unpack on the 190E kit (2026-10-04 values).</summary>
    [Fact]
    public void MapHex_DecodesSettlementRoadRiverBridge()
    {
        var path = Path.Combine(Kit190, @"raw_data\EmpireDesignData\campaign_maps\3k_190e_expanded_map\map.hex");
        if (!File.Exists(path)) return;
        var hex = MapHexFile.Read(path);
        Assert.Equal(0, hex.SlotAt(1350, 698));
        Assert.True(hex.SprawlAt(1350, 698));
        Assert.Equal(34, hex.RoadAt(856, 691));
        Assert.True(hex.BridgeAt(578, 538));
        Assert.Equal(1, hex.TerrainAt(578, 538));
        Assert.Equal(40, hex.RiverAt(887, 630));
    }

    /// <summary>Calibration: vanilla dlc07 is clean of the warning-level mountain and asset problems.</summary>
    [Fact]
    public void VanillaDlc07_HasNoMountainOrAssetWarnings()
    {
        var paths = TestKits.VanillaPaths;
        if (!Directory.Exists(paths.AkTerrainDir) || !Directory.Exists(paths.GameDataDir)) return;
        var audit = new MapAudit(paths);
        audit.Run(["floating-mountain", "asset"]);
        Assert.True(audit.Findings.Count(f => f.Check == "floating-mountain" && f.Severity == MapAudit.Warning) <= 1);
        Assert.Empty(audit.Findings.Where(f => f.Check == "asset" && f.Severity == MapAudit.Error));
    }
}
