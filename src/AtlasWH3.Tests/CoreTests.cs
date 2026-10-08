using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Operations;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Tests;

public class CoreTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;

    [Fact]
    public void TreeList_RoundTrips_Synthetic()
    {
        var list = new CampaignTreeList { WorldWidth = 595.1f, WorldHeight = 541.786f };
        var type = new TreeType { Name = "bamboo_1" };
        type.Instances.Add(new TreeInstance { X = 1, Y = 2, Z = 3, Flag = 1, Variant = 4, Seasons = [0, 1, 2, 3, 4] });
        type.Instances.Add(new TreeInstance { X = 5, Y = 6, Z = 7, Flag = 1, Variant = 0, Seasons = [CampaignTreeList.NoSeason] });
        list.Types.Add(type);

        var bytes = list.ToBytes();
        var back = CampaignTreeList.Read(bytes);
        Assert.Equal(bytes, back.ToBytes());
        Assert.Equal(2, back.TotalInstances);
        Assert.Equal(CampaignTreeList.NoSeason, back.Types[0].Instances[1].Seasons[0]);
    }

    [Fact]
    public void TreeList_RoundTrips_Vanilla()
    {
        if (!File.Exists(Paths.TreeList)) return; // data not available on this machine
        var original = File.ReadAllBytes(Paths.TreeList);
        Assert.Equal(original, CampaignTreeList.Read(original).ToBytes());
    }

    [Fact]
    public void LayerShifter_MovesPositions_AndSkipsGeneratedModels()
    {
        const string layer = """
            <layer version="35"><entities>
            <entity id="1"><ECMesh model_path="terrain/campaigns/x/models/river_3.wsmodel"/><ECTransform position="0 0 0" rotation="0 0 0"/></entity>
            <entity id="2"><ECTransform position="236.556076 6.36137009 458.75647" rotation="0 0 0"/></entity>
            </entities></layer>
            """;
        var (text, stats) = LayerShifter.Shift(layer, 10, -5);
        Assert.Equal(1, stats.Shifted);
        Assert.Equal(1, stats.SkippedGenerated);
        Assert.Contains("position=\"0 0 0\"", text);
        Assert.Contains("position=\"246.55608 6.36137009 453.75647\"", text);
    }

    [Fact]
    public void Expand_KeepsContentOnSamePixel()
    {
        var terrain = new TerrainData(
            new Raster<ushort>(80, 64), new Raster<ushort>(40, 32), new Raster<byte>(80, 64), new Raster<byte>(80, 64),
            null!, new WorldCoords(80 * 0.0834f, 64 * 0.0964f));
        terrain.Height[10, 20] = 5000;
        var trees = new CampaignTreeList { WorldWidth = terrain.Coords.WorldWidth, WorldHeight = terrain.Coords.WorldHeight };
        var (wx, wz) = terrain.Coords.ToWorld(10.5, 20.5, 80, 64);
        var type = new TreeType { Name = "t" };
        type.Instances.Add(new TreeInstance { X = (float)wx, Z = (float)wz, Seasons = [0] });
        trees.Types.Add(type);

        ExpandCanvas.Apply(terrain, trees, new HexPadding(2, 1, 1, 3));

        Assert.Equal(80 + 3 * 8, terrain.Width);
        Assert.Equal(64 + 4 * 8, terrain.HeightPx);
        Assert.Equal(40 + 3 * 4, terrain.SeaHeight.Width);
        Assert.Equal(5000, terrain.Height[10 + 16, 20 + 8]);
        var inst = trees.Types[0].Instances[0];
        var (c, r) = terrain.Coords.ToPixel(inst.X, inst.Z, terrain.Width, terrain.HeightPx);
        Assert.Equal(10.5 + 16, c, 3);
        Assert.Equal(20.5 + 8, r, 3);
    }

    [Fact]
    public void HeightBrush_UndoRedo_RestoresExactly()
    {
        var terrain = new TerrainData(
            new Raster<ushort>(600, 600), new Raster<ushort>(300, 300), new Raster<byte>(600, 600), new Raster<byte>(600, 600),
            null!, new WorldCoords(50, 50));
        Array.Fill(terrain.Height.Data, (ushort)20000);
        var original = (ushort[])terrain.Height.Data.Clone();

        var brush = new HeightBrush { Mode = HeightMode.Raise, Radius = 60, Strength = 1 };
        brush.Begin(terrain, 250, 250);
        brush.Dab(250, 250);
        brush.Dab(300, 260);
        var undo = brush.End()!;
        var edited = (ushort[])terrain.Height.Data.Clone();
        Assert.NotEqual(original, edited);

        undo.Undo();
        Assert.Equal(original, terrain.Height.Data);
        undo.Redo();
        Assert.Equal(edited, terrain.Height.Data);
    }

    [Fact]
    public void Tiff_Gray16_And_Palette8_RoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("terryclone_tests").FullName;
        var gray = new Raster<ushort>(37, 23);
        for (var i = 0; i < gray.Data.Length; i++) gray.Data[i] = (ushort)(i * 97);
        TiffMap.WriteGray16(Path.Combine(dir, "g.tif"), gray);
        Assert.Equal(gray.Data, TiffMap.ReadGray16(Path.Combine(dir, "g.tif")).Data);

        var idx = new Raster<byte>(31, 17);
        for (var i = 0; i < idx.Data.Length; i++) idx.Data[i] = (byte)(i % 32);
        var palette = new TiffMap.Palette(
            Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray(),
            new ushort[256], new ushort[256]);
        TiffMap.WritePalette8(Path.Combine(dir, "p.tif"), idx, palette, lzw: true);
        var (back, backPalette) = TiffMap.ReadPalette8(Path.Combine(dir, "p.tif"));
        Assert.Equal(idx.Data, back.Data);
        Assert.Equal(palette.R, backPalette.R);
        Directory.Delete(dir, true);
    }
}
