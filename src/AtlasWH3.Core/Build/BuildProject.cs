using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlasWH3.Core.Build;

/// <summary>
/// A map project (<c>.atlaswh3</c>, JSON): which map, which assembly kit, and how to build it. Paths may use the tokens
/// <c>{ak} {map} {devastated} {game} {out} {pack} {project}</c>; relative paths are relative to the project file's folder.
/// </summary>
public sealed class BuildProject
{
    public const string Extension = ".atlaswh3";

    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    /// <summary>Assembly kit root; empty = the default kit.</summary>
    public string AssemblyKit { get; set; } = "";
    /// <summary>Game data folder; empty = the default.</summary>
    public string GameData { get; set; } = "";
    /// <summary>Compiled-data root the editors read (terrain\ and campaign_maps\); empty = the default.</summary>
    public string CompiledRoot { get; set; } = "";
    /// <summary>Mod packs searched before the vanilla packs (highest priority first).</summary>
    public List<string> ModPacks { get; set; } = [];
    /// <summary>Where tile_map.png is read (kit, a file, or a pack entry) and where the editor saves it; null = the
    /// app setting (default the kit).</summary>
    public Campaign.TileMapCheck.TileMapSource? TileMap { get; set; }
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
        return project;
    }

    public void Save(string? path = null)
    {
        FilePath = Path.GetFullPath(path ?? FilePath ?? throw new InvalidOperationException("no path"));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>A new project for <paramref name="map"/> with the standard build: every native step into the kit's
    /// working_data, then one pack of the files the build wrote. With a mod pack (the map's own, linked for its DB
    /// tables and assets) the build is merged into a copy of it next to the project, which Install puts in the data
    /// folder: the mod's DB, scripts, battle terrain, CAIME's and the game's files stay as they are. Without one, a
    /// new pack of the build alone.</summary>
    public static BuildProject CreateDefault(string map, string assemblyKit = "", IReadOnlyList<string>? modPacks = null)
    {
        var pack = new PackSettings
        {
            Output = "{project}\\{map}.pack",
            Contents = [new PackContent { Source = PackContent.CompiledSource }],
        };
        var install = new InstallSettings();
        if (modPacks is [var modPack, ..])
        {
            pack.Mode = PackMode.Merge;
            pack.Base = modPack;
            pack.Output = "{project}\\" + System.IO.Path.GetFileName(modPack);
            install.Enabled = true;
        }
        return new BuildProject
        {
            Name = map,
            Map = map,
            AssemblyKit = assemblyKit,
            ModPacks = [.. modPacks ?? []],
            Build = new BuildProfile { Packs = [pack], Install = install },
        };
    }


    /// <summary>The editor / builder paths for this project, on top of <paramref name="defaults"/>.</summary>
    public ProjectPaths ToPaths(ProjectPaths? defaults = null)
    {
        var p = (defaults ?? new ProjectPaths()) with { MapName = Map };
        if (AssemblyKit.Length > 0) p = p with { AssemblyKitRoot = Resolve(AssemblyKit, p) };
        if (GameData.Length > 0) p = p with { GameDataDir = Resolve(GameData, p) };
        if (CompiledRoot.Length > 0) p = p with { VanillaRoot = Resolve(CompiledRoot, p) };
        if (ModPacks.Count > 0) p = p with { ModPacks = ModPacks.Select(m => Resolve(m, p)).ToList() };
        if (TileMap is { } tm) { var q = p; p = p with { TileMap = tm.ResolvePaths(v => Resolve(v, q)) }; }
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
            .Replace("{devastated}", DevastatedMap)
            .Replace("{map}", Map)
            .Replace("{game}", paths.GameDataDir);
        // {out} and {pack} may themselves contain the tokens above
        if (s.Contains("{out}")) s = s.Replace("{out}", OutputDir(paths));
        if (s.Contains("{pack}")) s = s.Replace("{pack}", PackOutput(paths) ?? "");
        return s;
    }

    /// <summary>The first pack's output file, or null when no pack has one ({pack}, ATLASWH3_PACK).</summary>
    public string? PackOutput(ProjectPaths paths) =>
        Build.Packs.FirstOrDefault(p => p.Output.Length > 0) is { } p ? Resolve(p.Output, paths) : null;

    /// <summary>The devastated project's name ({devastated}): the profile's, else &lt;map without _1&gt;_devastate_1.</summary>
    [JsonIgnore]
    public string DevastatedMap => Build.DevastatedMap is { Length: > 0 } d && !d.Equals("none", StringComparison.OrdinalIgnoreCase)
        ? d : Campaign.DevastationPiecesStep.DevastatedName(Map);

    /// <summary>Where Compile writes (laid out like working_data).</summary>
    public string OutputDir(ProjectPaths paths) => Resolve(Build.Output, paths);
}

