using AtlasWH3.Formats.Db;

namespace AtlasWH3.Formats.Trees;

/// <summary>
/// WH3's campaign tree tables (RPFM TSV exports): campaign_tree_ids (paint colour, removability per tree id) and
/// campaign_tree_variants (one model per tree id and tree type: BASE, BRETONNIA, …). Optional:
/// campaign_tree_type_cultures (which culture uses which tree type).
/// </summary>
public sealed class TreeDatabase
{
    /// <summary>The tree type every tree id has a model for.</summary>
    public const string BaseType = "BASE";

    public sealed record TreeId(string Id, bool CanBeRemoved, uint ColourRgb);
    public sealed record Variant(string TreeId, string TreeType, string ModelPath);

    public IReadOnlyDictionary<string, TreeId> Ids { get; }
    public IReadOnlyList<Variant> Variants { get; }
    /// <summary>culture key → tree type (campaign_tree_type_cultures), empty when that table is not loaded.</summary>
    public IReadOnlyDictionary<string, string> TypeByCulture { get; }

    private TreeDatabase(Dictionary<string, TreeId> ids, List<Variant> variants, Dictionary<string, string> typeByCulture)
    {
        Ids = ids;
        Variants = variants;
        TypeByCulture = typeByCulture;
    }

    public static TreeDatabase Load(string idsTsv, string variantsTsv, string? typeCulturesTsv = null)
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
            variantsTable.Get(row, "tree_type"),
            variantsTable.Get(row, "tree_rigid"))).ToList();

        var cultures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (typeCulturesTsv is not null && File.Exists(typeCulturesTsv))
        {
            var t = TsvTable.Load(typeCulturesTsv);
            foreach (var row in t.Rows) cultures[t.Get(row, "culture")] = t.Get(row, "tree_type");
        }
        return new TreeDatabase(ids, variants, cultures);
    }

    /// <summary>The model of <paramref name="treeId"/> for <paramref name="treeType"/>, else its BASE model, else any.</summary>
    public string? Model(string treeId, string treeType = BaseType)
    {
        Variant? best = null;
        foreach (var v in Variants)
        {
            if (!v.TreeId.Equals(treeId, StringComparison.OrdinalIgnoreCase)) continue;
            if (v.TreeType.Equals(treeType, StringComparison.OrdinalIgnoreCase)) return v.ModelPath;
            if (best is null || v.TreeType.Equals(BaseType, StringComparison.OrdinalIgnoreCase) && !best.TreeType.Equals(BaseType, StringComparison.OrdinalIgnoreCase))
                best = v;
        }
        return best?.ModelPath;
    }

    /// <summary>Tree ids sharing each paint colour, in BOB's pick order (ordinal by id).</summary>
    public IReadOnlyDictionary<uint, string[]> ColourGroups() =>
        Ids.Values.GroupBy(t => t.ColourRgb & 0xFFFFFF)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray());
}
