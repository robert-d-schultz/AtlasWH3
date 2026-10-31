using System.Text.Json.Nodes;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Rpfm;

namespace AtlasWH3.Formats.Packs;

/// <summary>A file of a pack: its path as the pack spells it (forward slashes, its own case).</summary>
public sealed record PackEntry(string Path);

/// <summary>
/// A pack opened in RPFM's server (<see cref="RpfmClient"/>): a pack file from disk, or the game's vanilla packs merged
/// in load order (<see cref="Vanilla"/>: the packs RPFM loads for the game, data\manifest.txt's with only the game's own
/// language pack). The server reads and decompresses the files. Paths are looked up case-insensitively with either
/// slash; the server's own lookups are exact, so they go through <see cref="Entries"/>. Opening a pack again reuses the
/// open one until the file on disk changes.
/// </summary>
public sealed class PackFile
{
    /// <summary>The pack file; for the vanilla packs, the game's data folder.</summary>
    public string SourcePath { get; }
    /// <summary>The pack's file name; "vanilla packs" for the vanilla packs.</summary>
    public string Name { get; }
    /// <summary><see cref="Normalize"/>d path → the file.</summary>
    public IReadOnlyDictionary<string, PackEntry> Entries => _entries;
    /// <summary>The pack type: 0 boot, 1 release, 2 patch, 3 mod, 4 movie.</summary>
    public uint PackType { get; }
    /// <summary>The packs this one names as its dependencies, in header order.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    internal string Key { get; }
    private readonly Dictionary<string, PackEntry> _entries;
    private readonly bool _vanilla;
    /// <summary>The server's copy was closed (the file is being replaced): reads go to the file as it is now.</summary>
    private volatile bool _closed;

    public static readonly IReadOnlyList<string> PackTypes = ["Boot", "Release", "Patch", "Mod", "Movie"];

    private PackFile(string sourcePath, string name, string key, JsonNode info, bool vanilla)
    {
        SourcePath = sourcePath;
        Name = name;
        Key = key;
        _vanilla = vanilla;
        var type = PackTypes.Select((t, i) => (t, i)).FirstOrDefault(t => t.t == (string?)info["pack_type"]);
        PackType = (uint)type.i;
        Dependencies = info["dependencies"] is JsonArray deps ? deps.Select(d => (string)d!["name"]!).ToList() : [];
        var files = Rpfm.Call("files.list", new { source = new { pack = key }, limit = int.MaxValue })!["files"]!.AsArray();
        _entries = new Dictionary<string, PackEntry>(files.Count, StringComparer.Ordinal);
        foreach (var f in files)
        {
            var path = (string)f!["path"]!;
            _entries.TryAdd(Normalize(path), new PackEntry(path));
        }
    }

    private static RpfmClient Rpfm => RpfmClient.Shared;

    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, (DateTime Written, long Length, PackFile Pack)> Opened = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pack at <paramref name="path"/>, opened in the server (or the one already open, if the file has not
    /// changed since).</summary>
    public static PackFile Open(string path)
    {
        var full = Path.GetFullPath(path);
        var file = new FileInfo(full);
        if (!file.Exists) throw new FileNotFoundException($"pack not found: {full}", full);
        lock (CacheGate)
        {
            if (Opened.TryGetValue(full, out var o))
            {
                if (o.Written == file.LastWriteTimeUtc && o.Length == file.Length) return o.Pack;
                Close(o.Pack);
            }
            string key;
            try { key = (string)Rpfm.Call("pack.open", new { paths = new[] { full }, lazy_loading = true })!["key"]!; }
            catch (RpfmException e) { throw new InvalidDataException($"{Path.GetFileName(full)}: {e.Message}", e); }
            var pack = new PackFile(full, Path.GetFileName(full), key, Rpfm.Call("pack.info", new { pack = key })!, vanilla: false);
            Opened[full] = (file.LastWriteTimeUtc, file.Length, pack);
            return pack;
        }
    }

    /// <summary>The game's vanilla packs (<paramref name="gameDataDir"/>: the game's data folder), merged.</summary>
    public static PackFile Vanilla(string gameDataDir)
    {
        var full = Path.GetFullPath(gameDataDir).TrimEnd(Path.DirectorySeparatorChar);
        lock (CacheGate)
        {
            if (Opened.TryGetValue(full, out var o)) return o.Pack;
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"game data folder not found: {full}");
            Rpfm.UseGameFolder(Path.GetDirectoryName(full)!);
            var key = (string)Rpfm.Call("pack.open_vanilla")!["key"]!;
            var pack = new PackFile(full, "vanilla packs", key, Rpfm.Call("pack.info", new { pack = key })!, vanilla: true);
            Opened[full] = (default, 0, pack);
            return pack;
        }
    }

