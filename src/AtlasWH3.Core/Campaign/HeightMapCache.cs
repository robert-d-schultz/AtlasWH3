using System.IO.Hashing;
using System.Runtime.InteropServices;
using AtlasWH3.Formats.Dds;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// The last full_height_map.dds a build encoded for a map, kept in the cache with a fingerprint of the heights it came
/// from: per square region of the map, a hash of its land and sea texels. The encoder decides each block from the
/// block's own 16 texels, so where a region's texels hash the same its blocks are copied from the cached file, and only
/// the regions whose heights changed are encoded again (the whole of IEE takes about 3 minutes). The fingerprint also
/// holds the map size, the encoder (the AtlasWH3.Formats build and <see cref="Bc6h.TwoRegionFactor"/>) and a hash of the
/// cached blocks: any difference, or a missing or damaged file, means everything is encoded.
/// </summary>
public sealed class HeightMapCache
{
    public const int DefaultRegionTexels = 256;
    private const string Magic = "AtlasWH3 full_height_map fingerprint 1";

    private readonly int _region;

    /// <param name="regionTexels">Region side in texels, a multiple of the 4-texel block (so no block spans two).</param>
    public HeightMapCache(string directory, int regionTexels = DefaultRegionTexels)
    {
        if (regionTexels <= 0 || regionTexels % 4 != 0) throw new ArgumentOutOfRangeException(nameof(regionTexels));
        Directory = directory;
        _region = regionTexels;
    }

    public string Directory { get; }
    private string DdsPath => Path.Combine(Directory, "full_height_map.dds");
    private string FingerprintPath => Path.Combine(Directory, "full_height_map.fingerprint");

    /// <summary>The encoder's identity: its assembly's build (a deterministic build of the same source keeps it) and the
    /// two-region factor.</summary>
    private static string Encoder =>
        $"{typeof(Bc6h).Assembly.ManifestModule.ModuleVersionId} {Bc6h.TwoRegionFactor.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Per region (rows of regions from the first stored row, then columns), a hash of its red and green texels
    /// (rows as stored, as <see cref="Bc6h.Encode"/> reads them).</summary>
    public UInt128[] Fingerprint(float[] red, float[] green, int w, int h)
    {
        int rw = (w + _region - 1) / _region, rh = (h + _region - 1) / _region;
        var result = new UInt128[rw * rh];
        Parallel.For(0, result.Length, r =>
        {
            int x0 = r % rw * _region, y0 = r / rw * _region;
            int x1 = Math.Min(w, x0 + _region), y1 = Math.Min(h, y0 + _region);
            var hash = new XxHash128();
            for (var y = y0; y < y1; y++)
            {
                hash.Append(MemoryMarshal.AsBytes(red.AsSpan(y * w + x0, x1 - x0)));
                hash.Append(MemoryMarshal.AsBytes(green.AsSpan(y * w + x0, x1 - x0)));
            }
            result[r] = hash.GetCurrentHashAsUInt128();
        });
        return result;
    }

    /// <summary>Per block (file order), whether the cached file holds it for these texels, and the cached blocks; null
    /// when no region can be reused.</summary>
    public (bool[] Same, ReadOnlyMemory<byte> Blocks)? Match(UInt128[] fingerprint, int w, int h)
    {
        if (!File.Exists(FingerprintPath) || !File.Exists(DdsPath)) return null;
        try
        {
            UInt128[] cached;
            UInt128 dataHash;
            using (var r = new BinaryReader(File.OpenRead(FingerprintPath)))
            {
                if (r.ReadString() != Magic || r.ReadString() != Encoder || r.ReadInt32() != w || r.ReadInt32() != h ||
                    r.ReadInt32() != _region || r.ReadInt32() != fingerprint.Length) return null;
                dataHash = Read128(r);
                cached = new UInt128[fingerprint.Length];
                for (var i = 0; i < cached.Length; i++) cached[i] = Read128(r);
            }
            var header = Header(w, h);
            var bytes = File.ReadAllBytes(DdsPath);
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (bytes.Length != header.Length + bw * bh * Bc6h.BlockBytes || !bytes.AsSpan(0, header.Length).SequenceEqual(header)) return null;
            var blocks = bytes.AsMemory(header.Length);
            if (XxHash128.HashToUInt128(blocks.Span) != dataHash) return null;
            var regionSame = new bool[fingerprint.Length];
            var any = false;
            for (var i = 0; i < regionSame.Length; i++) any |= regionSame[i] = cached[i] == fingerprint[i];
            if (!any) return null;
            var rw = (w + _region - 1) / _region;
            var perRegion = _region / 4;
            var same = new bool[bw * bh];
            for (var by = 0; by < bh; by++)
                for (var bx = 0; bx < bw; bx++)
                    same[by * bw + bx] = regionSame[by / perRegion * rw + bx / perRegion];
            return (same, blocks);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Keeps <paramref name="blocks"/> (every block of the map) as the cached file for
    /// <paramref name="fingerprint"/>. The old fingerprint goes first and the new one comes last, so a fingerprint on
    /// disk is never paired with blocks it did not hash.</summary>
    public void Save(UInt128[] fingerprint, int w, int h, ReadOnlySpan<byte> blocks)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.Delete(FingerprintPath);
        using (var f = File.Create(DdsPath))
        {
            f.Write(Header(w, h));
            f.Write(blocks);
        }
        var temp = FingerprintPath + ".tmp";
        using (var wr = new BinaryWriter(File.Create(temp)))
        {
            wr.Write(Magic);
            wr.Write(Encoder);
            wr.Write(w);
            wr.Write(h);
            wr.Write(_region);
            wr.Write(fingerprint.Length);
            Write128(wr, XxHash128.HashToUInt128(blocks));
            foreach (var v in fingerprint) Write128(wr, v);
        }
        File.Move(temp, FingerprintPath, true);
    }

    private static byte[] Header(int w, int h) => DdsHeader.BuildDx10(w, h, DdsHeader.DxgiBc6hSf16, true, Bc6h.BlockBytes);

    private static UInt128 Read128(BinaryReader r)
    {
        var lo = r.ReadUInt64();
        return new UInt128(r.ReadUInt64(), lo);
    }

    private static void Write128(BinaryWriter w, UInt128 v)
    {
        w.Write((ulong)v);
        w.Write((ulong)(v >> 64));
    }
}
