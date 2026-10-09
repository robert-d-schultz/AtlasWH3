using System.Runtime.InteropServices;
using AtlasWH3.Formats.Dds;

namespace AtlasWH3.Formats.Maps;

/// <summary>A dense single-channel raster, row-major, row 0 = north (max world z).</summary>
public sealed class Raster<T> where T : unmanaged
{
    public int Width { get; }
    public int Height { get; }
    public T[] Data { get; }

    public Raster(int width, int height, T[]? data = null)
    {
        Width = width;
        Height = height;
        Data = data ?? new T[width * height];
        if (Data.Length != width * height)
            throw new ArgumentException($"Raster data length {Data.Length} != {width}x{height}.");
    }

    public ref T this[int x, int y] => ref Data[y * Width + x];

    public bool Contains(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public T GetClamped(int x, int y) =>
        Data[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    public Raster<T> Clone() => new(Width, Height, (T[])Data.Clone());

    /// <summary>New raster padded on each side, filled with <paramref name="fill"/>.</summary>
    public Raster<T> Pad(int left, int top, int right, int bottom, T fill)
    {
        var result = new Raster<T>(Width + left + right, Height + top + bottom);
        Array.Fill(result.Data, fill);
        for (var y = 0; y < Height; y++)
            Array.Copy(Data, y * Width, result.Data, (y + top) * result.Width + left, Width);
        return result;
    }
}

/// <summary>Readers/writers for the compiled campaign terrain DDS maps.</summary>
public static class TerrainDds
{
    /// <summary>lf_height_map.dds / lf_sea_height_map.dds: uncompressed 16-bit luminance.</summary>
    public static Raster<ushort> ReadL16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var header = DdsHeader.Read(bytes);
        if (header.RgbBitCount != 16)
            throw new InvalidDataException($"{Path.GetFileName(path)} is {header.RgbBitCount}-bit, expected 16-bit L16.");
        var raster = new Raster<ushort>(header.Width, header.Height);
        MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan(header.DataOffset, header.Width * header.Height * 2))
            .CopyTo(raster.Data);
        return raster;
    }

    public static void WriteL16(string path, Raster<ushort> raster)
    {
        // DDPF_LUMINANCE (0x20000), 16-bit, mask 0xFFFF. BOB (and so vanilla) leaves dwFlags, dwPitchOrLinearSize
        // and dwCaps at 0; clearing them makes the file byte-identical to BOB's output.
        var header = DdsHeader.BuildUncompressed(raster.Width, raster.Height, 16, 0xFFFF, 0, 0, 0, 0x20000);
        Array.Clear(header, 8, 4);    // dwFlags
        Array.Clear(header, 20, 4);   // dwPitchOrLinearSize
        Array.Clear(header, 108, 4);  // dwCaps
        using var fs = File.Create(path);
        fs.Write(header);
        fs.Write(MemoryMarshal.AsBytes(raster.Data.AsSpan()));
    }

    /// <summary>global_blend.dds: 2 x 8-bit channels. Channel 0 = texture group (0-31), channel 1 = climate index
    /// (0 cold, 1 arid, 2 temperate, 3 sub_tropical).</summary>
    public static (Raster<byte> Group, Raster<byte> Extra) ReadBlend(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var header = DdsHeader.Read(bytes);
        if (header.RgbBitCount != 16)
            throw new InvalidDataException($"{Path.GetFileName(path)} is {header.RgbBitCount}-bit, expected 2x8-bit.");
        var n = header.Width * header.Height;
        var group = new Raster<byte>(header.Width, header.Height);
        var extra = new Raster<byte>(header.Width, header.Height);
        var src = bytes.AsSpan(header.DataOffset, n * 2);
        for (var i = 0; i < n; i++)
        {
            group.Data[i] = src[i * 2];
            extra.Data[i] = src[i * 2 + 1];
        }
        return (group, extra);
    }
}
