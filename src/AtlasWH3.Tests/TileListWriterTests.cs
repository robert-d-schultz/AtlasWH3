using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>tile_list.bin and tile_mask.dds writing (low/high, record order, tables, header, mask), on BOB's own placement
/// read back from the tile lists BOB made for the user's maps.</summary>
public class TileListWriterTests
{
    private static ProjectPaths Paths(string map) => new() { MapName = map, AssemblyKitRoot = TestKits.Kit(map), GameDataDir = TestKits.Wh3GameData };
    private static string Terry(string map) => Path.Combine(Paths(map).AkTerrainDir, map + ".terry");

    /// <summary>BOB's placement, read back from tile_list.bin: layer 2 = records of an also_place target set (sea) lying
    /// wholly inside an also_place tile's footprint (cliffs, coasts).</summary>
    public static List<PlacedTile> BobPlacement(CampaignTileDatabase db, TileList list)
    {
        var byLocation = db.Tiles.GroupBy(t => t.Variations[0].Location.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var climate = db.Climates.ToDictionary(c => c.Name, c => c.Index);
        var tiles = list.Records.Select(r => byLocation[list.Paths[(int)r.Path].ToLowerInvariant()]).ToList();
        int w = list.Ints[1], h = list.Ints[2];
        var cover = new bool[w * h];
        var alsoTargets = db.TileSets.Where(s => s.AlsoPlaceTileSet.Length > 0).Select(s => s.AlsoPlaceTileSet).ToHashSet();
        for (var k = 0; k < tiles.Count; k++)
            if (db.TileSet(tiles[k].TileSet)?.AlsoPlaceTileSet.Length > 0)
                foreach (var (x, y) in TileListWriter.Footprint(tiles[k], list.Records[k].X, list.Records[k].Y, list.Records[k].Orientation & 0xF0))
                    if ((uint)x < w && (uint)y < h) cover[y * w + x] = true;
        var result = new List<PlacedTile>();
        for (var k = 0; k < tiles.Count; k++)
        {
            var r = list.Records[k];
            var cells = TileListWriter.Footprint(tiles[k], r.X, r.Y, r.Orientation & 0xF0).Where(c => (uint)c.X < w && (uint)c.Y < h).ToList();
            var layer = alsoTargets.Contains(tiles[k].TileSet) && cells.Count > 0 && cells.All(c => cover[c.Y * w + c.X]) ? 2 : 1;
            result.Add(new PlacedTile(tiles[k], r.X, r.Y, r.Orientation & 0xF0, climate[list.Climates[r.Climate]], layer));
        }
        return result;
    }

    /// <summary>
    /// The fitted patch mask against BOB's (vanilla) on BOB's placement: same grid and header; columns are whole cells on
    /// both maps (3200 / 128, 4096 / 128), so only rows can change, more and more towards the north. 2026-10-09: IEE 625
    /// of 9,856 cells, Old World 1,298 of 14,080. Printed: how many cells change, by row.
    /// </summary>
    [Theory]
    [InlineData("cr_oldworld_map_1")]
    [InlineData("cr_combi_expanded_map_1")]
    public void FittedPatchMask_CoversTheNorthBand(string map)
    {
        var reference = CompiledFormatTests.Built(map, "tile_list.bin");
        if (!File.Exists(reference) || !File.Exists(Terry(map))) return;
        var db = TileMapValidator.LoadDatabase(Paths(map));
        var bob = TileList.Read(File.ReadAllBytes(reference));
        var project = TerryProject.Load(Terry(map));
        var land = HeightField.FromRaster(TerrainComposite.Heights(project, "Height"));
        var sea = HeightField.FromRaster(TerrainComposite.Heights(project, "HeightSea"));
        int width = bob.Ints[1], height = bob.Ints[2];
        TileListWriter.Build(db, width, height, BobPlacement(db, bob), land, sea, out _, out var vanilla, PatchMaskMode.Vanilla);
        TileListWriter.Build(db, width, height, BobPlacement(db, bob), land, sea, out _, out var fitted);

        Assert.Equal(vanilla.Length, fitted.Length);
        Assert.True(vanilla.AsSpan(0, 148).SequenceEqual(fitted.AsSpan(0, 148)));
        var (pw, ph) = TileListWriter.PatchGrid(width, height);
        Assert.Equal(0, width % pw);
        var changed = Enumerable.Range(0, ph).Select(y => Enumerable.Range(0, pw).Count(x => vanilla[148 + y * pw + x] != fitted[148 + y * pw + x])).ToList();
        Console.WriteLine($"{map}: {changed.Sum()} of {pw * ph} cells changed; by row from the south: {string.Join(" ", changed)}");
        // BOB's rows drift north of their area as they go, so the changes grow towards the north edge
        Assert.True(changed.Skip(ph / 2).Sum() > 2 * changed.Take(ph / 2).Sum());
    }

    /// <summary>
    /// 2026-10-08. Old World: tile_list.bin and tile_mask.dds byte-identical (patch_mask.dds too, 2026-10-09, also on IEE).
    /// IEE: byte-identical against the user's BOB reprocess of 2026-10-10 (the 473 low/high pairs of the older run were
    /// its stale heights: the height layers had been edited after it).
    /// </summary>
    [Theory]
    [InlineData("cr_oldworld_map_1", 0)]
    [InlineData("cr_combi_expanded_map_1", 0)]
    public void BobPlacement_ThroughWriter_MatchesBob(string map, int staleHeights)
    {
        var reference = CompiledFormatTests.Built(map, "tile_list.bin");
        var referenceMask = CompiledFormatTests.Built(map, "tile_mask.dds");
        if (!File.Exists(reference) || !File.Exists(referenceMask) || !File.Exists(Terry(map))) return;
        var db = TileMapValidator.LoadDatabase(Paths(map));
        var original = File.ReadAllBytes(reference);
        var bob = TileList.Read(original);
        var project = TerryProject.Load(Terry(map));
        var land = HeightField.FromRaster(TerrainComposite.Heights(project, "Height"));
        var sea = HeightField.FromRaster(TerrainComposite.Heights(project, "HeightSea"));
        var built = TileListWriter.Build(db, bob.Ints[1], bob.Ints[2], BobPlacement(db, bob), land, sea, out var mask, out var patch,
                                         PatchMaskMode.Vanilla);

        var referencePatch = CompiledFormatTests.Built(map, "patch_mask.dds");
        if (File.Exists(referencePatch)) Assert.Equal(File.ReadAllBytes(referencePatch), patch);
        Assert.Equal(File.ReadAllBytes(referenceMask), mask);
        Assert.Equal(bob.Ints, built.Ints);
        Assert.Equal(bob.Floats, built.Floats);
        Assert.Equal(bob.Paths, built.Paths);
        Assert.Equal(bob.Climates, built.Climates);
        Assert.Equal(bob.Records.Count, built.Records.Count);
        var heights = 0;
        for (var i = 0; i < bob.Records.Count; i++)
        {
            var (a, b) = (bob.Records[i], built.Records[i]);
            Assert.Equal((a.Path, a.X, a.Y, a.Orientation, a.Climate, a.Flag), (b.Path, b.X, b.Y, b.Orientation, b.Climate, b.Flag));
            if (a.LowHeight != b.LowHeight || a.HighHeight != b.HighHeight) heights++;
        }
        Assert.Equal(staleHeights, heights);
        if (staleHeights == 0) Assert.Equal(original, built.ToBytes());
    }

    /// <summary>
    /// TileMatchSimulator on the user's tile maps places exactly BOB's tiles: the same instances (tile, anchor, rotation),
    /// as many (IEE 339,338, Old World 598,161), in the order the writer then gives BOB's records. 2026-10-09, after WH3's
    /// scan_tile_areas point list and the off-map strip skip (IEE 82.5% and Old World 71.8% before).
    /// </summary>
    [Theory]
    [InlineData("cr_combi_expanded_map_1")]
    [InlineData("cr_oldworld_map_1")]
    public void Simulator_PlacesBobsTiles(string map)
    {
        var reference = CompiledFormatTests.Built(map, "tile_list.bin");
        var tileMap = Path.Combine(Paths(map).AkTerrainDir, "tile_map.png");
        if (!File.Exists(reference) || !File.Exists(tileMap)) return;
        var db = TileMapValidator.LoadDatabase(Paths(map));
        var bob = TileList.Read(reference);
        var hexMap = HexTileMap.Read(tileMap);
        var sim = new TileMatchSimulator(db).Run(hexMap, new byte[hexMap.Pixels.Length]);
        var simulated = sim.Tiles.Select(t => (t.Location.ToLowerInvariant(), t.X, t.Y, t.Rotation)).Order().ToList();
        var expected = bob.Records.Select(r => (bob.Paths[(int)r.Path].ToLowerInvariant(), (int)r.X, (int)r.Y, (int)r.Orientation & 0xF0)).Order().ToList();
        Assert.Equal(expected.Count, simulated.Count);
        Assert.Equal(expected, simulated);
    }

    /// <summary>use_alt_lf (the tile_list heights from HeightSea) is set on the sea tiles only, and on
    /// sea_coast\1x1_straight, which neither fixture uses.</summary>
    [Fact]
    public void TileDatabase_UseAltLf_OnSeaTiles()
    {
        var map = CompiledFormatTests.Maps[0];
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        var db = TileMapValidator.LoadDatabase(Paths(map));
        var alt = db.Tiles.Where(t => t.UseAltLf).Select(t => t.File).ToHashSet();
        Assert.Equal(db.Tiles.Where(t => t.TileSet == "sea").Select(t => t.File).Append("sea_coast_1x1_straight.bin").ToHashSet(), alt);
    }
}
