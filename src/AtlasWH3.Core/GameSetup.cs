using System.Text;
using System.Text.RegularExpressions;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core;

/// <summary>
/// First-run setup: finds the game through Steam and derives the data AtlasWH3 needs from the user's own install
/// (nothing of CA's is shipped): the compiled campaign map files and the few DB tables the tree tools read.
/// </summary>
public static partial class GameSetup
{
    public const string GameFolderName = "Total War THREE KINGDOMS";

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

    /// <summary>The DB tables (vanilla database packs) the tree tools read, as RPFM-style TSVs under <paramref name="dbRoot"/>.</summary>
    public static IReadOnlyList<string> ExtractDbTables(string gameDataDir, string dbRoot, Action<string>? log = null) =>
        ExtractDbTables(gameDataDir, dbRoot, [], log);

    /// <summary>As above, with the linked mod packs' table files added (read-only; a file that does not decode with
    /// the built-in schema is skipped with a log line).</summary>
    public static IReadOnlyList<string> ExtractDbTables(string gameDataDir, string dbRoot, IReadOnlyList<string> linkedPacks, Action<string>? log = null)
    {
        SourceGuard.EnsureWritable(dbRoot, gameDataDir, linkedPacks);
        var vanilla = PackSet.OpenVanilla(gameDataDir, n => n.StartsWith("database", StringComparison.OrdinalIgnoreCase));
        var mods = linkedPacks.Where(File.Exists).Select(PackFile.Open).ToList();
        var packs = new PackSet(mods.Concat(vanilla.Packs));
        var written = new List<string>();
        foreach (var table in DbBinaryTable.Schemas.Keys.Select(k => k.Table).Distinct())
        {
            var prefix = PackFile.Normalize($"db/{table}/");
            // every data file of the table across the database packs; the highest-priority pack wins per file name
            var files = packs.Packs.SelectMany(p => p.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))).Distinct().ToList();
            if (files.Count == 0) throw new FileNotFoundException($"{table} not found in {gameDataDir}\\database*.pack");
            DbBinaryTable.Table? merged = null;
            foreach (var file in files)
            {
                DbBinaryTable.Table t;
                try { t = DbBinaryTable.Read(table, packs.TryRead(file)!); }
                catch (Exception e) when (mods.Contains(packs.FindOwner(file)!) && e is InvalidDataException or NotSupportedException or EndOfStreamException or ArgumentException)
                {
                    log?.Invoke($"{file} in {Path.GetFileName(packs.FindOwner(file)!.SourcePath)} skipped: {e.Message}");
                    continue;
                }
                if (merged is null) merged = t;
                else merged.Rows.AddRange(t.Rows);
            }
            var path = Path.Combine(dbRoot, table, "data__.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToTsv(merged!));
            log?.Invoke($"{table}: {merged!.Rows.Count} rows");
            written.Add(path);
        }
        return written;
    }

    /// <summary>RPFM's TSV layout: header, #table;version;path, rows (campaign_tree_ids' r/g/b as one colour_hex).</summary>
    private static string ToTsv(DbBinaryTable.Table t)
    {
        var sb = new StringBuilder();
        var colour = t.Index("colour_r") >= 0;
        var columns = t.Columns.Select(c => c.Name).Where(n => !colour || n is not ("colour_r" or "colour_g" or "colour_b")).ToList();
        if (colour) columns.Add("colour_hex");
        sb.AppendJoin('\t', columns).Append('\n');
        sb.Append($"#{t.Name};{t.Version};db/{t.Name}/data__").Append('\t', columns.Count - 1).Append('\n');
        foreach (var row in t.Rows)
        {
            var values = columns.Select(c => c == "colour_hex"
                ? $"{(int)row[t.Index("colour_r")]!:X2}{(int)row[t.Index("colour_g")]!:X2}{(int)row[t.Index("colour_b")]!:X2}"
                : row[t.Index(c)] switch { bool b => b ? "true" : "false", var v => v?.ToString() ?? "" });
            sb.AppendJoin('\t', values).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A campaign map's compiled files (terrain\campaigns\&lt;map&gt; and campaign_maps\&lt;map&gt;) from the vanilla
    /// packs into <paramref name="compiledRoot"/>, the folder the editors read.</summary>
    public static int ExtractCompiledMap(string gameDataDir, string map, string compiledRoot, Action<string>? log = null) =>
        ExtractCompiledMap(gameDataDir, map, compiledRoot, [], log);

    /// <summary>As above, reading the linked mod packs first (highest priority first), then the vanilla packs. Packs
    /// are only read; <paramref name="compiledRoot"/> may not be inside the game data folder.</summary>
    public static int ExtractCompiledMap(string gameDataDir, string map, string compiledRoot, IReadOnlyList<string> linkedPacks,
                                         Action<string>? log = null)
    {
        SourceGuard.EnsureWritable(compiledRoot, gameDataDir, linkedPacks);
        var packs = OpenWithLinked(gameDataDir, linkedPacks);
        var prefixes = new[] { PackFile.Normalize($"terrain/campaigns/{map}/"), PackFile.Normalize($"campaign_maps/{map}/") };
        var files = packs.Packs.SelectMany(p => p.Entries.Keys).Where(k => prefixes.Any(x => k.StartsWith(x, StringComparison.Ordinal)))
            .Distinct().ToList();
        if (files.Count == 0)
            throw new FileNotFoundException($"no compiled files for {map} in the linked or vanilla packs ({gameDataDir}). " +
                                            "A map that is only in the assembly kit has to be built (Build) or its pack linked first.");
        var n = 0;
        foreach (var file in files)
        {
            var owner = packs.FindOwner(file)!;
            var path = Path.Combine(compiledRoot, owner.Entries[file].Path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, owner.TryRead(file)!);
            if (++n % 200 == 0) log?.Invoke($"{n}/{files.Count} files");
        }
        log?.Invoke($"{map}: {n} files");
        return n;
    }

    /// <summary>The linked packs (highest priority first) in front of the vanilla packs.</summary>
    public static PackSet OpenWithLinked(string gameDataDir, IReadOnlyList<string> linkedPacks, Func<string, bool>? vanillaFilter = null)
    {
        var mods = linkedPacks.Where(File.Exists).Select(PackFile.Open);
        return new PackSet(mods.Concat(PackSet.OpenVanilla(gameDataDir, vanillaFilter).Packs));
    }

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
