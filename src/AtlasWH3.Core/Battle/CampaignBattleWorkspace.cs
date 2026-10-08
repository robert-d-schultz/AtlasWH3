using AtlasWH3.Formats.Battle;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Battle;

/// <summary>
/// The campaign battles of one campaign map: its campaign-battle terrain's compiled battle_locations_map.bin
/// (catchment areas), the settlements of its map_data.esf, and the redirect checks. Everything is read from the linked
/// packs first, then the game's packs; nothing there is written. Saving goes to the output folder, a new mod pack or the
/// kit's working_data (see <see cref="SourceGuard"/>).
/// </summary>
public sealed class CampaignBattleWorkspace
{
    public const string DefaultTerrainFolder = "3k_main_map";

    public required ProjectPaths Paths { get; init; }
    /// <summary>terrain\battles\&lt;folder&gt; the campaign's battles are cut from (campaign_map_playable_areas.terrain_folder;
    /// every 3K campaign uses 3k_main_map).</summary>
    public required string TerrainFolder { get; init; }
    public required BattleLocations Map { get; init; }
    /// <summary>Where the catchments came from: a pack name or a file path.</summary>
    public required string Source { get; init; }
    public CampaignSettlements? Campaign { get; init; }
    public string? CampaignSource { get; init; }
    public ModRegionData? Mod { get; init; }
    public BattleTileMapFile? Tiles { get; init; }
    public required RedirectCatalog Redirects { get; init; }
    public required RegionBattleAnalyzer Regions { get; init; }
    /// <summary>map_path → type of the battles_tables rows found (mod data, then the DB TSV folder).</summary>
    public IReadOnlyDictionary<string, string> KnownBattles { get; init; } = new Dictionary<string, string>();
    /// <summary>Battle-map folders (terrain\battles\*) present in the packs, for the redirect picker.</summary>
    public IReadOnlyList<string> BattleFolders { get; init; } = [];
    public List<string> Notes { get; } = [];
    /// <summary>Redirect folders that get no battles_tables row from Save (their pack already ships one).</summary>
    public HashSet<string> NoRowFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    private byte[] _saved = [];

    /// <summary>True when the catchments differ from what was loaded or last saved.</summary>
    public bool IsDirty => !Map.Write().AsSpan().SequenceEqual(_saved);

    public void MarkSaved() => _saved = Map.Write();

    /// <summary>The internal pack path of the catchment file.</summary>
    public string BlmPackPath => $"terrain/battles/{TerrainFolder}/battle_locations_map.bin";

    /// <summary>Loads the workspace. <paramref name="blmFile"/> overrides the pack copy of battle_locations_map.bin;
    /// <paramref name="modDataDir"/> is an RPFM extract of the mod (db\, text\db\) for names and suggested kinds.</summary>
    public static CampaignBattleWorkspace Load(ProjectPaths paths, string? blmFile = null, string? modDataDir = null,
                                               string terrainFolder = DefaultTerrainFolder, Action<string>? log = null)
    {
        var linked = paths.ModPacks.Where(File.Exists).ToList();
        log?.Invoke($"opening {linked.Count} linked pack(s) and the game packs…");
        var packs = GameSetup.OpenWithLinked(paths.GameDataDir, linked);
        var vanilla = new PackSet(packs.Packs.Where(p => !linked.Any(l => string.Equals(Path.GetFullPath(l), Path.GetFullPath(p.SourcePath), StringComparison.OrdinalIgnoreCase))));
        return Load(paths, packs, vanilla, blmFile, modDataDir, terrainFolder, log);
    }

