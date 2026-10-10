using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtlasWH3.Core.Bob;

/// <summary>
/// A frozen fixture: a map's sources and BOB outputs (and the mod packs it ships in) as they were at one moment, so
/// parity tests keep comparing the same pair after the user edits the map in Terry or reprocesses it in BOB.
///
/// Layout of &lt;store&gt;\&lt;name&gt;:
///  - <c>maps\raw_data\…</c>, <c>maps\working_data\…</c>: each map's folders (terrain\campaigns, EmpireDesignData\campaign_maps,
///    campaign_maps), in the shape <see cref="ScratchKit.Options.MapSources"/> takes;
///  - <c>packs\</c>: the mod packs;
///  - <c>kit\</c>: an assembly kit to pass as <see cref="ProjectPaths.AssemblyKitRoot"/>: junctions into <c>maps\</c> for
///    the frozen folders, junctions to the live kit for everything else (binaries, DB, tiles, prefabs, other maps);
///  - <c>manifest.json</c>: every frozen file's size and time, the game build, when and from where it was frozen.
///
/// Files are hard links to the kit's and the data folder's (no disk space) when the store is on their volume, copies
/// otherwise. Terry and the pack install replace a file when they save it, which leaves the link holding the frozen
/// bytes. A tool that rewrote a file in place would change it here too: <see cref="Check"/> finds that (a hard link
/// shares its size and time), and the fixture then has to be frozen again.
/// </summary>
public static partial class KitFixture
{
    public const string MarkerFile = "atlaswh3_fixture.txt";
    public const string ManifestFile = "manifest.json";

    public sealed record FrozenFile(string Path, long Size, DateTime WriteTimeUtc);

    public sealed record Manifest
    {
        public required string Name { get; init; }
        public required DateTime Created { get; init; }
        public required string SourceKit { get; init; }
        public required string GameData { get; init; }
        public string? GameBuild { get; init; }
        public required IReadOnlyList<string> Maps { get; init; }
        public required IReadOnlyList<string> Packs { get; init; }
        public required IReadOnlyList<FrozenFile> Files { get; init; }
        /// <summary>Sources newer than their map's oldest BOB output when frozen (the pair may not match there).</summary>
        public IReadOnlyList<string> NewerSources { get; init; } = [];
    }

