namespace AtlasWH3.Core;

/// <summary>Where the tool reads compiled data, writes assembly-kit sources, and finds game packs.</summary>
public sealed record ProjectPaths
{
    public string MapName { get; init; } = MapCatalog.DefaultMap;
    /// <summary>Folder holding the compiled terrain\ and campaign_maps\ trees (vanilla files extracted at first run).</summary>
    public string VanillaRoot { get; init; } = Defaults.CompiledRoot;
    /// <summary>Assembly kit install. Override from the CLI with <c>--ak &lt;root&gt;</c>.</summary>
    public string AssemblyKitRoot { get; init; } = Defaults.AssemblyKit;
    public string GameDataDir { get; init; } = Defaults.GameData;
    /// <summary>Folder with db TSVs (campaign_tree_ids_tables etc.; extracted at first run, or an RPFM export).</summary>
    public string DbTsvRoot { get; init; } = Defaults.DbTsv;
    public string OutputRoot { get; init; } = Defaults.Output;
    public string CacheRoot { get; init; } = Defaults.Cache;
    /// <summary>Mod packs (<c>--pack &lt;file.pack&gt;</c>, repeatable, highest priority first): searched before the
    /// vanilla packs for assets, and, for the map's compiled files (tile_list.bin, texture_arrays.xml), before the
    /// kit's working_data and the vanilla folder.</summary>
    public IReadOnlyList<string> ModPacks { get; init; } = [];
    /// <summary>Where the tile map editor and the tile_list step read tile_map.png (kit, a file, or a pack entry) and
    /// where the editor saves it.</summary>
    public Campaign.TileMapCheck.TileMapSource TileMap { get; init; } = Defaults.TileMap;

    public string TerrainDir => Path.Combine(VanillaRoot, "terrain", "campaigns", MapName);
    public string CampaignMapDir => Path.Combine(VanillaRoot, "campaign_maps", MapName);
    public string HeightMapDds => Path.Combine(TerrainDir, "lf_height_map.dds");
    public string SeaHeightMapDds => Path.Combine(TerrainDir, "lf_sea_height_map.dds");
    public string GlobalPropsBin => Path.Combine(TerrainDir, "global_props.bin");
    public string BlendDds => Path.Combine(TerrainDir, "global_map", "global_blend.dds");
    public string TextureArraysXml => Path.Combine(TerrainDir, "global_map", "texture_arrays.xml");
    public string TreeList => Path.Combine(CampaignMapDir, "display", "trees", "trees.campaign_tree_list");
    public string TreeIdsTsv => Path.Combine(DbTsvRoot, "campaign_tree_ids_tables", "data__.tsv");
    public string TreeVariantsTsv => Path.Combine(DbTsvRoot, "campaign_tree_variants_tables", "data__.tsv");
    public string TreeTypeCulturesTsv => Path.Combine(DbTsvRoot, "campaign_tree_type_cultures_tables", "data__.tsv");

    public string AkTerrainDir => Path.Combine(AssemblyKitRoot, "raw_data", "terrain", "campaigns", MapName);
    public string AkWorkingDir => Path.Combine(AssemblyKitRoot, "working_data");
    public string AkWorkingCampaignMapDir => Path.Combine(AkWorkingDir, "campaign_maps", MapName);
    public string AkDesignCampaignMapDir => Path.Combine(AssemblyKitRoot, "raw_data", "EmpireDesignData", "campaign_maps", MapName);
    public string TextureCacheDir => Path.Combine(CacheRoot, "terrain_textures");

    /// <summary>Applies <c>--map &lt;name&gt;</c>, <c>--root &lt;compiled root&gt;</c>, <c>--ak &lt;assembly kit root&gt;</c> and
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
        if (Take("--root") is { } root) paths = paths with { VanillaRoot = root };
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
    public static string CompiledRoot => Or(AppSettings.Current.CompiledRoot, Path.Combine(LocalData, "vanilla"));
    public static string DbTsv => Or(AppSettings.Current.DbTsvFolder, Path.Combine(LocalData, "db"));
    public static string Output => Or(AppSettings.Current.OutputFolder, Path.Combine(LocalData, "output"));
    public static Campaign.TileMapCheck.TileMapSource TileMap => AppSettings.Current.TileMap ?? Campaign.TileMapCheck.TileMapSource.Kit;
    public static string Cache => Or(AppSettings.Current.CacheFolder, Path.Combine(LocalData, "cache"));

    private static string Or(string setting, string fallback) => string.IsNullOrWhiteSpace(setting) ? fallback : setting;
}
