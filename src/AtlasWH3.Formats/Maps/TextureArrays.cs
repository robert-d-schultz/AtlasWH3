using System.Xml.Linq;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// terrain\campaigns\&lt;map&gt;\global_map\texture_arrays.xml: the ordered terrain texture groups.
/// Index i matches blend value i in global_blend.dds channel 0 and the AK blend TIF palette.
/// </summary>
public sealed class TextureArrays
{
    public sealed record Group(int Index, string Name, string BaseColour, string? NormalRoughnessOcclusion);

    public IReadOnlyList<Group> Groups { get; }

    private TextureArrays(IReadOnlyList<Group> groups) => Groups = groups;

    /// <summary>No groups (battle terrain has no global blend).</summary>
    public static TextureArrays Empty { get; } = new([]);

    public static TextureArrays Load(string path)
    {
        var root = XDocument.Load(path).Root!;
        var names = root.Element("group_array")?.Elements("group").Select(e => e.Value.Trim()).ToList() ?? [];
        var colours = root.Element("base_colour_array")?.Elements("texture").Select(e => e.Value.Trim()).ToList() ?? [];
        var nro = root.Element("normal_roughness_occlusion_array")?.Elements().Select(e => e.Value.Trim()).ToList() ?? [];

        var groups = names.Select((name, i) => new Group(
            i, name,
            i < colours.Count ? colours[i] : "",
            i < nro.Count ? nro[i] : null)).ToList();
        return new TextureArrays(groups);
    }
}
