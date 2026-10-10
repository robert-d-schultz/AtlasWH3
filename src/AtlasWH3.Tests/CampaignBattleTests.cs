using System.Text;
using AtlasWH3.Core;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;
using AtlasWH3.Formats.Packs;
using Xunit;

namespace AtlasWH3.Tests;

public class CampaignBattleTests
{
    private const string Kit = "assembly_kit";

    // ---------------------------------------------------------------- codec

    [Fact]
    public void Edited_map_roundtrips_and_snapshots_are_deep_copies()
    {
        var map = SmallMap();
        var stack = new UndoStack();
        var before = map.CloneLists();
        var area = CatchmentOps.Add(map, BattleLocations.Standard, new CellBox(2, 2, 6, 6), "new_area");
        area.Redirection = "settlement_city_han_c_small_walled";
        stack.Push(new Snapshot(map, before, map.CloneLists()));
        area.Box = area.Box.Offset(1, 0);   // a later change must not leak into the stored snapshot

        var bytes = map.Write();
        Assert.True(BattleLocations.Read(bytes).Write().AsSpan().SequenceEqual(bytes));
        Assert.Equal("settlement_city_han_c_small_walled", BattleLocations.Read(bytes).List(BattleLocations.Standard)!.Areas.Single().Redirection);

        stack.Undo();
        Assert.Empty(map.List(BattleLocations.Standard)!.Areas);
        stack.Redo();
        var redone = map.List(BattleLocations.Standard)!.Areas.Single();
        Assert.Equal(new CellBox(2, 2, 6, 6), redone.Box);
        Assert.Equal("new_area", redone.Name);
    }

    [Fact]
    public void Coverage_counts_uncovered_battle_land_only()
    {
        var map = SmallMap();
        var land = map.Cells.Count(c => c == map.LandIndex);
        Assert.Equal(land, CatchmentOps.UncoveredLand(map, BattleLocations.Ambush));
        CatchmentOps.Add(map, BattleLocations.Ambush, new CellBox(0, 0, 9, 9), "a");
        Assert.Equal(land - 100, CatchmentOps.UncoveredLand(map, BattleLocations.Ambush));
        Assert.Equal(land, CatchmentOps.UncoveredLand(map, BattleLocations.Standard));
    }

    // ---------------------------------------------------------------- regions

    [Fact]
    public void Region_status_and_fixes_follow_the_blm_rules()
    {
        var map = SmallMap();
        // vanilla capital, covered in both lists without redirect
        var capital = new CampaignSettlement("3k_main_capital", 5, Flip(map, 5));
        CatchmentOps.EnsureSiegeAreas(map, capital.RegionKey, 5, 5);
        // vanilla resource region: unfortified only, as vanilla
        var resource = new CampaignSettlement("3k_main_resource_1", 15, Flip(map, 5));
        CatchmentOps.Add(map, BattleLocations.Unfortified, new CellBox(13, 3, 17, 7), "res_unf");
        // vanilla pass: gate battle only
        var pass = new CampaignSettlement("3k_main_pass", 25, Flip(map, 5));
        CatchmentOps.Add(map, BattleLocations.Gate, new CellBox(24, 4, 26, 6), "gate");
        // new regions
        var newCity = new CampaignSettlement("ironic_new_capital", 35, Flip(map, 5));
        var newLumber = new CampaignSettlement("ironic_new_resource_1", 5, Flip(map, 15));
        var wrong = new CampaignSettlement("ironic_new_resource_2", 15, Flip(map, 15));
        var badEmbed = new CampaignSettlement("ironic_new_resource_3", 25, Flip(map, 15));
        var unredirected = new CampaignSettlement("ironic_new_resource_4", 35, Flip(map, 15));
        var off = new CampaignSettlement("ironic_far_away", 200, 5);
        {
            var (s, u, _) = CatchmentOps.EnsureSiegeAreas(map, wrong.RegionKey, 15, 15);
            s.Redirection = "settlement_city_han_a_small_walled";
            u.Redirection = "settlement_city_han_a_small";
            (s, u, _) = CatchmentOps.EnsureSiegeAreas(map, badEmbed.RegionKey, 25, 15);
            s.Redirection = u.Redirection = "resource_han_tools_a_regular_small";
            CatchmentOps.EnsureSiegeAreas(map, unredirected.RegionKey, 35, 15);
        }

        var mod = ModFixture();
        try
        {
            var analyzer = new RegionBattleAnalyzer(map, [capital, resource, pass, newCity, newLumber, wrong, badEmbed, unredirected, off],
                FakeCatalog(), ModRegionData.Load(mod), vanillaRegions: new HashSet<string> { capital.RegionKey, resource.RegionKey, pass.RegionKey });
            var st = analyzer.Analyze().ToDictionary(s => s.Settlement.RegionKey);

            Assert.Equal(RegionBattleState.Ok, st[capital.RegionKey].State);
            Assert.Equal(RegionBattleState.Ok, st[resource.RegionKey].State);
            Assert.Equal(RegionBattleState.Ok, st[pass.RegionKey].State);
            Assert.Equal(RegionBattleState.NoCatchment, st[newCity.RegionKey].State);
            Assert.Equal(RegionBattleState.NoCatchment, st[newLumber.RegionKey].State);
            Assert.Equal(RegionBattleState.WrongType, st[wrong.RegionKey].State);
            Assert.Equal(RegionBattleState.RedirectInvalid, st[badEmbed.RegionKey].State);
            Assert.Contains(st[badEmbed.RegionKey].Issues, i => i.Contains("hole"));
            Assert.Equal(RegionBattleState.RedirectMissing, st[unredirected.RegionKey].State);
            Assert.Equal(RegionBattleState.OffGrid, st[off.RegionKey].State);
            Assert.Equal("Newtown", st[newCity.RegionKey].DisplayName);
            Assert.Equal(RegionKind.City('c'), st[newCity.RegionKey].Suggested);

            // one-click fixes
            foreach (var key in new[] { newCity.RegionKey, newLumber.RegionKey, wrong.RegionKey, badEmbed.RegionKey, unredirected.RegionKey })
                Assert.True(analyzer.Fix(analyzer.Analyze(st[key].Settlement)).Success, key);
            var after = analyzer.Analyze().ToDictionary(s => s.Settlement.RegionKey);
            Assert.All(after.Values.Where(s => s.Settlement != off), s => Assert.Equal(RegionBattleState.Ok, s.State));
            Assert.Equal(RegionKind.City('c'), after[newCity.RegionKey].Current);
            Assert.Equal("settlement_city_han_c_small_walled", after[newCity.RegionKey].Std!.Redirection);
            Assert.Equal($"{newCity.RegionKey}_std", after[newCity.RegionKey].Std!.Name);
            Assert.Equal(RegionKind.ResourceOf("lumber"), after[newLumber.RegionKey].Current);
            Assert.Equal(RegionKind.ResourceOf("tools"), after[badEmbed.RegionKey].Current);
            Assert.Equal("resource_han_tools_a_regular_large", after[badEmbed.RegionKey].Unf!.Redirection);
            Assert.False(analyzer.Fix(after[off.RegionKey]).Success);

            // a redirect target without its centre is refused
            var refused = analyzer.Apply(after[newCity.RegionKey], RegionKind.ResourceOf("salt"));
            Assert.False(refused.Success);
        }
        finally { Directory.Delete(mod, true); }
    }