    /// <summary>As <see cref="Load(ProjectPaths, string?, string?, string, Action{string}?)"/> with open pack sets
    /// (<paramref name="vanilla"/>: the game's own packs, to tell new regions from vanilla ones).</summary>
    public static CampaignBattleWorkspace Load(ProjectPaths paths, PackSet packs, PackSet? vanilla, string? blmFile, string? modDataDir,
                                               string terrainFolder = DefaultTerrainFolder, Action<string>? log = null)
    {
        var blmPath = $"terrain/battles/{terrainFolder}/battle_locations_map.bin";
        byte[] blmBytes;
        string source;
        if (blmFile is not null)
        {
            blmBytes = File.ReadAllBytes(blmFile);
            source = blmFile;
        }
        else
        {
            blmBytes = packs.TryRead(blmPath) ?? throw new FileNotFoundException($"{blmPath} is not in the linked or game packs");
            source = Path.GetFileName(packs.FindOwner(blmPath)?.SourcePath ?? "packs");
        }
        var map = BattleLocations.Read(blmBytes);
        log?.Invoke($"catchments from {source}: {map.AreaCount} areas, {map.Width}x{map.Height} cells");

        var notes = new List<string>();
        var esfPath = $"campaign_maps/{paths.MapName}/map_data.esf";
        CampaignSettlements? campaign = null;
        string? campaignSource = null;
        if (packs.TryRead(esfPath) is { } esf)
        {
            campaign = CampaignSettlements.Read(esf);
            campaignSource = Path.GetFileName(packs.FindOwner(esfPath)?.SourcePath);
            if (campaign.HexWidth != map.Width || Math.Abs(campaign.HexHeight - map.Height) > 1)
                notes.Add($"The campaign map {paths.MapName} is {campaign.HexWidth}x{campaign.HexHeight} hexes but the battle grid is " +
                          $"{map.Width}x{map.Height}: settlements are not on the battle grid. The battle terrain (catchments and tile map) " +
                          "has to be resized to the campaign map before its battles can be checked here.");
        }
        else notes.Add($"{esfPath} was not found in the linked or game packs: no region list.");

        HashSet<string>? vanillaRegions = null;
        if (vanilla?.TryRead(esfPath) is { } vesf)
            vanillaRegions = CampaignSettlements.Read(vesf).Settlements.Select(s => s.RegionKey).ToHashSet();

        BattleTileMapFile? tiles = null;
        if (packs.TryRead($"terrain/battles/{terrainFolder}/tile_map.index") is { } idx
            && packs.TryRead($"terrain/battles/{terrainFolder}/tile_map.tiles") is { } tileBytes)
            try { tiles = BattleTileMapFile.Read(idx, tileBytes); }
            catch (Exception e) { notes.Add($"tile_map.tiles could not be read ({e.Message}): painted cities are not detected."); }

        ModRegionData? mod = null;
        if (!string.IsNullOrWhiteSpace(modDataDir) && Directory.Exists(modDataDir))
        {
            mod = ModRegionData.Load(modDataDir);
            log?.Invoke($"mod data: {mod.DisplayNames.Count} names, {mod.LayoutByRegion.Count} layouts, {mod.PrimaryBuildingByRegion.Count} primary buildings");
        }
        else notes.Add("No mod data folder: region names and suggested kinds are not shown (File › Mod data folder…).");

        var redirects = new RedirectCatalog(packs.TryRead);
        var folders = packs.Packs.SelectMany(p => p.Entries.Keys)
            .Select(k => PackFile.Normalize(k).Split('\\'))
            .Where(p => p.Length >= 4 && p[0] == "terrain" && p[1] == "battles" && p[3] == "tile_list.bin")
            .Select(p => p[2]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        var dbRoots = new List<string>();
        if (mod is not null) dbRoots.Add(Path.Combine(mod.Root, "db"));
        dbRoots.Add(paths.DbTsvRoot);

        var ws = new CampaignBattleWorkspace
        {
            Paths = paths,
            TerrainFolder = terrainFolder,
            Map = map,
            Source = source,
            Campaign = campaign,
            CampaignSource = campaignSource,
            Mod = mod,
            Tiles = tiles,
            Redirects = redirects,
            Regions = new RegionBattleAnalyzer(map, campaign?.Settlements ?? [], redirects, mod, vanillaRegions, tiles,
                gridMatches: campaign is null || campaign.HexWidth == map.Width && Math.Abs(campaign.HexHeight - map.Height) <= 1),
            KnownBattles = KnownBattleRows(packs, dbRoots),
            BattleFolders = folders,
        };
        ws.Notes.AddRange(notes);
        ws._saved = blmBytes;
        return ws;
    }

    /// <summary>battles_tables rows from the RPFM TSV folders, then every pack's binary battles_tables.</summary>
    private static Dictionary<string, string> KnownBattleRows(PackSet packs, IEnumerable<string> dbRoots)
    {
        var known = BattlesTable.KnownMapPaths(dbRoots);
        foreach (var pack in packs.Packs)
            foreach (var entry in pack.Entries.Keys.Where(k => PackFile.Normalize(k).StartsWith(@"db\battles_tables\", StringComparison.OrdinalIgnoreCase)))
                if (pack.TryRead(entry) is { } bytes)
                    foreach (var (mapPath, type) in BattlesTable.ScanBinary(bytes))
                        known.TryAdd(mapPath, type);
        return known;
    }

    /// <summary>The redirect folders used by the catchments that no known battles_tables row has a map_path for, with the
    /// battle type of the list using them.</summary>
    public IReadOnlyList<(string Folder, string Type)> MissingBattleRows()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in Map.Lists)
            foreach (var a in list.Areas.Where(a => a.Redirection.Length > 0))
                if (!KnownBattles.ContainsKey(BattlesTable.MapPath(a.Redirection)) && !NoRowFolders.Contains(a.Redirection))
                    result.TryAdd(a.Redirection, BattlesTable.TypeForList(list.Key));
        return result.Select(kv => (kv.Key, kv.Value)).OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Checks a redirect for a list: the folder exists, its battles_tables type (when known) matches the list's
    /// battle type, and settlement folders embed their centre. Returns problems (empty = fine).</summary>
    public IReadOnlyList<string> CheckRedirect(string listKey, string folder)
    {
        var problems = new List<string>();
        if (folder.Length == 0) return problems;
        if (!Redirects.FolderExists(folder)) problems.Add($"'{folder}' is not a battle map in the linked or game packs");
        var want = BattlesTable.TypeForList(listKey);
        if (KnownBattles.TryGetValue(BattlesTable.MapPath(folder), out var type) && type != want)
            problems.Add($"'{folder}' is a {type} map but {listKey} battles are {want}");
        var settlementMap = folder.StartsWith("settlement_", StringComparison.Ordinal) || folder.StartsWith("resource_", StringComparison.Ordinal);
        if (listKey is BattleLocations.Standard or BattleLocations.Unfortified && settlementMap && Redirects.FolderExists(folder)
            && !Redirects.TryValidateEmbed(folder, out var why))
            problems.Add(why);
        return problems;
    }

    /// <summary>Writes the catchments (and battles_tables rows for redirect folders without one) as loose files under
    /// <paramref name="outDir"/>: terrain\battles\&lt;folder&gt;\battle_locations_map.bin and
    /// db\battles_tables\atlaswh3_campaign_battles.tsv. Returns the files written.</summary>
    public IReadOnlyList<string> SaveLoose(string outDir)
    {
        SourceGuard.EnsureWritable(outDir, Paths);
        var written = new List<string>();
        var bin = Path.Combine(outDir, "terrain", "battles", TerrainFolder, "battle_locations_map.bin");
        WriteAtomic(bin, Map.Write());
        written.Add(bin);
        var rows = MissingBattleRows();
        var tsv = Path.Combine(outDir, "db", "battles_tables", "atlaswh3_campaign_battles.tsv");
        if (rows.Count > 0)
        {
            WriteAtomic(tsv, System.Text.Encoding.UTF8.GetBytes(BattlesTable.ToTsv(rows.Select(r => BattlesTable.Row(r.Folder, r.Type)))));
            written.Add(tsv);
        }
        else if (File.Exists(tsv)) File.Delete(tsv);
        MarkSaved();
        return written;
    }

    /// <summary>A new mod pack holding the catchment file. DB rows are not packed (they need the battles_tables binary
    /// schema): import the TSV from <see cref="SaveLoose"/> with RPFM.</summary>
    public void ExportPack(string packPath)
    {
        // a new pack of our own: the folder must be writable and the file must not be a linked (source) pack. The
        // guard's "any .pack is read-only" rule is for existing source packs, so check the folder, not the file.
        var full = Path.GetFullPath(packPath);
        SourceGuard.EnsureWritable(Path.GetDirectoryName(full)!, Paths);
        if (Paths.ModPacks.Any(p => string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"Refusing to write: {packPath} is a linked pack (read-only).");
        var bytes = Map.Write();
        PackWriter.Write(packPath, [new PackWriter.Source(BlmPackPath, bytes.Length, s => s.Write(bytes))]);
    }

    /// <summary>The kit copy BOB's pack step picks up: &lt;kit&gt;\working_data\terrain\battles\&lt;folder&gt;\
    /// battle_locations_map.bin. An existing file is backed up to &lt;output&gt;\backups first. Returns the path.</summary>
    public string WriteToKit()
    {
        var path = Path.Combine(Paths.AkWorkingDir, "terrain", "battles", TerrainFolder, "battle_locations_map.bin");
        SourceGuard.EnsureWritable(path, Paths);
        if (File.Exists(path))
        {
            var backup = Path.Combine(Paths.OutputRoot, "backups", $"battle_locations_{TerrainFolder}_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path, backup);
        }
        WriteAtomic(path, Map.Write());
        return path;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
