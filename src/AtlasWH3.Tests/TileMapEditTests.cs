using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using Xunit;

namespace AtlasWH3.Tests;

public class TileMapEditTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static string DatabaseDir =>
        Path.Combine(Path.GetDirectoryName(Paths.VanillaRoot)!, "terrain", "tiles", "campaign", "_tile_database");
    private static string VanillaTileMap => Path.Combine(Path.GetDirectoryName(Paths.VanillaRoot)!, "3k_dlc07_main_map", "tile_map.png");
    private static readonly Lazy<CampaignTileDatabase> Db = new(() => CampaignTileDatabase.LoadFolder(DatabaseDir));
    private static bool HaveVanilla => File.Exists(VanillaTileMap) && Directory.Exists(DatabaseDir);

    private const uint Land = 0x96aa64, Sea = 0x3971b7, Mountain = 0xb69237, Road = 0x5d0018;

    [Fact]
    public void Cube_RoundTripsAndMatchesTheNeighbourTable()
    {
        var map = HexTileMap.Create(12, 10);
        for (var c = 0; c < 12; c++)
            for (var r = 0; r < 10; r++)
            {
                var (x, _, z) = TileMapOps.Cube(c, r);
                Assert.Equal((c, r), TileMapOps.Offset(x, z));
                for (var d = 0; d < 6; d++)
                    if (map.Neighbour(c, r, d, out var nc, out var nr))
                        Assert.Equal(1, TileMapOps.Distance((c, r), (nc, nr)));
            }
    }

    [Fact]
    public void Line_IsConnectedAndCircle_HasHexCount()
    {
        if (!Directory.Exists(DatabaseDir)) return;
        var path = TileMapOps.Line([(2, 3), (20, 9), (5, 17)]);
        for (var i = 1; i < path.Count; i++) Assert.Equal(1, TileMapOps.Distance(path[i - 1], path[i]));
        Assert.Equal((2, 3), path[0]);
        Assert.Equal((5, 17), path[^1]);
        var ops = new TileMapOps(HexTileMap.FromHexColours(30, 30, Enumerable.Repeat(Land, 900).ToArray()), Db.Value);
        Assert.Equal(19, ops.Circle(15, 15, 2).Count());
        Assert.All(ops.Circle(15, 15, 2), h => Assert.True(TileMapOps.Distance(h, (15, 15)) <= 2));
    }

    [Fact]
    public void Ops_PaintEraseFillReplace()
    {
        if (!Directory.Exists(DatabaseDir)) return;
        var colours = Enumerable.Repeat(Land, 40 * 30).ToArray();
        for (var r = 0; r < 30; r++)
            for (var c = 30; c < 40; c++) colours[r * 40 + c] = Sea;
        var map = HexTileMap.FromHexColours(40, 30, colours);
        var ops = new TileMapOps(map, Db.Value);

        ops.Apply(Op("""{"op":"paint","set":"mountains_temperate","circle":[10,10,2]}"""));
        Assert.Equal(Mountain, ops.Colour(10, 10));
        Assert.Equal(19, ops.Changed.Count);
        Assert.True(map.HexUniform(10, 10));

        ops.Apply(Op("""{"op":"erase","circle":[10,10,2]}"""));
        Assert.Equal(Land, ops.Colour(10, 10));             // removed: back to the surrounding land

        ops.Apply(Op("""{"op":"erase","rect":[28,5,31,7]}"""));
        Assert.Equal(Land, ops.Colour(28, 6));              // land and sea neighbours: area (land) wins
        Assert.Equal(Sea, ops.Colour(35, 6));

        ops.Apply(Op("""{"op":"line","set":"roads_tracks","points":[[2,2],[12,8]]}"""));
        Assert.Equal(Road, ops.Colour(2, 2));
        Assert.Equal(Road, ops.Colour(12, 8));

        ops.Apply(Op("""{"op":"replace","from":"roads_tracks","to":"roads_paved","all":true}"""));
        Assert.Equal(0x5d4218u, ops.Colour(2, 2));

        Assert.Throws<InvalidOperationException>(() => ops.Apply(Op("""{"op":"fill","set":"generic","at":[35,5],"max":10}""")));
        ops.Apply(Op("""{"op":"fill","set":"mountains_temperate","at":[35,5]}"""));
        Assert.Equal(Mountain, ops.Colour(39, 29));
        Assert.Equal(Land, ops.Colour(29, 29));
        Assert.Throws<KeyNotFoundException>(() => ops.Apply(Op("""{"op":"paint","set":"no_such_set","hexes":[[1,1]]}""")));
    }

    [Fact]
    public void Editor_WritesValidEdits_BlocksBadOnes_AndUndoesByteExactly()
    {
        if (!HaveVanilla) return;
        var dir = Path.Combine(Path.GetTempPath(), $"tile_edit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var tileMap = Path.Combine(dir, "tile_map.png");
        File.Copy(VanillaTileMap, tileMap);
        var original = File.ReadAllBytes(tileMap);
        var paths = Paths with { OutputRoot = Path.Combine(dir, "out") };
        try
        {
            var editor = new TileMapEditor(paths, tileMap, Db.Value);
            var map = editor.Load();
            var (c, r) = LandHex(map);
            var fillerBefore = Filler(map);

            // a mountain blob in open land: no new errors, written
            var ok = editor.Edit(new JsonArray(Op($$"""{"op":"paint","set":"mountains_temperate","circle":[{{c}},{{r}},2]}""")), "blob");
            Assert.True(ok.Written, string.Join("; ", ok.NewIssues.Select(i => i.Message)));
            Assert.Equal(1, ok.Seq);
            var after = HexTileMap.Read(tileMap);
            Assert.Equal(Mountain, after.HexColours()[after.Index(c, r)]);
            Assert.Equal(fillerBefore, Filler(after));
            var changed = ok.Changed.ToHashSet();
            var a0 = map.HexColours(); var a1 = after.HexColours();
            for (var i = 0; i < a0.Length; i++)
                if (a0[i] != a1[i]) Assert.Contains((i % map.Width, i / map.Width), changed);

            // a blob of road (lines must be one hex wide): a new rule issue, so nothing is written
            var written = File.ReadAllBytes(tileMap);
            var bad = editor.Edit(new JsonArray(Op($$"""{"op":"paint","set":"roads_tracks","circle":[{{c + 8}},{{r}},1]}""")));
            Assert.False(bad.Written);
            Assert.Contains(bad.NewIssues, f => f.Code == "line.thick");
            Assert.True(bad.Blocking > 0);
            Assert.Equal(written, File.ReadAllBytes(tileMap));
            Assert.Single(editor.Journal.History());

            // dry run never writes; checkpoint + rollback restore the original bytes
            editor.Journal.Checkpoint("after-blob");
            Assert.False(editor.Edit(new JsonArray(Op($$"""{"op":"erase","circle":[{{c}},{{r}},2]}""")), dryRun: true).Written);
            var erase = editor.Edit(new JsonArray(Op($$"""{"op":"erase","circle":[{{c}},{{r}},2]}""")));
            Assert.True(erase.Written);
            Assert.Equal(Land, HexTileMap.Read(tileMap).HexColours()[map.Index(c, r)]);
            editor.Rollback("after-blob");
            Assert.Equal(written, File.ReadAllBytes(tileMap));
            Assert.Single(editor.Ops());
            Assert.Equal(changed, editor.EditedHexes());

            // replay onto a "regenerated" map re-applies the recorded edit
            editor.Undo();
            Assert.Equal(original, File.ReadAllBytes(tileMap));
            Assert.Empty(editor.Ops());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Editor_ReplaysRecordedEditsOntoARegeneratedMap()
    {
        if (!HaveVanilla) return;
        var dir = Path.Combine(Path.GetTempPath(), $"tile_edit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var tileMap = Path.Combine(dir, "tile_map.png");
        File.Copy(VanillaTileMap, tileMap);
        try
        {
            var editor = new TileMapEditor(Paths with { OutputRoot = Path.Combine(dir, "out") }, tileMap, Db.Value);
            var (c, r) = LandHex(editor.Load());
            Assert.True(editor.Edit(new JsonArray(Op($$"""{"op":"paint","set":"mountains_temperate","circle":[{{c}},{{r}},1]}"""))).Written);
            File.Copy(VanillaTileMap, tileMap, overwrite: true);           // the builder regenerated the map
            Assert.Throws<InvalidOperationException>(() => editor.Undo());  // the file changed outside the editor
            var replay = editor.Replay();
            Assert.True(replay.Written);
            Assert.Equal(Mountain, HexTileMap.Read(tileMap).HexColours()[editor.Load().Index(c, r)]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TileHoles_VanillaBaseline()
    {
        var tileList = Path.Combine(Paths.VanillaRoot, "terrain", "campaigns", "3k_dlc07_main_map", "tile_list.bin");
        if (!File.Exists(tileList) || !Directory.Exists(DatabaseDir)) return;
        var report = TileHoles.Check(TileList.Read(tileList), Db.Value);
        Assert.Empty(report.UnknownPaths);
        Assert.Equal(1811, report.UncoveredCells);              // measured 2026-10-02 (mostly edge filler); the port matched
                                                                // research/main190/tile_holes.py exactly on main190 (54 cells)
        Assert.True(report.Clusters.Count(c => c.Kind == "suspect") <= 9);
    }

    [Fact]
    public void HexWorld_RoundTrips()
    {
        foreach (var (c, r) in new[] { (0, 0), (1, 0), (7, 12), (891, 701) })
        {
            var (x, z) = TileMapEditor.HexToWorld(c, r);
            Assert.Equal((c, r), TileMapEditor.WorldToHex(x, z));
        }
    }

    private static JsonObject Op(string json) => JsonNode.Parse(json)!.AsObject();

    private static uint[] Filler(HexTileMap map)
    {
        var result = new List<uint>();
        for (var x = 0; x < map.PixelWidth; x++)
            for (var y = 0; y < map.PixelHeight; y++)
                if (map.IsFiller(x, y)) result.Add(map.Pixel(x, y));
        return result.ToArray();
    }

    private static (int C, int R) LandHex(HexTileMap map, int radius = 12)
    {
        var colours = map.HexColours();
        for (var r = 300; r < map.Height - radius; r++)
            for (var c = 300; c < map.Width - radius; c++)
            {
                var ok = true;
                for (var dr = -radius; dr <= radius && ok; dr++)
                    for (var dc = -radius; dc <= radius && ok; dc++)
                        ok = colours[map.Index(c + dc, r + dr)] == Land;
                if (ok) return (c, r);
            }
        throw new InvalidOperationException("no open land in the vanilla map");
    }
}
