namespace AtlasWH3.Formats.Packs;

/// <summary>
/// Where game assets are read from, highest priority first: loose folders (an extracted mod, the kit's working_data),
/// then extra packs (mods, in the order given), then the vanilla packs in the game's load order. Paths are the
/// in-pack paths ("rigidmodels/campaign/....wsmodel"), compared case-insensitively with either slash.
/// </summary>
public sealed class AssetSource
{
    private readonly List<string> _loose;
    private readonly List<PackFile> _extraPacks;

    public PackSet Vanilla { get; }
    public IReadOnlyList<string> LooseRoots => _loose;
    public IReadOnlyList<PackFile> ExtraPacks => _extraPacks;

    public AssetSource(PackSet vanilla, IEnumerable<string>? looseRoots = null, IEnumerable<string>? extraPacks = null)
    {
        Vanilla = vanilla;
        _loose = (looseRoots ?? []).Where(Directory.Exists).Select(Path.GetFullPath).ToList();
        _extraPacks = (extraPacks ?? []).Where(File.Exists).Select(PackFile.Open).ToList();
    }

    public static AssetSource ForGame(string gameDataDir, IEnumerable<string>? looseRoots = null, IEnumerable<string>? extraPacks = null) =>
        new(PackSet.OpenVanilla(gameDataDir), looseRoots, extraPacks);

    public static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/');

    public bool Exists(string path) => Locate(path) is not null;

    public byte[]? TryRead(string path)
    {
        var p = Normalize(path);
        foreach (var root in _loose)
        {
            var full = Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return File.ReadAllBytes(full);
        }
        foreach (var pack in _extraPacks)
            if (pack.Contains(p)) return pack.TryRead(p);
        return Vanilla.TryRead(p);
    }

    public byte[] Read(string path) => TryRead(path) ?? throw new FileNotFoundException($"asset not found: {path}");

    /// <summary>Where a path resolves: a loose file path, or "pack.pack" (null when missing).</summary>
    public string? Locate(string path)
    {
        var p = Normalize(path);
        foreach (var root in _loose)
        {
            var full = Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return full;
        }
        foreach (var pack in _extraPacks)
            if (pack.Contains(p)) return Path.GetFileName(pack.SourcePath);
        return Vanilla.FindOwner(p) is { } owner ? Path.GetFileName(owner.SourcePath) : null;
    }

    /// <summary>Every asset path with one of the extensions, across all sources (deduplicated, normalized).</summary>
    public IEnumerable<string> Enumerate(params string[] extensions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Wanted(string p) => extensions.Length == 0 || extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        foreach (var root in _loose)
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var p = Normalize(Path.GetRelativePath(root, f));
                if (Wanted(p) && seen.Add(p)) yield return p;
            }
        foreach (var pack in _extraPacks.Concat(Vanilla.Packs))
            foreach (var e in pack.Entries.Values)
            {
                var p = Normalize(e.Path);
                if (Wanted(p) && seen.Add(p)) yield return p;
            }
    }
}
