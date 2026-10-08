using System.Collections.Concurrent;
using System.Text;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.Core.Battle;

public enum RegionKindType { Vanilla, City, Port, Resource, Composed, Custom }

/// <summary>
/// The battle a settlement gets. City and Port carry a layout letter (a–h, a–b); Resource carries a resource key
/// (lumber, livestock, salt, mine_iron, farm_grain, farm_rice, tools, fish, trade); Composed is a city tile painted
/// into the campaign-battle terrain (letter = the city layout); Vanilla = the composed terrain with no redirect.
/// Ported from BattleMaps' BattleLocationsMap tool (RegionKind).
/// </summary>
public readonly record struct RegionKind(RegionKindType Type, char Letter = 'a', string ResourceType = "")
{
    public static RegionKind City(char letter) => new(RegionKindType.City, letter);
    public static RegionKind Port(char letter) => new(RegionKindType.Port, letter);
    public static RegionKind ResourceOf(string type) => new(RegionKindType.Resource, ' ', type);
    public static RegionKind Composed(char cityLetter) => new(RegionKindType.Composed, cityLetter);
    public static RegionKind Vanilla => new(RegionKindType.Vanilla);
    public static RegionKind Custom => new(RegionKindType.Custom);

    public static readonly string[] ResourceTypes =
        ["lumber", "livestock", "salt", "mine_iron", "farm_grain", "farm_rice", "tools", "fish", "trade"];

    /// <summary>Every kind that can be applied as a redirect (cities a–h, ports a–b, resources).</summary>
    public static IEnumerable<RegionKind> Applicable()
    {
        foreach (var l in "abcdefgh") yield return City(l);
        foreach (var l in "ab") yield return Port(l);
        foreach (var r in ResourceTypes) yield return ResourceOf(r);
    }

    /// <summary>Same battle family (a city layout letter difference does not count).</summary>
    public bool SameFamily(RegionKind other) =>
        Type == other.Type && (Type != RegionKindType.Resource || ResourceType == other.ResourceType);

    public string Display() => Type switch
    {
        RegionKindType.City => $"City {Letter}",
        RegionKindType.Port => $"Port {Letter}",
        RegionKindType.Resource => $"Resource: {ResourceType}",
        RegionKindType.Composed => $"Painted city tile ({Letter})",
        RegionKindType.Custom => "Custom",
        _ => "Vanilla",
    };

    public override string ToString() => Display();
}

/// <summary>
/// Redirect targets per <see cref="RegionKind"/> and the embedded-centre check: a redirected settlement battle loads
/// terrain\battles\&lt;folder&gt;\, and a folder whose tile_list.bin does not embed its centre prefab loads with a hole in
/// the middle. Folders are read through <paramref name="readGameFile"/> (linked packs, then vanilla).
/// </summary>
public sealed class RedirectCatalog(Func<string, byte[]?> readGameFile)
{
    /// <summary>Resource key → a validated embedded map folder (both siege lists use it).</summary>
    public static readonly IReadOnlyDictionary<string, string> ResourceFolders = new Dictionary<string, string>
    {
        ["lumber"] = "resource_han_lumber_a_small",
        ["livestock"] = "resource_han_livestock_a_regular_small",
        ["salt"] = "resource_han_salt_a_small",
        ["mine_iron"] = "resource_han_mine_a_iron_large",
        ["farm_grain"] = "resource_han_farm_grain_a_large",
        ["farm_rice"] = "resource_han_farm_rice_a_large",
        ["tools"] = "resource_han_tools_a_regular_large",
        ["fish"] = "settlement_port_han_a_small",
        ["trade"] = "settlement_port_han_b_small",
    };

    private readonly ConcurrentDictionary<string, (bool Ok, string Why)> _embed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Redirect folders (walled siege list, unfortified list) for a kind; null for kinds that carry none.</summary>
    public static (string Std, string Unf)? FoldersFor(RegionKind kind) => kind.Type switch
    {
        RegionKindType.City when kind.Letter is >= 'a' and <= 'h' =>
            ($"settlement_city_han_{kind.Letter}_small_walled", $"settlement_city_han_{kind.Letter}_small"),
        RegionKindType.Port when kind.Letter is 'a' or 'b' =>
            ($"settlement_port_han_{kind.Letter}_small_walled", $"settlement_port_han_{kind.Letter}_small"),
        RegionKindType.Resource when ResourceFolders.TryGetValue(kind.ResourceType, out var f) => (f, f),
        _ => null,
    };

