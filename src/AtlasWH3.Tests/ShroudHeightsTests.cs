using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>shroud_heights.dds filled from the terrain (<see cref="ShroudHeights"/>), against vanilla's.</summary>
public class ShroudHeightsTests
{
    /// <summary>A shroud_heights.dds from the packs, row 0 = north.</summary>
    private static Raster<float>? Shipped(PackSet packs, string map)
    {
        if (packs.TryRead($"terrain/campaigns/{map}/shroud_heights.dds") is not { } dds) return null;
        var hdr = DdsHeader.Read(dds);
        var r = new Raster<float>(hdr.Width, hdr.Height);
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dds.AsSpan(hdr.DataOffset, r.Data.Length * 4));
        for (var y = 0; y < r.Height; y++) src.Slice((r.Height - 1 - y) * r.Width, r.Width).CopyTo(r.Data.AsSpan(y * r.Width));
        return r;
    }

    private static TreeHeightField Field(Raster<float> land, float worldWidth, List<TreeHeightField.Patch> patches)
    {
        var tmp = Path.GetTempFileName();
        try { return new TreeHeightField(HeightmapsStep.WriteLogicMap(land, tmp), worldWidth, patches); }
        finally { File.Delete(tmp); }
    }

    /// <summary>Half resolution, each pixel the Height texel at its north-west corner plus the offset (no patches).</summary>
    [Fact]
    public void Fill_SamplesNorthWestCornerPlusOffset()
    {
        var land = new Raster<float>(5, 4);
        for (var i = 0; i < land.Data.Length; i++) land.Data[i] = i * 0.25f;
        var shroud = ShroudHeights.Compute(land, Field(land, 100f, []));
        Assert.Equal((3, 2), (shroud.Width, shroud.Height));
        for (var r = 0; r < 2; r++)
            for (var c = 0; c < 3; c++)
                Assert.Equal(land.Data[2 * r * 5 + 2 * c] + ShroudHeights.Offset, shroud.Data[r * 3 + c], 1e-4f);
    }

    /// <summary>
    /// The fill against vanilla Immortal Empires' shroud_heights.dds, all from vanilla's own files: the heights from its
    /// full_logic_map, its props' height patches from global_props.bin (3060 props), its width from map_data.esf.
    /// 2026-10-18: 82.9% of pixels within 1e-3, 85.7% within 0.05, 90.8% within 0.5; away from the north and south edges
    /// 88.5% within 1e-3 and 95.7% within 0.5, 91.1% of the pixels the patches raise among them (76.4% of the interior
    /// without the patches). The width matters: IEE's world_width (1068.11, 961.299 over vanilla's columns) places the
    /// patches 0.001 off and loses 2.7 points. The rest is CA's own: walls painted on stretches of the north and south
    /// edges, softer strips along the west and east edges, and ground edited after the fill.
    /// </summary>
    [Fact]
    public void Fill_CloseToVanilla()
    {
        const string map = "wh3_main_combi_map_1";
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        var packs = PackSet.OpenVanilla(TestKits.Wh3GameData);
        var shipped = Shipped(packs, map)!;
        var logic = CompressedMap.Decode(packs.TryRead($"terrain/campaigns/{map}/full_logic_map.compressed_map")!);
        var width = MapDataBounds.Read(EsfTree.Read(packs.TryRead($"campaign_maps/{map}/map_data.esf")!)).Width;
        var patches = TreeHeightField.GlobalPropsPatches(packs.TryRead($"terrain/campaigns/{map}/global_props.bin")!, packs);

        // the logic map is stored south row first
        var (w, h) = (logic.Raster.Width, logic.Raster.Height);
        float lo = logic.Header[1], step = (logic.Header[4] - lo) / 65535f;
        var land = new Raster<float>(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) land.Data[y * w + x] = lo + logic.Raster.Data[(h - 1 - y) * w + x] * step;
        var shroud = ShroudHeights.Compute(land, new TreeHeightField(logic, width, patches));
        Assert.Equal((shipped.Width, shipped.Height), (shroud.Width, shroud.Height));

        int exact = 0, half = 0, inner = 0, innerExact = 0;
        for (var i = 0; i < shroud.Data.Length; i++)
        {
            var d = Math.Abs(shipped.Data[i] - shroud.Data[i]);
            if (d < 1e-3f) exact++;
            if (d < 0.5f) half++;
            if (i / shroud.Width is var r && r >= 200 && r < shroud.Height - 200) { inner++; if (d < 1e-3f) innerExact++; }
        }
        double n = shroud.Data.Length;
        Console.WriteLine($"vanilla combi: {patches.Count} patched props, width {width}; {exact / n:P2} within 1e-3, {half / n:P2} within 0.5, " +
                          $"interior {innerExact / (double)inner:P2} within 1e-3");
        Assert.True(exact / n >= 0.82, $"{exact / n:P2} within 1e-3");
        Assert.True(half / n >= 0.90, $"{half / n:P2} within 0.5");
    }
}
