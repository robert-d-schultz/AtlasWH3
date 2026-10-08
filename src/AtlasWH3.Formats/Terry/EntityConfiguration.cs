using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>One component slot of an entity type, as declared in Terry's entity_configuration.xml. Conditional slots
/// come from a <c>&lt;component parameter="shape"&gt;</c> group: the component is present only when the entity's
/// <paramref name="Parameter"/> equals <paramref name="Value"/> (e.g. shape=polyline gives ECPolyline).</summary>
public sealed record EntityComponentSlot(string Type, bool ReadOnly, string? Parameter = null, string? Value = null)
{
    public bool Conditional => Parameter is not null;
}

/// <summary>An <c>&lt;allow_if&gt;</c> rule: the entity may be created in these project types for this tile database.
/// Empty lists mean "any".</summary>
public sealed record EntityAllowRule(IReadOnlyList<string> ProjectTypes, string? TileDatabase);

/// <summary>One entity type Terry can create (AIHint, BattleCatchmentArea, Prop, ...).</summary>
public sealed record EntityTypeDefinition(
    string Type,
    string? DefaultName,
    IReadOnlyList<EntityComponentSlot> Components,
    IReadOnlyList<EntityAllowRule> AllowIf,
    IReadOnlyList<string> AllowIfChildOf)
{
    /// <summary>True when Terry offers this type in a project of the given kind ("tile_map", "prefab", ...) and
    /// database ("battle" / "campaign"). Types restricted to being children of another type return false.</summary>
    public bool AllowedIn(string projectType, string tileDatabase) =>
        AllowIf.Count == 0 ? AllowIfChildOf.Count == 0
        : AllowIf.Any(r => (r.ProjectTypes.Count == 0 || r.ProjectTypes.Contains(projectType))
                           && (r.TileDatabase is null || r.TileDatabase == tileDatabase));
}

/// <summary>
/// Terry's entity schema (assembly_kit/working_data/Terry/entity_configuration.xml): which component elements make up
/// each entity type and where the type may be created. Entities in .layer/.terry files are identified by matching
/// their component elements against these definitions (<see cref="Classify"/>).
/// </summary>
public sealed class EntityConfiguration
{
    public IReadOnlyList<EntityTypeDefinition> Types { get; }

    private EntityConfiguration(IReadOnlyList<EntityTypeDefinition> types) => Types = types;

    public const string RelativePath = "working_data/Terry/entity_configuration.xml";

    public static EntityConfiguration Load(string path) => Parse(XDocument.Load(path));

    /// <summary>The configuration shipped with the assembly kit, embedded in this assembly (used when no kit is given).</summary>
    public static EntityConfiguration Embedded => _embedded.Value;

    private static readonly Lazy<EntityConfiguration> _embedded = new(() =>
    {
        using var s = typeof(EntityConfiguration).Assembly.GetManifestResourceStream("AtlasWH3.entity_configuration.xml")
                      ?? throw new InvalidOperationException("entity_configuration.xml is not embedded");
        return Parse(XDocument.Load(s));
    });

    /// <summary>The kit's configuration when the kit has one, else the embedded copy.</summary>
    public static EntityConfiguration ForKit(string? akRoot) =>
        akRoot is not null && File.Exists(Path.Combine(akRoot, RelativePath)) ? LoadFromKit(akRoot) : Embedded;

    /// <summary>Loads from an assembly kit root.</summary>
    public static EntityConfiguration LoadFromKit(string akRoot) => Load(Path.Combine(akRoot, RelativePath));

    public static EntityConfiguration Parse(XDocument doc)
    {
        var types = new List<EntityTypeDefinition>();
        foreach (var e in doc.Root!.Elements("entity"))
        {
            var slots = new List<EntityComponentSlot>();
            foreach (var c in e.Elements("component"))
            {
                if ((string?)c.Attribute("parameter") is { } parameter)
                    slots.AddRange(c.Elements("component").Select(v =>
                        new EntityComponentSlot((string)v.Attribute("type")!, IsTrue(v.Attribute("readonly")), parameter, (string?)v.Attribute("value"))));
                else
                    slots.Add(new EntityComponentSlot((string)c.Attribute("type")!, IsTrue(c.Attribute("readonly"))));
            }
            var allow = e.Elements("allow_if")
                .Select(a => new EntityAllowRule(
                    ((string?)a.Attribute("project_type") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    (string?)a.Attribute("tile_database")))
                .ToList();
            var childOf = e.Elements("allow_if_child_of").Select(a => (string)a.Attribute("type")!).ToList();
            types.Add(new EntityTypeDefinition((string)e.Attribute("type")!, ((string?)e.Element("default_name"))?.Trim(),
                slots, allow, childOf));
        }
        return new EntityConfiguration(types);
    }

    private static bool IsTrue(XAttribute? a) => string.Equals((string?)a, "true", StringComparison.OrdinalIgnoreCase);

    public EntityTypeDefinition? Find(string type) => Types.FirstOrDefault(t => t.Type == type);

    /// <summary>Every component type named anywhere in the configuration.</summary>
    public IReadOnlySet<string> ComponentTypes => Types.SelectMany(t => t.Components).Select(c => c.Type).ToHashSet();

    /// <summary>
    /// The entity type whose component list best matches the entity's component elements: the definition sharing the
    /// most components with it, ties broken by fewest components the entity lacks. Null when nothing overlaps.
    /// </summary>
    public EntityTypeDefinition? Classify(XElement entity) =>
        Classify(entity.Elements().Select(c => c.Name.LocalName));

    public EntityTypeDefinition? Classify(IEnumerable<string> componentNames)
    {
        var have = componentNames.ToHashSet();
        EntityTypeDefinition? best = null;
        int bestShared = 0, bestMissing = int.MaxValue;
        foreach (var t in Types)
        {
            var declared = t.Components.Select(c => c.Type).ToHashSet();
            var shared = declared.Count(have.Contains);
            var missing = t.Components.Where(c => !c.Conditional).Select(c => c.Type).Distinct().Count(n => !have.Contains(n));
            // Prefer the definition that accounts for most of the entity, then the tightest fit.
            if (shared > bestShared || (shared == bestShared && shared > 0 && missing < bestMissing))
            {
                best = t;
                bestShared = shared;
                bestMissing = missing;
            }
        }
        return best;
    }
}
