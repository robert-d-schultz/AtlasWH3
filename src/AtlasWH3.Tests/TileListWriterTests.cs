using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

/// <summary>tile_list.bin writing (flow, low/high, record order, tables, header), checked on vanilla 3k_dlc07.</summary>
public class TileListWriterTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static string Vanilla(string name) => Path.Combine(Paths.TerrainDir, name);

    /// <summary>Vanilla's own placement, read back from tile_list.bin: layer 2 = generic_sea/beach records lying inside
    /// an also_place tile's footprint; flow bits dropped (the writer recomputes them).</summary>
    public static List<PlacedTile> VanillaPlacement(CampaignTileDatabase db, TileList list)
    {
        var byLocation = db.Tiles.GroupBy(t => t.Variations[0].Location.TrimEnd('\\').ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var climate = db.Climates.ToDictionary(c => c.Name, c => c.Index);
        var tiles = list.Records.Select(r => byLocation[list.Paths[(int)r.Path].TrimEnd('\\').ToLowerInvariant()]).ToList();
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
            var cells = TileListWriter.Footprint(tiles[k], r.X, r.Y, r.Orientation & 0xF0)
                .Where(c => (uint)c.X < w && (uint)c.Y < h).ToList();
            var layer = alsoTargets.Contains(tiles[k].TileSet) && cells.Count > 0 && cells.All(c => cover[c.Y * w + c.X]) ? 2 : 1;
            result.Add(new PlacedTile(tiles[k], r.X, r.Y, r.Orientation & 0xF0, climate[list.Climates[r.Climate]], layer));
        }
        return result;
    }

    [Fact]
    public void Vanilla_PlacementThroughWriter_IsByteIdentical()
    {
        var dbDir = TileMapValidator.DefaultDatabaseDir(Paths);
        if (!File.Exists(Vanilla("tile_list.bin")) || !Directory.Exists(dbDir)) return;
        var db = TileMapValidator.LoadDatabase(Paths);
        var original = File.ReadAllBytes(Vanilla("tile_list.bin"));
        var vanilla = TileList.Read(original);
        var placed = VanillaPlacement(db, vanilla);
        var lf = HeightField.FromRaster(TerrainDds.ReadL16(Vanilla("lf_height_map.dds")));
        // CA's sea source: the compiled sea map is source * 65535/44217, so source = ceil(v * 44217 / 65535)
        var seaDds = TerrainDds.ReadL16(Vanilla("lf_sea_height_map.dds"));
        var sea = new HeightField(seaDds.Data.Select(v => (float)Math.Ceiling(v * 44217.0 / 65535.0) / 65535f).ToArray(),
                                  seaDds.Width, seaDds.Height);
        var built = TileListWriter.Build(db, vanilla.Ints[1], vanilla.Ints[2], placed, lf, sea, t => t.UseAltLf);

        Assert.Equal(vanilla.Records.Count, built.Records.Count);
        var diffs = new List<string>();
        for (var i = 0; i < vanilla.Records.Count && diffs.Count < 10; i++)
        {
            var (a, b) = (vanilla.Records[i], built.Records[i]);
            if (vanilla.Paths[(int)a.Path] != built.Paths[(int)b.Path] || a.X != b.X || a.Y != b.Y || a.Orientation != b.Orientation
                || a.Climate != b.Climate || a.LowHeight != b.LowHeight || a.HighHeight != b.HighHeight)
                diffs.Add($"#{i} vanilla {vanilla.Paths[(int)a.Path]} {a.X},{a.Y} o{a.Orientation:X2} c{a.Climate} {a.LowHeight}/{a.HighHeight}" +
                          $" | built {built.Paths[(int)b.Path]} {b.X},{b.Y} o{b.Orientation:X2} c{b.Climate} {b.LowHeight}/{b.HighHeight}");
        }
        // 2026-10-04: byte-identical. calculate_flow neither flows nor queues a tile with no TLT_EQUALS entry link
        // (river_crossing\cross_5), so the river tiles upstream of it keep flow 0 as in vanilla.
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
        Assert.Equal(vanilla.Ints, built.Ints);
        Assert.Equal(vanilla.Paths, built.Paths);
        Assert.Equal(vanilla.Climates, built.Climates);
        var bytes = built.ToBytes();
        Assert.Equal(original.Length, bytes.Length);
        Assert.Equal(original, bytes);
    }
}
