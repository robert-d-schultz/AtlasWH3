namespace AtlasWH3.Core;

/// <summary>
/// Keeps the game's data folder and the mod packs away from writers that take a user-chosen path (AtlasWH3's data
/// folder in Settings, the Campaign battles window's saves). The only writer of packs is the build's Pack segment, into
/// the project's own pack; it does not go through here.
/// </summary>
public static class SourceGuard
{
    /// <summary>Why <paramref name="path"/> must not be written, or null when it may: inside the game data folder,
    /// a mod pack, or any .pack file.</summary>
    public static string? WhyProtected(string path, string? gameDataDir, IEnumerable<string>? linkedPacks = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Full(path);
        foreach (var pack in linkedPacks ?? [])
            if (!string.IsNullOrWhiteSpace(pack) && full.Equals(Full(pack), StringComparison.OrdinalIgnoreCase))
                return $"{path} is a mod pack: only the build's Pack writes a project's pack";
        if (full.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
            return $"{path} is a pack file: only the build's Pack writes a project's pack";
        if (!string.IsNullOrWhiteSpace(gameDataDir) && IsUnder(full, Full(gameDataDir)))
            return $"{path} is inside the game data folder ({gameDataDir}), where only the build's Pack writes (the project's pack)";
        return null;
    }

    public static bool IsProtected(string path, string? gameDataDir, IEnumerable<string>? linkedPacks = null) =>
        WhyProtected(path, gameDataDir, linkedPacks) is not null;

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> when <paramref name="path"/> is protected.</summary>
    public static void EnsureWritable(string path, string? gameDataDir, IEnumerable<string>? linkedPacks = null)
    {
        if (WhyProtected(path, gameDataDir, linkedPacks) is { } why) throw new UnauthorizedAccessException($"Refusing to write: {why}.");
    }

    /// <summary><see cref="EnsureWritable(string, string?, IEnumerable{string}?)"/> against the paths' game data
    /// folder and mod packs.</summary>
    public static void EnsureWritable(string path, ProjectPaths paths) => EnsureWritable(path, paths.GameDataDir, paths.ModPacks);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static bool IsUnder(string full, string root) =>
        full.Equals(root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
