namespace AtlasWH3.Core.Bob;

/// <summary>
/// An isolated copy of an assembly kit for BOB to run in, so a build never writes into the user's own kit: BOB writes
/// its outputs (and logs, and configuration files) inside the kit it runs from. Everything is a real copy, never a
/// hard link, so nothing BOB writes can reach the source kit. Only the chosen maps' folders are copied; the rest of the
/// kit (binaries, DB, prefabs, Terry configuration…) is small. Re-creating an existing scratch kit only copies files
/// whose size or time changed. A marker file identifies a scratch kit, and only a marked folder is ever cleared.
/// </summary>
public static class ScratchKit
{
    public const string MarkerFile = "atlaswh3_scratch_kit.txt";

    public sealed record Options
    {
        /// <summary>Campaign maps (terrain folder names) to copy, e.g. cr_oldworld_map_1 and its _devastate_1.</summary>
        public required IReadOnlyList<string> Maps { get; init; }
        /// <summary>Also copy the maps' existing BOB outputs (working_data\terrain\campaigns\&lt;map&gt;); default: start
        /// from an empty output folder.</summary>
        public bool IncludeOutputs { get; init; }
        /// <summary>Where the maps' sources come from instead of the kit (a frozen fixture), by map: a folder holding
        /// raw_data\… and working_data\… for that map.</summary>
        public IReadOnlyDictionary<string, string>? MapSources { get; init; }
    }

    public sealed record Result(string Root, int Copied, int Unchanged, long BytesCopied);

    /// <summary>The default scratch kit: assembly_kit_atlaswh3 next to <paramref name="kitRoot"/>, so BOB finds the
    /// game's data folder at the same relative place.</summary>
    public static string DefaultRoot(string kitRoot) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(kitRoot).TrimEnd('\\', '/'))!, "assembly_kit_atlaswh3");

    public static bool IsScratch(string root) => File.Exists(Path.Combine(root, MarkerFile));

    public static Result Create(string kitRoot, string root, Options options, Action<string>? log = null)
    {
        kitRoot = Path.GetFullPath(kitRoot);
        root = Path.GetFullPath(root);
        if (string.Equals(kitRoot.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("the scratch kit cannot be the source kit");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !IsScratch(root))
            throw new InvalidOperationException($"{root} exists and is not an AtlasWH3 scratch kit (no {MarkerFile}); refusing to write into it");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, MarkerFile),
            $"AtlasWH3 scratch kit: an isolated copy of {kitRoot} for headless BOB runs. Safe to delete.\n");

        var maps = options.Maps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int copied = 0, unchanged = 0;
        long bytes = 0;
        void Sync(string from, string to, Func<string, bool>? skip = null)
        {
            if (!Directory.Exists(from)) return;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(from, file);
                if (skip?.Invoke(rel.Replace('/', '\\')) == true) continue;
                var target = Path.Combine(to, rel);
                var src = new FileInfo(file);
                var dst = new FileInfo(target);
                if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc) { unchanged++; continue; }
                Directory.CreateDirectory(dst.DirectoryName!);
                if (dst.Exists && dst.IsReadOnly) dst.IsReadOnly = false;
                src.CopyTo(target, true);
                copied++;
                bytes += src.Length;
                if (copied % 500 == 0) log?.Invoke($"{copied} files copied ({bytes / 1e9:F1} GB)");
            }
        }
        static bool Under(string rel, string folder, out string rest)
        {
            rest = "";
            if (!rel.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            rest = rel[(folder.Length + 1)..];
            return true;
        }
        bool OtherMap(string rel, string folder) =>
            Under(rel, folder, out var rest) && rest.Contains('\\') && !maps.Contains(rest[..rest.IndexOf('\\')]);
        static bool Bulky(string rel) =>
            rel.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("\\backups\\", StringComparison.OrdinalIgnoreCase);

        log?.Invoke("binaries...");
        Sync(Path.Combine(kitRoot, "binaries"), Path.Combine(root, "binaries"),
            rel => rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("BOB\\", StringComparison.OrdinalIgnoreCase));
        log?.Invoke("raw_data...");
        Sync(Path.Combine(kitRoot, "raw_data"), Path.Combine(root, "raw_data"), rel =>
            Bulky(rel) || OtherMap(rel, @"terrain\campaigns") || OtherMap(rel, @"EmpireDesignData\campaign_maps")
            || options.MapSources is not null && (Under(rel, @"terrain\campaigns", out var r1) && SourcedElsewhere(r1)
                                                  || Under(rel, @"EmpireDesignData\campaign_maps", out var r2) && SourcedElsewhere(r2)));
        log?.Invoke("working_data...");
        Sync(Path.Combine(kitRoot, "working_data"), Path.Combine(root, "working_data"), rel =>
            Bulky(rel) || OtherMap(rel, @"terrain\campaigns") || OtherMap(rel, @"campaign_maps")
            || !options.IncludeOutputs && Under(rel, @"terrain\campaigns", out _)
            || options.MapSources is not null && Under(rel, @"campaign_maps", out var r3) && SourcedElsewhere(r3));
        foreach (var (map, source) in options.MapSources ?? new Dictionary<string, string>())
        {
            log?.Invoke($"{map} from {source}...");
            Sync(Path.Combine(source, "raw_data"), Path.Combine(root, "raw_data"));
            Sync(Path.Combine(source, "working_data"), Path.Combine(root, "working_data"),
                rel => !options.IncludeOutputs && Under(rel, @"terrain\campaigns", out _));
        }
        foreach (var map in maps) Directory.CreateDirectory(Path.Combine(root, "working_data", "terrain", "campaigns", map));
        log?.Invoke($"scratch kit {root}: {copied} files copied ({bytes / 1e9:F2} GB), {unchanged} unchanged");
        return new Result(root, copied, unchanged, bytes);

        bool SourcedElsewhere(string rest) =>
            rest.Contains('\\') && options.MapSources!.ContainsKey(rest[..rest.IndexOf('\\')]);
    }

    /// <summary>Deletes a map's BOB outputs in a scratch kit (working_data\terrain\campaigns\&lt;map&gt;) before a fresh run.</summary>
    public static void ClearOutputs(string root, string map)
    {
        if (!IsScratch(root)) throw new InvalidOperationException($"{root} is not an AtlasWH3 scratch kit; refusing to delete anything in it");
        var dir = Path.Combine(root, "working_data", "terrain", "campaigns", map);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }
}
