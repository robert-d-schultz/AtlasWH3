using System.Text;

namespace AtlasWH3.Formats.Packs;

// Read-only PFH4/PFH5 pack reader for Total War: Three Kingdoms, adapted from
// Z:\Claude\TKAudio\Tk3AudioTool\Services\Tk3Pack.cs (verified against real 3K packs).
//
// Header: "PFH5", u32 byteMask, u32 dependantCount, u32 dependantIndexSize, u32 fileCount,
// u32 fileIndexSize, timestamp buffer (4 bytes, or 24 if byteMask & 0x100), dependant names.
// File table: u32 size, [u32 timestamp if byteMask & 0x40], [u8 compressed if PFH5], zero-terminated path.
// Data follows the table, in table order.

public sealed record PackEntry(string Path, long Offset, uint Size, bool IsCompressed);

public sealed class PackFile
{
    public string SourcePath { get; }
    public IReadOnlyDictionary<string, PackEntry> Entries => _entries;

    private readonly Dictionary<string, PackEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private PackFile(string sourcePath) => SourcePath = sourcePath;

    public static PackFile Open(string path)
    {
        var pack = new PackFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new BinaryReader(stream, Encoding.ASCII);

        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != "PFH5" && magic != "PFH4")
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a PFH4/PFH5 pack (magic '{magic}').");

        var byteMask = reader.ReadUInt32();
        var dependantCount = reader.ReadUInt32();
        var dependantIndexSize = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        var fileIndexSize = reader.ReadUInt32();
        if ((byteMask & 0x80) != 0)
            throw new NotSupportedException("Encrypted-index packs are not supported.");

        var bufferLen = (byteMask & 0x100) != 0 ? 24 : 4;
        reader.ReadBytes(bufferLen);
        for (var i = 0; i < dependantCount; i++)
            ReadZeroTerminated(reader);

        var hasTimestamps = (byteMask & 0x40) != 0;
        var isPfh5 = magic == "PFH5";
        long offset = 24 + bufferLen + dependantIndexSize + fileIndexSize;

        for (uint i = 0; i < fileCount; i++)
        {
            var size = reader.ReadUInt32();
            if (hasTimestamps) reader.ReadUInt32();
            var compressed = isPfh5 && reader.ReadBoolean();
            var entryPath = ReadZeroTerminated(reader);
            pack._entries[Normalize(entryPath)] = new PackEntry(entryPath, offset, size, compressed);
            offset += size;
        }
        return pack;
    }

    public bool Contains(string internalPath) => _entries.ContainsKey(Normalize(internalPath));

    public byte[]? TryRead(string internalPath)
    {
        if (!_entries.TryGetValue(Normalize(internalPath), out var entry))
            return null;
        if (entry.IsCompressed)
            throw new NotSupportedException($"'{entry.Path}' in {Path.GetFileName(SourcePath)} is compressed.");

        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(entry.Offset, SeekOrigin.Begin);
        var data = new byte[entry.Size];
        stream.ReadExactly(data);
        return data;
    }

    public static string Normalize(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();

    private static string ReadZeroTerminated(BinaryReader reader)
    {
        var bytes = new List<byte>(96);
        byte b;
        while ((b = reader.ReadByte()) != 0)
            bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>
/// A set of packs searched like the game does: later packs in load order override earlier ones.
/// Only vanilla packs (listed in data\manifest.txt) are used by default so mods don't leak in.
/// </summary>
public sealed class PackSet
{
    private readonly List<PackFile> _packs;

    public IReadOnlyList<PackFile> Packs => _packs;

    public PackSet(IEnumerable<PackFile> packsInPriorityOrder) => _packs = packsInPriorityOrder.ToList();

    public static PackSet OpenVanilla(string gameDataDir, Func<string, bool>? filter = null)
    {
        var manifest = Path.Combine(gameDataDir, "manifest.txt");
        IEnumerable<string> names = File.Exists(manifest)
            ? File.ReadLines(manifest).Select(l => l.Split('\t')[0].Trim())
                  .Where(n => n.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
            : Directory.EnumerateFiles(gameDataDir, "*.pack").Select(p => Path.GetFileName(p));

        var packs = names
            .Where(n => filter == null || filter(n))
            .Select(n => Path.Combine(gameDataDir, n))
            .Where(File.Exists)
            .Select(PackFile.Open)
            .ToList();
        // Highest priority first (the game lets later/"newer" packs override; reverse load order).
        packs.Reverse();
        return new PackSet(packs);
    }

    public byte[]? TryRead(string internalPath)
    {
        foreach (var pack in _packs)
            if (pack.Contains(internalPath))
                return pack.TryRead(internalPath);
        return null;
    }

    public PackFile? FindOwner(string internalPath) => _packs.FirstOrDefault(p => p.Contains(internalPath));
}