    /// <summary>Closes the server's copy of a pack file opened here (before the file is replaced on disk).</summary>
    public static void Forget(string path)
    {
        lock (CacheGate)
            if (Opened.Remove(Path.GetFullPath(path), out var o)) Close(o.Pack);
    }

    /// <summary>Moves the pack file <paramref name="source"/> over <paramref name="target"/>, after the server lets go of
    /// both.</summary>
    public static void Replace(string source, string target)
    {
        Forget(source);
        Forget(target);
        File.Move(source, target, overwrite: true);
    }

    private static void Close(PackFile pack)
    {
        pack._closed = true;
        try { Rpfm.Call("pack.close", new { pack = pack.Key }); }
        catch (RpfmException) { }   // already gone with an earlier session
    }

    public bool Contains(string internalPath) => _entries.ContainsKey(Normalize(internalPath));

    public byte[]? TryRead(string internalPath) =>
        _closed ? Open(SourcePath).TryRead(internalPath)
        : _entries.TryGetValue(Normalize(internalPath), out var entry) ? Rpfm.ReadFile(Key, entry.Path) : null;

    /// <summary>The pack file a path comes from: this pack's name, or for the vanilla packs the CA pack holding it.</summary>
    public string? Origin(string internalPath)
    {
        if (!_entries.TryGetValue(Normalize(internalPath), out var entry)) return null;
        if (!_vanilla) return Name;
        var files = Rpfm.Call("files.info", new { pack = Key, paths = new[] { entry.Path } })!["files"]!.AsArray();
        return files.Count > 0 ? (string?)files[0]!["container_name"] : Name;
    }

    /// <summary>Extracts files under <paramref name="destination"/>, keeping their paths in the pack (in one request,
    /// which is much faster than reading them one by one). Returns the files written.</summary>
    public IReadOnlyList<string> Extract(IEnumerable<string> internalPaths, string destination)
    {
        if (_closed) return Open(SourcePath).Extract(internalPaths, destination);
        var paths = internalPaths.Select(p => _entries.TryGetValue(Normalize(p), out var e) ? e.Path : throw new FileNotFoundException($"{p} is not in {Name}")).ToList();
        if (paths.Count == 0) return [];
        var result = Rpfm.Call("files.extract", new { source = new { pack = Key }, paths, destination = Path.GetFullPath(destination) })!;
        return result["extracted"]!.AsArray().Select(p => (string)p!).ToList();
    }

    /// <summary>A DB table file of this pack (db/&lt;table&gt;/&lt;file&gt;), decoded by RPFM with its schema.</summary>
    public DbTables.Table ReadTable(string internalPath)
    {
        if (_closed) return Open(SourcePath).ReadTable(internalPath);
        var entry = _entries.TryGetValue(Normalize(internalPath), out var e) ? e : throw new FileNotFoundException($"{internalPath} is not in {Name}");
        var file = new { source = new { pack = Key }, path = entry.Path };
        return DbTables.FromRpfm(Rpfm.Call("table.info", new { file })!, Rpfm.Call("table.rows", new { file, limit = int.MaxValue })!);
    }

    /// <summary>A path as <see cref="Entries"/> keys it: backslashes, lower case.</summary>
    public static string Normalize(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();
}

/// <summary>
/// Packs searched like the game does, highest priority first: mod packs, then the vanilla packs (merged by RPFM in the
/// game's load order).
/// </summary>
public sealed class PackSet
{
    private readonly List<PackFile> _packs;

    public IReadOnlyList<PackFile> Packs => _packs;

    public PackSet(IEnumerable<PackFile> packsInPriorityOrder) => _packs = packsInPriorityOrder.ToList();

    public static PackSet OpenVanilla(string gameDataDir) => new([PackFile.Vanilla(gameDataDir)]);

    public byte[]? TryRead(string internalPath) => FindOwner(internalPath)?.TryRead(internalPath);

    public PackFile? FindOwner(string internalPath) => _packs.FirstOrDefault(p => p.Contains(internalPath));

    /// <summary>The pack file a path is read from (a vanilla file's own CA pack), or null.</summary>
    public string? Origin(string internalPath) => FindOwner(internalPath)?.Origin(internalPath);
}
