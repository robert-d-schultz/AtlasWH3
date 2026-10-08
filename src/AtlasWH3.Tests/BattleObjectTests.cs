using System.Xml.Linq;
using AtlasWH3.Core.Battle;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.Tests;

public class BattleObjectTests
{
    private const string TileDb = @"Z:\Claude\TerryClone\Vanilla\terrain\tiles\battle\_tile_database";
    private const string Vanilla = @"Z:\Claude\TerryClone\Vanilla\terrain\battles\3k_main_map";
    private const string Package = @"Z:\Claude\BattleMaps\out\share\3k_main_map_bob_sources\raw_data\terrain\battles\3k_main_map";

    [Fact]
    public void ExplicitFootprints_ContainWhatVanillaTilesOccupy()
    {
        var explicitPath = Path.Combine(Package, "explicit_tiles.txt");
        if (!Directory.Exists(TileDb) || !Directory.Exists(Vanilla) || !File.Exists(explicitPath)) return;
        var db = BattleTileDatabase.Load(TileDb);
        var map = BattleTileMapFile.Read(Vanilla);
        var tiles = ExplicitTilesFile.Read(explicitPath);
        var instances = map.Instances().ToLookup(i => (BattleTileDatabase.NormaliseLocation(i.Location), i.OriginX, i.OriginY));
        var checkedCount = 0;
        foreach (var t in tiles)
        {
            var tile = db.TileAt(t.Location)!;
            var (w, h) = t.Size(tile);
            var originY = map.Height - h - t.Y;            // image top-left → grid min corner
            var inst = instances[(BattleTileDatabase.NormaliseLocation(t.Location), t.X, originY)].Single();
            Assert.Equal(t.Rotation, inst.Rotation);
            var footprint = ExplicitTileGeometry.Footprint(t, tile).ToHashSet();
            for (var i = 0; i < map.W0.Length; i++)
            {
                var owner = map.W0[i] >= 1 ? i : map.W0[i] < 0 ? -map.W0[i] - 1 : -1;
                if (owner != inst.AnchorCell) continue;
                Assert.Contains((i % map.Width, map.Height - 1 - i / map.Width), footprint);
            }
            checkedCount++;
        }
        Assert.Equal(234, checkedCount);
        Assert.Empty(ExplicitTileGeometry.Conflicts(tiles, db, map.Width, map.Height));
    }

    [Fact]
    public void Conflicts_FlagOverlapsAndOffMap()
    {
        if (!Directory.Exists(TileDb)) return;
        var db = BattleTileDatabase.Load(TileDb);
        const string market = "terrain/tiles/battle/resource/resource_han_market_a";
        var tiles = new List<ExplicitTile> { new(10, 10, market, 0), new(11, 11, market, 90), new(20, 20, market, 0), new(891, 5, market, 0) };
        Assert.Equal([1, 3], ExplicitTileGeometry.Conflicts(tiles, db, 892, 703).Order());
    }

    [Fact]
    public void CatchmentLayer_WriteThenRead_KeepsEveryAreaAndOrder()
    {
        var layer = Directory.Exists(Package) ? Directory.GetFiles(Package, "*.layer").FirstOrDefault() : null;
        if (layer == null) return;
        var original = BattleCatchmentLayer.Read(layer, 892, 703);
        var doc = XDocument.Load(layer);
        BattleCatchmentLayer.Write(doc, original, 892, 703);
        var again = BattleCatchmentLayer.Read(doc, 892, 703);
        Assert.Equal(original.Count, again.Count);
        for (var i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Id, again[i].Id);
            Assert.Equal(original[i].Box, again[i].Box);
            Assert.Equal(original[i].Centre, again[i].Centre);
            Assert.Equal(original[i].Types, again[i].Types);
            Assert.Equal(original[i].RedirectTo, again[i].RedirectTo);
            Assert.Equal(original[i].BoundaryId, again[i].BoundaryId);
        }
        Assert.Equal(original.Count * 2, doc.Root!.Element("entities")!.Elements("entity").Count());
    }

    [Fact]
    public void CatchmentLayer_EditsAddAndRemove()
    {
        var doc = BattleCatchmentLayer.NewLayer();
        var a = new BattleCatchment("1a0000000000001", ["land_ambush"], "", "", (100, 200), new CellBox(93, 193, 107, 207), null);
        var b = new BattleCatchment("1a0000000000002", ["settlement_standard", "settlement_unfortified"], "gate_battles_b_both_deployments/", "",
            (446, 352), new CellBox(440, 345, 451, 356), null);
        BattleCatchmentLayer.Write(doc, [a, b], 892, 703);
        var read = BattleCatchmentLayer.Read(doc, 892, 703);
        Assert.Equal([a.Box, b.Box], read.Select(c => c.Box));
        Assert.Equal([a.Centre, b.Centre], read.Select(c => c.Centre));
        Assert.All(read, c => Assert.NotNull(c.BoundaryId));

        var moved = read[1] with { Box = read[1].Box.Offset(5, -3), Centre = (451, 349) };
        BattleCatchmentLayer.Write(doc, [moved], 892, 703);
        var after = BattleCatchmentLayer.Read(doc, 892, 703);
        Assert.Single(after);
        Assert.Equal(moved.Box, after[0].Box);
        Assert.Equal(read[1].BoundaryId, after[0].BoundaryId);
        Assert.Equal(2, doc.Root!.Element("entities")!.Elements("entity").Count());
        Assert.Single(doc.Root!.Element("associations")!.Element("Logical")!.Elements("from"));
    }

    [Fact]
    public void ListEdit_UndoRedo()
    {
        var list = new List<int> { 1, 2, 3 };
        var edit = ListEdit<int>.Apply(list, "remove 2", l => l.Remove(2));
        Assert.Equal([1, 3], list);
        edit!.Undo();
        Assert.Equal([1, 2, 3], list);
        edit.Redo();
        Assert.Equal([1, 3], list);
        Assert.Null(ListEdit<int>.Apply(list, "nothing", _ => { }));
    }

    [Fact]
    public void CompiledFiles_ReadVanilla_AndCompareWithItself()
    {
        if (!Directory.Exists(Vanilla) || !Directory.Exists(TileDb)) return;
        var blm = BattleLocationsMapFile.Read(Path.Combine(Vanilla, "battle_locations_map.bin"));
        Assert.Equal(["land", "sea"], blm.MetaItems);
        Assert.Equal(1296, blm.Lists.Sum(l => l.Areas.Count));
        Assert.Equal(532, blm.Lists.Single(l => l.Key == "encampments").Areas.Count);
        var report = BobBattleBuild.Compare(Vanilla, Vanilla, BattleTileDatabase.Load(TileDb));
        Assert.Contains(report, l => l.StartsWith("battle_locations_map.bin") && l.EndsWith("identical"));
        Assert.Contains(report, l => l.Contains("100.00% of cells"));
    }
}
