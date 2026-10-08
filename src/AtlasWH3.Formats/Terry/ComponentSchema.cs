using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>Value type of a component attribute, inferred from every value seen in the corpus.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType { String, Bool, Int, Float, Vec2, Vec3, Vec4, Colour, Enum, Id, Path }

public sealed class FieldSchema
{
    public string Name { get; set; } = "";
    public FieldType Type { get; set; }
    /// <summary>How many component instances carried this attribute.</summary>
    public int Count { get; set; }
    /// <summary>Most common value (Terry writes every attribute, so this is the de-facto default).</summary>
    public string? Default { get; set; }
    /// <summary>Every distinct value, when there are few (enums, flags); null for open-ended fields.</summary>
    public List<string>? Values { get; set; }
    public string Source { get; set; } = "corpus";
}

public sealed class ComponentTypeSchema
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public List<FieldSchema> Fields { get; set; } = [];
    /// <summary>Names of child elements (e.g. ECPolyline's point list), with how often each appeared.</summary>
    public Dictionary<string, int> Children { get; set; } = [];
    public bool InConfiguration { get; set; }
}

/// <summary>
/// Field-level schema of every Terry component, built by scanning real .layer/.terry files (the assembly kit's
/// raw_data is the corpus). Terry writes every attribute of every component, so the corpus gives the full field
/// list in Terry's order; types and enum value sets are inferred.
/// </summary>
/// <summary>How Terry writes an entity of one type: its component elements in order (the most common layout seen).</summary>
public sealed class EntityTemplate
{
    public int Count { get; set; }
    public List<string> Components { get; set; } = [];
    /// <summary>Other component layouts seen for the type, with counts (optional/conditional components vary).</summary>
    public Dictionary<string, int> Variants { get; set; } = [];
}

