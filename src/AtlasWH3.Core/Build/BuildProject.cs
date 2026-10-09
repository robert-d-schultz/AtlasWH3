using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlasWH3.Core.Build;

/// <summary>
/// A map project (<c>.atlaswh3</c>, JSON): which map, which assembly kit, and how to build it. Paths may use the tokens
/// <c>{ak} {map} {game} {out} {pack} {project}</c>; relative paths are relative to the project file's folder.
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
    /// working_data, then a new pack of the compiled map.</summary>
    public static BuildProject CreateDefault(string map, string assemblyKit = "") => new()
    {
        Name = map,
        Map = map,
        AssemblyKit = assemblyKit,
        Build = new BuildProfile
        {
            Pack = new PackSettings
            {
                Output = "{project}\\{map}.pack",
                Contents =
                [
                    new PackContent { Source = "{out}\\terrain\\campaigns\\{map}", Path = "terrain/campaigns/{map}" },
                    new PackContent { Source = "{out}\\campaign_maps\\{map}\\camera_heightmap.png", Path = "campaign_maps/{map}/camera_heightmap.png" },
                    new PackContent { Source = "{out}\\campaign_maps\\{map}\\display\\trees\\trees.campaign_tree_list", Path = "campaign_maps/{map}/display/trees/trees.campaign_tree_list" },
                ],
            },
        },
    };

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
            .Replace("{map}", Map)
            .Replace("{game}", paths.GameDataDir);
        // {out} and {pack} may themselves contain the tokens above
        if (s.Contains("{out}")) s = s.Replace("{out}", OutputDir(paths));
        if (s.Contains("{pack}")) s = s.Replace("{pack}", Build.Pack.Output.Length > 0 ? Resolve(Build.Pack.Output, paths) : "");
        return s;
    }

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
    /// <summary>Folders under the compiled terrain folder deleted before Compile (stale generated files).</summary>
    public List<string> Clean { get; set; } = [];
    /// <summary>When set, a rolling copy of the kit's raw terrain and the compiled terrain is kept here before Compile.</summary>
    public string Backup { get; set; } = "";
    public List<CustomStep> CustomSteps { get; set; } = [];
    public PackSettings Pack { get; set; } = new();
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
    /// <summary>New: a pack with only <see cref="Contents"/>. Merge: <see cref="Base"/> (default: the output pack itself)
    /// with <see cref="Contents"/> replacing or adding files.</summary>
    public PackMode Mode { get; set; } = PackMode.New;
    public string Output { get; set; } = "";
    public string Base { get; set; } = "";
    /// <summary>Merge: pack folders (e.g. terrain/campaigns/{map}/) whose base files are dropped unless re-added.</summary>
    public List<string> ReplaceDirs { get; set; } = [];
    /// <summary>Files or folders to pack; a later entry wins over an earlier one for the same pack path.</summary>
    public List<PackContent> Contents { get; set; } = [];
}

public sealed class PackContent
{
    /// <summary>File or folder on disk.</summary>
    public string Source { get; set; } = "";
    /// <summary>Path inside the pack (the folder for a folder source).</summary>
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
