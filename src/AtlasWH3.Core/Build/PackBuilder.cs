using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Build;

/// <summary>Builds uncompressed PFH5 mod packs from loose files: a new pack, or a merge over an existing pack.</summary>
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        PackWriter.Write(output, files.Values.Select(f => (f.Rel, f.Disk)));
        var check = PackFile.Open(output);
        var bytes = files.Values.Sum(f => new FileInfo(f.Disk).Length);
        return new Summary(output, files.Count, bytes, 0, 0, files.Count, 0, files.Keys.All(check.Contains));
    }

    /// <summary>A copy of <paramref name="basePack"/> with <paramref name="overrides"/> replacing or adding files. Base files
    /// inside a <paramref name="replaceDirs"/> folder that the overrides don't have are dropped (e.g. stale generated
    /// meshes), but only in a folder the overrides put at least one file in, so a build that did not make a folder's
    /// files never empties it. <paramref name="output"/> may be the base pack itself (written to a temp file, then
    /// swapped).</summary>
    public static Summary Merge(string basePack, string output, IReadOnlyDictionary<string, (string Rel, string Disk)> overrides,
                                IEnumerable<string>? replaceDirs = null)
    {
        var pack = PackFile.Open(basePack);
        var dirs = (replaceDirs ?? []).Select(d => PackFile.Normalize(d.TrimEnd('/', '\\') + "/"))
            .Where(d => overrides.Keys.Any(k => k.StartsWith(d, StringComparison.Ordinal))).ToList();
        var sources = new List<PackWriter.Source>();
        int kept = 0, replaced = 0, dropped = 0;
        foreach (var (key, entry) in pack.Entries)
        {
            if (overrides.ContainsKey(key)) { replaced++; continue; }
            if (dirs.Any(d => key.StartsWith(d, StringComparison.Ordinal))) { dropped++; continue; }
            sources.Add(PackWriter.FromPack(pack, entry));
            kept++;
        }
        foreach (var (_, f) in overrides) sources.Add(PackWriter.FromDisk(f.Rel, f.Disk));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        PackWriter.Write(output, sources);
        var check = PackFile.Open(output);
        return new Summary(output, sources.Count, sources.Sum(s => s.Size), kept, replaced, overrides.Count - replaced, dropped,
                           sources.All(s => check.Contains(s.InternalPath)));
    }
}