    [Fact]
    public void A_campaign_map_off_the_battle_grid_is_not_fixed()
    {
        var map = SmallMap();
        var s = new CampaignSettlement("ironic_new_capital", 5, 5);
        var analyzer = new RegionBattleAnalyzer(map, [s], FakeCatalog(), gridMatches: false);
        var status = analyzer.Analyze(s);
        Assert.Equal(RegionBattleState.OffGrid, status.State);
        Assert.False(analyzer.Fix(status).Success);
        Assert.All(map.Lists, l => Assert.Empty(l.Areas));
    }

    // ---------------------------------------------------------------- redirects and battles_tables

    [Fact]
    public void Redirect_folders_invert_and_battles_rows_point_at_the_folder()
    {
        foreach (var kind in RegionKind.Applicable())
        {
            var f = RedirectCatalog.FoldersFor(kind)!.Value;
            Assert.Equal(kind, RedirectCatalog.KindFromFolders(f.Std, f.Unf));
        }
        Assert.Null(RedirectCatalog.FoldersFor(RegionKind.Vanilla));

        var row = BattlesTable.Row("224ad6d5_3784_4c2d_93d4_87287c140efa", "siege", "custom_test_map");
        Assert.Equal(BattlesTable.Columns.Length, row.Length);
        Assert.Equal(@"terrain\battles\224ad6d5_3784_4c2d_93d4_87287c140efa\", row[Array.IndexOf(BattlesTable.Columns, "map_path")]);
        Assert.Equal(@"terrain\battles\224ad6d5_3784_4c2d_93d4_87287c140efa\", row[Array.IndexOf(BattlesTable.Columns, "specification")]);
        Assert.Equal("siege", row[1]);
        Assert.Equal("1839542190", row[^1]);   // same unique_id as the blm tool's battles-row for this key
        var tsv = BattlesTable.ToTsv([row]).Split('\n');
        Assert.StartsWith("key\ttype\t", tsv[0]);
        Assert.StartsWith(BattlesTable.Metadata, tsv[1]);
        Assert.Equal("siege", BattlesTable.TypeForList(BattleLocations.Standard));
        Assert.Equal("unfortified_settlement", BattlesTable.TypeForList(BattleLocations.Unfortified));
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public void Saving_refuses_the_game_data_folder_and_packs()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlaswh3_battles_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var linked = Path.Combine(root, "mods", "my_mod.pack");
        Directory.CreateDirectory(data);
        try
        {
            var map = SmallMap();
            var ws = new CampaignBattleWorkspace
            {
                Paths = new ProjectPaths { GameDataDir = data, ModPacks = [linked], OutputRoot = Path.Combine(root, "out"), AssemblyKitRoot = Path.Combine(root, Kit) },
                TerrainFolder = "3k_main_map", Map = map, Source = "test",
                Redirects = FakeCatalog(), Regions = new RegionBattleAnalyzer(map, [], FakeCatalog()),
            };
            Assert.Throws<UnauthorizedAccessException>(() => ws.SaveLoose(Path.Combine(data, "campaign_battles")));
            Assert.Throws<UnauthorizedAccessException>(() => ws.ExportPack(linked));
            Assert.Throws<UnauthorizedAccessException>(() => ws.ExportPack(Path.Combine(data, "my_battles.pack")));

            CatchmentOps.Add(map, BattleLocations.Standard, new CellBox(1, 1, 3, 3), "x").Redirection = "my_custom_map";
            Assert.True(ws.IsDirty);
            var written = ws.SaveLoose(Path.Combine(root, "out"));
            Assert.False(ws.IsDirty);
            Assert.Equal(map.Write(), File.ReadAllBytes(written[0]));
            Assert.Contains(written, w => w.EndsWith("atlaswh3_campaign_battles.tsv"));   // no known row for my_custom_map
            Assert.Contains(@"terrain\battles\my_custom_map\", File.ReadAllText(written[1]));

            // a map whose own pack ships its row: no row is written
            ws.NoRowFolders.Add("my_custom_map");
            Assert.Empty(ws.MissingBattleRows());
            Assert.Single(ws.SaveLoose(Path.Combine(root, "out")));
            Assert.False(File.Exists(written[1]));

            var pack = Path.Combine(root, "out", "battles.pack");
            ws.ExportPack(pack);
            Assert.Equal(map.Write(), PackFile.Open(pack).TryRead("terrain/battles/3k_main_map/battle_locations_map.bin"));
            Assert.False(File.Exists(linked));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>40×20 cells, meta index 1 = battle land everywhere but column 39, empty lists.</summary>
    private static BattleLocations SmallMap()
    {
        var map = new BattleLocations { Width = 40, Height = 20, MetaItems = ["land", "sea"] };
        map.Cells = Enumerable.Range(0, 800).Select(i => i % 40 == 39 ? 0 : 1).ToArray();
        foreach (var key in new[] { BattleLocations.Ambush, BattleLocations.Standard, BattleLocations.Encampments, BattleLocations.Unfortified, BattleLocations.Gate })
            map.Lists.Add(new CatchmentList(key));
        map.Lists[0].Areas.Add(new CatchmentArea { Box = new CellBox(30, 10, 30, 10), Centre = (30, 10), Name = "seed" });
        map.CalibrateLand();
        map.Lists[0].Areas.Clear();
        return map;
    }

    /// <summary>The campaign hex row of a catchment row.</summary>
    private static int Flip(BattleLocations map, int catchmentY) => map.Height - 1 - catchmentY;

    /// <summary>Settlement maps embed their centre except the multi-size tools map, as in vanilla.</summary>
    private static RedirectCatalog FakeCatalog() => new(path =>
    {
        var folder = path.Split('/')[2];
        if (folder == "resource_han_tools_a_regular_small" || folder == "resource_han_salt_a_small") return Encoding.Latin1.GetBytes("no centre here");
        return folder.StartsWith("settlement_city", StringComparison.Ordinal) ? Encoding.Latin1.GetBytes("prefabs\\settlement_cities\\x")
            : folder.StartsWith("resource_", StringComparison.Ordinal) ? Encoding.Latin1.GetBytes("prefabs\\resource\\x") : null;
    });

    private static string ModFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlaswh3_moddata_" + Guid.NewGuid().ToString("N"));
        void Tsv(string relative, params string[] lines)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
        }
        Tsv(@"text\db\regions.loc.tsv", "key\ttext\ttooltip", "#Loc;1;text/db/regions.loc", "regions_onscreen_ironic_new_capital\tNewtown\tfalse");
        Tsv(@"db\campaign_settlement_display_settlement_layouts_tables\mod.tsv", "settlement\tlayout",
            "#campaign_settlement_display_settlement_layouts_tables;0;db/x", "settlement:ironic_new_capital\tsettlement_land_layout_c");
        Tsv(@"db\start_pos_settlements_tables\mod.tsv", "id\tsettlement\tprimary_building", "#start_pos_settlements_tables;5;db/x",
            "1\tsettlement:ironic_new_resource_1\t3k_resource_wood_lumber_pine_1",
            "2\tsettlement:ironic_new_resource_2\t3k_resource_wood_livestock_1",
            "3\tsettlement:ironic_new_resource_3\t3k_resource_metal_tools_1",
            "4\tsettlement:ironic_new_resource_4\t3k_resource_wood_lumber_pine_1");
        return root;
    }

    private sealed class Snapshot(BattleLocations map, List<CatchmentList> before, List<CatchmentList> after) : IUndoable
    {
        public string Description => "snapshot";
        public void Undo() => map.Lists = before.Select(l => l.Clone()).ToList();
        public void Redo() => map.Lists = after.Select(l => l.Clone()).ToList();
    }
}
