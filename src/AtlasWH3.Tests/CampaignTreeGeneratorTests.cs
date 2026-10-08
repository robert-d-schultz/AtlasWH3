using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Tests;

/// <summary>BOB's campaign tree placement, checked against vanilla 3k_dlc07_main_map.</summary>
public class CampaignTreeGeneratorTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static readonly HexGrid VanillaGrid = HexGrid.ForTreeMap(1784, 1405, 595.1f);

    private static bool HaveVanilla =>
        File.Exists(Paths.TreeList) && File.Exists(Paths.TreeIdsTsv);

    private static TreeDatabase Db() => TreeDatabase.Load(Paths.TreeIdsTsv, Paths.TreeVariantsTsv);

    [Fact]
    public void CaHash_MatchesCalibsMurmur()
    {
        // Values from calling ?murmur_hash@CA@@YAIPEBEI@Z in calibs.modder.x64.dll.
        Assert.Equal(0x7195E3E8u, CaHash.Murmur("abc"));
        Assert.Equal(0x83BF88BCu, CaHash.Murmur("bamboo_1"));
        Assert.Equal(0xB45F22F3u, CaHash.Murmur("general_rock_medium_1"));
    }

    [Fact]
    public void HexGrid_Vanilla_MatchesTreeListHeader()
    {
        Assert.Equal((892, 702), (VanillaGrid.Columns, VanillaGrid.Rows));
        Assert.Equal(595.1f, VanillaGrid.WorldWidth);
        Assert.Equal(541.78619384765625f, VanillaGrid.WorldHeight);
    }

    [Fact]
    public void Vanilla_DecodeThenGenerate_IsByteIdentical()
    {
        if (!HaveVanilla) return;
        var db = Db();
        var original = File.ReadAllBytes(Paths.TreeList);
        var (colours, heights) = CampaignTreeGenerator.Decode(CampaignTreeList.Read(original), VanillaGrid, db);
        var rebuilt = CampaignTreeGenerator.Generate(colours, VanillaGrid, db,
            (col, row, _, _) => heights[row * VanillaGrid.Columns + col]);
        Assert.Equal(original, rebuilt.ToBytes());
    }

    [Fact]
    public void Vanilla_WithLfHeights_EveryTreeInTheSameSpot()
    {
        var lfPath = Path.Combine(Paths.TerrainDir, "lf_height_map.compressed_map");
        if (!HaveVanilla || !File.Exists(lfPath)) return;
        var db = Db();
        var original = CampaignTreeList.Load(Paths.TreeList);
        var (colours, _) = CampaignTreeGenerator.Decode(original, VanillaGrid, db);
        var ts = TileHfHeight.TileSize3K;
        var lf = new LfSampler(CompressedMap.Read(lfPath), 1784 * ts, 1405 * ts, ts);
        var rebuilt = CampaignTreeGenerator.Generate(colours, VanillaGrid, db,
            (_, _, x, z) => lf.Height(x, z / TreesStep.CampaignZScale));

        Assert.Equal(original.Types.Select(t => t.Name), rebuilt.Types.Select(t => t.Name));
        var dy = new List<float>();
        for (var i = 0; i < original.Types.Count; i++)
        {
            var (a, b) = (original.Types[i].Instances, rebuilt.Types[i].Instances);
            Assert.Equal(a.Count, b.Count);
            for (var j = 0; j < a.Count; j++)
            {
                Assert.Equal((a[j].X, a[j].Z, a[j].Variant, a[j].Flag), (b[j].X, b[j].Z, b[j].Variant, b[j].Flag));
                Assert.Equal(a[j].Seasons, b[j].Seasons);
                dy.Add(Math.Abs(a[j].Y - b[j].Y));
            }
        }
        dy.Sort();
        Console.WriteLine($"TREES {dy.Count} same spot; |dy| median {dy[dy.Count / 2]:G3}, 99% {dy[(int)(dy.Count * 0.99)]:G3}, " +
                          $"99.9% {dy[(int)(dy.Count * 0.999)]:G3}, max {dy[^1]:F2}, >0.1: {dy.Count(d => d > 0.1f)}");
    }

    [Fact]
    public void TreeMap_WriteThenRead_KeepsEveryHex()
    {
        if (!HaveVanilla) return;
        var db = Db();
        var (colours, _) = CampaignTreeGenerator.Decode(CampaignTreeList.Load(Paths.TreeList), VanillaGrid, db);
        var (map, palette) = CampaignTreeGenerator.WriteTreeMap(colours, VanillaGrid, 1784, 1405, db, AkExporter.NoTreeIndex);
        Assert.Equal(colours, CampaignTreeGenerator.ReadTreeMap(map, palette, VanillaGrid, AkExporter.NoTreeIndex));
    }

    [Fact]
    public void LfHeights_InTileSpace_CloseToVanilla()
    {
        var lfPath = Path.Combine(Paths.TerrainDir, "lf_height_map.compressed_map");
        if (!HaveVanilla || !File.Exists(lfPath)) return;
        var ts = TileHfHeight.TileSize3K;
        var lf = new LfSampler(CompressedMap.Read(lfPath), 1784 * ts, 1405 * ts, ts);
        var trees = CampaignTreeList.Load(Paths.TreeList).Types.SelectMany(t => t.Instances).ToList();
        var exact = trees.Count(t => lf.Height(t.X, t.Z / TreesStep.CampaignZScale) == t.Y);
        var close = trees.Count(t => Math.Abs(lf.Height(t.X, t.Z / TreesStep.CampaignZScale) - t.Y) < 1e-3f);
        Assert.True(exact > trees.Count * 0.55, $"{exact} of {trees.Count} bit-exact");
        Assert.True(close > trees.Count * 0.98, $"{close} of {trees.Count} within 1e-3");
    }
}