/// <summary>How a project builds. Segments run in this order: Validate, custom steps "beforeCompile", Compile,
/// "afterCompile", Pack, "afterPack", Install, "afterInstall".</summary>
public sealed class BuildProfile
{
    /// <summary>Compile target, laid out like working_data. The default overwrites the kit's own working_data, as BOB did.</summary>
    public string Output { get; set; } = "{ak}\\working_data";
    /// <summary>Native steps Compile runs; empty = all of them.</summary>
    public List<string> Steps { get; set; } = [];
    /// <summary>Tile-map pre-flight error codes accepted for this map (e.g. layout.mesh_columns).</summary>
    public List<string> AcceptTileMap { get; set; } = [];
    /// <summary>patch_mask.dds: fitted to the whole map (default) or BOB's, with its north-band bug.</summary>
    public Campaign.PatchMaskMode PatchMask { get; set; } = Campaign.PatchMaskMode.Fitted;
    /// <summary>lookup: regions moved to the end of the lookup palette, past the 1024 entries the game reads (region keys,
    /// or text files of them, relative to the project folder).</summary>
    public List<string> LookupLast { get; set; } = [];
    /// <summary>devastation_pieces: the devastated project whose pieces it also cuts. Empty: &lt;map&gt;_devastate_1 when the
    /// kit has it; "none": no devastated pieces.</summary>
    public string DevastatedMap { get; set; } = "";
    /// <summary>Folders under the compiled terrain folder deleted before Compile (stale generated files).</summary>
    public List<string> Clean { get; set; } = [];
    /// <summary>When set, a rolling copy of the kit's raw terrain and the compiled terrain is kept here before Compile.</summary>
    public string Backup { get; set; } = "";
    public List<CustomStep> CustomSteps { get; set; } = [];
    /// <summary>The packs Pack writes, in order (e.g. Old World ships its map in one pack and the event-area pieces in a
    /// second).</summary>
    public List<PackSettings> Packs { get; set; } = [];
    /// <summary>An older project's single pack (read only; it becomes the first of <see cref="Packs"/>).</summary>
    [JsonPropertyName("pack")]
    public PackSettings? LegacyPack { get => null; set { if (value is not null) Packs.Insert(0, value); } }
    public InstallSettings Install { get; set; } = new();
}

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

public enum PackMode { New, Merge }

public sealed class PackSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>A name for the build log (default: the output's file name).</summary>
    public string Name { get; set; } = "";
    /// <summary>New: a pack with only <see cref="Contents"/>. Merge: <see cref="Base"/> (default: the output pack itself)
    /// with <see cref="Contents"/> replacing or adding files.</summary>
    public PackMode Mode { get; set; } = PackMode.New;
    public string Output { get; set; } = "";
    public string Base { get; set; } = "";
    /// <summary>Merge: more pack folders whose base files are dropped unless re-added (only where the pack adds files).
    /// The steps' own files need none: river models and pieces the build no longer writes are always dropped.</summary>
    public List<string> ReplaceDirs { get; set; } = [];
    /// <summary>Files or folders to pack; a later entry wins over an earlier one for the same pack path.</summary>
    public List<PackContent> Contents { get; set; } = [];
    /// <summary>Pack paths (files, or folders ending in /) left out of <see cref="Contents"/>, e.g. the pieces that go to
    /// another pack.</summary>
    public List<string> Exclude { get; set; } = [];
}

public sealed class PackContent
{
    /// <summary>The source "the files the build wrote" (<see cref="BuildManifest"/>), each at its path under the output.</summary>
    public const string CompiledSource = "{compiled}";

    /// <summary>File or folder on disk, or <see cref="CompiledSource"/>.</summary>
    public string Source { get; set; } = "";
    /// <summary>Path inside the pack (the folder for a folder source). For <see cref="CompiledSource"/>, the pack folder
    /// it is limited to (empty: all of it).</summary>
    public string Path { get; set; } = "";
    /// <summary>Missing source is a warning instead of an error.</summary>
    public bool Optional { get; set; }
}

public sealed class InstallSettings
{
    public bool Enabled { get; set; }
    /// <summary>Keep one copy of the pack being replaced (in the app's backup folder).</summary>
    public bool Backup { get; set; } = true;
}
