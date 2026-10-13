namespace AtlasWH3.Core;

/// <summary>Where the tool finds the map's sources (the assembly kit), the game's packs and the mod packs, and where it
/// writes its own output.</summary>
public sealed record ProjectPaths
{
    public string MapName { get; init; } = MapCatalog.DefaultMap;
    /// <summary>Assembly kit install. Override from the CLI with <c>--ak &lt;root&gt;</c>.</summary>
    public string AssemblyKitRoot { get; init; } = Defaults.AssemblyKit;
    public string GameDataDir { get; init; } = Defaults.GameData;
    public string OutputRoot { get; init; } = Defaults.Output;
    public string CacheRoot { get; init; } = Defaults.Cache;
    /// <summary>Mod packs (a project's, or <c>--pack &lt;file.pack&gt;</c>, repeatable, highest priority first): read
    /// before the vanilla packs.</summary>
    public IReadOnlyList<string> ModPacks { get; init; } = [];
    /// <summary>The Terry project's tile_map.png: what the tile map editor edits and tile_list builds from.</summary>
    public string TileMapPng => Path.Combine(AkTerrainDir, "tile_map.png");

    public string AkTerrainDir => Path.Combine(AssemblyKitRoot, "raw_data", "terrain", "campaigns", MapName);
    /// <summary>The kit's working_data: BOB's outputs. Written to (a build profile's target, the battle catchments for
    /// BOB's pack step), never read as an input.</summary>
    public string AkWorkingDir => Path.Combine(AssemblyKitRoot, "working_data");
    public string AkDesignCampaignMapDir => Path.Combine(AssemblyKitRoot, "raw_data", "EmpireDesignData", "campaign_maps", MapName);
    public string TextureCacheDir => Path.Combine(CacheRoot, "terrain_textures");
    /// <summary>The CLI's default build target for the map (laid out like working_data).</summary>
    public string BuildTarget => Path.Combine(OutputRoot, "compiled", MapName);

    /// <summary>Applies <c>--map &lt;name&gt;</c>, <c>--ak &lt;assembly kit root&gt;</c> and
    /// <c>--pack &lt;mod pack&gt;</c> (repeatable)
    /// and returns the arguments that are left.</summary>
    public static ProjectPaths FromArgs(string[] args, out string[] rest)
    {
        var paths = new ProjectPaths();
        var list = args.ToList();
        string? Take(string name)
        {
            var i = list.FindIndex(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            if (i + 1 >= list.Count) throw new ArgumentException($"{name} needs a value");
            var value = list[i + 1];
            list.RemoveRange(i, 2);
            return value;
        }
        if (Take("--map") is { } map) paths = paths with { MapName = map };
        if (Take("--ak") is { } ak) paths = paths with { AssemblyKitRoot = ak };
        var packs = new List<string>();
        while (Take("--pack") is { } pack) packs.Add(Path.GetFullPath(pack));
        if (packs.Count > 0) paths = paths with { ModPacks = packs };
        rest = list.ToArray();
        return paths;
    }
}

/// <summary>Default paths: <see cref="AppSettings"/> first, then the Steam install, then %LocalAppData%\AtlasWH3.
/// Read on access, so settings saved during the session (first-run setup) apply to new <see cref="ProjectPaths"/>.</summary>
public static class Defaults
{
    private static readonly Lazy<string?> Detected = new(GameSetup.FindGameFolder);

    public static string LocalData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtlasWH3");
    public static string GameFolder => Or(AppSettings.Current.GameFolder,
        Detected.Value ?? @"C:\Program Files (x86)\Steam\steamapps\common\Total War WARHAMMER III");
    public static string AssemblyKit => Or(AppSettings.Current.AssemblyKit, Path.Combine(GameFolder, "assembly_kit"));
    public static string GameData => Path.Combine(GameFolder, "data");
    /// <summary>AtlasWH3's own data folder: <see cref="LocalData"/> unless the settings move it.</summary>
    public static string DataFolder => Or(AppSettings.Current.DataFolder, LocalData);
    public static string Output => Path.Combine(DataFolder, "output");
    public static string Cache => Path.Combine(DataFolder, "cache");

    private static string Or(string setting, string fallback) => string.IsNullOrWhiteSpace(setting) ? fallback : setting;
}
