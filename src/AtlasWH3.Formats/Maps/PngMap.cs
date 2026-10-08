using System.Runtime.InteropServices;
using SkiaSharp;

namespace AtlasWH3.Formats.Maps;

/// <summary>RGB(A) PNG maps (climate_map.png, tile_map.png) as packed 0xAABBGGRR rasters.</summary>
public static class PngMap
{
    public static Raster<uint> Read(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"Cannot decode {path}");
        using var bitmap = decoded.ColorType == SKColorType.Rgba8888 ? decoded.Copy() : decoded.Copy(SKColorType.Rgba8888);
        var raster = new Raster<uint>(bitmap.Width, bitmap.Height);
        MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()).CopyTo(raster.Data);
        return raster;
    }

    /// <summary>Writes an opaque PNG (RGB, as the AK maps are).</summary>
    public static void Write(string path, Raster<uint> raster)
    {
        var info = new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        MemoryMarshal.AsBytes(raster.Data.AsSpan()).CopyTo(bitmap.GetPixelSpan());
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var temp = path + ".tmp";
        using (var fs = File.Create(temp)) data.SaveTo(fs);
        File.Move(temp, path, overwrite: true);
    }
}
