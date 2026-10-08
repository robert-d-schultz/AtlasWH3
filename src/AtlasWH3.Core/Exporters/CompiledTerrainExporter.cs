using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Exporters;

/// <summary>
/// BOB's normalisation of a u16 height source to its own range: v = trunc(f32(src - lo) / f32(hi - lo) * 65535), with
/// lo/65535 and hi/65535 stored in header floats 1 and 4. (Atlas3K's lf_height_map, lf_sea_height_map and
/// climate_map.cm writers were cut: WH3 ships none of those files.)
/// </summary>
public static class CompiledTerrainExporter
{
    /// <summary>BOB's normalisation of a u16 source to its own min–max range.</summary>
    public static (Raster<ushort> Values, float[] Header) Normalise(Raster<ushort> source)
    {
        ushort lo = ushort.MaxValue, hi = 0;
        foreach (var v in source.Data) { if (v < lo) lo = v; if (v > hi) hi = v; }
        var result = new Raster<ushort>(source.Width, source.Height);
        if (hi > lo)
        {
            var range = (float)(hi - lo);
            for (var i = 0; i < source.Data.Length; i++)
                result.Data[i] = (ushort)(float)((float)(source.Data[i] - lo) / range * 65535f);
        }
        return (result, [0, lo / 65535f, 0, 0, hi / 65535f, 0]);
    }
}
