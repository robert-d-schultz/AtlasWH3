using AtlasWH3.Core;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

public class BattleProjectTests
{
    private const string TileDb = @"Z:\Claude\TerryClone\Vanilla\terrain\tiles\battle\_tile_database";
    private const string Package = @"Z:\Claude\BattleMaps\out\share\3k_main_map_bob_sources\raw_data\terrain\battles\3k_main_map";
    private const string PlacementGroups =
        @"C:\Program Files (x86)\Steam\steamapps\common\Total War THREE KINGDOMS\assembly_kit\raw_data\terrain\battles\tile_placement_groups.xml";

    [Fact]
    public void TileDatabase_ReadsVanillaSetsClimatesAndTiles()
    {
        if (!Directory.Exists(TileDb)) return; // data not available
        var db = BattleTileDatabase.Load(TileDb);
        Assert.Equal(54, db.TileSets.Count);
        Assert.Equal((byte)0, db.TileSets["sea"].R);
        Assert.Equal((byte)128, db.TileSets["sea"].G);
        Assert.Equal((byte)255, db.TileSets["sea"].B);
        Assert.Equal("sea_coast", db.TileSets["settlement_ports"].LinkAs);
        Assert.Equal(["default", "arid", "arid_fertile", "cold", "subtropical", "temperate", "tropical"],
            db.Climates.Take(7).Select(c => c.Name));
        Assert.Equal((195, 156, 93), (db.Climates[1].R, db.Climates[1].G, db.Climates[1].B));
        var city = db.TileAt("terrain/tiles/battle/settlement_cities/settlement_city_han_f/medium");
        Assert.NotNull(city);
        Assert.Equal((8, 8), (city!.Width, city.Height));
        Assert.True(city.HasColour);
    }

    [Fact]
    public void Palette_HasAllBobColoursWithoutClashes()
    {
        if (!Directory.Exists(TileDb) || !File.Exists(PlacementGroups)) return;
        var palette = BattlePalette.Build(BattleTileDatabase.Load(TileDb), PlacementGroups);
        Assert.Equal(188, palette.Entries.Count);
        Assert.Equal(palette.Entries.Count, palette.Entries.Select(e => e.Rgb).Distinct().Count());
        Assert.Equal("plains_1", palette.Find(BattlePalette.Pack(120, 150, 70))?.Name);
        Assert.Null(palette.Find(0));
    }

    [Fact]
    public void ExplicitTiles_RoundTripExactly()
    {
        var path = Path.Combine(Package, "explicit_tiles.txt");
        if (!File.Exists(path)) return;
        var tiles = ExplicitTilesFile.Read(path);
        Assert.Equal(234, tiles.Count);
        Assert.Equal(File.ReadAllText(path), ExplicitTilesFile.Format(tiles));
    }

    [Fact]
    public void CatchmentLayer_MatchesVanillaBattleLocationsMap()
    {
        var layer = Path.Combine(Package, "3k_main_map.bb0000000000000.layer");
        if (!File.Exists(layer)) return;
        var catchments = BattleCatchmentLayer.Read(layer, 892, 703);
        Assert.Equal(698, catchments.Count);
        var first = catchments.Single(c => c.Id == "172ef9c67c76cb6");
        Assert.Equal(["land_ambush"], first.Types);
        Assert.Equal(new CellBox(265, 627, 279, 641), first.Box);
        Assert.Equal((272, 634), first.Centre);
        Assert.Equal(66, catchments.Count(c => c.Types.Contains("settlement_standard")));
        Assert.Equal(192, catchments.Count(c => c.Types.Contains("settlement_unfortified")));
    }

    [Fact]
    public void CellWorld_RoundTrips()
    {
        for (var cx = 0; cx < 892; cx += 7)
        for (var cy = 0; cy < 703; cy += 11)
        {
            var (x, z) = BattleCatchmentLayer.CellToWorld(cx, cy, 892, 703);
            Assert.Equal((cx, cy), BattleCatchmentLayer.ToCell(x, z, 892, 703));
        }
    }

