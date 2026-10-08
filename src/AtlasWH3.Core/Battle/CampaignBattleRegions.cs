using AtlasWH3.Formats.Battle;
using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Core.Battle;

/// <summary>A land region's settlement on the campaign hex grid (x, y; row 0 = south, same grid as tile_map.tiles).</summary>
public sealed record CampaignSettlement(string RegionKey, int X, int Y)
{
    /// <summary>The catchment row of the settlement (battle_locations_map rows run north to south).</summary>
    public int CatchmentY(int mapHeight) => mapHeight - 1 - Y;
}

/// <summary>The settlements of a campaign_maps\&lt;map&gt;\map_data.esf (REGIONS_BLOCK → REGION_DATA → SETTLEMENT_INFO).</summary>
public sealed class CampaignSettlements
{
    public int HexWidth { get; private init; }
    public int HexHeight { get; private init; }
    public IReadOnlyList<CampaignSettlement> Settlements { get; private init; } = [];

    public static CampaignSettlements Read(byte[] mapDataEsf)
    {
        var esf = EsfTree.Read(mapDataEsf);
        var hex = esf.Root.Descendants("HEX_MAP_DATA").FirstOrDefault()?.Values;
        var list = new List<CampaignSettlement>();
        foreach (var block in esf.Root.Descendants("REGIONS_BLOCK").Take(1))
            foreach (var group in block.Groups)
            {
                if (group.OfType<EsfRecord>().FirstOrDefault(r => r.Name == "REGION_DATA") is not { } rd) continue;
                var v = rd.Values;
                if (v.Count < 2 || v[1].Int != 0) continue;   // sea region
                var key = esf.Ascii.GetValueOrDefault((uint)v[0].Int, "");
                if (rd.Record("SETTLEMENT_INFO")?.Values is not { Count: >= 2 } s || s[0].Int == 0xFFFF || key.Length == 0) continue;
                list.Add(new CampaignSettlement(key, (int)s[0].Int, (int)s[1].Int));
            }
        return new CampaignSettlements
        {
            HexWidth = hex is { Count: >= 2 } ? (int)hex[0].Int : 0,
            HexHeight = hex is { Count: >= 2 } ? (int)hex[1].Int : 0,
            Settlements = list,
        };
    }
}

/// <summary>
/// Region data from an RPFM-extracted mod folder (holding db\ and text\db\): onscreen names, settlement display layouts
/// and start_pos primary buildings, enough to suggest each region's <see cref="RegionKind"/>. Missing tables give
/// empty maps. Ported from the blm tool's ModRegionContext.
/// </summary>
public sealed class ModRegionData
{
    public string Root { get; }
    public IReadOnlyDictionary<string, string> DisplayNames { get; }
    public IReadOnlyDictionary<string, string> LayoutByRegion { get; }
    public IReadOnlyDictionary<string, string> PrimaryBuildingByRegion { get; }

    private ModRegionData(string root, Dictionary<string, string> names, Dictionary<string, string> layouts, Dictionary<string, string> buildings)
    {
        Root = root;
        DisplayNames = names;
        LayoutByRegion = layouts;
        PrimaryBuildingByRegion = buildings;
    }

    public static ModRegionData Load(string root)
    {
        var names = new Dictionary<string, string>();
        var textDb = Path.Combine(root, "text", "db");
        if (Directory.Exists(textDb))
            foreach (var loc in Directory.EnumerateFiles(textDb, "*.loc.tsv"))
                foreach (var f in DataRows(loc))
                    if (f.Length >= 2 && f[0].StartsWith("regions_onscreen_", StringComparison.Ordinal))
                        names.TryAdd(f[0]["regions_onscreen_".Length..], f[1]);

        var layouts = new Dictionary<string, string>();
        foreach (var tsv in TableFiles(root, "campaign_settlement_display_settlement_layouts_tables"))
            foreach (var f in DataRows(tsv))
                if (f.Length >= 2 && f[0].StartsWith("settlement:", StringComparison.Ordinal))
                    layouts.TryAdd(f[0]["settlement:".Length..], f[1]);

        var buildings = new Dictionary<string, string>();
        foreach (var tsv in TableFiles(root, "start_pos_settlements_tables"))
        {
            string[]? header = null;
            var col = -1;
            foreach (var line in File.ReadLines(tsv))
            {
                var f = line.Split('\t');
                if (header is null) { header = f; col = Array.IndexOf(header, "primary_building"); continue; }
                if (f.Length == 0 || f[0].StartsWith('#') || col < 0 || f.Length <= col) continue;
                var keyCol = f.FirstOrDefault(c => c.StartsWith("settlement:", StringComparison.Ordinal));
                if (keyCol is null || f[col].Length == 0) continue;
                buildings.TryAdd(keyCol["settlement:".Length..], f[col]);
            }
        }
        return new ModRegionData(root, names, layouts, buildings);
    }

