using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace AtlasWH3.Formats.Models;

/// <summary>
/// A .wsmodel: the geometry (.rigid_model_v2) it wraps and the .material for each (part, LOD); part = mesh index
/// within the LOD. Example:
/// <code>&lt;model version="1"&gt;&lt;geometry&gt;…rigid_model_v2&lt;/geometry&gt;
///   &lt;materials&gt;&lt;material part_index="0" lod_index="0"&gt;….xml.material&lt;/material&gt;…</code>
/// </summary>
public sealed record WsModelFile(string Geometry, IReadOnlyDictionary<(int Part, int Lod), string> Materials)
{
    public static WsModelFile Parse(byte[] data)
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root!;
        var materials = new Dictionary<(int, int), string>();
        foreach (var m in root.Element("materials")?.Elements("material") ?? [])
        {
            int I(string n) => int.TryParse((string?)m.Attribute(n), out var v) ? v : 0;
            materials[(I("part_index"), I("lod_index"))] = m.Value.Trim();
        }
        return new WsModelFile(((string?)root.Element("geometry"))?.Trim() ?? "", materials);
    }

    /// <summary>The material for a part at a LOD, falling back to the closest lower LOD that has one.</summary>
    public string? MaterialFor(int part, int lod)
    {
        for (var l = lod; l >= 0; l--)
            if (Materials.TryGetValue((part, l), out var m)) return m;
        return Materials.Where(kv => kv.Key.Part == part).OrderBy(kv => kv.Key.Lod).Select(kv => kv.Value).FirstOrDefault();
    }
}

/// <summary>A .material XML: shader, textures by slot (s_xml_base_colour, s_xml_normal, ...) and typed params.</summary>
public sealed record MaterialFile(string Name, string Shader, IReadOnlyDictionary<string, string> Textures,
                                  IReadOnlyDictionary<string, (string Type, string Value)> Params)
{
    public static MaterialFile Parse(byte[] data)
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿')).Root!;
        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in root.Element("textures")?.Elements("texture") ?? [])
            if (((string?)t.Element("slot"))?.Trim() is { Length: > 0 } slot)
                textures[slot] = ((string?)t.Element("source"))?.Trim() ?? "";
        var parameters = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.Element("params")?.Elements("param") ?? [])
            if (((string?)p.Element("name"))?.Trim() is { Length: > 0 } name)
                parameters[name] = (((string?)p.Element("type"))?.Trim() ?? "", ((string?)p.Element("value"))?.Trim() ?? "");
        return new MaterialFile(((string?)root.Element("name"))?.Trim() ?? "", ((string?)root.Element("shader"))?.Trim() ?? "", textures, parameters);
    }

    /// <summary>The colour texture: base colour (PBR), else diffuse.</summary>
    public string? BaseColour =>
        Textures.GetValueOrDefault("s_xml_base_colour") is { Length: > 0 } b ? b
        : Textures.GetValueOrDefault("s_xml_diffuse") is { Length: > 0 } d ? d
        : Textures.FirstOrDefault(kv => kv.Key.Contains("colour", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("diffuse", StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Alpha-tested shaders (leaves, decals, "alpha_on" materials).</summary>
    public bool AlphaTest =>
        Shader.Contains("alpha", StringComparison.OrdinalIgnoreCase) || Name.Contains("alpha_on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How much of the terrain height the vertex shader adds to each vertex (0 = none): the campaign mountains'
    /// shaders/rigid_detail_map_terrain_blend_with_LF_offset adds sp_adjust_model_to_terrain × the lf height at the
    /// vertex's world x/z to its world y (vs40_main disassembly), so their stored y is an offset above the ground.
    /// </summary>
    public float TerrainOffset => Shader.Contains("with_lf_offset", StringComparison.OrdinalIgnoreCase) ? Float("adjust_model_to_terrain", 1) : 0;

    public float Float(string name, float fallback = 0) =>
        Params.TryGetValue(name, out var p) && float.TryParse(p.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
