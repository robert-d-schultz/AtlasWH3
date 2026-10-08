using AtlasWH3.Formats.Db;

namespace AtlasWH3.Formats.Trees;

/// <summary>campaign_tree_ids + campaign_tree_variants: colour, removability and season models per tree id.</summary>
public sealed class TreeDatabase
{
    // Season ids used in trees.campaign_tree_list: the seasons_tables "index" column
    // (spring 0, summer 1, harvest 2, autumn 3, winter 4), used when no seasons table is loaded.
    public static readonly string[] SeasonNames = ["spring", "summer", "harvest", "autumn", "winter"];

    public sealed record TreeId(string Id, bool CanBeRemoved, uint ColourRgb);
    public sealed record Variant(string TreeId, string Season, string ModelPath);

    public IReadOnlyDictionary<string, TreeId> Ids { get; }
    public IReadOnlyList<Variant> Variants { get; }
    /// <summary>seasons_tables key → index column (season_spring → 0, ...).</summary>
    public IReadOnlyDictionary<string, int> SeasonIndex { get; }

    private TreeDatabase(Dictionary<string, TreeId> ids, List<Variant> variants, Dictionary<string, int> seasonIndex)
    {
        Ids = ids;
        Variants = variants;
        SeasonIndex = seasonIndex;
    }

    public static TreeDatabase Load(string idsTsv, string variantsTsv, string? seasonsTsv = null)
    {
        var idsTable = TsvTable.Load(idsTsv);
        var ids = new Dictionary<string, TreeId>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in idsTable.Rows)
        {
            var id = idsTable.Get(row, "tree_id");
            var removable = idsTable.Get(row, "can_be_removed") is "true" or "1" or "True";
            var colour = Convert.ToUInt32(idsTable.Get(row, "colour_hex").TrimStart('#'), 16);
            ids[id] = new TreeId(id, removable, colour);
        }

        var variantsTable = TsvTable.Load(variantsTsv);
        var variants = variantsTable.Rows.Select(row => new Variant(
            variantsTable.Get(row, "tree_id"),
            variantsTable.Get(row, "season"),
            variantsTable.Get(row, "tree_rigid"))).ToList();

        var seasonIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        if (seasonsTsv != null && File.Exists(seasonsTsv))
        {
            var seasons = TsvTable.Load(seasonsTsv);
            foreach (var row in seasons.Rows)
                seasonIndex[seasons.Get(row, "season")] = int.Parse(seasons.Get(row, "index"));
        }
        else
            for (var i = 0; i < SeasonNames.Length; i++) seasonIndex["season_" + SeasonNames[i]] = i;
        return new TreeDatabase(ids, variants, seasonIndex);
    }

    /// <summary>Season ids a tree type should be written with, derived from its DB variant rows.</summary>
    public uint[] SeasonsFor(string treeId)
    {
        var seasons = Variants.Where(v => string.Equals(v.TreeId, treeId, StringComparison.OrdinalIgnoreCase))
            .Select(v => Array.IndexOf(SeasonNames, v.Season.ToLowerInvariant()))
            .ToList();
        if (seasons.Count == 0 || seasons.All(s => s < 0))
            return [CampaignTreeList.NoSeason];
        return seasons.Where(s => s >= 0).Distinct().OrderBy(s => s).Select(s => (uint)s).ToArray();
    }

    /// <summary>
    /// The season list BOB writes for a tree id (QTU::CampaignTreeGenerator, decompiled): each variant row sets the
    /// model of its season (rows without a known season set the default model; later rows win). Every season whose
    /// model is non-empty contributes its seasons_tables index, ascending; a non-empty default model adds
    /// <see cref="CampaignTreeList.NoSeason"/> last.
    /// </summary>
    public uint[] BobSeasonsFor(string treeId)
    {
        var models = new Dictionary<int, string>(); // season index, -1 = default
        foreach (var v in Variants.Where(v => v.TreeId == treeId))
            models[SeasonIndex.TryGetValue(v.Season, out var i) ? i : -1] = v.ModelPath;
        var ids = models.Where(kv => kv.Key is >= 0 and < 15 && kv.Value.Length > 0).Select(kv => (uint)kv.Key)
            .Order().ToList();
        if (models.TryGetValue(-1, out var fallback) && fallback.Length > 0) ids.Add(CampaignTreeList.NoSeason);
        return [.. ids];
    }

    /// <summary>Tree ids sharing each paint colour, in BOB's pick order (ordinal by id).</summary>
    public IReadOnlyDictionary<uint, string[]> ColourGroups() =>
        Ids.Values.GroupBy(t => t.ColourRgb & 0xFFFFFF)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray());
}
