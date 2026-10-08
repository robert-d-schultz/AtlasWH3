using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Tests;

public class TreeEditingTests
{
    private static TerrainData FlatLand(int w = 400, int h = 400)
    {
        var terrain = new TerrainData(
            new Raster<ushort>(w, h), new Raster<ushort>(w / 2, h / 2), new Raster<byte>(w, h), new Raster<byte>(w, h),
            null!, new WorldCoords(w * 0.0834f, h * 0.0964f));
        Array.Fill(terrain.Height.Data, (ushort)30000);   // land, well above
        Array.Fill(terrain.SeaHeight.Data, (ushort)14219); // sea level
        return terrain;
    }

    private static CampaignTreeList ExistingForest()
    {
        var list = new CampaignTreeList { WorldWidth = 400 * 0.0834f, WorldHeight = 400 * 0.0964f };
        var fir = new TreeType { Name = "cold_tree_fir_large_1" };
        for (var i = 0; i < 50; i++)
            fir.Instances.Add(new TreeInstance { X = 5 + i * 0.05f, Z = 5, Y = 1, Flag = 1 });
        list.Types.Add(fir);
        return list;
    }

    [Fact]
    public void Scatter_AddsTrees_WithHeightAndSpacing_AndUndoRestores()
    {
        var terrain = FlatLand();
        var trees = ExistingForest();
        var log = new TreeEditLog();
        var brush = new TreeBrush(trees, null, log)
        {
            Mode = TreeBrushMode.Scatter, Species = "cold_tree_fir_large_1", Radius = 60, Strength = 1, Density = 20, MinSpacing = 0.2,
        };
        brush.Begin(terrain, 250, 250);
        for (var i = 0; i < 10; i++) brush.Dab(250, 250);
        var undo = brush.End()!;

        var added = trees.Types[0].Instances.Skip(50).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, t =>
        {
            Assert.Equal(HeightScale.ToWorld(30000), t.Y, 3);
            Assert.Equal(0xFF, t.Tag);
            Assert.InRange(t.Variant, 0, 5);
        });
        for (var i = 0; i < added.Count; i++)
            for (var j = i + 1; j < added.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(added[i].X - added[j].X, 2) + Math.Pow(added[i].Z - added[j].Z, 2));
                Assert.True(d >= 0.2 - 1e-4, $"trees {i},{j} only {d:F3} apart");
            }
        Assert.False(log.IsEmpty);

        undo.Undo();
        Assert.Equal(50, trees.TotalInstances);
        undo.Redo();
        Assert.Equal(50 + added.Count, trees.TotalInstances);
    }

    [Fact]
    public void Scatter_NewSpecies_CreatesType_AndAvoidsWater()
    {
        var terrain = FlatLand();
        // Left half underwater.
        for (var y = 0; y < terrain.HeightPx; y++)
            for (var x = 0; x < terrain.Width / 2; x++)
                terrain.Height[x, y] = 3084;
        var trees = ExistingForest();
        var brush = new TreeBrush(trees, null, new TreeEditLog())
        {
            Mode = TreeBrushMode.Scatter, Species = "bamboo_1", Radius = 80, Strength = 1, Density = 15,
        };
        brush.Begin(terrain, 200, 200);
        for (var i = 0; i < 10; i++) brush.Dab(200, 200);
        var undo = brush.End();

        var bamboo = trees.Types.Single(t => t.Name == "bamboo_1");
        Assert.NotEmpty(bamboo.Instances);
        Assert.All(bamboo.Instances, t =>
        {
            var (col, _) = terrain.Coords.ToPixel(t.X, t.Z, terrain.Width, terrain.HeightPx);
            Assert.True(col >= terrain.Width / 2 - 1, $"tree placed in water at col {col:F1}");
        });

        undo!.Undo();
        Assert.DoesNotContain(trees.Types, t => t.Name == "bamboo_1");
    }

    [Fact]
    public void Erase_RemovesOnlyInsideRadius_AndRespectsHiddenSpecies()
    {
        var terrain = FlatLand();
        var trees = ExistingForest();
        var rock = new TreeType { Name = "general_rock_small_1" };
        rock.Instances.Add(new TreeInstance { X = 5.5f, Z = 5 });
        trees.Types.Add(rock);

        var (mx, my) = terrain.Coords.ToPixel(5.5, 5, terrain.Width, terrain.HeightPx);
        var brush = new TreeBrush(trees, null, new TreeEditLog())
        {
            Mode = TreeBrushMode.Erase, Radius = 12 /* px ≈ 1 world unit */, Strength = 1,
            HiddenSpecies = new HashSet<string> { "general_rock_small_1" },
        };
        brush.Begin(terrain, mx, my);
        brush.Dab(mx, my);
        brush.End();

        Assert.Single(rock.Instances); // hidden species untouched
        var fir = trees.Types[0].Instances;
        Assert.True(fir.Count < 50);
        var worldRadius = 12 * terrain.Coords.WorldWidth / terrain.Width;
        Assert.All(fir, t => Assert.True(Math.Abs(t.X - 5.5) > worldRadius - 1e-3 || Math.Abs(t.Z - 5) > worldRadius));
    }

    [Fact]
    public void UpdatePaint_SetsDominantSpeciesColour_OrNoTree()
    {
        var trees = ExistingForest();
        var paint = new Raster<byte>(100, 100);
        Array.Fill(paint.Data, AkExporter.NoTreeIndex);
        var r = new ushort[256]; var g = new ushort[256]; var b = new ushort[256];
        r[3] = 0x10 * 257; g[3] = 0x20 * 257; b[3] = 0x30 * 257;  // not the fir colour
        r[5] = 0x40 * 257; g[5] = 0x50 * 257; b[5] = 0x60 * 257;  // fir colour
        var palette = new TiffMap.Palette(r, g, b);
        var db = TreeDatabaseFor("cold_tree_fir_large_1", 0x405060);

        var log = new TreeEditLog();
        log.Add(5, 5);          // cell with firs
        log.Add(20, 20);        // cell with nothing
        var cellA = ((int)(5 / trees.WorldWidth * 100), (int)((1 - 5 / trees.WorldHeight) * 100));
        var cellB = ((int)(20 / trees.WorldWidth * 100), (int)((1 - 20 / trees.WorldHeight) * 100));
        paint[cellB.Item1, cellB.Item2] = 3;

        var changed = TreeExporter.UpdatePaint(paint, palette, trees, db, log);
        Assert.Equal(2, changed);
        Assert.Equal(5, paint[cellA.Item1, cellA.Item2]);
        Assert.Equal(AkExporter.NoTreeIndex, paint[cellB.Item1, cellB.Item2]);
    }

    private static TreeDatabase TreeDatabaseFor(string id, uint colour)
    {
        var dir = Directory.CreateTempSubdirectory("terryclone_db").FullName;
        var ids = Path.Combine(dir, "ids.tsv");
        var variants = Path.Combine(dir, "variants.tsv");
        File.WriteAllText(ids, $"tree_id\tcan_be_removed\tseason\tcolour_hex\n#campaign_tree_ids_tables;2;db/x\n{id}\ttrue\tALL\t{colour:X6}\n");
        File.WriteAllText(variants, $"tree_id\ttree_rigid\ttree_type\ttree_audio\treceive_decals\n#campaign_tree_variants_tables;3;db/x\n{id}\tx.wsmodel\tBASE\t\tfalse\n");
        return TreeDatabase.Load(ids, variants);
    }
}
