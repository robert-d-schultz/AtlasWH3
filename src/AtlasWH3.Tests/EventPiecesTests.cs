using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;
using static AtlasWH3.Formats.Maps.EventPieces;

namespace AtlasWH3.Tests;

/// <summary>Event-area pieces (BOB's Devastation pieces): texture_info, crops, mask, and the step against the IEE pack.</summary>
public class EventPiecesTests
{
    // IEE's map textures (12800 x 7764 at 4 px per event-mask pixel, the masks at 1, the shroud at 2)
    private static readonly TextureLayout Height = new(12800, 7764, 4, 16, 1, 148), SeaColour = new(12800, 7764, 4, 8, 14, 148),
        Normal = new(12800, 7764, 4, 16, 14, 128), Corruption = new(3200, 1941, 1, 1, 12, 148), TileMask = new(3200, 1941, 1, 1, 1, 128),
        Snow = new(3204, 1944, 4, 8, 12, 148), Shroud = new(6400, 3882, 1, 4, 1, 148), Blend = new(12800, 7764, 1, 1, 1, 128);

    /// <summary>IEE's event_0006dc (helspire mountains crater) as BOB wrote it: 64-px grid for mipped block textures, 8 for
    /// mipped R8, flipped rows for the height map and tile mask, the last pixel's start + 1 for the unmipped ones.</summary>
    [Fact]
    public void TextureInfo_MatchesBob()
    {
        var box = new PieceBox(782, 170, 348, 152);
        var layouts = new[] { Height, SeaColour, Normal, Corruption, TileMask, Snow, Shroud, SeaColour, Blend };
        var textures = Textures.Select((t, i) => Layout(t.Name, t.Flipped, layouts[i], 3200, 1941, box));
        const string expected =
            "mask 3200 1941 782 170 348 152\r\nfull_height_map.dds 1 1\r\n3128 6476 1392 608 16 348 152 148\r\n" +
            "lf_sea_colour.dds 10 0\r\n3072 640 1472 704 8 368 176 148\r\n1536 320 736 352 8 184 88 518292\r\n" +
            "768 160 368 176 8 92 44 647828\r\n384 80 184 88 8 46 22 680212\r\n192 40 92 44 8 23 11 688308\r\n" +
            "96 20 48 24 8 12 6 690332\r\n48 8 24 16 8 6 4 690908\r\n24 4 12 8 8 3 2 691100\r\n12 0 8 4 8 2 1 691148\r\n" +
            "4 0 4 4 8 1 1 691164\r\nlf_normal.dds 10 0\r\n3072 640 1472 704 16 368 176 128\r\n1536 320 736 352 16 184 88 1036416\r\n" +
            "768 160 368 176 16 92 44 1295488\r\n384 80 184 88 16 46 22 1360256\r\n192 40 92 44 16 23 11 1376448\r\n" +
            "96 20 48 24 16 12 6 1380496\r\n48 8 24 16 16 6 4 1381648\r\n24 4 12 8 16 3 2 1382032\r\n12 0 8 4 16 2 1 1382128\r\n" +
            "4 0 4 4 16 1 1 1382160\r\ncorruption_mask.dds 8 0\r\n776 168 360 160 1 360 160 148\r\n388 84 180 80 1 180 80 57748\r\n" +
            "194 42 90 40 1 90 40 72148\r\n97 21 45 20 1 45 20 75748\r\n48 10 22 10 1 22 10 76648\r\n24 5 11 5 1 11 5 76868\r\n" +
            "12 2 5 2 1 5 2 76923\r\n6 1 2 1 1 2 1 76933\r\ntile_mask.dds 1 1\r\n782 1619 348 152 1 348 152 128\r\n" +
            "snow_mask.dds 9 0\r\n768 128 384 256 8 96 64 148\r\n384 64 192 128 8 48 32 49300\r\n192 32 96 64 8 24 16 61588\r\n" +
            "96 16 48 32 8 12 8 64660\r\n48 8 24 16 8 6 4 65428\r\n24 4 12 8 8 3 2 65620\r\n12 0 8 8 8 2 2 65668\r\n" +
            "4 0 8 4 8 2 1 65700\r\n0 0 4 4 8 1 1 65716\r\nshroud_heights.dds 1 0\r\n1564 340 695 303 4 695 303 148\r\n" +
            "colour_overlay.dds 10 0\r\n3072 640 1472 704 8 368 176 148\r\n1536 320 736 352 8 184 88 518292\r\n" +
            "768 160 368 176 8 92 44 647828\r\n384 80 184 88 8 46 22 680212\r\n192 40 92 44 8 23 11 688308\r\n" +
            "96 20 48 24 8 12 6 690332\r\n48 8 24 16 8 6 4 690908\r\n24 4 12 8 8 3 2 691100\r\n12 0 8 4 8 2 1 691148\r\n" +
            "4 0 4 4 8 1 1 691164\r\nglobal_blend.dds 1 0\r\n3128 680 1389 605 1 1389 605 128\r\n";
        Assert.Equal(expected, TextureInfo(3200, 1941, box, textures));
    }

