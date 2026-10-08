using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

/// <summary>Scene-editor terrain tools on the kit sources: TIF round trips, height brush math, tree hexes, save.</summary>
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

    /// <summary>The kits' own TIFs (vanilla and 190E, when installed): an unchanged save is byte-identical (heights are
    /// patched in place; the LZW tree map is rewritten, so only its pixels are compared when it was not written by us).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KitTifs_UnchangedSave_IsByteIdentical(bool expanded)
    {
        var paths = expanded ? TestKits.VanillaPaths with { AssemblyKitRoot = TestKits.Expanded } : TestKits.VanillaPaths;
        if (!Directory.Exists(paths.AkTerrainDir)) return;
        var dir = TempDir();
        var checkedFiles = 0;
        foreach (var source in Directory.EnumerateFiles(paths.AkTerrainDir, "*.tif")
                     .Where(f => Path.GetFileName(f).Contains(".height.") || Path.GetFileName(f).Contains(".sea_height.") || Path.GetFileName(f).Contains(".tree.")))
        {
            var copy = Path.Combine(dir, Path.GetFileName(source));
            File.Copy(source, copy);
            var before = File.ReadAllBytes(copy);
            if (copy.Contains(".tree."))
            {
                var (map, palette) = TiffMap.ReadPalette8(copy);
                TiffMap.SavePalette8Like(copy, map, palette);
                Assert.Equal(map.Data, TiffMap.ReadPalette8(copy).Indices.Data);
                if (TiffMap.ReadLayout(source).Compression == 1) Assert.Equal(before, File.ReadAllBytes(copy));
            }
            else
            {
                Assert.True(TiffMap.SaveGray16Like(copy, TiffMap.ReadGray16(copy)));
                Assert.Equal(before, File.ReadAllBytes(copy));
            }
            File.Delete(copy);
            checkedFiles++;
        }
        Directory.Delete(dir, true);
        Assert.True(checkedFiles >= 3, $"{checkedFiles} kit TIFs checked in {paths.AkTerrainDir}");
    }

    // ---------------------------------------------------------------- brush math

    private static Raster<ushort> Flat(int w, int h, ushort v)
    {
        var r = new Raster<ushort>(w, h);
        Array.Fill(r.Data, v);
        return r;
    }

    [Fact]
    public void HeightBrush_RaiseLowerSet_AndUndo()
    {
        var r = Flat(64, 64, 20000);
        var brush = new KitHeightBrush { Mode = KitHeightMode.Raise, Radius = 10, Strength = 1, Softness = 0.5, MaxStepPerDab = 500 };
        brush.Begin(r, 32, 32);
        var rect = brush.Dab(32, 32);
        Assert.Equal(20500, r[32, 32]);                       // hard core: full step
        Assert.Equal(20000, r[32 + 10, 32]);                  // at the radius: untouched
        Assert.True(r[32 + 7, 32] is > 20000 and < 20500);     // soft edge: part step
        Assert.True(rect.X0 <= 22 && rect.X1 >= 42);
        var undo = brush.End()!;
        undo.Undo();
        Assert.All(r.Data, v => Assert.Equal(20000, v));
        undo.Redo();
        Assert.Equal(20500, r[32, 32]);

        brush.Mode = KitHeightMode.SetValue;
        brush.Value = KitTerrainEditSession.SeaLevel;
        brush.Softness = 0;
        brush.Begin(r, 10, 10);
        brush.Dab(10, 10);
        brush.End();
        Assert.Equal(KitTerrainEditSession.SeaLevel, r[10, 10]);
        Assert.Equal(KitTerrainEditSession.SeaLevel, r[10 + 9, 10]);

        brush.Mode = KitHeightMode.Lower;
        brush.Begin(r, 50, 50);
        brush.Dab(50, 50);
        brush.End();
        Assert.Equal(19500, r[50, 50]);
    }

    [Fact]
    public void HeightBrush_SmoothFlattensASpike_FlattenPullsToStart()
    {
        var r = Flat(40, 40, 10000);
        r[20, 20] = 30000;
        var brush = new KitHeightBrush { Mode = KitHeightMode.Smooth, Radius = 6, Strength = 1, Softness = 0 };
        brush.Begin(r, 20, 20);
        brush.Dab(20, 20);
        brush.End();
        Assert.True(r[20, 20] < 13000, $"spike {r[20, 20]}");     // 3x3 mean of the spike = 12222

        var slope = new Raster<ushort>(40, 40);
        for (var y = 0; y < 40; y++) for (var x = 0; x < 40; x++) slope[x, y] = (ushort)(10000 + 100 * x);
        brush.Mode = KitHeightMode.Flatten;
        brush.Begin(slope, 20, 20);
        for (var i = 0; i < 20; i++) brush.Dab(20, 20);
        brush.End();
        Assert.InRange(slope[23, 20], 12000, 12010);
        Assert.InRange(slope[17, 20], 11990, 12000);
    }

    // ---------------------------------------------------------------- tree hexes

    private static (KitTerrainEditSession Session, string Dir) TreeSession(int mapW = 40, int mapH = 31)
    {
        var dir = TempDir();
        var land = Flat(80, 60, 20000);
        var sea = Flat(40, 30, KitTerrainEditSession.SeaLevel);
        var map = new Raster<byte>(mapW, mapH);
        Array.Fill(map.Data, KitTerrainEditSession.NoTreeIndex);
        var r = new ushort[256]; var g = new ushort[256]; var b = new ushort[256];
        r[2] = 0x1100; g[5] = 0x2200;
        var palette = new TiffMap.Palette(r, g, b);
        string landPath = Path.Combine(dir, "m.height.1.tif"), seaPath = Path.Combine(dir, "m.sea_height.2.tif"), treePath = Path.Combine(dir, "m.tree.3.tif");
        TiffMap.WriteGray16(landPath, land);
        TiffMap.WriteGray16(Path.Combine(dir, "lf_heights.tif"), land);
        TiffMap.WriteGray16(seaPath, sea);
        TiffMap.WritePalette8(treePath, map, palette, lzw: true);
        var session = new KitTerrainEditSession(new FileJournal(Path.Combine(dir, "journal")), 100, 80,
            landPath, TiffMap.ReadGray16(landPath), [Path.Combine(dir, "lf_heights.tif")],
            seaPath, TiffMap.ReadGray16(seaPath), null,
            treePath, TiffMap.ReadPalette8(treePath).Indices, palette);
        return (session, dir);
    }

    [Fact]
    public void Hexes_CentreAndPixelMapping_RoundTrip()
    {
        var (s, dir) = TreeSession();
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

        // painting a hex sets its 2x2 footprint, which is what ReadTreeMap samples
        s.TreeIndex = 2;
        s.RadiusWorld = 0.01;
        var (hx, hz) = s.HexCentre(7, 4);
        s.BeginStroke(KitTarget.Trees, hx, hz);
        var edit = s.EndStroke()!;
        Assert.Equal([s.HexId(7, 4)], edit.Hexes);
        Assert.Equal(4, s.TreeMap!.Data.Count(v => v == 2));
        var colours = CampaignTreeGenerator.ReadTreeMap(s.TreeMap, s.TreePalette!, g, KitTerrainEditSession.NoTreeIndex);
        Assert.Equal(0x110000, colours[s.HexId(7, 4)]);
        Assert.Equal(1, colours.Count(c => c != CampaignTreeGenerator.NoTree));
        Assert.Equal(1, s.TreeCounts()[2]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Trees_FillConnected_StopsAtOtherSpecies_AndUndoes()
    {
        var (s, dir) = TreeSession();
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

    // ---------------------------------------------------------------- session save

    [Fact]
    public void Session_DirtyTracking_SaveWithMirrorsAndJournal()
    {
        var (s, dir) = TreeSession();
        Assert.False(s.IsDirty());
        s.HeightBrush.Mode = KitHeightMode.Raise;
        s.HeightBrush.Strength = 1;
        s.RadiusWorld = 5;
        s.BeginStroke(KitTarget.Land, 50, 40);
        s.StrokeTo(60, 40);
        var edit = s.EndStroke()!;
        Assert.Equal(KitTarget.Land, edit.Target);
        Assert.NotEmpty(edit.Hexes);                                 // trees there follow the ground
        Assert.True(s.IsDirty(KitTarget.Land));
        Assert.False(s.IsDirty(KitTarget.Sea));

        var (files, seq) = s.Save("test");
        Assert.Equal(1, seq);
        Assert.Equal(2, files.Count);                                // the layer TIF and lf_heights.tif
        Assert.False(s.IsDirty());
        var saved = TiffMap.ReadGray16(s.PathOf(KitTarget.Land)!);
        Assert.Equal(s.Land.Data, saved.Data);
        Assert.Equal(s.Land.Data, TiffMap.ReadGray16(Path.Combine(dir, "lf_heights.tif")).Data);
        Assert.Single(s.Journal.History());

        // undo past the save is dirty again; a file changed by another tool blocks the save
        s.Undo();
        Assert.True(s.IsDirty(KitTarget.Land));
        File.SetLastWriteTimeUtc(s.PathOf(KitTarget.Land)!, DateTime.UtcNow.AddMinutes(5));
        Assert.Throws<InvalidOperationException>(() => s.Save("blocked"));
        s.Save("forced", force: true);
        Assert.Equal(20000, TiffMap.ReadGray16(s.PathOf(KitTarget.Land)!).Data.Max());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Session_SeaTarget_UsesItsOwnResolution()
    {
        var (s, dir) = TreeSession();
        s.HeightBrush.Mode = KitHeightMode.SetValue;
        s.HeightBrush.Value = 15000;
        s.HeightBrush.Strength = 1;
        s.HeightBrush.Softness = 0;
        s.RadiusWorld = 10;                                          // sea: 0.4 px per world unit → radius 4 px
        s.BeginStroke(KitTarget.Sea, 50, 40);
        s.EndStroke();
        Assert.Equal((ushort)15000, s.RawAt(KitTarget.Sea, 50, 40));
        Assert.Equal(KitTerrainEditSession.SeaLevel, s.RawAt(KitTarget.Sea, 50 + 12, 40));
        Assert.All(s.Land.Data, v => Assert.Equal(20000, v));
        Directory.Delete(dir, true);
    }
}
