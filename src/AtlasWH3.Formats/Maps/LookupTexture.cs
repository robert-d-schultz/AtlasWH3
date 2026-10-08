using System.Buffers.Binary;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// BOB's "Texture / Convert lookup texture": the campaign region lookup .bmp becomes a paletted .tga, an R16_UNORM
/// .dds of the palette indices, and a quarter-size _minimap.tga.
///  - palette: the BMP's unique colours in first-appearance order, scanning rows from the top of the image
///  - .tga: 18-byte header (colour-map type 1, image type 1, first-entry field 18, 32-bit entries, 16-bit pixels,
///    descriptor 0x10), BGRA palette with A = 255, u16 indices top row first, no footer
///  - .dds: 148-byte DX10 header (DXGI 56 R16_UNORM), same indices
///  - minimap: indices[::4, ::4] with the same palette
/// </summary>
public sealed class LookupTexture
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>0x00RRGGBB colours.</summary>
    public IReadOnlyList<uint> Palette { get; }
    /// <summary>Row-major, row 0 = top.</summary>
    public ushort[] Indices { get; }

    private LookupTexture(int width, int height, IReadOnlyList<uint> palette, ushort[] indices)
    {
        Width = width;
        Height = height;
        Palette = palette;
        Indices = indices;
    }

    /// <summary>Reads an uncompressed 24- or 32-bit BI_RGB bitmap (bottom-up or top-down).</summary>
    public static LookupTexture FromBmp(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b[0] != 'B' || b[1] != 'M') throw new InvalidDataException($"{Path.GetFileName(path)} is not a BMP.");
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(10));
        var width = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22));
        var bpp = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(30));
        if ((bpp != 24 && bpp != 32) || compression != 0)
            throw new InvalidDataException($"{Path.GetFileName(path)}: only uncompressed 24/32-bit BMPs are supported.");
        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0;
        var bytesPerPixel = bpp / 8;
        var stride = (width * bytesPerPixel + 3) & ~3;

        var palette = new List<uint>();
        var lookup = new Dictionary<uint, ushort>();
        var indices = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = dataOffset + (bottomUp ? height - 1 - y : y) * stride;
            for (var x = 0; x < width; x++)
            {
                var p = row + x * bytesPerPixel;
                var rgb = (uint)(b[p + 2] << 16 | b[p + 1] << 8 | b[p]);
                if (!lookup.TryGetValue(rgb, out var index))
                {
                    if (palette.Count > ushort.MaxValue)
                        throw new InvalidDataException($"{Path.GetFileName(path)} has more than 65536 colours.");
                    index = (ushort)palette.Count;
                    lookup.Add(rgb, index);
                    palette.Add(rgb);
                }
                indices[y * width + x] = index;
            }
        }
        return new LookupTexture(width, height, palette, indices);
    }

    public LookupTexture Minimap()
    {
        int w = Width / 4, h = Height / 4;
        var indices = new ushort[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                indices[y * w + x] = Indices[y * 4 * Width + x * 4];
        return new LookupTexture(w, h, Palette, indices);
    }

    public byte[] ToTga()
    {
        var result = new byte[18 + Palette.Count * 4 + Indices.Length * 2];
        var s = result.AsSpan();
        s[1] = 1; // colour-map type
        s[2] = 1; // uncompressed colour-mapped
        BinaryPrimitives.WriteUInt16LittleEndian(s[3..], 18);   // first entry index (BOB quirk: the header size)
        BinaryPrimitives.WriteUInt16LittleEndian(s[5..], (ushort)Palette.Count);
        s[7] = 32;
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)Height);
        s[16] = 16;
        s[17] = 0x10;
        var o = 18;
        foreach (var rgb in Palette)
        {
            s[o++] = (byte)rgb;
            s[o++] = (byte)(rgb >> 8);
            s[o++] = (byte)(rgb >> 16);
            s[o++] = 0xFF;
        }
        WriteIndices(s[o..]);
        return result;
    }

    public byte[] ToDds()
    {
        var result = new byte[148 + Indices.Length * 2];
        void W(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), value);
        var s = result.AsSpan();
        W(0, 0x20534444);
        W(4, 124);
        W(8, 0x0002100F);
        W(12, (uint)Height);
        W(16, (uint)Width);
        W(20, (uint)(Width * 2));
        W(24, 1);                    // depth
        W(28, 1);                    // mip count
        W(76, 32);                   // pixel format size
        W(80, 0x4);                  // DDPF_FOURCC
        "DX10"u8.CopyTo(s[84..]);
        W(108, 0x1000);              // DDSCAPS_TEXTURE
        W(128, 56);                  // DXGI_FORMAT_R16_UNORM
        W(132, 3);                   // TEXTURE2D
        W(140, 1);                   // array size
        WriteIndices(s[148..]);
        return result;
    }

    private void WriteIndices(Span<byte> s)
    {
        for (var i = 0; i < Indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(s[(i * 2)..], Indices[i]);
    }
}
