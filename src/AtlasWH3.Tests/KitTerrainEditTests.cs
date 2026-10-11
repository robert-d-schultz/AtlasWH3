using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

/// <summary>Scene-editor terrain tools on the kit sources: TIF round trips, height brush math, tree hexes, layered edits, save.</summary>
public class KitTerrainEditTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a3k_kitedit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A Photoshop-like 16-bit TIF: header, IFD first with a private tag, then one strip of pixels.</summary>
    private static byte[] HandMadeGray16(Raster<ushort> r, bool bigEndian)
    {
        var tags = new List<(ushort Tag, ushort Type, uint Count, uint Value)>();
        const int ifdAt = 8, nTags = 9;
        var extraAt = ifdAt + 2 + nTags * 12 + 4;
        var extra = new byte[] { 0x38, 0x42, 0x49, 0x4D, 1, 2, 3, 4, 5, 6, 7, 8 }; // "8BIM..." private blob
        var pixelsAt = (uint)(extraAt + extra.Length);
        var size = (uint)(r.Width * r.Height * 2);
        tags.Add((256, 3, 1, (uint)r.Width));
        tags.Add((257, 3, 1, (uint)r.Height));
        tags.Add((258, 3, 1, 16));
        tags.Add((259, 3, 1, 1));
        tags.Add((262, 3, 1, 1));
        tags.Add((273, 4, 1, pixelsAt));
        tags.Add((278, 3, 1, (uint)r.Height));
        tags.Add((279, 4, 1, size));
        tags.Add((34377, 1, (uint)extra.Length, (uint)extraAt));
        var b = new List<byte>();
        void U16(int v) { if (bigEndian) { b.Add((byte)(v >> 8)); b.Add((byte)v); } else { b.Add((byte)v); b.Add((byte)(v >> 8)); } }
        void U32(uint v) { if (bigEndian) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); } else { U16((int)(v & 0xFFFF)); U16((int)(v >> 16)); } }
        b.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        U32(ifdAt);
        U16(nTags);
        foreach (var (tag, type, count, value) in tags)
        {
            U16(tag); U16(type); U32(count);
            if (type == 3 && count == 1) { U16((int)value); U16(0); }
            else U32(value);
        }
        U32(0);
        b.AddRange(extra);
        foreach (var v in r.Data) U16(v);
        return [.. b];
    }

    private static Raster<ushort> Ramp(int w, int h)
    {
        var r = new Raster<ushort>(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) r[x, y] = (ushort)(10000 + x * 37 + y * 101);
        return r;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gray16_SaveLike_PatchesOnlyPixels(bool bigEndian)
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "h.tif");
        var r = Ramp(23, 17);
        var original = HandMadeGray16(r, bigEndian);
        File.WriteAllBytes(path, original);
        Assert.Equal(r.Data, TiffMap.ReadGray16(path).Data);

        // unchanged round trip: byte for byte
        Assert.True(TiffMap.SaveGray16Like(path, TiffMap.ReadGray16(path)));
        Assert.Equal(original, File.ReadAllBytes(path));

        // an edit changes the two bytes of that pixel and nothing else
        var edited = r.Clone();
        edited[5, 7] = 0xBEEF;
        TiffMap.SaveGray16Like(path, edited);
        var after = File.ReadAllBytes(path);
        Assert.Equal(original.Length, after.Length);
        var diffs = Enumerable.Range(0, after.Length).Where(i => after[i] != original[i]).ToList();
        Assert.InRange(diffs.Count, 1, 2);
        Assert.Equal(edited.Data, TiffMap.ReadGray16(path).Data);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Palette8_SaveLike_KeepsCompressionAndStrips()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "t.tif");
        var map = new Raster<byte>(40, 31);
        for (var i = 0; i < map.Data.Length; i++) map.Data[i] = (byte)(i % 7 == 0 ? 3 : 19);
        var palette = new TiffMap.Palette(new ushort[256], new ushort[256], new ushort[256]);
        palette.R[3] = 0xFF00;
        TiffMap.WritePalette8(path, map, palette, lzw: true, rowsPerStrip: 9);
        var original = File.ReadAllBytes(path);
        var (back, backPalette) = TiffMap.ReadPalette8(path);
        Assert.False(TiffMap.SavePalette8Like(path, back, backPalette));
        Assert.Equal(original, File.ReadAllBytes(path));
        var layout = TiffMap.ReadLayout(path);
        Assert.Equal((5, 9), (layout.Compression, layout.RowsPerStrip));

        // uncompressed tree maps are patched in place
        TiffMap.WritePalette8(path, map, palette, lzw: false);
        original = File.ReadAllBytes(path);
        back[1, 0] = 3;
        Assert.True(TiffMap.SavePalette8Like(path, back, palette));
        Assert.Equal(1, Enumerable.Range(0, original.Length).Count(i => File.ReadAllBytes(path)[i] != original[i]));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Float32_SaveLike_KeepsLayoutAndPixels()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "h.tif");
        var r = new Raster<float>(37, 23);
        for (var i = 0; i < r.Data.Length; i++) r.Data[i] = (i % 11 - 5) * 0.37f;
        TiffMap.WriteFloat32(path, r);
        var layout = TiffMap.ReadLayout(path);
        Assert.Equal((5, 1, 32), (layout.Compression, layout.RowsPerStrip, layout.Bits));   // Terry's: LZW, one row per strip
        Assert.Equal(r.Data, TiffMap.ReadFloat32(path).Data);

        TiffMap.WriteFloat32(path, r, lzw: false, rowsPerStrip: 4);
        r[3, 4] = -12.5f;
        TiffMap.SaveFloat32Like(path, r);
        Assert.Equal((1, 4), (TiffMap.ReadLayout(path).Compression, TiffMap.ReadLayout(path).RowsPerStrip));
        Assert.Equal(r.Data, TiffMap.ReadFloat32(path).Data);
        Directory.Delete(dir, true);
    }

    /// <summary>The fixture maps' tree TIFs: an unchanged save keeps every pixel (the LZW tree map is rewritten, so its
    /// bytes are compared only when it is uncompressed).</summary>
    [Theory]
    [InlineData(TestKits.Iee)]
    [InlineData(TestKits.OldWorld)]
    public void KitTreeTifs_UnchangedSave_KeepsPixels(string mapName)
    {
        var paths = TestKits.Paths(mapName);
        if (!Directory.Exists(paths.AkTerrainDir)) return;
        var dir = TempDir();
        var checkedFiles = 0;
        foreach (var source in Directory.EnumerateFiles(paths.AkTerrainDir, "*.tif").Where(f => Path.GetFileName(f).Contains(".tree.")))
        {
            var copy = Path.Combine(dir, Path.GetFileName(source));
            File.Copy(source, copy);
            var before = File.ReadAllBytes(copy);
            var (map, palette) = TiffMap.ReadPalette8(copy);
            TiffMap.SavePalette8Like(copy, map, palette);
            Assert.Equal(map.Data, TiffMap.ReadPalette8(copy).Indices.Data);
            if (TiffMap.ReadLayout(source).Compression == 1) Assert.Equal(before, File.ReadAllBytes(copy));
            File.Delete(copy);
            checkedFiles++;
        }
        Directory.Delete(dir, true);
        Assert.True(checkedFiles >= 1, $"{checkedFiles} tree TIFs checked in {paths.AkTerrainDir}");
    }

    // ---------------------------------------------------------------- brush math

    private static Raster<float> Flat(int w, int h, float v)
    {
        var r = new Raster<float>(w, h);
        Array.Fill(r.Data, v);
        return r;
    }

    [Fact]
    public void HeightBrush_RaiseLowerSet_AndUndo()
    {
        var r = Flat(64, 64, 2f);
        var brush = new KitHeightBrush { Mode = KitHeightMode.Raise, Radius = 10, Strength = 1, Softness = 0.5, MaxStepPerDab = 0.5 };
        brush.Begin(r, 32, 32);
        var rect = brush.Dab(32, 32);
        Assert.Equal(2.5f, r[32, 32]);                        // hard core: full step
        Assert.Equal(2f, r[32 + 10, 32]);                     // at the radius: untouched
        Assert.True(r[32 + 7, 32] is > 2f and < 2.5f);        // soft edge: part step
        Assert.True(rect.X0 <= 22 && rect.X1 >= 42);
        var undo = brush.End()!;
        undo.Undo();
        Assert.All(r.Data, v => Assert.Equal(2f, v));
        undo.Redo();
        Assert.Equal(2.5f, r[32, 32]);

        brush.Mode = KitHeightMode.SetValue;
        brush.Value = -1.25f;
        brush.Softness = 0;
        brush.Begin(r, 10, 10);
        brush.Dab(10, 10);
        brush.End();
        Assert.Equal(-1.25f, r[10, 10]);
        Assert.Equal(-1.25f, r[10 + 9, 10]);

        brush.Mode = KitHeightMode.Lower;
        brush.Begin(r, 50, 50);
        brush.Dab(50, 50);
        brush.End();
        Assert.Equal(1.5f, r[50, 50]);
    }

    [Fact]
    public void HeightBrush_SmoothFlattensASpike_FlattenPullsToStart()
    {
        var r = Flat(40, 40, 1f);
        r[20, 20] = 10f;
        var brush = new KitHeightBrush { Mode = KitHeightMode.Smooth, Radius = 6, Strength = 1, Softness = 0 };
        brush.Begin(r, 20, 20);
        brush.Dab(20, 20);
        brush.End();
        Assert.True(r[20, 20] < 2.1f, $"spike {r[20, 20]}");      // 3x3 mean of the spike = 2

        var slope = new Raster<float>(40, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 40; x++) slope[x, y] = 0.1f * x;
        brush.Mode = KitHeightMode.Flatten;
        brush.Begin(slope, 20, 20);
        for (var i = 0; i < 20; i++) brush.Dab(20, 20);
        brush.End();
        Assert.InRange(slope[23, 20], 2f, 2.001f);
        Assert.InRange(slope[17, 20], 1.999f, 2f);
    }

    // ---------------------------------------------------------------- layered session

    private const byte Empty = KitTerrainEditSession.NoTreeIndex;

    /// <summary>A project's maps as layer TIFs: Height = base (flat 2) + "top" at opacity 0.5 (zero), HeightSea one flat
    /// layer (-3) at the same size, CampaignTree base + top (both empty), 40 × 31 (20 × 15 hexes); world 100 wide.</summary>
    private static (KitTerrainEditSession Session, string Dir) LayeredSession()
    {
        var dir = TempDir();
        string P(string kind, string id) => Path.Combine(dir, $"m.{kind}.{id}.tif");
        TiffMap.WriteFloat32(P("height", "base"), Flat(80, 60, 2f));
        TiffMap.WriteFloat32(P("height", "top"), Flat(80, 60, 0f));
        TiffMap.WriteFloat32(P("sea_height", "sea"), Flat(80, 60, -3f));
        var trees = new Raster<byte>(40, 31);
        Array.Fill(trees.Data, Empty);
        var r = new ushort[256]; var g = new ushort[256]; var b = new ushort[256];
        r[2] = 0x1100; g[5] = 0x2200;
        var palette = new TiffMap.Palette(r, g, b);
        TiffMap.WritePalette8(P("tree", "tbase"), trees, palette, lzw: true, rowsPerStrip: 5);
        TiffMap.WritePalette8(P("tree", "ttop"), trees, palette, lzw: true, rowsPerStrip: 5);
        var land = new KitTerrainEditSession.MapLayers("Height", 80, 60,
            [new("base", "base", P("height", "base"), 1f), new("top", "top", P("height", "top"), 0.5f)]);
        var sea = new KitTerrainEditSession.MapLayers("HeightSea", 80, 60, [new("sea", "sea", P("sea_height", "sea"), 1f)]);
        var treeLayers = new KitTerrainEditSession.MapLayers("CampaignTree", 40, 31,
            [new("tbase", "base", P("tree", "tbase"), 1f), new("ttop", "top", P("tree", "ttop"), 1f)]);
        var session = new KitTerrainEditSession(new FileJournal(Path.Combine(dir, "journal")), 100,
            land, Flat(80, 60, 2f), sea, () => Flat(80, 60, -3f), treeLayers, trees.Clone(), palette);
        return (session, dir);
    }

    [Fact]
    public void Hexes_CentreAndPixelMapping_RoundTrip()
    {
        var (s, dir) = LayeredSession();
        var g = s.Grid!.Value;
        Assert.Equal((20, 15), (g.Columns, g.Rows));
        for (var c = 0; c < g.Columns; c++)
            for (var row = 0; row < g.Rows; row++)
            {
                var (x, z) = s.HexCentre(c, row);
                Assert.Equal((c, row), s.NearestHex(x + 0.1 * g.HexSize, z - 0.1 * g.HexSize));
                // BOB's own lookup agrees on the centre
                Assert.Equal((c, row), g.HexAt((float)x, (float)z));
                foreach (var (nc, nr) in s.Neighbours(c, row))
                    Assert.Contains((c, row), s.Neighbours(nc, nr));
            }

        // painting a hex sets its 2x2 footprint in the top layer, which is what ReadTreeMap samples
        s.TreeIndex = 2;
        s.RadiusWorld = 0.01;
        var (hx, hz) = s.HexCentre(7, 4);
        s.BeginStroke(KitTarget.Trees, hx, hz);
        var edit = s.EndStroke()!;
        Assert.Equal([s.HexId(7, 4)], edit.Hexes);
        Assert.Equal("ttop", edit.LayerId);
        Assert.Equal(4, s.TreeMap!.Data.Count(v => v == 2));
        var colours = CampaignTreeGenerator.ReadTreeMap(s.TreeMap, s.TreePalette!, g, Empty);
        Assert.Equal(0x110000, colours[s.HexId(7, 4)]);
        Assert.Equal(1, colours.Count(c => c != CampaignTreeGenerator.NoTree));
        Assert.Equal(1, s.TreeCounts()[2]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Trees_EraseInTheTopLayer_ShowsTheLayerBelow()
    {
        var (s, dir) = LayeredSession();
        s.RadiusWorld = 0.01;
        var (hx, hz) = s.HexCentre(4, 6);
        s.SetLayer(KitTarget.Trees, "tbase");
        s.TreeIndex = 5;
        s.BeginStroke(KitTarget.Trees, hx, hz);
        s.EndStroke();
        s.SetLayer(KitTarget.Trees, "ttop");
        s.TreeIndex = 2;
        s.BeginStroke(KitTarget.Trees, hx, hz);
        s.EndStroke();
        Assert.Equal(2, s.HexIndex(4, 6));                         // the top layer wins
        s.BeginStroke(KitTarget.Trees, hx, hz, erase: true);
        s.EndStroke();
        Assert.Equal(5, s.HexIndex(4, 6));                         // cleared: the base layer's tree shows
        s.Undo();
        Assert.Equal(2, s.HexIndex(4, 6));

        // saving writes both tree layers; they composite to what the session shows
        var (files, _) = s.Save("trees");
        Assert.Equal(2, files.Count);
        var bottom = TiffMap.ReadPalette8(files.Single(f => f.Contains("tbase"))).Indices;
        var top = TiffMap.ReadPalette8(files.Single(f => f.Contains("ttop"))).Indices;
        Assert.Equal(s.TreeMap!.Data, bottom.Data.Zip(top.Data, (lo, hi) => hi != Empty ? hi : lo).ToArray());
        Assert.Equal(5, TiffMap.ReadLayout(files[0]).RowsPerStrip);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Trees_FillConnected_StopsAtOtherSpecies_AndUndoes()
    {
        var (s, dir) = LayeredSession();
        var g = s.Grid!.Value;
        // a wall of species 5 down column 10
        s.TreeIndex = 5;
        s.RadiusWorld = 0.01;
        for (var row = 0; row < g.Rows; row++)
        {
            var (x, z) = s.HexCentre(10, row);
            s.BeginStroke(KitTarget.Trees, x, z);
            s.EndStroke();
        }
        s.TreeIndex = 2;
        var (fx, fz) = s.HexCentre(3, 3);
        var fill = s.FillTrees(fx, fz, TreeFillScope.Connected)!;
        Assert.Equal(10 * g.Rows, fill.Hexes.Length);               // columns 0-9
        Assert.Equal(10 * g.Rows, s.TreeCounts()[2]);
        Assert.Equal(g.Rows, s.TreeCounts()[5]);
        s.Undo();
        Assert.Equal(0, s.TreeCounts()[2]);
        s.Redo();
        Assert.Equal(10 * g.Rows, s.TreeCounts()[2]);

        // region fill, bounded by a fake region map (x < 30 = "west")
        s.TreeIndex = 5;
        s.FillTrees(fx, fz, TreeFillScope.Region, regionAt: (x, _) => x < 30 ? "west" : "east");
        var west = Enumerable.Range(0, g.Columns).Count(c => s.HexCentre(c, 0).X < 30);   // columns 0-5
        Assert.Equal(6, west);
        Assert.Equal((10 - west) * g.Rows, s.TreeCounts()[2]);
        Assert.Equal(g.Rows + west * g.Rows, s.TreeCounts()[5]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Session_LandStroke_GoesIntoTheLayerByOpacity_SaveAndJournal()
    {
        var (s, dir) = LayeredSession();
        Assert.False(s.IsDirty());
        Assert.Equal("top", s.LayerOf(KitTarget.Land)!.Id);          // the top layer by default
        s.HeightBrush.Mode = KitHeightMode.Raise;
        s.HeightBrush.Strength = 1;
        s.HeightBrush.Softness = 0;
        s.HeightBrush.MaxStepPerDab = 0.25;
        s.RadiusWorld = 5;
        s.BeginStroke(KitTarget.Land, 50, 40);
        var edit = s.EndStroke()!;
        Assert.Equal((KitTarget.Land, "top"), (edit.Target, edit.LayerId));
        Assert.NotEmpty(edit.Hexes);                                 // trees there follow the ground
        Assert.True(s.IsDirty(KitTarget.Land));
        Assert.False(s.IsDirty(KitTarget.Sea));
        Assert.Equal(2.25f, s.ValueAt(KitTarget.Land, 50, 40));

        var (files, seq) = s.Save("test");
        Assert.Equal(1, seq);
        Assert.Equal([s.PathOf(KitTarget.Land)!], files);             // only the edited layer
        Assert.False(s.IsDirty());
        var top = TiffMap.ReadFloat32(files[0]);
        Assert.Equal(0.5f, top.Data.Max());                          // 0.25 on the composite at opacity 0.5
        var bottom = TiffMap.ReadFloat32(Path.Combine(dir, "m.height.base.tif"));
        Assert.Equal(s.Land.Data, bottom.Data.Zip(top.Data, (a, b) => a + 0.5f * b).ToArray());
        Assert.Single(s.Journal.History());

        // undo past the save is dirty again; a file changed by another tool blocks the save
        s.Undo();
        Assert.True(s.IsDirty(KitTarget.Land));
        File.SetLastWriteTimeUtc(s.PathOf(KitTarget.Land)!, DateTime.UtcNow.AddMinutes(5));
        Assert.Throws<InvalidOperationException>(() => s.Save("blocked"));
        s.Save("forced", force: true);
        Assert.Equal(0f, TiffMap.ReadFloat32(s.PathOf(KitTarget.Land)!).Data.Max());

        // another layer: its own dirty state and file
        s.SetLayer(KitTarget.Land, "base");
        s.BeginStroke(KitTarget.Land, 20, 20);
        s.EndStroke();
        Assert.Equal(["m.height.base.tif"], s.DirtyLayers().Select(l => Path.GetFileName(l.Path)));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Session_SeaTarget_LoadsOnFirstUse()
    {
        var (s, dir) = LayeredSession();
        Assert.Null(s.SeaIfLoaded);
        Assert.Null(s.ValueAt(KitTarget.Sea, 50, 40));
        s.HeightBrush.Mode = KitHeightMode.SetValue;
        s.HeightBrush.Value = -5;
        s.HeightBrush.Strength = 1;
        s.HeightBrush.Softness = 0;
        s.RadiusWorld = 10;
        var info = s.BeginStroke(KitTarget.Sea, 50, 40)!;
        s.EndStroke();
        Assert.Empty(info.Hexes);                                    // trees stand on the Height map
        Assert.Equal(-5f, s.ValueAt(KitTarget.Sea, 50, 40));
        Assert.Equal(-3f, s.ValueAt(KitTarget.Sea, 50 + 12, 40));
        Assert.All(s.Land.Data, v => Assert.Equal(2f, v));
        Assert.Equal(-5f, TiffMap.ReadFloat32(s.Save("sea").Files.Single()).Data.Min());
        Directory.Delete(dir, true);
    }
}
