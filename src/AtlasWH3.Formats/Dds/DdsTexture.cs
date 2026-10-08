using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace AtlasWH3.Formats.Dds;

/// <summary>Decodes the top mip of a (typically BCn-compressed) DDS texture to RGBA32.</summary>
public static class DdsTexture
{
    public sealed record Image(int Width, int Height, byte[] Rgba);

    public static Image Decode(byte[] data) => DecodeMip(data, int.MaxValue);

    /// <summary>
    /// Decodes the largest mip whose width and height are both at most <paramref name="maxSize"/> (the smallest mip if
    /// none is; the top mip when the file has no mip chain). Mips follow each other in the payload; block formats
    /// take max(1, ⌈w/4⌉)·max(1, ⌈h/4⌉) blocks of 8 (BC1, BC4) or 16 bytes.
    /// </summary>
    public static Image DecodeMip(byte[] data, int maxSize)
    {
        var top = DdsHeader.Read(data);
        var format = ResolveFormat(top);
        int w = top.Width, h = top.Height, offset = top.DataOffset;
        var mips = Math.Max(1, top.MipCount);
        for (var level = 0; level < mips - 1 && (w > maxSize || h > maxSize); level++)
        {
            offset += MipBytes(format, top, w, h);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        if (offset >= data.Length) (w, h, offset) = (top.Width, top.Height, top.DataOffset); // truncated chain
        var header = new DdsHeader
        {
            Width = w, Height = h, MipCount = 1, PixelFormatFlags = top.PixelFormatFlags, FourCC = top.FourCC,
            RgbBitCount = top.RgbBitCount, RMask = top.RMask, GMask = top.GMask, BMask = top.BMask, AMask = top.AMask,
            DxgiFormat = top.DxgiFormat, DataOffset = offset,
        };
        return DecodeLevel(header, format, data);
    }

    private static int MipBytes(CompressionFormat? format, DdsHeader h, int w, int hgt)
    {
        if (format is null) return w * hgt * Math.Max(1, (h.IsDx10 ? 32 : h.RgbBitCount) / 8);
        var blockBytes = format is CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc4 ? 8 : 16;
        return Math.Max(1, (w + 3) / 4) * Math.Max(1, (hgt + 3) / 4) * blockBytes;
    }

    private static Image DecodeLevel(DdsHeader header, CompressionFormat? format, byte[] data)
    {
        if (format == null)
            return DecodeUncompressed(header, data);

        var decoder = new BcDecoder();
        using var payload = new MemoryStream(data, header.DataOffset, data.Length - header.DataOffset, writable: false);
        var pixels = decoder.DecodeRaw(payload, header.Width, header.Height, format.Value);
        var rgba = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }
        return new Image(header.Width, header.Height, rgba);
    }

    private static CompressionFormat? ResolveFormat(DdsHeader h)
    {
        if (h.IsDx10)
        {
            return h.DxgiFormat switch
            {
                70 or 71 or 72 => CompressionFormat.Bc1WithAlpha, // BC1 punch-through alpha (leaf cards)
                73 or 74 or 75 => CompressionFormat.Bc2,
                76 or 77 or 78 => CompressionFormat.Bc3,
                79 or 80 or 81 => CompressionFormat.Bc4,
                82 or 83 or 84 => CompressionFormat.Bc5,
                97 or 98 or 99 => CompressionFormat.Bc7,
                _ => null,
            };
        }
        return h.FourCC switch
        {
            "DXT1" => CompressionFormat.Bc1WithAlpha,
            "DXT3" => CompressionFormat.Bc2,
            "DXT5" => CompressionFormat.Bc3,
            "ATI1" or "BC4U" => CompressionFormat.Bc4,
            "ATI2" or "BC5U" => CompressionFormat.Bc5,
            _ => null,
        };
    }

    private static Image DecodeUncompressed(DdsHeader h, byte[] data)
    {
        if (h.IsDx10 && h.DxgiFormat is 28 or 29)          // R8G8B8A8
            return new Image(h.Width, h.Height, data.AsSpan(h.DataOffset, h.Width * h.Height * 4).ToArray());

        if (h.RgbBitCount == 32)                            // A8R8G8B8 / A8B8G8R8
        {
            var rgba = new byte[h.Width * h.Height * 4];
            var src = data.AsSpan(h.DataOffset);
            var bgra = h.RMask == 0x00FF0000;
            for (var i = 0; i < h.Width * h.Height; i++)
            {
                var p = src.Slice(i * 4, 4);
                rgba[i * 4] = bgra ? p[2] : p[0];
                rgba[i * 4 + 1] = p[1];
                rgba[i * 4 + 2] = bgra ? p[0] : p[2];
                rgba[i * 4 + 3] = h.AMask == 0 ? (byte)255 : p[3];
            }
            return new Image(h.Width, h.Height, rgba);
        }
        if (h.RgbBitCount == 8 && !h.IsDx10)               // L8 / A8 (e.g. the campaign snow masks): grey, opaque
        {
            var rgba = new byte[h.Width * h.Height * 4];
            var src = data.AsSpan(h.DataOffset, h.Width * h.Height);
            for (var i = 0; i < src.Length; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = src[i];
                rgba[i * 4 + 3] = 255;
            }
            return new Image(h.Width, h.Height, rgba);
        }
        throw new NotSupportedException($"Unsupported DDS format (fourcc '{h.FourCC}', dxgi {h.DxgiFormat}, {h.RgbBitCount} bpp).");
    }
}
