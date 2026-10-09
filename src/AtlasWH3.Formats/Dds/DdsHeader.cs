using System.Text;

namespace AtlasWH3.Formats.Dds;

/// <summary>Minimal DDS header parser (DX9 and DX10 extended headers).</summary>
public sealed class DdsHeader
{
    public const uint Magic = 0x20534444; // "DDS "

    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public uint PixelFormatFlags { get; init; }
    public string FourCC { get; init; } = "";
    public int RgbBitCount { get; init; }
    public uint RMask { get; init; }
    public uint GMask { get; init; }
    public uint BMask { get; init; }
    public uint AMask { get; init; }
    public uint DxgiFormat { get; init; }
    /// <summary>Byte offset of the first pixel (128, or 148 with a DX10 header).</summary>
    public int DataOffset { get; init; }

    public bool IsDx10 => FourCC == "DX10";

    public static DdsHeader Read(ReadOnlySpan<byte> data)
    {
        if (BitConverter.ToUInt32(data[..4]) != Magic)
            throw new InvalidDataException("Not a DDS file.");

        // Offsets relative to DDS_HEADER (after the 4-byte magic):
        // size(0) flags(4) height(8) width(12) pitch(16) depth(20) mips(24) reserved(28..71)
        // DDS_PIXELFORMAT at 72: size flags(76) fourcc(80) rgbBitCount(84) r(88) g(92) b(96) a(100)
        var h = data[4..];
        var fourCC = Encoding.ASCII.GetString(h.Slice(80, 4)).TrimEnd('\0');
        var isDx10 = fourCC == "DX10";
        return new DdsHeader
        {
            Height = (int)U(h, 8),
            Width = (int)U(h, 12),
            MipCount = Math.Max(1, (int)U(h, 24)),
            PixelFormatFlags = U(h, 76),
            FourCC = fourCC,
            RgbBitCount = (int)U(h, 84),
            RMask = U(h, 88),
            GMask = U(h, 92),
            BMask = U(h, 96),
            AMask = U(h, 100),
            DxgiFormat = isDx10 ? BitConverter.ToUInt32(data.Slice(128, 4)) : 0,
            DataOffset = isDx10 ? 148 : 128,
        };
    }

    private static uint U(ReadOnlySpan<byte> span, int offset) => BitConverter.ToUInt32(span.Slice(offset, 4));

    /// <summary>Builds a 128-byte uncompressed DX9 header (as CA writes for lf_height_map / global_blend).</summary>
    public static byte[] BuildUncompressed(int width, int height, int bitCount, uint rMask, uint gMask, uint bMask, uint aMask, uint pfFlags)
    {
        var header = new byte[128];
        void W(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(header, offset);
        W(0, Magic);
        W(4, 124);                               // header size
        W(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x8);    // CAPS | HEIGHT | WIDTH | PIXELFORMAT | PITCH
        W(12, (uint)height);
        W(16, (uint)width);
        W(20, (uint)(width * bitCount / 8));     // pitch
        W(76, 32);                               // pixel format size
        W(80, pfFlags);
        W(88, (uint)bitCount);
        W(92, rMask);
        W(96, gMask);
        W(100, bMask);
        W(104, aMask);
        W(108, 0x1000);                          // DDSCAPS_TEXTURE
        return header;
    }

    public const uint DxgiR32Float = 41, DxgiBc6hSf16 = 96;

    /// <summary>A 148-byte DX10 header for one 2D texture with one mip, as WH3's BOB writes it: block-compressed formats
    /// carry LINEARSIZE (bytes of the top level), uncompressed ones PITCH (bytes per row).</summary>
    public static byte[] BuildDx10(int width, int height, uint dxgiFormat, bool blockCompressed, int bytesPerPixelOrBlock)
    {
        var header = new byte[148];
        void W(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(header, offset);
        W(0, Magic);
        W(4, 124);
        // CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT, plus LINEARSIZE or PITCH
        W(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | (blockCompressed ? 0x80000u : 0x8u));
        W(12, (uint)height);
        W(16, (uint)width);
        W(20, blockCompressed
            ? (uint)(((width + 3) / 4) * ((height + 3) / 4) * bytesPerPixelOrBlock)
            : (uint)(width * bytesPerPixelOrBlock));
        W(24, 1);                                // depth
        W(28, 1);                                // mip count
        W(76, 32);
        W(80, 0x4);                              // DDPF_FOURCC
        W(84, 0x30315844);                       // "DX10"
        W(108, 0x1000);
        W(128, dxgiFormat);
        W(132, 3);                               // TEXTURE2D
        W(140, 1);                               // array size
        return header;
    }
}