    /// <summary>Display layout first (capitals and ports), else the primary building chain.</summary>
    public RegionKind? SuggestKind(string regionKey)
    {
        if (LayoutByRegion.TryGetValue(regionKey, out var layout))
        {
            if (layout.StartsWith("settlement_land_layout_", StringComparison.Ordinal) && layout.Length > 23 && layout[^1] is >= 'a' and <= 'h')
                return RegionKind.City(layout[^1]);
            if (layout.Contains("port_layout_1")) return RegionKind.Port('a');
            if (layout.Contains("port_layout_2")) return RegionKind.Port('b');
        }
        if (PrimaryBuildingByRegion.TryGetValue(regionKey, out var b))
        {
            if (b.Contains("lumber")) return RegionKind.ResourceOf("lumber");
            if (b.Contains("livestock")) return RegionKind.ResourceOf("livestock");
            if (b.Contains("iron")) return RegionKind.ResourceOf("mine_iron");
            if (b.Contains("farms_rice")) return RegionKind.ResourceOf("farm_rice");
            if (b.Contains("farms_grain")) return RegionKind.ResourceOf("farm_grain");
            if (b.Contains("salt")) return RegionKind.ResourceOf("salt");
            if (b.Contains("trading_port")) return RegionKind.ResourceOf("trade");
            if (b.Contains("tools")) return RegionKind.ResourceOf("tools");
            if (b.Contains("fish")) return RegionKind.ResourceOf("fish");
            if (b.Contains("3k_city_")) return RegionKind.City('a');
        }
        return null;
    }

    private static IEnumerable<string> TableFiles(string root, string table)
    {
        var dir = Path.Combine(root, "db", table);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.tsv") : [];
    }

    private static IEnumerable<string[]> DataRows(string tsv) =>
        File.ReadLines(tsv).Skip(1).Select(l => l.Split('\t')).Where(f => f.Length > 0 && !f[0].StartsWith('#'));
}

public enum RegionBattleState
{
    Ok,
    /// <summary>Neither siege list covers the settlement: no settlement battle can start there.</summary>
    NoCatchment,
    /// <summary>Only one of the two siege lists covers it (walled or unfortified battles are missing).</summary>
    PartialCatchment,
    /// <summary>Redirected to a different battle family than the region's settlement (e.g. a city map on a lumber camp).</summary>
    WrongType,
    /// <summary>A new region without a redirect or a painted settlement tile: its battle has no settlement in it.</summary>
    RedirectMissing,
    /// <summary>The redirect target is missing, or lacks its centre prefab (the battle loads with a hole).</summary>
    RedirectInvalid,
    /// <summary>The settlement is outside the battle grid (the campaign map is larger than the battle terrain).</summary>
    OffGrid,
}

public sealed record RegionBattleStatus(
    CampaignSettlement Settlement, string DisplayName, bool IsNew, RegionKind? Current, RegionKind? Suggested,
    CatchmentArea? Std, CatchmentArea? Unf, RegionBattleState State, IReadOnlyList<string> Issues)
{
    public string StateText => State switch
    {
        RegionBattleState.Ok => "OK",
        RegionBattleState.NoCatchment => "No catchment",
        RegionBattleState.PartialCatchment => "Half covered",
        RegionBattleState.WrongType => "Wrong type",
        RegionBattleState.RedirectMissing => "Redirect missing",
        RegionBattleState.RedirectInvalid => "Redirect invalid",
        _ => "Off the battle grid",
    };
}

public sealed record RegionFixResult(bool Success, string Message, bool CreatedAreas = false);

