using System.Text.RegularExpressions;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core;

/// <summary>
/// Finds the game through Steam and reads the campaign map files AtlasWH3 needs straight from the user's own packs
/// (nothing of CA's is shipped or copied out).
/// </summary>
public static partial class GameSetup
{
    public const string GameFolderName = "Total War WARHAMMER III";

    /// <summary>The game install folder (holding data\ and assembly_kit\) from Steam's library folders, or null.</summary>
    public static string? FindGameFolder()
    {
        foreach (var library in SteamLibraries())
        {
            var game = Path.Combine(library, "steamapps", "common", GameFolderName);
            if (Directory.Exists(Path.Combine(game, "data"))) return TrueCase(game);
        }
        return null;
    }

    /// <summary>The path as cased on disk (Steam's registry value is lower case).</summary>
    private static string TrueCase(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!.ToUpperInvariant();
        var result = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Directory.EnumerateFileSystemEntries(result, part).FirstOrDefault();
            result = match ?? Path.Combine(result, part);
        }
        return result;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, key, value) in new[]
            {
                (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
                if (hive.OpenSubKey(key)?.GetValue(value) is string path && Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            if (seen.Add(root)) yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in LibraryPath().Matches(File.ReadAllText(vdf)))
            {
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(lib) && seen.Add(lib)) yield return lib;
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();

    /// <summary>The mod packs (highest priority first) in front of the vanilla packs.</summary>
    public static PackSet OpenWithMods(string gameDataDir, IReadOnlyList<string> modPacks, Func<string, bool>? vanillaFilter = null)
    {
        var mods = modPacks.Where(File.Exists).Select(PackFile.Open);
        return new PackSet(mods.Concat(PackSet.OpenVanilla(gameDataDir, vanillaFilter).Packs));
    }

    /// <summary>campaign_maps\&lt;map&gt;\&lt;file&gt; as the game loads it, with the pack it came from: the mod packs
    /// (highest priority first), then the vanilla packs. Null when no pack has it. CAIME's map_data.esf and
    /// pathfinding.ppd ship in the map's mod pack, so the build reads them there.</summary>
    public static (byte[] Data, string Pack)? CampaignMapFile(ProjectPaths paths, string map, string file)
    {
        var internalPath = $"campaign_maps/{map}/{file}";
        return CampaignMapPack(paths, internalPath) is { } owner ? (owner.TryRead(internalPath)!, owner.SourcePath) : null;
    }

    /// <summary>Whether <see cref="CampaignMapFile"/> finds the file, from the packs' indexes only (nothing is read).</summary>
    public static bool HasCampaignMapFile(ProjectPaths paths, string map, string file) =>
        CampaignMapPack(paths, $"campaign_maps/{map}/{file}") is not null;

    private static PackFile? CampaignMapPack(ProjectPaths paths, string internalPath)
    {
        foreach (var path in paths.ModPacks.Where(File.Exists))
            if (PackFile.Open(path) is var pack && pack.Contains(internalPath)) return pack;
        return Directory.Exists(paths.GameDataDir) ? PackSet.OpenVanilla(paths.GameDataDir).FindOwner(internalPath) : null;
    }

    /// <summary>Where <see cref="CampaignMapFile"/> looks, for a missing-input message.</summary>
    public static string CampaignMapFileMissing(ProjectPaths paths, string map, string file) =>
        $"no campaign_maps\\{map}\\{file} in " + (paths.ModPacks.Count > 0
            ? $"the mod packs ({string.Join(", ", paths.ModPacks.Select(Path.GetFileName))}) or the game's packs"
            : "the game's packs, and the project has no mod packs: add the map's mod pack to the project (Build > Project settings > Mod packs)");

    /// <summary>Campaign maps in the vanilla packs (folders under campaign_maps\ that have terrain).</summary>
    public static IReadOnlyList<string> VanillaMaps(string gameDataDir)
    {
        var packs = PackSet.OpenVanilla(gameDataDir);
        return packs.Packs.SelectMany(p => p.Entries.Keys)
            .Where(k => k.StartsWith(@"terrain\campaigns\", StringComparison.Ordinal))
            .Select(k => k.Split('\\')[2]).Where(m => m.Length > 0 && !m.Contains('.'))
            .Distinct().Order().ToList();
    }
}
