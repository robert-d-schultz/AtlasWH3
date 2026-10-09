using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public sealed class TerrainCompositeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atlaswh3_composite").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Map(string type, string size, params (string Id, string Visible, string Opacity)[] layers) =>
        "    <pc type=\"QTU::TerrainMap\">\n" +
        $"      <data type=\"{type}\" size=\"{size}\" id=\"m{type}\"/>\n" +
        string.Concat(layers.Select(l =>
            $"      <pc type=\"QTU::TerrainMapLayer\"><data id=\"{l.Id}\" name=\"n{l.Id}\" visible=\"{l.Visible}\" serializable=\"1\" opacity=\"{l.Opacity}\"/></pc>\n")) +
        "    </pc>\n";

    private TerryProject Project(params string[] maps)
    {
        var path = Path.Combine(_dir, "t.terry");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project version="27" id="1">
              <pc type="QTU::ProjectTileMap">
                <data terrain_setup="terrain/campaigns/t/" world_width="12.5"/>
              </pc>
              <pc type="QTU::Scene">
                <data version="42"/>
              </pc>
              <pc type="QTU::Terrain">
            {string.Concat(maps)}  </pc>
            </project>
            """);
        return TerryProject.Load(path);
    }

    [Fact]
    public void Heights_AddVisibleLayersWithOpacity()
    {
        var p = Project(Map("HeightShroud", "3x2", ("a", "1", "1"), ("b", "1", "0.5"), ("c", "0", "1"), ("d", "1", "0")));
        TiffMap.WriteFloat32(Path.Combine(_dir, "t.height_shroud.a.tif"), new Raster<float>(3, 2, [1, 2, 3, 4, 5, 6]));
        TiffMap.WriteFloat32(Path.Combine(_dir, "t.height_shroud.b.tif"), new Raster<float>(3, 2, [10, 10, 10, -2, 0, 0]));
        // hidden and zero-opacity layers are skipped (their TIFs need not exist)
        Assert.Equal(12.5f, p.WorldWidth);
        Assert.Equal(["na", "nb"], TerrainComposite.Inputs(p, p.Find("HeightShroud")!).Select(i => i.Layer.Name));
        Assert.Equal([6f, 7, 8, 3, 5, 6], TerrainComposite.Heights(p, "HeightShroud").Data);
    }

    [Fact]
    public void Indexed_UpperLayersReplaceExcept255()
    {
        var p = Project(Map("BlendCampaign", "4x1", ("a", "1", "1"), ("b", "1", "1")));
        var palette = new TiffMap.Palette(new ushort[256], new ushort[256], new ushort[256]);
        TiffMap.WritePalette8(Path.Combine(_dir, "t.blend.a.tif"), new Raster<byte>(4, 1, [1, 2, 255, 4]), palette, true);
        TiffMap.WritePalette8(Path.Combine(_dir, "t.blend.b.tif"), new Raster<byte>(4, 1, [255, 9, 255, 0]), palette, true);
        var (indices, pal) = TerrainComposite.Indexed(p, "BlendCampaign");
        Assert.Equal([1, 9, 255, 0], indices.Data);
        Assert.NotNull(pal);
    }

    [Fact]
    public void Mask_IsTheUnionOfItsLayers()
    {
        var p = Project(Map("SnowMask", "3x1", ("a", "1", "1"), ("b", "1", "1")));
        TiffMap.WriteGray8(Path.Combine(_dir, "t.snow_mask.a.tif"), new Raster<byte>(3, 1, [0, 255, 128]));
        TiffMap.WriteGray8(Path.Combine(_dir, "t.snow_mask.b.tif"), new Raster<byte>(3, 1, [51, 0, 128]));
        // 128/255 + 128/255 · (1 − 128/255) = 0.7520 → 191.77, truncated to 191
        Assert.Equal([51, 255, 191], TerrainComposite.Mask(p, "SnowMask").Data);
    }

    [Fact]
    public void Overlay_HardLightsUpperLayersWhereOpaque()
    {
        var p = Project(Map("ColorOverlay", "2x1", ("a", "1", "1"), ("b", "1", "1")));
        TiffMap.WriteRgba8(Path.Combine(_dir, "t.color_overlay.a.tif"), new Raster<uint>(2, 1, [0xFF294018u, 0xFF294018u]));   // (24, 64, 41)
        TiffMap.WriteRgba8(Path.Combine(_dir, "t.color_overlay.b.tif"), new Raster<uint>(2, 1, [0xFFCEF26Bu, 0x00CEF26Bu]));   // (107, 242, 206)
        var o = TerrainComposite.Overlay(p, "ColorOverlay").Data;
        Assert.Equal((TerrainComposite.HardLight(107, 24), TerrainComposite.HardLight(242, 64), TerrainComposite.HardLight(206, 41)),
            ((int)(o[0] & 0xFF), (int)(o[0] >> 8 & 0xFF), (int)(o[0] >> 16 & 0xFF)));
        Assert.Equal(0xFF294018u, o[1]);                       // alpha 0 passes the bottom through
        Assert.Equal(128, TerrainComposite.HardLight(128, 128));
        Assert.InRange(TerrainComposite.HardLight(128, 77), 77, 78); // mid grey is (about) neutral
    }

    /// <summary>BOB's shroud_heights.dds is the composited HeightShroud, flipped; its event area and corruption masks
    /// are the composited maps as they are (the user's IEE project: 2 shroud layers, a hidden corruption layer).</summary>
    [Fact]
    public void Iee_CompositesMatchBob()
    {
        const string map = "cr_combi_expanded_map_1";
        var terry = Path.Combine(TestKits.Wh3Kit, "raw_data", "terrain", "campaigns", map, map + ".terry");
        var built = Path.Combine(TestKits.Wh3Kit, "working_data", "terrain", "campaigns", map);
        if (!File.Exists(terry) || !File.Exists(Path.Combine(built, "shroud_heights.dds"))) return;
        var p = TerryProject.Load(terry);

        var shroud = TerrainComposite.Heights(p, "HeightShroud");
        var dds = File.ReadAllBytes(Path.Combine(built, "shroud_heights.dds"));
        var off = DdsHeader.Read(dds).DataOffset;
        var w = shroud.Width;
        for (var y = 0; y < shroud.Height; y += 7)
            Assert.True(dds.AsSpan(off + (shroud.Height - 1 - y) * w * 4, w * 4)
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(shroud.Data.AsSpan(y * w, w))), $"row {y}");

        foreach (var (type, file, raster) in new[]
                 {
                     ("EventAreaMask", "event_area_mask.dds", TerrainComposite.Indexed(p, "EventAreaMask").Indices),
                     ("CorruptionMask", "corruption_mask.dds", TerrainComposite.Mask(p, "CorruptionMask")),
                 })
        {
            var bytes = File.ReadAllBytes(Path.Combine(built, file));
            Assert.True(bytes.AsSpan(DdsHeader.Read(bytes).DataOffset, raster.Data.Length).SequenceEqual(raster.Data), type);
        }
    }
}