/// <summary>
/// Per-settlement battle status on a campaign-battle terrain, and the fixes (ported from the blm tool's
/// RegionBattleService; tile painting of composed cities is not ported). Edits change <see cref="Map"/> in place; the
/// caller snapshots for undo.
/// </summary>
public sealed class RegionBattleAnalyzer(
    BattleLocations map, IReadOnlyList<CampaignSettlement> settlements, RedirectCatalog redirects,
    ModRegionData? mod = null, ISet<string>? vanillaRegions = null, BattleTileMapFile? tiles = null, bool gridMatches = true)
{
    public BattleLocations Map => map;
    public IReadOnlyList<CampaignSettlement> Settlements => settlements;

    public IReadOnlyList<RegionBattleStatus> Analyze() => settlements.Select(Analyze).ToList();

    public string DisplayName(string regionKey) => mod?.DisplayNames.GetValueOrDefault(regionKey) ?? regionKey;

    /// <summary>Not in the vanilla map (or, without the vanilla map, not a 3k_ key).</summary>
    public bool IsNew(string regionKey) =>
        vanillaRegions is { Count: > 0 } v ? !v.Contains(regionKey) : !regionKey.StartsWith("3k_", StringComparison.Ordinal);

    public RegionBattleStatus Analyze(CampaignSettlement s)
    {
        var issues = new List<string>();
        var suggested = mod?.SuggestKind(s.RegionKey);
        var isNew = IsNew(s.RegionKey);
        var name = DisplayName(s.RegionKey);
        var cy = s.CatchmentY(map.Height);
        if (!gridMatches)
        {
            issues.Add("the campaign map is not the size of the battle grid, so its hexes are not battle cells");
            return new RegionBattleStatus(s, name, isNew, null, suggested, null, null, RegionBattleState.OffGrid, issues);
        }
        if (s.X < 0 || s.X >= map.Width || cy < 0 || cy >= map.Height)
        {
            issues.Add($"settlement hex {s.X},{s.Y} is outside the {map.Width}x{map.Height} battle grid");
            return new RegionBattleStatus(s, name, isNew, null, suggested, null, null, RegionBattleState.OffGrid, issues);
        }
        if (!map.IsLand(s.X, cy)) issues.Add("the settlement cell is not land on the battle grid");

        var std = CatchmentOps.Covering(map, BattleLocations.Standard, s.X, cy);
        var unf = CatchmentOps.Covering(map, BattleLocations.Unfortified, s.X, cy);
        if (std is null && unf is null)
        {
            if (CatchmentOps.Covering(map, BattleLocations.Gate, s.X, cy) is { } gate)
            {
                issues.Add($"gate battle ({gate.Name})");
                return new RegionBattleStatus(s, name, isNew, RegionKind.Vanilla, suggested, null, null, RegionBattleState.Ok, issues);
            }
            return new RegionBattleStatus(s, name, isNew, null, suggested, null, null, RegionBattleState.NoCatchment, issues);
        }
        if (unf is null)
        {
            issues.Add("no settlement_unfortified area: unwalled battles here have no location");
            return new RegionBattleStatus(s, name, isNew, null, suggested, std, unf, RegionBattleState.PartialCatchment, issues);
        }
        if (std is null) issues.Add("unfortified only (no walled siege area), as vanilla resource settlements");

        RegionKind current;
        var state = RegionBattleState.Ok;
        var stdRedirect = std?.Redirection ?? unf.Redirection;
        if (stdRedirect.Length == 0 && unf.Redirection.Length == 0)
        {
            current = RegionKind.Vanilla;
            if (tiles is not null && ComposedKind(tiles.OwnerLocation(s.Y * tiles.Width + s.X)) is { } composed) current = composed;
            if (isNew && current.Type == RegionKindType.Vanilla)
            {
                state = RegionBattleState.RedirectMissing;
                issues.Add(tiles is null
                    ? "new region with no redirect (the battle terrain tile map was not loaded to check for a painted city)"
                    : "new region with no redirect and no painted settlement tile");
            }
        }
        else
        {
            current = RedirectCatalog.KindFromFolders(stdRedirect, unf.Redirection)
                      ?? (std is null ? KindFromUnfortified(unf.Redirection) : null) ?? RegionKind.Custom;
            foreach (var folder in new[] { std?.Redirection ?? "", unf.Redirection }.Where(f => f.Length > 0).Distinct())
            {
                var settlementMap = folder.StartsWith("settlement_", StringComparison.Ordinal) || folder.StartsWith("resource_", StringComparison.Ordinal);
                if (current.Type == RegionKindType.Custom && !settlementMap)
                {
                    // a hand-made map (e.g. a custom siege): it only has to exist
                    if (!redirects.FolderExists(folder))
                    {
                        state = RegionBattleState.RedirectInvalid;
                        issues.Add($"battle map folder '{folder}' was not found in the linked or game packs");
                    }
                }
                else if (!redirects.TryValidateEmbed(folder, out var why))
                {
                    state = RegionBattleState.RedirectInvalid;
                    issues.Add(why);
                }
            }
            if (current.Type == RegionKindType.Custom)
                issues.Add($"custom redirects: walled '{std?.Redirection}', unfortified '{unf.Redirection}'");
        }

        if (state == RegionBattleState.Ok && suggested is { } sug && current.Type is RegionKindType.City or RegionKindType.Port or RegionKindType.Resource)
        {
            if (!current.SameFamily(sug))
            {
                state = RegionBattleState.WrongType;
                issues.Add($"redirected as {current.Display()}, but the region is {sug.Display()}");
            }
            else if (current != sug) issues.Add($"layout {current.Display()}, the region's display layout is {sug.Display()}");
        }
        return new RegionBattleStatus(s, name, isNew, current, suggested, std, unf, state, issues);
    }

    /// <summary>The one-click fix: covers the settlement in both siege lists and, for a new or wrongly redirected
    /// region, redirects it to its suggested kind (or its current catalogue kind).</summary>
    public RegionFixResult Fix(RegionBattleStatus status)
    {
        if (status.State == RegionBattleState.OffGrid)
            return new(false, $"{status.DisplayName} is outside the battle grid: the battle terrain has to be resized first");
        RegionKind? target = status.Suggested
                             ?? (status.Current is { Type: RegionKindType.City or RegionKindType.Port or RegionKindType.Resource } c ? c : null);
        if (status.State == RegionBattleState.Ok) return new(true, $"{status.DisplayName}: nothing to fix");
        if (status.State is RegionBattleState.NoCatchment or RegionBattleState.PartialCatchment)
        {
            var hadRedirect = status.Std?.Redirection.Length > 0 || status.Unf?.Redirection.Length > 0;
            if ((status.IsNew || hadRedirect) && target is not null) return Apply(status, target.Value);
            var (_, _, created) = CatchmentOps.EnsureSiegeAreas(map, status.Settlement.RegionKey, status.Settlement.X, status.Settlement.CatchmentY(map.Height));
            return new(true, status.IsNew
                ? $"{status.DisplayName}: siege areas added; no kind is known for this region, so pick one and Apply"
                : $"{status.DisplayName}: siege areas added (vanilla terrain, no redirect)", created);
        }
        return target is null
            ? new(false, $"{status.DisplayName}: no kind is known for this region; pick a city, port or resource kind and Apply")
            : Apply(status, target.Value);
    }

    /// <summary>Sets a region's battle kind: covers both siege lists and sets (or clears, for Vanilla) the redirects.
    /// Refuses a redirect target without its embedded centre prefab.</summary>
    public RegionFixResult Apply(RegionBattleStatus status, RegionKind kind)
    {
        var s = status.Settlement;
        var cy = s.CatchmentY(map.Height);
        if (status.State == RegionBattleState.OffGrid) return new(false, $"{status.DisplayName} is outside the battle grid");
        if (kind.Type == RegionKindType.Vanilla)
        {
            var (vs, vu, vc) = CatchmentOps.EnsureSiegeAreas(map, s.RegionKey, s.X, cy);
            vs.Redirection = "";
            vu.Redirection = "";
            return new(true, $"{status.DisplayName} → Vanilla (redirects cleared)", vc);
        }
        if (RedirectCatalog.FoldersFor(kind) is not { } folders) return new(false, $"{kind.Display()} cannot be applied as a redirect");
        foreach (var folder in new[] { folders.Std, folders.Unf }.Distinct())
            if (!redirects.TryValidateEmbed(folder, out var why)) return new(false, why);
        var (std, unf, created) = CatchmentOps.EnsureSiegeAreas(map, s.RegionKey, s.X, cy);
        std.Redirection = folders.Std;
        unf.Redirection = folders.Unf;
        return new(true, $"{status.DisplayName} → {kind.Display()} (walled → {folders.Std}, unfortified → {folders.Unf})", created);
    }

    /// <summary>The kind of an unfortified-only redirect (a resource map, or the unwalled half of a city/port pair).</summary>
    private static RegionKind? KindFromUnfortified(string folder)
    {
        foreach (var kind in RegionKind.Applicable())
            if (RedirectCatalog.FoldersFor(kind) is { } f && f.Unf == folder) return kind;
        return null;
    }

    /// <summary>A painted settlement city tile, from its tile folder (…\settlement_city_han_f\…).</summary>
    public static RegionKind? ComposedKind(string? tilePath)
    {
        if (tilePath is null) return null;
        var i = tilePath.IndexOf("settlement_city_han_", StringComparison.Ordinal);
        if (i >= 0 && i + 20 < tilePath.Length && tilePath[i + 20] is >= 'a' and <= 'h') return RegionKind.Composed(tilePath[i + 20]);
        return tilePath.Contains("settlement_ports") ? RegionKind.Custom : null;
    }
}
