using System.Text;
using System.Xml.Linq;
using AtlasWH3.Formats.Db;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// terrain\campaigns\&lt;map&gt;\global_map\texture_arrays.xml: the ordered terrain texture groups.
/// Index i matches blend value i in global_blend.dds and the AK blend TIF palette.
/// </summary>
public sealed class TextureArrays
{
    /// <param name="Colour">0xRRGGBB, the group's colour in Terry's BlendCampaign palette (asset db display_r/g/b); null
    /// when read from the XML, which does not carry it.</param>
    public sealed record Group(int Index, string Name, string BaseColour, string? NormalRoughnessOcclusion,
                               string? MaterialMap = null, uint? Colour = null);

    public IReadOnlyList<Group> Groups { get; }

    private TextureArrays(IReadOnlyList<Group> groups) => Groups = groups;

    /// <summary>No groups (battle terrain has no global blend).</summary>
    public static TextureArrays Empty { get; } = new([]);

    public static TextureArrays Load(string path)
    {
        var root = XDocument.Load(path).Root!;
        var names = root.Element("group_array")?.Elements("group").Select(e => e.Value.Trim()).ToList() ?? [];
        var colours = root.Element("base_colour_array")?.Elements("texture").Select(e => e.Value.Trim()).ToList() ?? [];
        var materials = root.Element("material_map_array")?.Elements("texture").Select(e => e.Value.Trim()).ToList() ?? [];
        var nro = root.Element("normal_roughness_occlusion_array")?.Elements().Select(e => e.Value.Trim()).ToList() ?? [];

        var groups = names.Select((name, i) => new Group(
            i, name,
            i < colours.Count ? colours[i] : "",
            i < nro.Count ? nro[i] : null,
            i < materials.Count ? materials[i] : null)).ToList();
        return new TextureArrays(groups);
    }

    public const string BaseColourNamespace = "campaign_base_colour", MaterialNamespace = "campaign_material",
                        NormalNamespace = "campaign_normal";

    /// <summary>
    /// The groups BOB's Global Tilemap writes, from the merged asset variation db (every warscape_asset_variation_db\
    /// *.assetdb of the game and the mods, <paramref name="dbs"/> in merge order): one group per campaign_base_colour key,
    /// in ordinal key order, each with its first variation's file in campaign_base_colour, campaign_material and
    /// campaign_normal. Byte-identical on IEE (vanilla's 172 groups) and Old World (173, one from its mod).
    /// <paramref name="problems"/> lists keys with more than one variation (only the first is used) or missing a
    /// material or normal entry.
    /// </summary>
    public static TextureArrays FromAssetDbs(IEnumerable<AssetVariationDb> dbs, out List<string> problems)
    {
        var byNamespace = new Dictionary<string, Dictionary<string, List<AssetVariationDb.Variation>>>(StringComparer.Ordinal);
        foreach (var e in dbs.SelectMany(d => d.Entries))
        {
            if (!byNamespace.TryGetValue(e.Namespace, out var keys)) byNamespace[e.Namespace] = keys = new(StringComparer.Ordinal);
            if (!keys.TryGetValue(e.Key, out var list)) keys[e.Key] = list = [];
            list.AddRange(e.Variations);
        }
        problems = [];
        if (!byNamespace.TryGetValue(BaseColourNamespace, out var baseColours)) return Empty;
        Dictionary<string, List<AssetVariationDb.Variation>> Ns(string name) => byNamespace.GetValueOrDefault(name) ?? [];
        var (materials, normals) = (Ns(MaterialNamespace), Ns(NormalNamespace));
        var groups = new List<Group>();
        foreach (var key in baseColours.Keys.Order(StringComparer.Ordinal))
        {
            var colour = baseColours[key];
            var material = materials.GetValueOrDefault(key);
            var normal = normals.GetValueOrDefault(key);
            if (colour.Count > 1 || material?.Count > 1 || normal?.Count > 1)
                problems.Add($"texture group {key} has more than one variation; the first is used");
            if (material is null) problems.Add($"texture group {key} has no {MaterialNamespace} entry");
            if (normal is null) problems.Add($"texture group {key} has no {NormalNamespace} entry");
            var c = colour[0];
            groups.Add(new Group(groups.Count, key, c.FileName, normal?[0].FileName, material?[0].FileName,
                                 (uint)(c.R << 16 | c.G << 8 | c.B)));
        }
        return new TextureArrays(groups);
    }

    /// <summary>The XML as BOB writes it: tabs, CRLF, no declaration, an empty normal_array and an empty climate per
    /// group. An empty array as &lt;name/&gt; is assumed from normal_array, not seen.</summary>
    public byte[] ToXml()
    {
        var s = new StringBuilder();
        void Line(string text) => s.Append(text).Append("\r\n");
        void Array(string name, string element, Func<Group, string?> value)
        {
            if (Groups.Count == 0) { Line($"\t<{name}/>"); return; }
            Line($"\t<{name}>");
            foreach (var g in Groups)
                Line(value(g) is { } v ? $"\t\t<{element}>{System.Security.SecurityElement.Escape(v)}</{element}>" : $"\t\t<{element}/>");
            Line($"\t</{name}>");
        }
        Line("<TERRAIN_TEXTURE_ARRAYS>");
        Line("\t<serialise_version>2</serialise_version>");
        Array("group_array", "group", g => g.Name);
        Array("base_colour_array", "texture", g => g.BaseColour);
        Line("\t<normal_array/>");
        Array("material_map_array", "texture", g => g.MaterialMap);
        Array("climate_array", "climate", _ => null);
        Array("normal_roughness_occlusion_array", "normal_roughness_occlusion", g => g.NormalRoughnessOcclusion);
        Line("</TERRAIN_TEXTURE_ARRAYS>");
        return Encoding.UTF8.GetBytes(s.ToString());
    }
}
