using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>global_map\ (BOB Global Tilemap + Campaign Global Blendmap) against BOB's output for the user's maps.</summary>
public class GlobalMapTests
{
    private const string OldWorld = "cr_oldworld_map_1", Iee = "cr_combi_expanded_map_1";
    /// <summary>Old World's mod pack: its asset db adds the texture group mud_dry_darklands.</summary>
    private static string OldWorldPack => TestKits.Pack(TestKits.OldWorldPack);

    private static ProjectPaths Paths(string map, params string[] modPacks) =>
        new() { MapName = map, AssemblyKitRoot = TestKits.Kit(map), GameDataDir = TestKits.Wh3GameData, ModPacks = modPacks };

    /// <summary>The whole step on Old World (2026-10-09): global_blend.dds, texture_arrays.xml and tile_list.bin
    /// byte-identical to BOB's, with the root tile list taken from working_data.</summary>
    [Fact]
    public void Step_OldWorld_MatchesBob()
    {
        var bob = CompiledFormatTests.Built(OldWorld, "global_map");
        if (!Directory.Exists(bob) || !File.Exists(OldWorldPack)) return;
        var target = Path.Combine(Path.GetTempPath(), "atlaswh3_global_map_test");
        try
        {
            var ctx = new CampaignBuildContext(Paths(OldWorld, OldWorldPack), target);
            Assert.Empty(new GlobalMapStep().CheckInputs(ctx));
            var result = new GlobalMapStep().Run(ctx);
            Assert.DoesNotContain(result.Notes, n => n.StartsWith("global_blend:") || n.StartsWith("texture group"));
            foreach (var file in new[] { "global_blend.dds", "texture_arrays.xml", "tile_list.bin" })
                Assert.True(File.ReadAllBytes(Path.Combine(bob, file)).AsSpan().SequenceEqual(File.ReadAllBytes(ctx.OutFile("global_map", file))), file);
        }
        finally { if (Directory.Exists(target)) Directory.Delete(target, true); }
    }

    /// <summary>IEE's texture_arrays.xml is vanilla's (172 groups, no mod adds any); Old World's has its mod's
    /// mud_dry_darklands at index 99, so every later group's index shifts by one.</summary>
    [Fact]
    public void TextureArrays_FromAssetDbs_MatchBob()
    {
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        foreach (var (map, packs) in new[] { (Iee, Array.Empty<string>()), (OldWorld, [OldWorldPack]) })
        {
            var bob = CompiledFormatTests.Built(map, "global_map", "texture_arrays.xml");
            if (!File.Exists(bob) || !packs.All(File.Exists)) continue;
            var set = GameSetup.OpenWithLinked(TestKits.Wh3GameData, packs);
            var prefix = PackFile.Normalize(AssetVariationDb.Folder + "/");
            var dbs = set.Packs.SelectMany(p => p.Entries.Keys).Distinct()
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(".assetdb", StringComparison.Ordinal))
                .Select(k => AssetVariationDb.Read(set.TryRead(k)!));
            var arrays = TextureArrays.FromAssetDbs(dbs, out var problems);
            Assert.Empty(problems);
            Assert.Equal(File.ReadAllBytes(bob), arrays.ToXml());
            Assert.Equal(TextureArrays.Load(bob).Groups.Select(g => (g.Name, g.BaseColour, g.MaterialMap, g.NormalRoughnessOcclusion)),
                         arrays.Groups.Select(g => (g.Name, g.BaseColour, g.MaterialMap, g.NormalRoughnessOcclusion)));
        }
    }

    /// <summary>global_map\tile_list.bin = the root list cut to exclude_from_global_mesh tiles (IEE 52,707 of 339,338
    /// records, Old World 73,817 of 598,161: roads, roads_light, cliff_gen(_ends), sea_coast).</summary>
    [Theory]
    [InlineData(Iee, 52707)]
    [InlineData(OldWorld, 73817)]
    public void TileList_GlobalMapSubset_MatchesBob(string map, int records)
    {
        var root = CompiledFormatTests.Built(map, "tile_list.bin");
        var bob = CompiledFormatTests.Built(map, "global_map", "tile_list.bin");
        if (!File.Exists(root) || !File.Exists(bob)) return;
        var db = TileMapValidator.LoadDatabase(Paths(map));
        var excluded = db.Tiles.Where(t => db.TileSet(t.TileSet) is { ExcludeFromGlobalMesh: true })
            .Select(t => t.Variations[0].Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subset = TileList.Read(root).GlobalMapSubset(excluded.Contains);
        Assert.Equal(records, subset.Records.Count);
        Assert.Equal(File.ReadAllBytes(bob), subset.ToBytes());
    }

    /// <summary>global_blend.dds on IEE: header identical; 187,094 pixels differ, all in one 1240 x 556 px area of the
    /// "iee" blend layer, which was saved on 2026-10-03, after that BOB run (2026-09-30).</summary>
    [Fact]
    public void GlobalBlend_Iee_DiffersOnlyInTheLaterEdit()
    {
        var bob = CompiledFormatTests.Built(Iee, "global_map", "global_blend.dds");
        var terry = Path.Combine(Paths(Iee).AkTerrainDir, Iee + ".terry");
        if (!File.Exists(bob) || !File.Exists(terry)) return;
        var (blend, _) = TerrainComposite.Indexed(TerryProject.Load(terry), "BlendCampaign");
        var expected = File.ReadAllBytes(bob);
        var header = AtlasWH3.Formats.Dds.DdsHeader.BuildGlobalBlend(blend.Width, blend.Height);
        Assert.Equal(expected[..header.Length], header);
        int diffs = 0, x0 = int.MaxValue, x1 = 0, y0 = int.MaxValue, y1 = 0;
        for (var i = 0; i < blend.Data.Length; i++)
        {
            var v = blend.Data[i] == 255 ? 0 : blend.Data[i];
            if (v == expected[header.Length + i]) continue;
            diffs++;
            var (x, y) = (i % blend.Width, i / blend.Width);
            (x0, x1, y0, y1) = (Math.Min(x0, x), Math.Max(x1, x), Math.Min(y0, y), Math.Max(y1, y));
        }
        Assert.Equal(187094, diffs);
        Assert.Equal((11498, 12054, 6471, 7711), (x0, x1, y0, y1));
    }
}
