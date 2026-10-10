using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core;

/// <summary>Where a campaign map was found: the vanilla game packs, an assembly kit's raw_data (a .terry), or a linked
/// mod pack.</summary>
public enum MapOrigin { Vanilla, Kit, Pack }

/// <summary>A campaign map and every place it was found (<see cref="Packs"/>: the linked packs that hold its compiled
/// files).</summary>
public sealed record MapEntry(string Name, IReadOnlySet<MapOrigin> Origins, IReadOnlyList<string> Packs)
{
    public bool InKit => Origins.Contains(MapOrigin.Kit);
    /// <summary>"kit, pack" etc., for lists.</summary>
    public string OriginText => string.Join(", ", Origins.Order().Select(o => o switch
    {
        MapOrigin.Vanilla => "vanilla",
        MapOrigin.Kit => "kit",
        _ => "pack",
    }));
    public override string ToString() => $"{Name}  ({OriginText})";
}

/// <summary>
/// Map discovery for the Start screen and "Prepare game data": the campaign maps of an assembly kit (folders under
/// raw_data\terrain\campaigns holding a .terry), of linked mod packs (terrain\campaigns\&lt;map&gt;\ or
/// campaign_maps\&lt;map&gt;\ entries) and of the vanilla packs. Packs are only read.
/// </summary>
public static class MapCatalog
{
    public const string DefaultMap = "wh3_main_combi_map_1";

    /// <summary>Maps in the kit's raw_data\terrain\campaigns that have a .terry project.</summary>
    public static IReadOnlyList<string> KitMaps(string assemblyKitRoot)
    {
        var dir = Path.Combine(assemblyKitRoot, "raw_data", "terrain", "campaigns");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir)
            .Where(d => Directory.EnumerateFiles(d, "*.terry").Any())
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Campaign maps a pack holds compiled files for (terrain\campaigns\&lt;map&gt;\ or campaign_maps\&lt;map&gt;\).</summary>
    public static IReadOnlyList<string> PackMaps(PackFile pack) => MapsIn(pack.Entries.Keys);

    public static IReadOnlyList<string> PackMaps(string packPath) => PackMaps(PackFile.Open(packPath));

    /// <summary>Map folder names under terrain\campaigns\ or campaign_maps\ in normalized pack paths.</summary>
    public static IReadOnlyList<string> MapsIn(IEnumerable<string> entries)
    {
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in entries)
        {
            var parts = PackFile.Normalize(key).Split('\\');
            var name = parts.Length >= 4 && parts[0] == "terrain" && parts[1] == "campaigns" ? parts[2]
                : parts.Length >= 3 && parts[0] == "campaign_maps" ? parts[1] : null;
            if (!string.IsNullOrEmpty(name) && !name.Contains('.')) maps.Add(name);
        }
        return maps.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Assembly kits next to the game: folders named assembly_kit* that hold raw_data.</summary>
    public static IReadOnlyList<string> Kits(string gameFolder)
    {
        if (!Directory.Exists(gameFolder)) return [];
        return Directory.EnumerateDirectories(gameFolder, "assembly_kit*")
            .Where(d => Directory.Exists(Path.Combine(d, "raw_data")))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Every map from the kit, the linked packs and (when <paramref name="gameDataDir"/> is given) the vanilla packs,
    /// merged by name. A pack that cannot be read is reported through <paramref name="log"/> and skipped.
    /// </summary>
    public static IReadOnlyList<MapEntry> All(string? assemblyKitRoot, IEnumerable<string> linkedPacks, string? gameDataDir = null,
                                              Action<string>? log = null)
    {
        var origins = new Dictionary<string, HashSet<MapOrigin>>(StringComparer.OrdinalIgnoreCase);
        var packs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string map, MapOrigin origin, string? pack = null)
        {
            if (!origins.TryGetValue(map, out var set)) origins[map] = set = [];
            set.Add(origin);
            if (pack is null) return;
            if (!packs.TryGetValue(map, out var list)) packs[map] = list = [];
            list.Add(pack);
        }
        if (!string.IsNullOrEmpty(assemblyKitRoot))
            foreach (var m in KitMaps(assemblyKitRoot)) Add(m, MapOrigin.Kit);
        foreach (var pack in linkedPacks)
        {
            try { foreach (var m in PackMaps(pack)) Add(m, MapOrigin.Pack, pack); }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                log?.Invoke($"{Path.GetFileName(pack)}: {e.Message}");
            }
        }
        if (!string.IsNullOrEmpty(gameDataDir) && Directory.Exists(gameDataDir))
        {
            try { foreach (var m in GameSetup.VanillaMaps(gameDataDir)) Add(m, MapOrigin.Vanilla); }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                log?.Invoke($"vanilla packs: {e.Message}");
            }
        }
        return origins.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new MapEntry(kv.Key, kv.Value, packs.TryGetValue(kv.Key, out var p) ? p : []))
            .ToList();
    }

    /// <summary>The Start screen's paths: <paramref name="paths"/> with the remembered map, kit and linked packs
    /// (<see cref="AppSettings.MapName"/>, <see cref="AppSettings.LinkedPacks"/>), keeping anything set on the command
    /// line (a non-default map, <c>--pack</c>).</summary>
    public static ProjectPaths WithSelection(ProjectPaths paths, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.MapName) && paths.MapName == DefaultMap) paths = paths with { MapName = settings.MapName.Trim() };
        if (paths.ModPacks.Count == 0)
        {
            var linked = settings.LinkedPacks.Where(File.Exists).ToList();
            if (linked.Count > 0) paths = paths with { ModPacks = linked };
        }
        return paths;
    }
}
