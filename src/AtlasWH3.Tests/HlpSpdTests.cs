using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Tests;

/// <summary>hlp_data.esf / spd_data.esf (campaign AI pathfinding data): format round trip and native generation.</summary>
public class HlpSpdTests
{
    private const string KitMaps = @"C:\Program Files (x86)\Steam\steamapps\common\Total War THREE KINGDOMS\assembly_kit\working_data\campaign_maps";
    private const string Extracted = @"Z:\Claude\TerryClone\output\hlp_spd";

    /// <summary>Reference map folders that hold hlp_data.esf + spd_data.esf (kit copy of dlc07, plus extracted pack copies).</summary>
    public static IEnumerable<string> MapDirs()
    {
        var kit = Path.Combine(KitMaps, "3k_dlc07_main_map");
        if (File.Exists(Path.Combine(kit, "hlp_data.esf"))) yield return kit;
        if (!Directory.Exists(Extracted)) yield break;
        foreach (var d in Directory.GetDirectories(Extracted, "*", SearchOption.AllDirectories))
            if (File.Exists(Path.Combine(d, "hlp_data.esf")) && d.Contains("campaign_maps")) yield return d;
    }

    [Fact]
    public void Hlp_RoundTripsByteIdentical()
    {
        foreach (var dir in MapDirs())
        {
            var original = File.ReadAllBytes(Path.Combine(dir, "hlp_data.esf"));
            Assert.Equal(original, HlpData.Read(original).ToBytes());
        }
    }

    /// <summary>Vanilla maps whose pathfinding.ppd and map_data.esf generated the shipped spd_data.esf.</summary>
    public static IEnumerable<string> VanillaMapDirs() =>
        MapDirs().Where(d => !d.Contains("190e") && File.Exists(Path.Combine(d, "pathfinding.ppd")) && File.Exists(Path.Combine(d, "map_data.esf")));

    [Fact]
    public void Spd_NativeBuild_MatchesVanillaByteForByte()
    {
        var maps = 0;
        foreach (var dir in VanillaMapDirs())
        {
            maps++;
            var reference = File.ReadAllBytes(Path.Combine(dir, "spd_data.esf"));
            var grid = new AtlasWH3.Core.Campaign.AiPathfinding.CampaignPathGrid(
                AtlasWH3.Formats.Maps.PathfindingPpd.Read(Path.Combine(dir, "pathfinding.ppd")),
                AtlasWH3.Core.Campaign.AiPathfinding.MapDataRegions.Read(Path.Combine(dir, "map_data.esf")));
            var built = AtlasWH3.Core.Campaign.AiPathfinding.SpdBuilder.Build(grid, SpdData.Read(reference).Timestamp).ToBytes();
            Assert.True(reference.AsSpan().SequenceEqual(built), $"{dir}: spd_data.esf differs");
        }
        if (Directory.Exists(Extracted)) Assert.True(maps >= 5, $"only {maps} vanilla maps found");
    }

    /// <summary>The native hlp_data.esf is not byte-identical yet; guard the field-level parity reached on the vanilla
    /// maps (2026-10-05: dlc07 330/334 areas identical, 2394/2398 transitions with the same hexes, target and cost).</summary>
    [Fact]
    public void Hlp_NativeBuild_FieldParityOnVanilla()
    {
        var maps = 0;
        foreach (var dir in VanillaMapDirs().Where(d => d.Contains("dlc06") || d.Contains("dlc07")))
        {
            maps++;
            var reference = HlpData.Read(Path.Combine(dir, "hlp_data.esf"));
            var built = AtlasWH3.Core.Campaign.AiPathfinding.HlpBuilder.Build(
                AtlasWH3.Formats.Maps.PathfindingPpd.Read(Path.Combine(dir, "pathfinding.ppd")),
                AtlasWH3.Core.Campaign.AiPathfinding.MapDataRegions.Read(Path.Combine(dir, "map_data.esf")),
                new AtlasWH3.Core.Campaign.AiPathfinding.CampaignPathGrid.Settings(), reference.Timestamp);
            Assert.Equal(reference.Nodes.Count, built.Nodes.Count);
            var refAreas = reference.Nodes.SelectMany(n => n.Areas).ToDictionary(a => a.AreaId);
            int areas = 0, same = 0, transitions = 0, matched = 0;
            foreach (var a in built.Nodes.SelectMany(n => n.Areas))
            {
                var r = refAreas[a.AreaId];
                areas++;
                Assert.Equal((r.CentreX, r.CentreY, r.A), (a.CentreX, a.CentreY, a.A));
                if (a.Transitions.SequenceEqual(r.Transitions) && a.Costs.SequenceEqual(r.Costs) && a.B == r.B) same++;
                transitions += r.Transitions.Count;
                matched += r.Transitions.Count(t => a.Transitions.Any(m =>
                    (m.X, m.Y, m.OtherX, m.OtherY, m.TargetArea, m.Cost) == (t.X, t.Y, t.OtherX, t.OtherY, t.TargetArea, t.Cost)));
            }
            Assert.True(same >= areas * 0.98, $"{dir}: {same}/{areas} identical areas");
            Assert.True(matched >= transitions * 0.995, $"{dir}: {matched}/{transitions} transitions");
        }
        if (Directory.Exists(Extracted)) Assert.True(maps >= 2, $"only {maps} maps found");
    }

    [Fact]
    public void Spd_RoundTripsByteIdentical()
    {
        foreach (var dir in MapDirs())
        {
            var path = Path.Combine(dir, "spd_data.esf");
            if (!File.Exists(path)) continue;
            var original = File.ReadAllBytes(path);
            Assert.Equal(original, SpdData.Read(original).ToBytes());
        }
    }
}