    /// <summary>The kind a redirect pair stands for, or null when it is not one of the catalogue's pairs.</summary>
    public static RegionKind? KindFromFolders(string std, string unf)
    {
        for (var l = 'a'; l <= 'h'; l++)
            if (std == $"settlement_city_han_{l}_small_walled" && unf == $"settlement_city_han_{l}_small") return RegionKind.City(l);
        for (var l = 'a'; l <= 'b'; l++)
            if (std == $"settlement_port_han_{l}_small_walled" && unf == $"settlement_port_han_{l}_small") return RegionKind.Port(l);
        if (std == unf)
            foreach (var (type, folder) in ResourceFolders)
                if (std == folder) return RegionKind.ResourceOf(type);
        return null;
    }

    public bool FolderExists(string folder) => readGameFile($"terrain/battles/{folder}/tile_list.bin") is not null;

    /// <summary>True when terrain\battles\&lt;folder&gt;\tile_list.bin embeds a centre prefab (a settlement city, port or
    /// resource prefab).</summary>
    public bool TryValidateEmbed(string folder, out string reason)
    {
        var (ok, why) = _embed.GetOrAdd(folder, f =>
        {
            var bytes = readGameFile($"terrain/battles/{f}/tile_list.bin");
            if (bytes is null) return (false, $"battle map folder '{f}' was not found in the linked or game packs");
            var text = Encoding.Latin1.GetString(bytes);
            return text.Contains("settlement_cities") || text.Contains("settlement_ports") || text.Contains("resource\\")
                ? (true, "")
                : (false, $"'{f}' does not embed a centre prefab: a redirected battle there loads with a hole in the middle");
        });
        reason = why;
        return ok;
    }
}

/// <summary>Catchment edits shared by the region fixer and the map tools.</summary>
public static class CatchmentOps
{
    /// <summary>Adds an area with vanilla defaults (deployment (2, −1), all approaches allowed, centre in the middle).</summary>
    public static CatchmentArea Add(BattleLocations map, string listKey, CellBox box, string name, (int X, int Y)? centre = null)
    {
        var list = map.List(listKey) ?? throw new InvalidOperationException($"the map has no list '{listKey}'");
        var area = new CatchmentArea
        {
            Box = box,
            Centre = centre ?? ((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2),
            Name = name,
        };
        list.Areas.Add(area);
        return area;
    }

    /// <summary>The first area of a list covering a cell.</summary>
    public static CatchmentArea? Covering(BattleLocations map, string listKey, int x, int y) =>
        map.List(listKey)?.Areas.FirstOrDefault(a => a.Box.Contains(x, y));

    /// <summary>Cells covered by at least one area of a list (row-major).</summary>
    public static bool[] CoverageMask(BattleLocations map, string listKey)
    {
        var mask = new bool[map.Width * map.Height];
        foreach (var a in map.List(listKey)?.Areas ?? [])
        {
            int x0 = Math.Max(0, a.Box.MinX), x1 = Math.Min(map.Width - 1, a.Box.MaxX);
            int y0 = Math.Max(0, a.Box.MinY), y1 = Math.Min(map.Height - 1, a.Box.MaxY);
            for (var y = y0; y <= y1; y++)
                Array.Fill(mask, true, y * map.Width + x0, Math.Max(0, x1 - x0 + 1));
        }
        return mask;
    }

    /// <summary>Land cells no area of the list covers: places where that battle type has no location.</summary>
    public static int UncoveredLand(BattleLocations map, string listKey)
    {
        var mask = CoverageMask(map, listKey);
        var land = map.LandIndex;
        var n = 0;
        for (var i = 0; i < mask.Length; i++)
            if (map.Cells[i] == land && !mask[i]) n++;
        return n;
    }

    /// <summary>
    /// Makes sure a settlement cell is covered in both siege lists (settlement_standard and settlement_unfortified):
    /// reuses a covering area, else adds a 15×15 one (clamped to the map) named &lt;region&gt;_std / _unf, as the blm tool
    /// does. <paramref name="y"/> is a catchment row (row 0 = north).
    /// </summary>
    public static (CatchmentArea Std, CatchmentArea Unf, bool Created) EnsureSiegeAreas(BattleLocations map, string regionKey, int x, int y, int half = 7)
    {
        var created = false;
        CatchmentArea Ensure(string listKey, string suffix)
        {
            if (Covering(map, listKey, x, y) is { } existing) return existing;
            created = true;
            var box = new CellBox(Math.Max(0, x - half), Math.Max(0, y - half), Math.Min(map.Width - 1, x + half), Math.Min(map.Height - 1, y + half));
            return Add(map, listKey, box, $"{regionKey}_{suffix}", (x, y));
        }
        return (Ensure(BattleLocations.Standard, "std"), Ensure(BattleLocations.Unfortified, "unf"), created);
    }

    /// <summary>A name not used by any area of the map.</summary>
    public static string UniqueName(BattleLocations map, string stem)
    {
        var used = map.Lists.SelectMany(l => l.Areas).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(stem)) return stem;
        for (var i = 2; ; i++)
            if (!used.Contains($"{stem}_{i}")) return $"{stem}_{i}";
    }
}

