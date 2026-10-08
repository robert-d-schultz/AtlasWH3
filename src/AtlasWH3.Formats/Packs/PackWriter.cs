using System.Text;

namespace AtlasWH3.Formats.Packs;

/// <summary>
/// Writes an uncompressed PFH5 mod pack (Three Kingdoms): "PFH5", u32 flags (3 = mod), u32 dependency count (0),
/// u32 dependency index size (0), u32 file count, u32 file index size, u32 timestamp, then per file
/// (u32 size, u8 compressed = 0, backslash path + \0), then the file data in index order.
/// Same layout <see cref="PackFile"/> reads.
/// </summary>
public static class PackWriter
{
    public const uint ModPack = 3;

    /// <summary>One file to pack: its path inside the pack (/ or \), size and a callback that copies its bytes.</summary>
    public sealed record Source(string InternalPath, long Size, Action<Stream> CopyTo);

    /// <summary>A file on disk.</summary>
    public static Source FromDisk(string internalPath, string diskPath) =>
        new(internalPath, new FileInfo(diskPath).Length, s => { using var src = File.OpenRead(diskPath); src.CopyTo(s); });

    /// <summary>A file taken unchanged from an existing uncompressed pack.</summary>
    public static Source FromPack(PackFile pack, PackEntry entry) => new(entry.Path, entry.Size, s =>
    {
        if (entry.IsCompressed) throw new NotSupportedException($"{entry.Path} is compressed.");
        using var src = new FileStream(pack.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        src.Seek(entry.Offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 20];
        var left = (long)entry.Size;
        while (left > 0)
        {
            var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) throw new EndOfStreamException(entry.Path);
            s.Write(buffer, 0, n);
            left -= n;
        }
    });

    public static void Write(string packPath, IEnumerable<(string InternalPath, string DiskPath)> files) =>
        Write(packPath, files.Select(f => FromDisk(f.InternalPath, f.DiskPath)));

    public static void Write(string packPath, IEnumerable<Source> sources)
    {
        var list = sources.Select(f => (Name: f.InternalPath.Replace('/', '\\').TrimStart('\\'), Source: f))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var index = new MemoryStream();
        using (var w = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
            foreach (var (name, source) in list)
            {
                w.Write(checked((uint)source.Size));
                w.Write((byte)0);
                w.Write(Encoding.UTF8.GetBytes(name));
                w.Write((byte)0);
            }

        var temp = packPath + ".tmp";
        using (var fs = File.Create(temp))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("PFH5"u8);
            w.Write(ModPack);
            w.Write(0u);
            w.Write(0u);
            w.Write((uint)list.Count);
            w.Write((uint)index.Length);
            w.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            w.Write(index.ToArray());
            w.Flush();
            foreach (var (_, source) in list)
            {
                var before = fs.Position;
                source.CopyTo(fs);
                if (fs.Position - before != source.Size)
                    throw new InvalidDataException($"{source.InternalPath}: wrote {fs.Position - before} bytes, expected {source.Size}.");
            }
        }
        File.Move(temp, packPath, overwrite: true);
    }
}
