using AtlasWH3.Formats.Rpfm;

namespace AtlasWH3.Formats.Packs;

/// <summary>
/// Writes packs through RPFM's server (<see cref="RpfmClient"/>): a new pack from files, or an existing pack with files
/// deleted and added. RPFM saves them (PFH5 for WH3); a .tsv file added is imported as its DB table. Each pack is saved
/// beside its target and then swapped in, so a failed write never leaves half a pack.
/// </summary>
public static class PackWriter
{
    public const uint ModPack = 3;

    /// <summary>The header fields a new pack gets: its type and the packs it depends on.</summary>
    public sealed record Header(uint PackType, IReadOnlyList<string> Dependencies)
    {
        public static readonly Header Mod = new(ModPack, []);
    }

    private static RpfmClient Rpfm => RpfmClient.Shared;

    /// <summary>A new pack at <paramref name="packPath"/> holding <paramref name="files"/> (path in the pack, file on disk).</summary>
    public static void Write(string packPath, IEnumerable<(string InternalPath, string DiskPath)> files, Header? header = null)
    {
        header ??= Header.Mod;
        var key = (string)Rpfm.Call("pack.new")!["key"]!;
        try
        {
            Rpfm.Call("pack.update", new
            {
                pack = key, pack_type = PackFile.PackTypes[(int)header.PackType],
                dependencies = header.Dependencies.Select(d => new { enabled = true, name = d }),
            });
            Add(key, files.ToList());
            Save(key, packPath);
        }
        finally { Close(key); }
    }

    /// <summary>A new pack holding in-memory files (staged in a temporary folder).</summary>
    public static void Write(string packPath, IEnumerable<(string InternalPath, byte[] Data)> files, Header? header = null)
    {
        var stage = Directory.CreateTempSubdirectory("atlaswh3_pack_").FullName;
        try
        {
            var onDisk = files.Select((f, i) =>
            {
                var disk = Path.Combine(stage, $"{i}_{Path.GetFileName(f.InternalPath.Replace('\\', '/'))}");
                File.WriteAllBytes(disk, f.Data);
                return (f.InternalPath, disk);
            }).ToList();
            Write(packPath, onDisk, header);
        }
        finally { Directory.Delete(stage, true); }
    }

    /// <summary><paramref name="basePack"/> with the files at <paramref name="delete"/> (paths as the pack spells them)
    /// removed and <paramref name="add"/> added, saved to <paramref name="output"/> (which may be the base pack). The
    /// base pack's type, dependencies and compression are kept.</summary>
    public static void Merge(string basePack, string output, IReadOnlyCollection<string> delete,
                             IEnumerable<(string InternalPath, string DiskPath)> add)
    {
        PackFile.Forget(basePack);   // the server opens a pack file only once
        var key = (string)Rpfm.Call("pack.open", new { paths = new[] { Path.GetFullPath(basePack) }, lazy_loading = true })!["key"]!;
        try
        {
            if (delete.Count > 0) Rpfm.Call("files.delete", new { pack = key, paths = delete });
            Add(key, add.ToList());
            Save(key, output);
        }
        finally { Close(key); }
    }

    private static void Add(string key, List<(string InternalPath, string DiskPath)> files)
    {
        if (files.Count == 0) return;
        var result = Rpfm.Call("files.add_from_disk", new
        {
            pack = key,
            paths = files.Select(f => Path.GetFullPath(f.DiskPath)),
            pack_paths = files.Select(f => f.InternalPath.Replace('\\', '/').TrimStart('/')),
        })!;
        if ((string?)result["error"] is { Length: > 0 } error)
            throw new IOException($"RPFM could not add every file to the pack: {error}");
    }

    /// <summary>Saves the pack beside <paramref name="packPath"/>, closes it, and swaps it in.</summary>
    private static void Save(string key, string packPath)
    {
        var full = Path.GetFullPath(packPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".rpfm.tmp";
        Rpfm.Call("pack.save", new { pack = key, path = temp });
        Close(key);
        PackFile.Replace(temp, full);
    }

    private static void Close(string key)
    {
        try { Rpfm.Call("pack.close", new { pack = key }); }
        catch (RpfmException) { }   // closed already
    }
}
