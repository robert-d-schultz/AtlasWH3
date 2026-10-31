using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Build;

/// <summary>Builds mod packs from loose files through RPFM's server: a new pack, or a merge over an existing pack.</summary>
public static class PackBuilder
{
    public sealed record Summary(string Pack, int Files, long Bytes, int Kept, int Replaced, int Added, int Dropped, bool Verified);

    /// <summary>Every file under <paramref name="root"/> (pack paths relative to it), minus paths containing an exclude.</summary>
    public static Dictionary<string, (string Rel, string Disk)> FromFolder(string root, IEnumerable<string>? excludes = null)
    {
        root = Path.GetFullPath(root);
        var ex = excludes?.ToList() ?? [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(root, f), Disk: f))
            .Where(f => !ex.Any(x => f.Rel.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(f => PackFile.Normalize(f.Rel), f => f);
    }

    /// <summary>Files for a list of (disk file or folder, pack path) entries; later entries win. Missing sources are
    /// reported through <paramref name="missing"/>.</summary>
    public static Dictionary<string, (string Rel, string Disk)> Collect(IEnumerable<(string Source, string PackPath)> contents, Action<string> missing)
    {
        var files = new Dictionary<string, (string Rel, string Disk)>();
        foreach (var (source, packPath) in contents)
        {
            var target = packPath.Replace('\\', '/').Trim('/');
            if (File.Exists(source))
                files[PackFile.Normalize(target)] = (target, source);
            else if (Directory.Exists(source))
                foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var rel = target.Length == 0 ? Path.GetRelativePath(source, f) : target + "/" + Path.GetRelativePath(source, f).Replace('\\', '/');
                    files[PackFile.Normalize(rel)] = (rel, f);
                }
            else
                missing(source);
        }
        return files;
    }

    /// <summary>A pack with exactly <paramref name="files"/>, re-read to check every file is present.</summary>
    public static Summary New(string output, IReadOnlyDictionary<string, (string Rel, string Disk)> files)
    {
        PackWriter.Write(output, files.Values.Select(f => (f.Rel, f.Disk)));
        var bytes = files.Values.Sum(f => new FileInfo(f.Disk).Length);
        return new Summary(output, files.Count, bytes, 0, 0, files.Count, 0, Check(output, files.Keys));
    }

    /// <summary>A copy of <paramref name="basePack"/> with <paramref name="overrides"/> replacing or adding files. Base files
    /// inside a <paramref name="replaceDirs"/> folder that the overrides don't have are dropped (e.g. stale generated
    /// meshes), but only in a folder the overrides put at least one file in, so a build that did not make a folder's
    /// files never empties it. <paramref name="output"/> may be the base pack itself. Base files under an
    /// <paramref name="owned"/> prefix (a step's own files, e.g. terrain/campaigns/&lt;map&gt;/models/river_) that the
    /// overrides don't have are always dropped. The base pack's type, dependencies and compression are kept.</summary>
    public static Summary Merge(string basePack, string output, IReadOnlyDictionary<string, (string Rel, string Disk)> overrides,
                                IEnumerable<string>? replaceDirs = null, IEnumerable<string>? owned = null)
    {
        var pack = PackFile.Open(basePack);
        var dirs = (replaceDirs ?? []).Select(d => PackFile.Normalize(d.TrimEnd('/', '\\') + "/"))
            .Where(d => overrides.Keys.Any(k => k.StartsWith(d, StringComparison.Ordinal)))
            .Concat((owned ?? []).Select(PackFile.Normalize)).ToList();
        // replaced files are deleted first: the server's paths are case-sensitive, ours are not
        var delete = new List<string>();
        var keep = new List<string>();
        int replaced = 0, dropped = 0;
        foreach (var (key, entry) in pack.Entries)
        {
            if (overrides.ContainsKey(key)) replaced++;
            else if (dirs.Any(d => key.StartsWith(d, StringComparison.Ordinal))) dropped++;
            else { keep.Add(key); continue; }
            delete.Add(entry.Path);
        }
        PackWriter.Merge(basePack, output, delete, overrides.Values.Select(f => (f.Rel, f.Disk)));
        var bytes = overrides.Values.Sum(f => new FileInfo(f.Disk).Length);
        return new Summary(output, keep.Count + overrides.Count, bytes, keep.Count, replaced, overrides.Count - replaced, dropped,
                           Check(output, keep.Concat(overrides.Keys)));
    }

    /// <summary>Whether the written pack holds every one of <paramref name="keys"/> (and no more).</summary>
    private static bool Check(string pack, IEnumerable<string> keys)
    {
        var written = PackFile.Open(pack);
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        var ok = written.Entries.Count == expected.Count && expected.All(written.Contains);
        PackFile.Forget(pack);
        return ok;
    }
}
