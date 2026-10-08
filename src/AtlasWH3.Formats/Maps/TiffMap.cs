using BitMiracle.LibTiff.Classic;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// Reads/writes the assembly-kit terrain TIFs:
///  - 16-bit greyscale, uncompressed (height / sea_height / lf_heights / lf_sea_heights)
///  - 8-bit palettised (blend: LZW; tree: uncompressed)
/// </summary>
public static class TiffMap
{
    /// <summary>TIFF colormap: three arrays of 256 16-bit channel values.</summary>
    public sealed record Palette(ushort[] R, ushort[] G, ushort[] B);

    static TiffMap()
    {
        // Photoshop-authored AK TIFs carry private tags LibTiff warns about; keep the console quiet.
        Tiff.SetErrorHandler(new QuietErrorHandler());
    }

    public static Raster<ushort> ReadGray16(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new IOException($"Cannot open {path}");
        var (w, h) = Size(tif);
        if (tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt() != 16)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not 16-bit.");
        var raster = new Raster<ushort>(w, h);
        var row = new byte[tif.ScanlineSize()];
        for (var y = 0; y < h; y++)
        {
            tif.ReadScanline(row, y);
            Buffer.BlockCopy(row, 0, raster.Data, y * w * 2, w * 2);
        }
        return raster;
    }

    public static (Raster<byte> Indices, Palette Palette) ReadPalette8(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new IOException($"Cannot open {path}");
        var (w, h) = Size(tif);
        var map = tif.GetField(TiffTag.COLORMAP) ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no colormap.");
        var palette = new Palette(map[0].ToUShortArray(), map[1].ToUShortArray(), map[2].ToUShortArray());
        var raster = new Raster<byte>(w, h);
        var row = new byte[tif.ScanlineSize()];
        for (var y = 0; y < h; y++)
        {
            tif.ReadScanline(row, y);
            Buffer.BlockCopy(row, 0, raster.Data, y * w, w);
        }
        return (raster, palette);
    }

    public static void WriteGray16(string path, Raster<ushort> raster)
    {
        WriteAtomically(path, temp =>
        {
            using var tif = Tiff.Open(temp, "w") ?? throw new IOException($"Cannot create {temp}");
            SetCommon(tif, raster.Width, raster.Height, 16, Photometric.MINISBLACK, Compression.NONE);
            var row = new byte[raster.Width * 2];
            for (var y = 0; y < raster.Height; y++)
            {
                Buffer.BlockCopy(raster.Data, y * raster.Width * 2, row, 0, row.Length);
                tif.WriteScanline(row, y);
            }
        });
    }

    public static void WritePalette8(string path, Raster<byte> raster, Palette palette, bool lzw, int rowsPerStrip = 0)
    {
        WriteAtomically(path, temp =>
        {
            using var tif = Tiff.Open(temp, "w") ?? throw new IOException($"Cannot create {temp}");
            SetCommon(tif, raster.Width, raster.Height, 8, Photometric.PALETTE, lzw ? Compression.LZW : Compression.NONE, rowsPerStrip);
            tif.SetField(TiffTag.COLORMAP, palette.R, palette.G, palette.B);
            var row = new byte[raster.Width];
            for (var y = 0; y < raster.Height; y++)
            {
                Buffer.BlockCopy(raster.Data, y * raster.Width, row, 0, row.Length);
                tif.WriteScanline(row, y);
            }
        });
    }