    /// <summary>snow_mask.dds is stretched (3204 × 1944 over the 3200 × 1941 mask), so its box scales by a fraction:
    /// IEE's event_12e353 starts at row 704, not the 640 a scale of 1 would give.</summary>
    [Fact]
    public void Layout_ScalesTheStretchedSnowMask()
    {
        var snow = Layout("snow_mask.dds", false, Snow, 3200, 1941, new PieceBox(1273, 703, 63, 58));
        Assert.Equal(
            [new(1216, 704, 128, 64, 8, 32, 16, 148), new(608, 352, 64, 32, 8, 16, 8, 4244), new(304, 176, 32, 16, 8, 8, 4, 5268),
             new(152, 88, 16, 8, 8, 4, 2, 5524), new(76, 44, 8, 4, 8, 2, 1, 5588), new(36, 20, 8, 4, 8, 2, 1, 5604),
             new PieceMip(16, 8, 8, 4, 8, 2, 1, 5620)], snow.Mips);
    }

    /// <summary>A crop is copied block row by block row, reading on linearly past the map's right edge (into the next
    /// row) and its end (zeros), as BOB does; the header takes the crop's size and mip count.</summary>
    [Fact]
    public void Crop_ReadsLinearlyPastTheEdge()
    {
        // a 4 x 3 R8 map, one level, pixel = its index
        var header = AtlasWH3.Formats.Dds.DdsHeader.BuildDx10(4, 3, AtlasWH3.Formats.Dds.DdsHeader.DxgiR8Unorm, false, 1);
        var map = header.Concat(Enumerable.Range(1, 12).Select(i => (byte)i)).ToArray();
        var layout = TextureLayout.Of(map);
        var piece = new PieceTexture("x.dds", false, [new PieceMip(2, 1, 3, 3, 1, 3, 3, 148)]);
        var crop = Crop(map, layout, piece);
        Assert.Equal(new byte[] { 7, 8, 9, 11, 12, 0, 0, 0, 0 }, crop[148..]);
        Assert.Equal(3, BitConverter.ToInt32(crop, 12));   // height
        Assert.Equal(3, BitConverter.ToInt32(crop, 16));   // width
        Assert.Equal(1, BitConverter.ToInt32(crop, 28));   // mips
    }

    [Fact]
    public void Mask_IsLsbFirstPaddedTo4Bytes()
    {
        byte[] indices = [9, 9, 9, 9,
                          9, 5, 9, 5,
                          9, 5, 5, 9];
        var mask = EventPieces.Mask(indices, 4, new PieceBox(1, 1, 3, 2), 5);   // bits: 1 0 1 / 1 1 0
        Assert.Equal(new byte[] { 0b011101, 0, 0, 0 }, mask);
        Assert.Equal(new PieceBox(1, 1, 3, 2), Boxes(indices, 4)[5]);
    }

    /// <summary>
    /// The step's writer against BOB's pieces in IEE's mod pack (extracted with RPFM to %LocalAppData%\AtlasWH3\pack_refs\iee;
    /// skipped without it), cutting from the pack's own map textures: every texture, texture_info and mask
    /// byte-identical, every road list the same tiles (event_tiles is numbered in BOB's hash-map order).
    /// </summary>
    [Fact]
    public void Write_MatchesIeePack()
    {
        var reference = Path.Combine(Defaults.LocalData, "pack_refs", "iee", "terrain", "campaigns", "cr_combi_expanded_map_1");
        var paths = new ProjectPaths { MapName = "cr_combi_expanded_map_1", AssemblyKitRoot = TestKits.Wh3Kit, GameDataDir = TestKits.Wh3GameData };
        var ctx = new CampaignBuildContext(paths, Path.Combine(Path.GetTempPath(), "atlaswh3_pieces_test"));
        if (!Directory.Exists(reference) || !File.Exists(ctx.TerryFile)) return;
        try
        {
            List<string> written = [], notes = [];
            DevastationPiecesStep.Write(TerryProject.Load(ctx.TerryFile), rel => Path.Combine(reference, rel) is var p && File.Exists(p) ? p : null,
                null, ctx.TerrainOutDir, () => TileMapValidator.LoadDatabase(paths), ctx, written, notes);
            var mine = Path.Combine(ctx.TerrainOutDir, "pieces");
            var theirs = Directory.GetDirectories(Path.Combine(reference, "pieces")).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(theirs, Directory.GetDirectories(mine).Select(Path.GetFileName).Order().ToList());
            var ourTiles = File.ReadAllLines(Path.Combine(ctx.TerrainOutDir, "event_tiles"));
            var bobTiles = File.ReadAllLines(Path.Combine(reference, "event_tiles"));
            foreach (var piece in theirs)
            {
                foreach (var file in Textures.Select(t => t.Name).Append("texture_info").Append("mask"))
                    Assert.True(File.ReadAllBytes(Path.Combine(reference, "pieces", piece!, file))
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(mine, piece!, file))), $"{piece}\\{file}");
                var bob = Path.Combine(reference, "pieces", piece!, "tile_list");
                Assert.Equal(File.Exists(bob), File.Exists(Path.Combine(mine, piece!, "tile_list")));
                if (File.Exists(bob))
                    Assert.Equal(ReadTileList(File.ReadAllBytes(bob)).Select(t => (t.X, t.Y, bobTiles[t.Index])),
                                 ReadTileList(File.ReadAllBytes(Path.Combine(mine, piece!, "tile_list"))).Select(t => (t.X, t.Y, ourTiles[t.Index])));
            }
        }
        finally { if (Directory.Exists(ctx.TargetRoot)) Directory.Delete(ctx.TargetRoot, true); }
    }
}