    public sealed record Fixture(string Root, Manifest Manifest)
    {
        public string KitRoot => Path.Combine(Root, "kit");
        public string MapsRoot => Path.Combine(Root, "maps");
        public string PacksRoot => Path.Combine(Root, "packs");
        public string? Pack(string name) =>
            Manifest.Packs.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } p ? Path.Combine(PacksRoot, p) : null;
        public bool HasMap(string map) => Manifest.Maps.Contains(map, StringComparer.OrdinalIgnoreCase);
        /// <summary>The <see cref="ScratchKit.Options.MapSources"/> for a BOB run on the frozen sources.</summary>
        public IReadOnlyDictionary<string, string> MapSources => Manifest.Maps.ToDictionary(m => m, _ => MapsRoot, StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Result(Fixture Fixture, int Linked, int Copied, long BytesCopied, int Junctions);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The default store: atlaswh3_fixtures next to the kit, on its volume, so the files can be hard links.</summary>
    public static string DefaultStore(string kitRoot) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(kitRoot).TrimEnd('\\', '/'))!, "atlaswh3_fixtures");

    /// <summary>A map's folders, relative to the kit (and to a fixture's maps\).</summary>
    public static IEnumerable<string> MapFolders(string map) =>
    [
        Path.Combine("raw_data", "terrain", "campaigns", map),
        Path.Combine("raw_data", "EmpireDesignData", "campaign_maps", map),
        Path.Combine("working_data", "terrain", "campaigns", map),
        Path.Combine("working_data", "campaign_maps", map),
    ];

    public static Fixture? Open(string store, string name)
    {
        var root = Path.Combine(store, name);
        var manifest = Path.Combine(root, ManifestFile);
        if (!File.Exists(manifest)) return null;
        return new Fixture(root, JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest))!);
    }

    public static IReadOnlyList<Fixture> List(string store) =>
        Directory.Exists(store)
            ? Directory.EnumerateDirectories(store).Select(d => Open(store, Path.GetFileName(d))).OfType<Fixture>().ToList()
            : [];

    /// <summary>Frozen files whose size or time changed, gone, or added since the freeze (empty: intact).</summary>
    public static IReadOnlyList<string> Check(Fixture fixture)
    {
        var drift = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixture.Manifest.Files)
        {
            known.Add(f.Path);
            var info = new FileInfo(Path.Combine(fixture.Root, f.Path));
            if (!info.Exists) drift.Add($"gone: {f.Path}");
            else if (info.Length != f.Size || info.LastWriteTimeUtc != f.WriteTimeUtc) drift.Add($"changed: {f.Path}");
        }
        foreach (var dir in new[] { fixture.MapsRoot, fixture.PacksRoot }.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                if (!known.Contains(Path.GetRelativePath(fixture.Root, file))) drift.Add($"added: {Path.GetRelativePath(fixture.Root, file)}");
        return drift;
    }

    /// <summary>Freezes <paramref name="maps"/> (their folders that exist) and <paramref name="packs"/> (file names in
    /// <paramref name="gameData"/>) into &lt;store&gt;\&lt;name&gt;. An existing fixture is replaced only with
    /// <paramref name="replace"/>.</summary>
    public static Result Freeze(string kitRoot, string gameData, string store, string name, IReadOnlyList<string> maps,
        IReadOnlyList<string> packs, bool replace = false, Action<string>? log = null)
    {
        kitRoot = Path.GetFullPath(kitRoot);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException($"bad fixture name '{name}'");
        if (maps.Count == 0) throw new ArgumentException("a fixture needs at least one map");
        foreach (var map in maps)
            if (!Directory.Exists(Path.Combine(kitRoot, "raw_data", "terrain", "campaigns", map)))
                throw new DirectoryNotFoundException($"{map}: no raw_data\\terrain\\campaigns\\{map} in {kitRoot}");
        foreach (var pack in packs)
            if (!File.Exists(Path.Combine(gameData, pack))) throw new FileNotFoundException($"no {pack} in {gameData}");

        var root = Path.GetFullPath(Path.Combine(store, name));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!File.Exists(Path.Combine(root, MarkerFile)))
                throw new InvalidOperationException($"{root} exists and is not an AtlasWH3 fixture (no {MarkerFile}); refusing to write into it");
            if (!replace) throw new InvalidOperationException($"fixture {name} exists ({root}); freeze again with --replace");
            log?.Invoke($"removing the old {name}...");
            DeleteTree(root);
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, MarkerFile),
            $"AtlasWH3 fixture {name}: frozen map sources, BOB outputs and packs (hard links, or copies). kit\\ holds junctions to the live kit: " +
            "delete this folder with AtlasWH3 (fixture delete), never with a tool that follows junctions.\n");

        int linked = 0, copied = 0, junctions = 0;
        long bytes = 0;
        var files = new List<FrozenFile>();
        void Freeze(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (Link(to, from)) linked++;
            else
            {
                File.Copy(from, to);
                copied++;
                bytes += new FileInfo(from).Length;
            }
            var info = new FileInfo(to);
            files.Add(new FrozenFile(Path.GetRelativePath(root, to), info.Length, info.LastWriteTimeUtc));
        }

        var frozen = new List<string>();
        foreach (var map in maps)
            foreach (var rel in MapFolders(map))
            {
                var from = Path.Combine(kitRoot, rel);
                if (!Directory.Exists(from)) continue;
                log?.Invoke($"{rel}...");
                frozen.Add(rel);
                Directory.CreateDirectory(Path.Combine(root, "maps", rel));
                foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    Freeze(file, Path.Combine(root, "maps", rel, Path.GetRelativePath(from, file)));
            }
        foreach (var pack in packs)
        {
            log?.Invoke($"{pack}...");
            Freeze(Path.Combine(gameData, pack), Path.Combine(root, "packs", pack));
        }

        log?.Invoke("kit...");
        // The kit: real folders down to each frozen folder, a junction into maps\ for it, and a junction to the live kit
        // for every other folder beside them; the files beside them are links (or copies), not frozen.
        void Mirror(string rel)
        {
            var live = Path.Combine(kitRoot, rel);
            var target = Path.Combine(root, "kit", rel);
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(live))
            {
                var childRel = Path.Combine(rel, Path.GetFileName(entry));
                var child = Path.Combine(root, "kit", childRel);
                if (frozen.Contains(childRel, StringComparer.OrdinalIgnoreCase)) { Junction(child, Path.Combine(root, "maps", childRel)); junctions++; }
                else if (frozen.Any(f => f.StartsWith(childRel + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) Mirror(childRel);
                else if (Directory.Exists(entry)) { Junction(child, entry); junctions++; }
                else if (!Link(child, entry)) File.Copy(entry, child);
            }
        }
        Mirror("");

        var manifest = new Manifest
        {
            Name = name, Created = DateTime.UtcNow, SourceKit = kitRoot, GameData = Path.GetFullPath(gameData), GameBuild = GameBuild(gameData),
            Maps = maps.ToList(), Packs = packs.ToList(), Files = files, NewerSources = maps.SelectMany(m => NewerSources(root, m)).ToList(),
        };
        File.WriteAllText(Path.Combine(root, ManifestFile), JsonSerializer.Serialize(manifest, Json));
        log?.Invoke($"fixture {name}: {linked} files linked, {copied} copied ({bytes / 1e9:F2} GB), {junctions} junctions");
        return new Result(new Fixture(root, manifest), linked, copied, bytes, junctions);
    }

    /// <summary>Deletes a fixture: the junctions themselves (never what they point to), then the frozen files.</summary>
    public static void Delete(string store, string name)
    {
        var root = Path.Combine(store, name);
        if (!File.Exists(Path.Combine(root, MarkerFile))) throw new InvalidOperationException($"{root} is not an AtlasWH3 fixture; refusing to delete anything in it");
        DeleteTree(root);
    }

    private static void DeleteTree(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(sub, false);
            else DeleteTree(sub);
        }
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            // a hard link shares its attributes with the kit's file: clear read-only only when it is in the way
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(dir, false);
    }

    /// <summary>Source files written after the map's oldest BOB output (top-level files of its terrain output folder).</summary>
    private static IEnumerable<string> NewerSources(string root, string map)
    {
        var outputs = Path.Combine(root, "maps", "working_data", "terrain", "campaigns", map);
        var sources = Path.Combine(root, "maps", "raw_data", "terrain", "campaigns", map);
        if (!Directory.Exists(outputs) || !Directory.Exists(sources)) return [];
        var built = Directory.EnumerateFiles(outputs).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MaxValue).Min();
        return Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".terry.user", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(f) > built)
            .Select(f => Path.GetRelativePath(Path.Combine(root, "maps"), f)).ToList();
    }

    /// <summary>The Steam build id of the game the data folder belongs to (steamapps\appmanifest_1142710.acf).</summary>
    public static string? GameBuild(string gameData)
    {
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(gameData).TrimEnd('\\', '/'))));
        var acf = steamapps is null ? null : Path.Combine(steamapps, "appmanifest_1142710.acf");
        return acf is not null && File.Exists(acf) && BuildId().Match(File.ReadAllText(acf)) is { Success: true } m ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"buildid\"\\s+\"(\\d+)\"")]
    private static partial Regex BuildId();

    private static bool Link(string link, string existing) => OperatingSystem.IsWindows() && CreateHardLinkW(link, existing, IntPtr.Zero);

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"junction {link} -> {target}: {error.Trim()}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