/// <summary>battles_tables rows for redirect folders (RPFM TSV, battles_tables version 10).</summary>
public static class BattlesTable
{
    public static readonly string[] Columns =
    [
        "key", "type", "is_naval", "specification", "screenshot_path", "map_path", "team_size_1", "team_size_2", "release",
        "multiplayer", "singleplayer", "intro_movie", "year", "defender_funds_ratio", "has_key_buildings", "matchmaking",
        "playable_area_width", "playable_area_height", "is_large_settlement", "has_15m_walls", "is_underground",
        "catchment_name", "unique_id",
    ];

    public const string Metadata = "#battles_tables;10;db/battles_tables/data__";

    /// <summary>The battles_tables battle type a catchment list's battles have.</summary>
    public static string TypeForList(string listKey) => listKey switch
    {
        BattleLocations.Standard => "siege",
        BattleLocations.Unfortified => "unfortified_settlement",
        BattleLocations.Gate => "gate_battle",
        _ => "classic",
    };

    /// <summary>map_path for a folder: terrain\battles\&lt;folder&gt;\ (must equal the catchment's redirect exactly).</summary>
    public static string MapPath(string folder) => $"terrain\\battles\\{folder}\\";

    /// <summary>A row for <paramref name="folder"/>; unique_id is a stable positive FNV-1a hash of the key. Both
    /// <c>specification</c> and <c>map_path</c> hold terrainattles\&lt;folder&gt;\: custom battles read specification
    /// (most vanilla rows fill only that), the redirect proven in game used map_path.</summary>
    public static string[] Row(string folder, string type, string? key = null, int teamSize = 4)
    {
        key ??= "atlaswh3_" + folder;
        uint h = 2166136261;
        foreach (var c in key) { h ^= c; h *= 16777619; }
        return
        [
            key, type, "false", MapPath(folder), "", MapPath(folder), teamSize.ToString(), teamSize.ToString(), "false", "true", "true", "",
            "0", type == "siege" ? "0.8000" : "1.0000", "false", "false", "0.0000", "0.0000", "false", "false", "false", "",
            ((int)(h & 0x7FFFFFFF)).ToString(),
        ];
    }

    public static string ToTsv(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join('\t', Columns)).Append('\n');
        sb.Append(Metadata).Append(new string('\t', Columns.Length - 1)).Append('\n');
        foreach (var r in rows) sb.Append(string.Join('\t', r)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// (map_path, type) of every row of a binary battles_tables file. Only the first columns are located (key, type,
    /// is_naval, map_path, each string u16-length-prefixed), by finding each terrain\battles\…\ string and the type
    /// string ahead of its is_naval byte, so it reads any table version without the full schema.
    /// </summary>
    public static IEnumerable<(string MapPath, string Type)> ScanBinary(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        for (var p = text.IndexOf("terrain\\battles\\", StringComparison.OrdinalIgnoreCase); p >= 0;
             p = text.IndexOf("terrain\\battles\\", p + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (p < 5) continue;
            int len = data[p - 2] | data[p - 1] << 8;
            if (len <= 0 || p + len > data.Length) continue;
            var mapPath = text.Substring(p, len);
            var typeEnd = p - 3;   // the is_naval byte sits between the type string and the map_path length
            for (var l = 1; l <= 40 && typeEnd - l - 2 >= 0; l++)
            {
                var at = typeEnd - l;
                if ((data[at - 2] | data[at - 1] << 8) != l) continue;
                var type = text.Substring(at, l);
                if (type.All(c => c is >= 'a' and <= 'z' or '_')) { yield return (mapPath, type); break; }
            }
        }
    }

    /// <summary>map_path → type of the rows in RPFM battles_tables TSVs under <paramref name="dbRoots"/> (each a folder
    /// holding battles_tables\*.tsv); missing folders are skipped.</summary>
    public static Dictionary<string, string> KnownMapPaths(IEnumerable<string> dbRoots)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in dbRoots)
        {
            var dir = Path.Combine(root, "battles_tables");
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.tsv"))
            {
                string[]? header = null;
                foreach (var line in File.ReadLines(file))
                {
                    var f = line.Split('\t');
                    if (header is null) { header = f; continue; }
                    if (f.Length == 0 || f[0].StartsWith('#')) continue;
                    var path = Array.IndexOf(header, "map_path");
                    var type = Array.IndexOf(header, "type");
                    if (path < 0 || type < 0 || f.Length <= Math.Max(path, type) || f[path].Length == 0) continue;
                    known.TryAdd(f[path], f[type]);
                }
            }
        }
        return known;
    }
}
