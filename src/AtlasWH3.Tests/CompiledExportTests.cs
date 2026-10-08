using AtlasWH3.Core;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Tests;

public class CompiledExportTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;
    private static string Vanilla(string relative) => Path.Combine(Paths.TerrainDir, relative);

    [Fact]
    public void CompressedMap_RoundTrips_Synthetic_AllTileModes()
    {
        // 40x20: partial edge tiles; tiles that are constant, palette, base+delta and raw
        var r = new Raster<ushort>(40, 20);
        var rng = new Random(7);
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 40; x++)
            r[x, y] = x switch
            {
                < 16 => 1234,                                    // constant
                < 32 when y < 16 => (ushort)(100 + (x + y) % 5), // palette
                < 32 => (ushort)(5000 + rng.Next(3000)),         // base+delta (12 bits)
                _ => (ushort)rng.Next(65536),                    // raw
            };
        var bytes = CompressedMap.Encode(r, [0, 0, 0, 0, 1, 0]);
        var back = CompressedMap.Decode(bytes);
        Assert.Equal(r.Data, back.Raster.Data);
        Assert.Equal(bytes, CompressedMap.Encode(back.Raster, back.Header));
    }

    [Fact]
    public void CompressedMap_Vanilla_DecodesToDds_AndReencodesByteIdentical()
    {
        foreach (var (cm, dds) in new[] { ("lf_height_map.compressed_map", "lf_height_map.dds"),
                                          ("lf_sea_height_map.compressed_map", "lf_sea_height_map.dds") })
        {
            if (!File.Exists(Vanilla(cm))) return; // data not available on this machine
            var original = File.ReadAllBytes(Vanilla(cm));
            var map = CompressedMap.Decode(original);
            Assert.Equal(TerrainDds.ReadL16(Vanilla(dds)).Data, map.Raster.Data);
            Assert.Equal(original, CompressedMap.Encode(map.Raster, map.Header, map.Version, map.TileWidth));
        }
    }

    [Fact]
    public void CompressedMap_Vanilla_PatchesClimateAndMeshes_ReencodeByteIdentical()
    {
        if (!Directory.Exists(Paths.TerrainDir)) return;
        var files = new[] { Vanilla("climate_map.cm") }
            .Concat(Directory.GetFiles(Vanilla("height_patches"), "*.compressed_map").Take(10))
            .Concat(Directory.GetFiles(Vanilla("global_meshes"), "*.compressed_map").Take(10));
        foreach (var file in files)
        {
            var original = File.ReadAllBytes(file);
            var map = CompressedMap.Decode(original);
            Assert.Equal(original, CompressedMap.Encode(map.Raster, map.Header, map.Version, map.TileWidth));
        }
    }

    [Fact]
    public void Normalise_MatchesBobFormula()
    {
        var src = new Raster<ushort>(4, 1, [0, 14219, 30000, 44217]);
        var (values, header) = CompiledTerrainExporter.Normalise(src);
        // v = trunc(f32(src - lo) / f32(hi - lo) * 65535), lo = 0, hi = 44217
        Assert.Equal(new ushort[] { 0, 21074, 44463, 65535 }, values.Data);
        Assert.Equal(44217 / 65535f, header[4]);
        Assert.Equal(0f, header[1]);

        var flat = CompiledTerrainExporter.Normalise(new Raster<ushort>(2, 1, [14219, 14219]));
        Assert.All(flat.Values.Data, v => Assert.Equal(0, v));
        Assert.Equal(flat.Header[1], flat.Header[4]);
    }

    [Fact]
    public void TerrainDds_WriteL16_MatchesVanillaHeader()
    {
        if (!File.Exists(Paths.HeightMapDds)) return;
        var vanilla = File.ReadAllBytes(Paths.HeightMapDds);
        var temp = Path.GetTempFileName();
        try
        {
            TerrainDds.WriteL16(temp, TerrainDds.ReadL16(Paths.HeightMapDds));
            Assert.Equal(vanilla.AsSpan(0, 128).ToArray(), File.ReadAllBytes(temp).AsSpan(0, 128).ToArray());
        }
        finally { File.Delete(temp); }
    }
}