    private static void SetCommon(Tiff tif, int width, int height, int bits, Photometric photometric, Compression compression, int rowsPerStrip = 0)
    {
        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, height);
        tif.SetField(TiffTag.BITSPERSAMPLE, bits);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        tif.SetField(TiffTag.PHOTOMETRIC, photometric);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.COMPRESSION, compression);
        tif.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip > 0 ? rowsPerStrip : compression == Compression.NONE ? 1 : 2);
    }

    /// <summary>Strip layout of a classic (non-Big) TIFF's first image, read from its IFD.</summary>
    public sealed record Layout(int Width, int Height, int Bits, int SamplesPerPixel, int Compression, int Planar, int Predictor,
                                int RowsPerStrip, bool BigEndian, long[] StripOffsets, long[] StripByteCounts)
    {
        /// <summary>Pixels stored raw, one sample per pixel: they can be rewritten in place.</summary>
        public bool Patchable => Compression == 1 && SamplesPerPixel == 1 && Planar == 1 && Bits is 8 or 16
                                 && StripOffsets.Length == StripByteCounts.Length
                                 && StripByteCounts.Sum() >= (long)Width * Height * (Bits / 8);
    }

    public static Layout ReadLayout(string path)
    {
        using var f = File.OpenRead(path);
        var head = new byte[8];
        f.ReadExactly(head);
        var big = head[0] == (byte)'M';
        if (!(head[0] == head[1] && head[0] is (byte)'I' or (byte)'M') || U16(head, 2, big) != 42)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a classic TIFF.");
        f.Position = U32(head, 4, big);
        var countBytes = new byte[2];
        f.ReadExactly(countBytes);
        var n = U16(countBytes, 0, big);
        var ifd = new byte[n * 12];
        f.ReadExactly(ifd);
        var tags = new Dictionary<int, long[]>();
        for (var i = 0; i < n; i++)
        {
            int o = i * 12, tag = U16(ifd, o, big), type = U16(ifd, o + 2, big);
            var count = U32(ifd, o + 4, big);
            if (tag is not (256 or 257 or 258 or 259 or 273 or 277 or 278 or 279 or 284 or 317)) continue;
            var size = type switch { 3 => 2, 4 => 4, _ => 0 };
            if (size == 0 || count > 10_000_000) continue;
            var data = new byte[count * size];
            if (count * size <= 4) Array.Copy(ifd, o + 8, data, 0, data.Length);
            else
            {
                var back = f.Position;
                f.Position = U32(ifd, o + 8, big);
                f.ReadExactly(data);
                f.Position = back;
            }
            var values = new long[count];
            for (var k = 0; k < count; k++) values[k] = size == 2 ? U16(data, k * 2, big) : U32(data, k * 4, big);
            tags[tag] = values;
        }
        long One(int tag, long fallback) => tags.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : fallback;
        var height = (int)One(257, 0);
        return new Layout((int)One(256, 0), height, (int)One(258, 1), (int)One(277, 1), (int)One(259, 1), (int)One(284, 1),
                          (int)One(317, 1), (int)Math.Min(One(278, height), height), big,
                          tags.GetValueOrDefault(273) ?? [], tags.GetValueOrDefault(279) ?? []);
    }

    private static int U16(byte[] b, int o, bool big) => big ? b[o] << 8 | b[o + 1] : b[o] | b[o + 1] << 8;
    private static uint U32(byte[] b, int o, bool big) =>
        big ? (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]) : (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    /// <summary>
    /// Saves <paramref name="raster"/> over an existing 16-bit TIF in the file's own format: an uncompressed file gets
    /// only its pixel bytes rewritten (header, tags and Photoshop metadata stay byte for byte); anything else is rewritten
    /// with <see cref="WriteGray16"/>. Returns true when patched in place.
    /// </summary>
    public static bool SaveGray16Like(string path, Raster<ushort> raster)
    {
        var layout = ReadLayout(path);
        if (layout.Patchable && layout.Bits == 16 && (layout.Width, layout.Height) == (raster.Width, raster.Height))
        {
            PatchPixels<ushort>(path, layout, raster.Data, 2);
            return true;
        }
        WriteGray16(path, raster);
        return false;
    }

    /// <summary>
    /// Saves an 8-bit palette raster over an existing TIF in the file's own format: uncompressed files are patched in
    /// place (only pixel bytes change); compressed files are rewritten with the same compression, rows per strip and
    /// palette. Returns true when patched in place.
    /// </summary>
    public static bool SavePalette8Like(string path, Raster<byte> raster, Palette palette)
    {
        var layout = ReadLayout(path);
        if (layout.Patchable && layout.Bits == 8 && (layout.Width, layout.Height) == (raster.Width, raster.Height))
        {
            PatchPixels<byte>(path, layout, raster.Data, 1);
            return true;
        }
        WritePalette8(path, raster, palette, lzw: layout.Compression == 5, rowsPerStrip: layout.RowsPerStrip);
        return false;
    }

    private static void PatchPixels<T>(string path, Layout layout, ReadOnlySpan<T> pixels, int bytesPerSample) where T : unmanaged
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels).ToArray();
        if (bytesPerSample == 2 && (layout.BigEndian == BitConverter.IsLittleEndian))
            for (var i = 0; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        var rowBytes = layout.Width * bytesPerSample;
        var temp = path + ".tmp";
        File.Copy(path, temp, overwrite: true);
        try
        {
            using (var f = new FileStream(temp, FileMode.Open, FileAccess.Write))
            {
                for (var s = 0; s < layout.StripOffsets.Length; s++)
                {
                    var y0 = s * layout.RowsPerStrip;
                    var rows = Math.Min(layout.RowsPerStrip, layout.Height - y0);
                    if (rows <= 0) break;
                    f.Position = layout.StripOffsets[s];
                    f.Write(bytes, y0 * rowBytes, rows * rowBytes);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void WriteAtomically(string path, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        write(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Width and height of a TIF without reading its pixels.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new IOException($"Cannot open {path}");
        return Size(tif);
    }

    private static (int W, int H) Size(Tiff tif) =>
        (tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(), tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt());

    private sealed class QuietErrorHandler : TiffErrorHandler
    {
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }
}