    /// <summary>A tiny battle project: 8x6 cells, land 8 px/cell, sea 4 px/cell.</summary>
    private static string MakeProject()
    {
        var dir = Directory.CreateTempSubdirectory("battle_").FullName;
        var tile = new Raster<uint>(8, 6);
        Array.Fill(tile.Data, BattleProject.ToPixel(BattlePalette.Pack(120, 150, 70)));   // plains_1
        tile[0, 0] = BattleProject.ToPixel(0);
        PngMap.Write(Path.Combine(dir, "tile_map.png"), tile);
        var climate = new Raster<uint>(8, 6);
        Array.Fill(climate.Data, BattleProject.ToPixel(BattlePalette.Pack(32, 176, 107)));  // temperate
        PngMap.Write(Path.Combine(dir, "climate_map.png"), climate);
        var land = new Raster<ushort>(64, 48);
        for (var i = 0; i < land.Data.Length; i++) land.Data[i] = (ushort)(1000 + i % 64 * 10);
        var sea = new Raster<ushort>(32, 24);
        Array.Fill(sea.Data, (ushort)862);
        foreach (var name in new[] { "lf_heights.tif", "t.height.aaa.tif" }) TiffMap.WriteGray16(Path.Combine(dir, name), land);
        foreach (var name in new[] { "lf_sea_heights.tif", "t.sea_height.bbb.tif" }) TiffMap.WriteGray16(Path.Combine(dir, name), sea);
        File.WriteAllText(Path.Combine(dir, "explicit_tiles.txt"), "2,1,terrain/tiles/battle/resource/resource_han_market_a,90\r\n");
        File.WriteAllText(Path.Combine(dir, "t.terry"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project version="20" id="p">
              <pc type="QTU::Terrain">
                <pc type="QTU::TerrainMap">
                  <data type="LowFrequencyHeight" size="64x48" id="h"/>
                  <pc type="QTU::TerrainMapLayer"><data id="aaa" name="base" visible="1" serializable="1" opacity="1"/></pc>
                </pc>
                <pc type="QTU::TerrainMap">
                  <data type="LowFrequencyHeightSea" size="32x24" id="s"/>
                  <pc type="QTU::TerrainMapLayer"><data id="bbb" name="base" visible="1" serializable="1" opacity="1"/></pc>
                </pc>
              </pc>
            </project>
            """);
        return dir;
    }

    private static BattlePaths TestPaths => new() { TileDatabaseDir = TileDb, PlacementGroupsXml = File.Exists(PlacementGroups) ? PlacementGroups : null };

    [Fact]
    public void Project_UneditedSaveWritesNothing()
    {
        if (!Directory.Exists(TileDb)) return;
        var dir = MakeProject();
        var before = Directory.GetFiles(dir).ToDictionary(f => f, File.ReadAllBytes);
        var project = BattleProject.Load(dir, TestPaths);
        Assert.Equal((8, 6, 8, 4), (project.Width, project.Height, project.LandPerCell, project.SeaPerCell));
        Assert.Empty(project.ChangedParts());
        Assert.DoesNotContain(project.Notes, n => n.Contains("differ"));
        var result = project.Save(Path.Combine(dir, "_backups"));
        Assert.Empty(result.Written);
        foreach (var (file, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void Project_PaintAndHeightEdits_SaveBothCopiesAndReload()
    {
        if (!Directory.Exists(TileDb)) return;
        var dir = MakeProject();
        var project = BattleProject.Load(dir, TestPaths);

        var paint = new TilePaintTool { Rgb = BattlePalette.Pack(0, 128, 255), RadiusCells = 0.5 };   // sea
        paint.Begin(project, 3.5 * 8, 2.5 * 8);
        paint.Dab(3.5 * 8, 2.5 * 8);
        Assert.NotNull(paint.End());
        Assert.Equal(BattlePalette.Pack(0, 128, 255), BattleProject.RgbAt(project.TileMap, 3, 2));
        Assert.Equal(BattlePalette.Pack(120, 150, 70), BattleProject.RgbAt(project.TileMap, 4, 2));   // exact, no spill

        var raise = new HeightTool(new HeightBrush { Mode = HeightMode.Raise, Strength = 1 }) { RadiusCells = 1 };
        raise.Begin(project, 20, 20);
        raise.Dab(20, 20);
        raise.End();
        Assert.Equal(["tile_map", "land"], project.ChangedParts());

        var result = project.Save(Path.Combine(dir, "_backups"));
        Assert.Equal(3, result.Written.Count);           // tile_map.png + both land copies
        Assert.NotNull(result.BackupDir);
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "lf_heights.tif")), File.ReadAllBytes(Path.Combine(dir, "t.height.aaa.tif")));
        Assert.Empty(project.ChangedParts());

        var reloaded = BattleProject.Load(dir, TestPaths);
        Assert.Equal(BattlePalette.Pack(0, 128, 255), BattleProject.RgbAt(reloaded.TileMap, 3, 2));
        Assert.Equal(project.Land.Data, reloaded.Land.Data);
        Assert.Equal(0u, BattleProject.RgbAt(reloaded.TileMap, 0, 0));
    }

    [Fact]
    public void Project_ObjectEdits_SaveLayerAndExplicitTiles()
    {
        if (!Directory.Exists(TileDb)) return;
        var dir = MakeProject();
        // Give the project a catchment layer, referenced from the .terry like a real one.
        var terry = Path.Combine(dir, "t.terry");
        File.WriteAllText(terry, File.ReadAllText(terry).Replace("<pc type=\"QTU::Terrain\">", """
            <pc type="QTU::Scene"><data version="35">
              <entity id="1b0000000000000" name="catchments"><ECLayerFile/><ECLayer/><ECLayerExport export="true"/></entity>
            </data></pc>
            <pc type="QTU::Terrain">
            """));
        var layer = BattleCatchmentLayer.NewLayer();
        BattleCatchmentLayer.Write(layer, [new BattleCatchment("1a0000000000001", ["land_ambush"], "", "", (3, 3), new CellBox(1, 1, 5, 4), null)], 8, 6);
        BattleCatchmentLayer.Save(layer, Path.Combine(dir, "t.1b0000000000000.layer"));

        var project = BattleProject.Load(dir, TestPaths);
        Assert.Single(project.Catchments);
        Assert.Empty(project.ChangedParts());

        var edit = ListEdit<BattleCatchment>.Apply(project.Catchments, "move", l => l[0] = l[0] with { Box = l[0].Box.Offset(1, 1), Centre = (4, 4) });
        ListEdit<ExplicitTile>.Apply(project.ExplicitTiles, "rotate", l => l[0] = l[0] with { Rotation = 180 });
        Assert.Equal(["explicit_tiles", "catchments"], project.ChangedParts());
        edit!.Undo();
        Assert.Equal(["explicit_tiles"], project.ChangedParts());
        edit.Redo();

        var result = project.Save(Path.Combine(dir, "_backups"));
        Assert.Equal(2, result.Written.Count);
        Assert.Empty(project.ChangedParts());

        var reloaded = BattleProject.Load(dir, TestPaths);
        Assert.Equal(new CellBox(2, 2, 6, 5), reloaded.Catchments[0].Box);
        Assert.Equal((4, 4), reloaded.Catchments[0].Centre);
        Assert.Equal("1a0000000000001", reloaded.Catchments[0].Id);
        Assert.Equal(180, reloaded.ExplicitTiles[0].Rotation);
        Assert.EndsWith("\r\n", File.ReadAllText(Path.Combine(dir, "explicit_tiles.txt")));
    }

    [Fact]
    public void PaintAndFill_Undo_RestoresTileMap()
    {
        if (!Directory.Exists(TileDb)) return;
        var project = BattleProject.Load(MakeProject(), TestPaths);
        var original = (uint[])project.TileMap.Data.Clone();

        var fill = TileFill.Fill(project, 5, 5, BattlePalette.Pack(0, 128, 255));
        Assert.NotNull(fill);
        Assert.Equal(47, project.TileMap.Data.Count(p => BattleProject.RgbAt(new Raster<uint>(1, 1, [p]), 0, 0) == BattlePalette.Pack(0, 128, 255)));
        fill!.Undo();
        Assert.Equal(original, project.TileMap.Data);

        var paint = new TilePaintTool { Rgb = BattlePalette.Pack(0, 128, 255), RadiusCells = 3, ReplaceColourUnderStart = true };
        paint.Begin(project, 4, 4);          // starts on the black cell (0,0): only black cells may change
        paint.Dab(4, 4);
        var stroke = paint.End();
        Assert.Equal(BattlePalette.Pack(0, 128, 255), BattleProject.RgbAt(project.TileMap, 0, 0));
        Assert.Equal(BattlePalette.Pack(120, 150, 70), BattleProject.RgbAt(project.TileMap, 1, 0));
        stroke!.Undo();
        Assert.Equal(original, project.TileMap.Data);
    }
}
