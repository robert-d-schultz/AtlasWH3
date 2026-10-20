using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlasWH3.Core.Build;

/// <summary>
/// A build project (<c>.atlaswh3</c>, JSON): which map, and how to build it: what Terry's project doesn't hold (the mod
/// packs, custom steps, packing and installing). The Assembly Kit and the game come from the settings. Paths may use the
/// tokens <c>{ak} {map} {game} {out} {pack} {project}</c>; relative paths are relative to the project
/// file's folder.
/// </summary>
public sealed class BuildProject
{
    public const string Extension = ".atlaswh3";

    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    /// <summary>Mod packs searched before the vanilla packs (highest priority first).</summary>
    public List<string> ModPacks { get; set; } = [];
    public BuildProfile Build { get; set; } = new();

    [JsonIgnore] public string? FilePath { get; private set; }
    [JsonIgnore] public string Folder => FilePath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(FilePath)!;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static BuildProject Load(string path)
    {
        var full = Path.GetFullPath(path);
        var project = JsonSerializer.Deserialize<BuildProject>(File.ReadAllText(full), Json)
                      ?? throw new InvalidDataException($"{path} is empty");
        project.FilePath = full;
        if (string.IsNullOrWhiteSpace(project.Map)) throw new InvalidDataException($"{path}: \"map\" is required");
        // older projects wrote the old default, the Assembly Kit's working_data: AtlasWH3's own folder now
        if (project.Build.Output.Equals(@"{ak}\working_data", StringComparison.OrdinalIgnoreCase)) project.Build.Output = "";
        project.Build.MigrateLegacyPacks();
        // the packs are read first anyway: not listed again among the other packs
        foreach (var own in new[] { project.Build.PackFile, project.Build.PiecesPackFile }.Where(f => f.Length > 0))
        {
            var paths = new ProjectPaths();
            var pack = project.Resolve(own, paths);
            project.ModPacks.RemoveAll(m => project.Resolve(m, paths).Equals(pack, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var c in project.Build.CustomSteps.Where(c => c.RunAt == CustomStepStage.AfterInstall)) c.RunAt = CustomStepStage.AfterPack;
        return project;
    }

    public void Save(string? path = null)
    {
        FilePath = Path.GetFullPath(path ?? FilePath ?? throw new InvalidOperationException("no path"));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>A new project for <paramref name="map"/> with the standard build: every native step, then the compiled
    /// files written into the map's pack, the first of <paramref name="modPacks"/> (the campaign's own, with its DB,
    /// map_data.esf and pathfinding.ppd; its other files are kept). Without one, a new pack beside the project.</summary>
    public static BuildProject CreateDefault(string map, IReadOnlyList<string>? modPacks = null) => new()
    {
        Name = map,
        Map = map,
        ModPacks = [.. (modPacks ?? []).Skip(1)],
        Build = new BuildProfile { PackFile = modPacks is [var pack, ..] ? pack : "{project}\\{map}.pack" },
    };


    /// <summary>The editor / builder paths for this project, on top of <paramref name="defaults"/>: the map, and the
    /// packs read before the game's (the project's pack, when it exists, then the other mod packs).</summary>
    public ProjectPaths ToPaths(ProjectPaths? defaults = null)
    {
        var p = (defaults ?? new ProjectPaths()) with { MapName = Map };
        var packs = new List<string>();
        if (PackOutput(p) is { } pack && File.Exists(pack)) packs.Add(pack);
        if (PiecesPackOutput(p) is { } pieces && File.Exists(pieces)) packs.Add(pieces);
        packs.AddRange(ModPacks.Select(m => Resolve(m, p)));
        packs = packs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (packs.Count > 0) p = p with { ModPacks = packs };
        return p;
    }

    /// <summary>Expands the tokens and makes a path absolute (relative to the project folder).</summary>
    public string Resolve(string value, ProjectPaths paths)
    {
        var s = Expand(value, paths);
        return Path.IsPathRooted(s) ? Path.GetFullPath(s) : Path.GetFullPath(Path.Combine(Folder, s));
    }

    /// <summary>Expands the tokens only (for command lines).</summary>
    public string Expand(string value, ProjectPaths paths)
    {
        var s = value
            .Replace("{project}", Folder)
            .Replace("{ak}", paths.AssemblyKitRoot)
            .Replace("{map}", Map)
            .Replace("{game}", paths.GameDataDir);
        // {out} and {pack} may themselves contain the tokens above
        if (s.Contains("{out}")) s = s.Replace("{out}", OutputDir(paths));
        if (s.Contains("{pack}")) s = s.Replace("{pack}", PackOutput(paths) ?? "");
        return s;
    }

    /// <summary>The pack the build writes into, or null when none is set ({pack}, ATLASWH3_PACK).</summary>
    public string? PackOutput(ProjectPaths paths) => Build.PackFile.Length > 0 ? Resolve(Build.PackFile, paths) : null;

    /// <summary>The pack the event-area pieces go into, or null when they go into <see cref="PackOutput"/>.</summary>
    public string? PiecesPackOutput(ProjectPaths paths) => Build.PiecesPackFile.Length > 0 ? Resolve(Build.PiecesPackFile, paths) : null;

    /// <summary>Where Compile writes (laid out like working_data): AtlasWH3's output\compiled\&lt;map&gt; unless the
    /// project (or the CLI's --out) names another folder.</summary>
    public string OutputDir(ProjectPaths paths) => Build.Output.Length > 0 ? Resolve(Build.Output, paths) : (paths with { MapName = Map }).BuildTarget;
}

/// <summary>How a project builds. Segments run in this order: Validate, custom steps "beforeCompile", Compile,
/// "afterCompile", Pack, "afterPack".</summary>
public sealed class BuildProfile
{
    /// <summary>Compile target, laid out like working_data; empty = AtlasWH3's output\compiled\&lt;map&gt;.</summary>
    public string Output { get; set; } = "";
    /// <summary>Native steps Compile runs; empty = all of them.</summary>
    public List<string> Steps { get; set; } = [];
    /// <summary>Tile-map pre-flight error codes accepted for this map (<see cref="Campaign.TileMapCheck.TileMapValidator.ErrorCodes"/>).</summary>
    public List<string> AcceptTileMap { get; set; } = [];
    /// <summary>patch_mask.dds: fitted to the whole map (default) or BOB's, with its north-band bug.</summary>
    public Campaign.PatchMaskMode PatchMask { get; set; } = Campaign.PatchMaskMode.Fitted;
    /// <summary>lookup: regions moved to the end of the lookup palette, past the 1024 entries the game reads (region keys,
    /// or text files of them, relative to the project folder).</summary>
    public List<string> LookupLast { get; set; } = [];
    /// <summary>global_props: objects reaching outside the quadtree root (the map bounds grown by quadtree_margin) go in
    /// the nearest cell (the box clamped into the root) instead of being dropped as BOB drops them.</summary>
    public bool KeepOutsideQuadtree { get; set; }
    /// <summary>heightmaps: shroud_heights.dds is 0 over the map's westmost 10 units
    /// (<see cref="Campaign.Terrain.ShroudHeights.WestEdgeZeroWidth"/>), for a westward visual extension.</summary>
    public bool ZeroShroudWestEdge { get; set; }
    public List<CustomStep> CustomSteps { get; set; } = [];
    /// <summary>The map's .pack: Pack writes the compiled files into it (replacing the build's files, keeping everything
    /// else) or creates it. It is also read first, before the other mod packs.</summary>
    public string PackFile { get; set; } = "";
    /// <summary>Optional: the .pack the event-area pieces go into (the map's pieces\ and the devastated map's folder), for
    /// a campaign that ships them in a pack of their own (Old World). Empty: they go into <see cref="PackFile"/>.</summary>
    public string PiecesPackFile { get; set; } = "";
    /// <summary>Whether Build all runs Pack (the Build window's Pack tick).</summary>
    public bool WritePack { get; set; } = true;
    /// <summary>Keep one copy of the pack as it was before Pack writes it (AtlasWH3's output\pack_backups).</summary>
    public bool BackupPack { get; set; } = true;

    // ---- older projects: "packs" / "pack" (output, merge base) and "install", read once into PackFile

    public sealed class LegacyPack
    {
        public bool Enabled { get; set; } = true;
        public string Output { get; set; } = "";
    }

    public sealed class LegacyInstall
    {
        public bool Enabled { get; set; }
        public bool Backup { get; set; } = true;
    }

    private List<LegacyPack>? _legacyPacks;
    private LegacyInstall? _legacyInstall;

    [JsonPropertyName("packs")]
    public List<LegacyPack>? LegacyPacks { get => null; set => _legacyPacks = value; }
    [JsonPropertyName("pack")]
    public LegacyPack? LegacySinglePack { get => null; set { if (value is not null) (_legacyPacks ??= []).Insert(0, value); } }
    [JsonPropertyName("install")]
    public LegacyInstall? LegacyInstallSettings { get => null; set => _legacyInstall = value; }

    /// <summary>An older project's packs become <see cref="PackFile"/> and, for a second one (Old World's pieces),
    /// <see cref="PiecesPackFile"/>: where each ended up (the game's data folder when it was installed, else its
    /// output).</summary>
    internal void MigrateLegacyPacks()
    {
        if (_legacyPacks is { Count: > 0 } packs && PackFile.Length == 0)
        {
            string Where(LegacyPack p) => _legacyInstall?.Enabled == true ? "{game}\\" + Path.GetFileName(p.Output) : p.Output;
            var used = packs.Where(p => p.Output.Length > 0).ToList();
            if (used.Count > 0) PackFile = Where(used[0]);
            if (used.Count > 1) PiecesPackFile = Where(used[1]);
            WritePack = packs.Any(p => p.Enabled);
        }
        if (_legacyInstall is { } install) BackupPack = install.Backup;
        _legacyPacks = null;
        _legacyInstall = null;
    }
}

/// <summary>When a custom step runs. AfterInstall is an older project's: it runs after Pack, which now writes the pack in
/// place.</summary>
public enum CustomStepStage { BeforeCompile, AfterCompile, AfterPack, AfterInstall }

/// <summary>An external command run as part of the build (python scripts, CAIME, an RPFM startpos build, ...).</summary>
public sealed class CustomStep
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public CustomStepStage RunAt { get; set; } = CustomStepStage.AfterCompile;
    public string Command { get; set; } = "";
    /// <summary>One command-line string; tokens are expanded, quote paths with spaces.</summary>
    public string Arguments { get; set; } = "";
    public string WorkingDir { get; set; } = "{project}";
    public int TimeoutSeconds { get; set; } = 3600;
    /// <summary>A non-zero exit is logged but does not stop the build.</summary>
    public bool ContinueOnError { get; set; }
}

