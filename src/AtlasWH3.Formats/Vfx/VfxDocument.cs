using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;

namespace AtlasWH3.Formats.Vfx;

/// <summary>One property as written: its attributes (value, x/y/z/w, min/max, type, path) and a curve's text.</summary>
public sealed record VfxProperty(string Name, IReadOnlyDictionary<string, string> Attributes, string? Text)
{
    public string? Value => Attributes.GetValueOrDefault("value");
    public bool IsTrue => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);

    public float Number(string attribute, float fallback = 0) =>
        Attributes.TryGetValue(attribute, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>A <c>float2(8.0,8.0)</c> value.</summary>
    public Vector2 Float2(Vector2 fallback)
    {
        var parts = Value?.Replace("float2(", "").TrimEnd(')').Split(',');
        return parts is { Length: 2 }
               && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? new Vector2(x, y) : fallback;
    }

    public VfxCurve? Curve => VfxCurve.Parse(Text);
}

/// <summary>An emitter: its type (continuous_mesh, continuous_rigid, …) and its quality-0 properties.</summary>
public sealed record VfxEmitter(string Id, string Type, IReadOnlyDictionary<string, VfxProperty> Properties);

/// <summary>One modifier of a behaviour chain (a shader fragment: ps_texture, vs_colour, …).</summary>
public sealed record VfxModifier(string Fragment, IReadOnlyDictionary<string, VfxProperty> Properties, IReadOnlyDictionary<string, string> Textures);

/// <summary>What a reference says about one emitter of the effect it references, by emitter id.</summary>
public sealed record VfxEmitterOverride(bool Enable, Vector4 TintMin, Vector4 TintMax, Vector2 ParticleLifeBias, Vector2 EmitterLifeBias, Vector2 RateScale)
{
    public static readonly VfxEmitterOverride None = new(true, Vector4.One, Vector4.One, Vector2.Zero, Vector2.Zero, Vector2.One);
}

/// <summary>A reference to another effect, placed (scale, then rotation in degrees about X, Y, Z, then position), with
/// its timing and per-emitter overrides.</summary>
public sealed record VfxReference(string Target, Vector2 Scale, Vector3 Position, Vector3 RotationDegrees, Vector2 StartTime,
    bool InfiniteLife, float SpeedMultiplier, IReadOnlyDictionary<string, VfxEmitterOverride> Overrides);

/// <summary>
/// One vfx/*.xml at quality 0 (the highest; CA's files write 1 and 2 for the lower settings). The file has several root
/// elements (shader_library, vfx, vfx_references, editor_data) and is UTF-16 in the packs. An emitter, its behaviour
/// chain and its render stack share one id.
/// </summary>
public sealed class VfxDocument
{
    public List<VfxEmitter> Emitters { get; } = [];
    /// <summary>Behaviour chain id → its quality-0 modifiers. A chain with no quality-0 block is absent.</summary>
    public Dictionary<string, IReadOnlyList<VfxModifier>> Chains { get; } = [];
    /// <summary>Render stack id → its quality-0 scene_view_type, where one is set.</summary>
    public Dictionary<string, string> SceneViews { get; } = [];
    public List<VfxReference> References { get; } = [];

    public static VfxDocument Parse(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (text.StartsWith("<?xml", StringComparison.Ordinal)) text = text[(text.IndexOf("?>", StringComparison.Ordinal) + 2)..];
        var root = XElement.Parse("<root>" + text + "</root>");
        var doc = new VfxDocument();
        var vfx = root.Element("vfx");

        foreach (var e in vfx?.Element("emitters")?.Elements("emitter") ?? [])
            doc.Emitters.Add(new VfxEmitter((string?)e.Attribute("id") ?? "", (string?)e.Attribute("type") ?? "",
                Properties(e.Elements().Where(x => x.Name.LocalName is "property" or "ranged_property"))));

        foreach (var chain in vfx?.Element("behaviour_chains")?.Elements("behaviour_chain") ?? [])
        {
            var block = chain.Elements("quality").FirstOrDefault(q => (string?)q.Attribute("value") == "0");
            if (block is null) continue;
            doc.Chains[(string?)chain.Attribute("id") ?? ""] = block.Elements("modifier").Select(m => new VfxModifier(
                (string?)m.Attribute("fragment") ?? "",
                Properties(m.Elements("property")),
                m.Elements("texture_property").GroupBy(t => (string?)t.Attribute("name") ?? "")
                    .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("path") ?? ""))).ToList();
        }

        foreach (var stack in vfx?.Element("effect_render_stacks")?.Elements("effect_render_stack") ?? [])
            if (Properties(stack.Elements("property")).GetValueOrDefault("scene_view_type")?.Value is { } view)
                doc.SceneViews[(string?)stack.Attribute("id") ?? ""] = view;

        foreach (var r in root.Element("vfx_references")?.Elements("vfx") ?? [])
        {
            var overrides = new Dictionary<string, VfxEmitterOverride>();
            foreach (var o in r.Elements("emitter"))
                overrides[(string?)o.Attribute("name") ?? ""] = new VfxEmitterOverride((string?)o.Attribute("enable") != "false",
                    new Vector4(Num(o, "tint_min_r", 1), Num(o, "tint_min_g", 1), Num(o, "tint_min_b", 1), Num(o, "tint_min_a", 1)),
                    new Vector4(Num(o, "tint_max_r", 1), Num(o, "tint_max_g", 1), Num(o, "tint_max_b", 1), Num(o, "tint_max_a", 1)),
                    new Vector2(Num(o, "particle_lifetime_bias_min", 0), Num(o, "particle_lifetime_bias_max", 0)),
                    new Vector2(Num(o, "emitter_lifetime_bias_min", 0), Num(o, "emitter_lifetime_bias_max", 0)),
                    new Vector2(Num(o, "spawn_rate_scale_factor_min", 1), Num(o, "spawn_rate_scale_factor_max", 1)));
            doc.References.Add(new VfxReference((string?)r.Attribute("vfx_ref") ?? "",
                new Vector2(Num(r, "scale_min", 1), Num(r, "scale_max", 1)),
                new Vector3(Num(r, "pos_x", 0), Num(r, "pos_y", 0), Num(r, "pos_z", 0)),
                new Vector3(Num(r, "rot_x", 0), Num(r, "rot_y", 0), Num(r, "rot_z", 0)),
                new Vector2(Num(r, "start_time_min", 0), Num(r, "start_time_max", 0)),
                (string?)r.Attribute("infinite_life") != "false", Num(r, "simulation_speed_multiplier", 1), overrides));
        }
        return doc;
    }

    /// <summary>The quality-0 properties (no quality attribute counts as 0); the first written wins.</summary>
    private static Dictionary<string, VfxProperty> Properties(IEnumerable<XElement> elements)
    {
        var output = new Dictionary<string, VfxProperty>();
        foreach (var e in elements)
        {
            if ((string?)e.Attribute("name") is not { } name || ((string?)e.Attribute("quality") ?? "0") != "0") continue;
            var attributes = e.Attributes().Where(a => a.Name.LocalName is not ("name" or "quality")).ToDictionary(a => a.Name.LocalName, a => a.Value);
            output.TryAdd(name, new VfxProperty(name, attributes, e.Value.Length == 0 ? null : e.Value));
        }
        return output;
    }

    private static float Num(XElement e, string attribute, float fallback) =>
        float.TryParse((string?)e.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