public sealed class ComponentSchema
{
    public int FilesScanned { get; set; }
    public int EntitiesScanned { get; set; }
    public Dictionary<string, ComponentTypeSchema> Components { get; set; } = [];
    /// <summary>Per entity type (see <see cref="TerryEntityTypes.Classify"/>), the component layout Terry writes.</summary>
    public Dictionary<string, EntityTemplate> EntityTemplates { get; set; } = [];
    /// <summary>Entities whose component set matched no configured entity type, keyed by component signature.</summary>
    public Dictionary<string, int> UnclassifiedSignatures { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The schema generated from the assembly kit corpus and embedded in this assembly.</summary>
    public static ComponentSchema Embedded => _embedded.Value;

    private static readonly Lazy<ComponentSchema> _embedded = new(() =>
    {
        using var s = typeof(ComponentSchema).Assembly.GetManifestResourceStream("AtlasWH3.component_schema.json")
                      ?? throw new InvalidOperationException("component_schema.json is not embedded");
        return JsonSerializer.Deserialize<ComponentSchema>(s, Json)!;
    });

    public static ComponentSchema Load(string path) => JsonSerializer.Deserialize<ComponentSchema>(File.ReadAllText(path), Json)!;
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public ComponentTypeSchema? Find(string component) => Components.GetValueOrDefault(component);

    public FieldSchema? Field(string component, string field) => Find(component)?.Fields.FirstOrDefault(f => f.Name == field);

    /// <summary>
    /// Checks a value against the field's inferred type. Returns an error for values Terry could not read (a
    /// non-number in a float field, a wrong vector length, ...) and null when the value is acceptable. Enum values
    /// outside the set seen in the corpus are accepted (the corpus may not show every value); see <see cref="Warn"/>.
    /// </summary>
    public string? Validate(string component, string field, string value)
    {
        var f = Field(component, field);
        if (f is null || value.Length == 0) return null;
        static bool Num(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d);
        string? Vec(int n)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == n && parts.All(Num) ? null : $"{component}.{field} needs {n} numbers separated by spaces, got '{value}'";
        }
        return f.Type switch
        {
            FieldType.Bool => value is "true" or "false" ? null : $"{component}.{field} must be true or false, got '{value}'",
            FieldType.Int or FieldType.Float => Num(value) ? null : $"{component}.{field} must be a number, got '{value}'",
            FieldType.Vec2 => Vec(2),
            FieldType.Vec3 => Vec(3),
            FieldType.Vec4 => Vec(4),
            FieldType.Colour => value.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } c
                                && c.All(x => int.TryParse(x, out var b) && b is >= 0 and <= 255) ? null
                : $"{component}.{field} must be 4 bytes 'r g b a', got '{value}'",
            FieldType.Id => value.Length == 15 && value.All(char.IsAsciiHexDigitLower) ? null
                : $"{component}.{field} must be a 15-digit lowercase hex entity id or empty, got '{value}'",
            _ => null,
        };
    }

    /// <summary>A soft warning: an enum value never seen in the corpus.</summary>
    public string? Warn(string component, string field, string value) =>
        Field(component, field) is { Type: FieldType.Enum, Values: { } values } && value.Length > 0 && !values.Contains(value)
            ? $"{component}.{field} = '{value}' was never seen in the kit (seen: {string.Join(", ", values.Where(v => v.Length > 0))})"
            : null;

    /// <summary>Builds the schema from the given files.</summary>
    public static ComponentSchema Scan(IEnumerable<string> files, EntityConfiguration? config = null, Action<string>? log = null)
    {
        var builder = new Builder(config);
        foreach (var f in files)
        {
            try { builder.AddFile(XDocument.Load(f)); }
            catch (Exception ex) { log?.Invoke($"skip {f}: {ex.Message}"); }
        }
        return builder.Build();
    }

    /// <summary>Defaults the corpus cannot give (the field varies per instance, and zero would be wrong).</summary>
    private static readonly Dictionary<(string, string), string> KnownDefaults = new()
    {
        [("ECTransform", "scale")] = "1 1 1",
        [("ECDecal", "tiling")] = "1",
        [("ECDecal", "parallax_scale")] = "1",
        [("ECPointLight", "radius")] = "10",
        [("ECRiverSpline", "spline_step_size")] = "1",
    };

    private static FieldSchema KnownDefault(string component, FieldSchema f)
    {
        if (KnownDefaults.TryGetValue((component, f.Name), out var d) && f.Default != d && f.Values is null)
        {
            f.Default = d;
            f.Source = "known-default";
        }
        return f;
    }

    private sealed class Builder(EntityConfiguration? config)
    {
        private const int MaxValues = 40;
        private readonly Dictionary<string, ComponentAcc> _components = [];
        private readonly Dictionary<string, int> _unclassified = [];
        private readonly Dictionary<string, Dictionary<string, int>> _layouts = []; // type -> ordered layout -> count
        private int _files, _entities;

        public void AddFile(XDocument doc)
        {
            _files++;
            foreach (var entity in doc.Descendants("entity"))
            {
                // Association <to>/<from> and project-level <entity> wrappers share no names with components,
                // so only elements whose children are EC* components count.
                var comps = entity.Elements().Where(c => c.Name.LocalName.StartsWith("EC", StringComparison.Ordinal)).ToList();
                if (comps.Count == 0) continue;
                _entities++;
                foreach (var c in comps) Acc(c.Name.LocalName).Add(c);
                var names = comps.Select(c => c.Name.LocalName).ToList();
                var type = TerryEntityTypes.Classify(entity, config);
                if (!_layouts.TryGetValue(type, out var layouts)) _layouts[type] = layouts = [];
                var layout = string.Join(",", names);
                layouts[layout] = layouts.GetValueOrDefault(layout) + 1;
                if (config is not null && !TerryEntityTypes.IsLayerType(type) && type is not (TerryEntityTypes.Group or TerryEntityTypes.Signature))
                {
                    var declared = config.Find(type)?.Components.Select(s => s.Type).ToHashSet();
                    if (declared is null || names.Any(n => !declared.Contains(n)))
                    {
                        var sig = string.Join(",", names.Order(StringComparer.Ordinal));
                        _unclassified[sig] = _unclassified.GetValueOrDefault(sig) + 1;
                    }
                }
            }
        }

        private ComponentAcc Acc(string name) =>
            _components.TryGetValue(name, out var a) ? a : _components[name] = new ComponentAcc();

        public ComponentSchema Build()
        {
            var known = config?.ComponentTypes ?? new HashSet<string>();
            var result = new ComponentSchema
            {
                FilesScanned = _files,
                EntitiesScanned = _entities,
                UnclassifiedSignatures = _unclassified.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            foreach (var (type, layouts) in _layouts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var ordered = layouts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                result.EntityTemplates[type] = new EntityTemplate
                {
                    Count = ordered.Sum(kv => kv.Value),
                    Components = [.. ordered[0].Key.Split(',')],
                    Variants = ordered.Skip(1).Take(12).ToDictionary(kv => kv.Key, kv => kv.Value),
                };
            }
            foreach (var (name, acc) in _components.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                result.Components[name] = new ComponentTypeSchema
                {
                    Name = name,
                    Count = acc.Count,
                    InConfiguration = known.Contains(name),
                    Children = acc.Children,
                    Fields = acc.FieldOrder.Select(f => KnownDefault(name, acc.Fields[f].Build(f))).ToList(),
                };
            // Components that are declared but never seen still get an (empty) entry.
            foreach (var name in known.Where(n => !result.Components.ContainsKey(n)))
                result.Components[name] = new ComponentTypeSchema { Name = name, InConfiguration = true };
            return result;
        }

        private sealed class ComponentAcc
        {
            public int Count;
            public readonly List<string> FieldOrder = [];
            public readonly Dictionary<string, FieldAcc> Fields = [];
            public readonly Dictionary<string, int> Children = [];

            public void Add(XElement c)
            {
                Count++;
                foreach (var a in c.Attributes())
                {
                    var n = a.Name.LocalName;
                    if (!Fields.TryGetValue(n, out var f))
                    {
                        Fields[n] = f = new FieldAcc();
                        FieldOrder.Add(n);
                    }
                    f.Add(a.Value);
                }
                foreach (var child in c.Elements())
                    Children[child.Name.LocalName] = Children.GetValueOrDefault(child.Name.LocalName) + 1;
            }
        }

        private sealed class FieldAcc
        {
            private int _count;
            private readonly Dictionary<string, int> _values = [];
            private bool _overflow;
            private bool _bool = true, _int = true, _float = true, _id = true, _path = true;
            private int _vecLen = -1; // -1 unset, 0 = not a vector

            public void Add(string v)
            {
                _count++;
                if (!_overflow)
                {
                    _values[v] = _values.GetValueOrDefault(v) + 1;
                    if (_values.Count > MaxValues) _overflow = true;
                }
                else if (_values.ContainsKey(v)) _values[v]++;

                if (v.Length == 0) return; // empty fits any type
                _bool &= v is "true" or "false";
                _int &= long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                _float &= double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                _id &= v.Length == 15 && v.All(char.IsAsciiHexDigitLower);
                _path &= v.Contains('/') || v.Contains('\\');
                var parts = v.Split(' ');
                var isVec = parts.Length is >= 2 and <= 4
                            && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                _vecLen = !isVec ? 0 : _vecLen == -1 || _vecLen == parts.Length ? parts.Length : 0;
            }

            public FieldSchema Build(string name)
            {
                var nonEmpty = _values.Keys.Count(k => k.Length > 0);
                var all = _values.Keys.Where(k => k.Length > 0).ToList();
                var type =
                    nonEmpty == 0 ? FieldType.String
                    : _bool ? FieldType.Bool
                    : _id ? FieldType.Id
                    : _int ? FieldType.Int
                    : _float ? FieldType.Float
                    : _vecLen == 4 && all.All(IsByteQuad) ? FieldType.Colour
                    : _vecLen == 2 ? FieldType.Vec2
                    : _vecLen == 3 ? FieldType.Vec3
                    : _vecLen == 4 ? FieldType.Vec4
                    : _path ? FieldType.Path
                    : !_overflow && nonEmpty <= 24 && all.All(IsIdentifier) ? FieldType.Enum
                    : FieldType.String;
                var keepValues = !_overflow && type is FieldType.Enum or FieldType.Bool or FieldType.Int or FieldType.String;
                // The most common value is the default only when it dominates; a per-instance field (a model path,
                // a position) gets a neutral value instead of some random instance's.
                var top = _values.Count == 0 ? default : _values.MaxBy(kv => kv.Value);
                var dominant = _values.Count > 0 && (top.Value * 2 >= _count || !_overflow); // few distinct values: a real choice
                var neutral = type switch
                {
                    FieldType.Bool => "false",
                    FieldType.Int or FieldType.Float => "0",
                    FieldType.Vec2 => "0 0",
                    FieldType.Vec3 => "0 0 0",
                    FieldType.Vec4 => "0 0 0 0",
                    FieldType.Colour => "255 255 255 255",
                    FieldType.Enum => top.Key,
                    _ => "",
                };
                return new FieldSchema
                {
                    Name = name,
                    Type = type,
                    Count = _count,
                    Default = _values.Count == 0 ? null : dominant ? top.Key : neutral,
                    Values = keepValues ? _values.Keys.Order(StringComparer.Ordinal).ToList() : null,
                };
            }

            private static bool IsByteQuad(string v) =>
                v.Split(' ').All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b is >= 0 and <= 255);

            private static bool IsIdentifier(string v) => v.Length <= 64 && v.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ',');
        }
    }
}
