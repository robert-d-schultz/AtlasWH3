using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>One component of an entity: its element name and attributes in file order.</summary>
public sealed record TerryComponentData(string Name, IReadOnlyList<KeyValuePair<string, string>> Fields, int ChildElements)
{
    public string? this[string field] => Fields.FirstOrDefault(f => f.Key == field).Value;
}

/// <summary>A shape drawn by an entity (polyline, spline, rectangle, circle), in the entity's local x/z plane.</summary>
public sealed record TerryOutline(bool Closed, IReadOnlyList<(double X, double Z)> Points);

/// <summary>Any entity of a layer, as Terry shows it: type, name, logical parents (the layers it sits in), components.
/// <paramref name="Group"/> is the id of the ECGroup entity it is nested in, if any.</summary>
public sealed record TerryEntityData(string Id, string? Name, string Type, IReadOnlyList<string> Parents,
                                     IReadOnlyList<TerryComponentData> Components, string? Group)
{
    public TerryComponentData? Component(string name) => Components.FirstOrDefault(c => c.Name == name);

    /// <summary>Shapes of ECPolyline / ECRiverSpline / ECRectangle / ECCircle, in local coordinates.</summary>
    public IReadOnlyList<TerryOutline> Outlines { get; init; } = [];

    /// <summary>ECTransform position, rotation (degrees) and scale; null without a transform.</summary>
    public (double[] Position, double[] Rotation, double[] Scale)? Transform =>
        Component("ECTransform") is { } t
            ? (LayerDocument.ReadVector(t["position"] ?? "0 0 0"), LayerDocument.ReadVector(t["rotation"] ?? "0 0 0"),
               LayerDocument.ReadVector(t["scale"] ?? "1 1 1"))
            : null;

    /// <summary>A local x/z point (y = 0) placed in the world by the entity's transform, projected to x/z.</summary>
    public (double X, double Z) ToWorld(double x, double z)
    {
        if (Transform is not var (p, r, s)) return (x, z);
        var (wx, _, wz) = new TerryTransform(p, r, s).Apply(x, 0, z);
        return (wx, wz);
    }
}

/// <summary>
/// Generic entity editing: any entity type, any component, any field, checked against the corpus schema
/// (<see cref="ComponentSchema"/>) and Terry's entity configuration (<see cref="EntityConfiguration"/>).
/// Hierarchy inside a layer file is the &lt;associations&gt;&lt;Logical&gt; graph: a layer entity (folder or tag
/// layer) is the "from" of its members.
/// </summary>
public sealed partial class LayerDocument
{
    private EntityConfiguration? _config;
    private ComponentSchema? _schema;

    /// <summary>Configuration and schema used for typing and validation (defaults: the embedded copies).</summary>
    public EntityConfiguration Config { get => _config ??= EntityConfiguration.Embedded; set => _config = value; }
    public ComponentSchema Schema { get => _schema ??= ComponentSchema.Embedded; set => _schema = value; }

    // ---------------------------------------------------------------- reading

    /// <summary>Every entity (groups' members included) in file order.</summary>
    public List<TerryEntityData> ReadEntities()
    {
        var parents = ParentMap();
        return AllEntityElements.Select(e => Read(e, parents)).ToList();
    }

    public TerryEntityData Read(string id) => Read(Require(id), ParentMap());

    public string TypeOf(string id) => TerryEntityTypes.Classify(Require(id), Config);

    private TerryEntityData Read(XElement e, Dictionary<string, List<string>> parents)
    {
        var id = (string?)e.Attribute("id") ?? "";
        var group = e.Parent?.Parent?.Parent?.Parent is { Name.LocalName: "entity" } g ? (string?)g.Attribute("id") : null;
        return new TerryEntityData(id, (string?)e.Attribute("name"), TerryEntityTypes.Classify(e, Config),
            parents.TryGetValue(id, out var p) ? p : [],
            e.Elements().Select(c => new TerryComponentData(c.Name.LocalName,
                c.Attributes().Select(a => KeyValuePair.Create(a.Name.LocalName, a.Value)).ToList(),
                c.Elements().Count())).ToList(),
            group) { Outlines = OutlinesOf(e) };
    }

    /// <summary>An entity element outside any document (e.g. from a prefab), without parents or group.</summary>
    public static TerryEntityData Describe(XElement e, EntityConfiguration? config = null) =>
        new((string?)e.Attribute("id") ?? "", (string?)e.Attribute("name"), TerryEntityTypes.Classify(e, config), [],
            e.Elements().Select(c => new TerryComponentData(c.Name.LocalName,
                c.Attributes().Select(a => KeyValuePair.Create(a.Name.LocalName, a.Value)).ToList(), c.Elements().Count())).ToList(),
            null) { Outlines = OutlinesOf(e) };

    private static IReadOnlyList<TerryOutline> OutlinesOf(XElement e)
    {
        List<TerryOutline>? list = null;
        static double D(string? s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var poly in e.Element("ECPolyline")?.Elements("polyline") ?? [])
            (list ??= []).Add(new TerryOutline((string?)poly.Attribute("closed") == "true",
                poly.Elements("point").Select(pt => (D((string?)pt.Attribute("x")), D((string?)pt.Attribute("y")))).ToList()));
        foreach (var spline in e.Element("ECRiverSpline")?.Elements("spline") ?? [])
            (list ??= []).Add(new TerryOutline((string?)spline.Attribute("closed") == "true",
                spline.Elements("point").Select(pt => ((string?)pt.Attribute("position") ?? "0,0,0").Split(','))
                    .Select(v => (D(v.ElementAtOrDefault(0)), D(v.ElementAtOrDefault(2)))).ToList()));
        if (e.Element("ECRectangle") is { } rect)
        {
            double w = D((string?)rect.Attribute("width")) / 2, h = D((string?)rect.Attribute("height")) / 2;
            (list ??= []).Add(new TerryOutline(true, [(-w, -h), (w, -h), (w, h), (-w, h)]));
        }
        if (e.Element("ECCircle") is { } circle)
        {
            var r = D((string?)circle.Attribute("radius"));
            var half = (string?)circle.Attribute("is_semicircle") == "true";
            var n = half ? 17 : 32;
            (list ??= []).Add(new TerryOutline(!half, Enumerable.Range(0, n)
                .Select(i => (r * Math.Cos(i * (half ? Math.PI / (n - 1) : 2 * Math.PI / n)), r * Math.Sin(i * (half ? Math.PI / (n - 1) : 2 * Math.PI / n)))).ToList()));
        }
        return list ?? (IReadOnlyList<TerryOutline>)[];
    }

    /// <summary>Member id → ids of the entities whose Logical association lists it.</summary>
    private Dictionary<string, List<string>> ParentMap()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var from in Root.Element("associations")?.Element("Logical")?.Elements("from") ?? [])
        {
            var parent = (string?)from.Attribute("id");
            if (parent is null) continue;
            foreach (var to in from.Elements("to"))
                if ((string?)to.Attribute("id") is { } child)
                {
                    if (!map.TryGetValue(child, out var list)) map[child] = list = [];
                    list.Add(parent);
                }
        }
        return map;
    }

    /// <summary>Direct logical members of a layer entity.</summary>
    public List<string> Members(string layerId) =>
        Logical.Elements("from").Where(f => (string?)f.Attribute("id") == layerId)
            .SelectMany(f => f.Elements("to")).Select(t => (string)t.Attribute("id")!).ToList();

    // ---------------------------------------------------------------- field edits

    /// <summary>
    /// Sets one field of one component. A field the entity's component lacks is added when the schema knows it
    /// (in the schema's attribute order); unknown fields are refused, since Terry drops attributes it does not know.
    /// Returns a warning (e.g. an enum value never seen in the kit) or null.
    /// </summary>
    public string? SetField(string id, string component, string field, string value)
    {
        var c = Require(id).Element(component) ?? throw new InvalidOperationException($"entity {id} has no {component}");
        if (Schema.Validate(component, field, value) is { } error) throw new ArgumentException(error);
        if (c.Attribute(field) is not null)
        {
            c.SetAttributeValue(field, value);
            return Schema.Warn(component, field, value);
        }
        var order = Schema.Find(component)?.Fields.Select(f => f.Name).ToList();
        if (order is null || !order.Contains(field))
            throw new InvalidOperationException($"{component} has no field '{field}' (known: {string.Join(", ", order ?? [])})");
        var attrs = c.Attributes().Select(a => (a.Name.LocalName, a.Value)).Append((field, value))
            .OrderBy(a => order.IndexOf(a.Item1) is var i && i < 0 ? int.MaxValue : i).ToList();
        c.RemoveAttributes();
        foreach (var (n, v) in attrs) c.SetAttributeValue(n, v);
        return Schema.Warn(component, field, value);
    }

    /// <summary>Sets or (with null/empty) removes the entity's name.</summary>
    public void SetName(string id, string? name) => Require(id).SetAttributeValue("name", string.IsNullOrEmpty(name) ? null : name);

    /// <summary>Adds a component with the schema's default field values, placed where Terry's layout for the entity's
    /// type puts it. Returns false when the entity already has it.</summary>
    public bool AddComponent(string id, string component, IReadOnlyDictionary<string, string>? fields = null)
    {
        var e = Require(id);
        if (e.Element(component) is not null) return false;
        var c = NewComponent(component, fields);
        var layout = Schema.EntityTemplates.GetValueOrDefault(TerryEntityTypes.Classify(e, Config))?.Components ?? [];
        var rank = layout.IndexOf(component);
        // Insert before the first existing component that comes later in the layout; else append.
        var before = rank < 0 ? null
            : e.Elements().FirstOrDefault(x => layout.IndexOf(x.Name.LocalName) is var r && r > rank);
        if (before is not null) before.AddBeforeSelf(c);
        else e.Add(c);
        return true;
    }

    public bool RemoveComponent(string id, string component)
    {
        var c = Require(id).Element(component);
        if (c is null) return false;
        if (component is "ECLayer" or "ECLayerFile" or "ECFileLayer")
            throw new InvalidOperationException($"removing {component} would change what {id} is; delete the layer instead");
        c.Remove();
        return true;
    }

    /// <summary>A component with default values. Its attribute set and order follow the same component elsewhere in
    /// this file when there is one (Terry versions differ in both), else the schema's.</summary>
    private XElement NewComponent(string component, IReadOnlyDictionary<string, string>? fields)
    {
        var c = new XElement(component);
        var schema = Schema.Find(component);
        var sibling = AllEntityElements.Select(e => e.Element(component)).FirstOrDefault(x => x is not null);
        var names = sibling?.Attributes().Select(a => a.Name.LocalName).ToList() ?? schema?.Fields.Select(f => f.Name).ToList() ?? [];
        foreach (var n in names)
            c.SetAttributeValue(n, fields?.GetValueOrDefault(n) ?? schema?.Fields.FirstOrDefault(f => f.Name == n)?.Default ?? "");
        foreach (var (k, v) in fields ?? new Dictionary<string, string>())
        {
            if (schema is not null && schema.Fields.All(f => f.Name != k))
                throw new InvalidOperationException($"{component} has no field '{k}'");
            if (Schema.Validate(component, k, v) is { } error) throw new ArgumentException(error);
            if (c.Attribute(k) is null) c.SetAttributeValue(k, v);
        }
        return c;
    }

    // ---------------------------------------------------------------- entity creation and structure

    /// <summary>
    /// Creates an entity of a configured type (Prop, Decal, VFX, PointLight, ...) with Terry's component layout for
    /// that type and the schema defaults, then applies <paramref name="fields"/> ("ECComponent.field" → value).
    /// <paramref name="components"/> overrides the component list (e.g. to pick a conditional shape component).
    /// The entity goes into <paramref name="parentLayer"/> (a folder or tag layer of this file) if given.
    /// </summary>
    public string CreateEntity(string type, IReadOnlyDictionary<string, string>? fields = null, string? name = null,
                               string? parentLayer = null, IReadOnlyList<string>? components = null)
    {
        if (TerryEntityTypes.IsLayerType(type)) throw new ArgumentException($"use CreateLayer for {type}");
        var def = Config.Find(type);
        var list = components?.ToList()
                   ?? Schema.EntityTemplates.GetValueOrDefault(type)?.Components
                   ?? def?.Components.Where(s => !s.Conditional).Select(s => s.Type)
                       .Concat(def.Components.Where(s => s.Conditional).GroupBy(s => s.Parameter).Select(g => g.First().Type))
                       .ToList()
                   ?? throw new ArgumentException($"unknown entity type '{type}' (see entity types)");
        var byComponent = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (key, value) in fields ?? new Dictionary<string, string>())
        {
            var dot = key.IndexOf('.');
            if (dot < 0) throw new ArgumentException($"field key '{key}' must be Component.field");
            var comp = key[..dot];
            if (!list.Contains(comp)) throw new ArgumentException($"{type} has no {comp} (components: {string.Join(", ", list)})");
            if (!byComponent.TryGetValue(comp, out var d)) byComponent[comp] = d = [];
            d[key[(dot + 1)..]] = value;
        }

        var id = NewId();
        var e = new XElement("entity", new XAttribute("id", id));
        if (!string.IsNullOrEmpty(name ?? def?.DefaultName)) e.SetAttributeValue("name", name ?? def!.DefaultName);
        foreach (var comp in list) e.Add(NewComponent(comp, byComponent.GetValueOrDefault(comp)));
        InsertObject(e, after: null);
        if (parentLayer is not null) SetParent(id, parentLayer);
        return id;
    }

    /// <summary>
    /// Creates a layer inside this file: a plain folder layer, or a tag layer when <paramref name="tags"/> is given
    /// (its members export with those meta tags). Nested in <paramref name="parentLayer"/> if given. Returns its id.
    /// </summary>
    public string CreateLayer(string name, string? tags = null, string? parentLayer = null)
    {
        var id = NewId();
        var e = new XElement("entity", new XAttribute("id", id), new XAttribute("name", name));
        if (string.IsNullOrEmpty(tags)) e.Add(new XElement("ECLayer"));
        else foreach (var line in LayerWriter.TagLayerLines(tags)) e.Add(XElement.Parse(line));
        EntitiesElement.Add(e);
        if (parentLayer is not null) SetParent(id, parentLayer);
        return id;
    }

    /// <summary>
    /// Makes <paramref name="parentLayer"/> (a layer entity of this file, or null for the file's top level) the only
    /// logical parent of the entity. Tag layers emptied by the move are removed, as Terry does.
    /// </summary>
    public void SetParent(string id, string? parentLayer)
    {
        Require(id);
        if (parentLayer is not null)
        {
            var p = Require(parentLayer);
            if (p.Element("ECLayer") is null) throw new InvalidOperationException($"{parentLayer} is not a layer");
            if (parentLayer == id || Ancestors(parentLayer).Contains(id))
                throw new InvalidOperationException($"{parentLayer} is inside {id}; that would make a cycle");
        }
        var emptied = RemoveLinks(Logical.Elements("from").SelectMany(f => f.Elements("to")).Where(t => (string?)t.Attribute("id") == id));
        if (parentLayer is not null)
        {
            var from = Logical.Elements("from").FirstOrDefault(f => (string?)f.Attribute("id") == parentLayer);
            if (from is null) Logical.Add(from = new XElement("from", new XAttribute("id", parentLayer)));
            from.Add(new XElement("to", new XAttribute("id", id)));
        }
        RemoveIfEmpty(emptied);
        RemoveEmptyTagLayers();
    }

    private HashSet<string> Ancestors(string id)
    {
        var parents = ParentMap();
        var seen = new HashSet<string>();
        var stack = new Stack<string>([id]);
        while (stack.Count > 0)
            foreach (var p in parents.GetValueOrDefault(stack.Pop()) ?? [])
                if (seen.Add(p)) stack.Push(p);
        return seen;
    }

    /// <summary>
    /// Deletes any entity. A layer's members are deleted with it when <paramref name="withMembers"/> is set, else
    /// they move up to the layer's own parent. Transform links (child objects attached to a parent object) are
    /// dropped. Returns the ids removed.
    /// </summary>
    public List<string> DeleteEntity(string id, bool withMembers = false)
    {
        var e = Require(id);
        var removed = new List<string>();
        if (e.Element("ECLayer") is not null)
        {
            var members = Members(id);
            var parent = ParentMap().GetValueOrDefault(id)?.FirstOrDefault();
            foreach (var m in members)
            {
                if (Find(m) is null) continue;
                if (withMembers) removed.AddRange(DeleteEntity(m, withMembers: true));
                else SetParent(m, parent);
            }
            // SetParent may already have removed an emptied tag layer.
            if (Find(id) is null) return [.. removed, id];
        }
        // Group members go with their group.
        foreach (var nested in e.Descendants("entity")) removed.Add((string)nested.Attribute("id")!);
        e.Remove();
        removed.Add(id);
        var gone = removed.ToHashSet();
        var assoc = Root.Element("associations");
        var links = assoc?.Descendants().Where(x => x.Name.LocalName is "to" or "from" && gone.Contains((string?)x.Attribute("id") ?? "")).ToList() ?? [];
        RemoveIfEmpty(RemoveLinks(links));
        RemoveEmptyTagLayers();
        return removed;
    }

    /// <summary>Removes an entity from this document and returns its element (with group members) for
    /// <see cref="ImportEntity"/> into another layer file. Its logical links in this file are dropped.</summary>
    public XElement DetachEntity(string id)
    {
        var e = Require(id);
        if (e.Element("ECLayer") is not null) throw new InvalidOperationException($"{id} is a layer; move its members instead");
        var copy = new XElement(e);
        DeleteEntity(id);
        return copy;
    }

    /// <summary>Adds an entity element (from <see cref="DetachEntity"/>) keeping its id; refuses ids already used here.</summary>
    public void ImportEntity(XElement entity, string? parentLayer = null)
    {
        var ids = entity.DescendantsAndSelf("entity").Select(x => (string?)x.Attribute("id")).ToList();
        var used = Root.Descendants("entity").Select(x => (string?)x.Attribute("id")).ToHashSet();
        if (ids.FirstOrDefault(used.Contains) is { } clash) throw new InvalidOperationException($"entity {clash} already exists in {_idSeed}");
        InsertObject(new XElement(entity), after: null);
        if (parentLayer is not null) SetParent((string)entity.Attribute("id")!, parentLayer);
    }

    /// <summary>Removes association elements and returns the &lt;from&gt; blocks that lost a &lt;to&gt;.</summary>
    private static List<XElement> RemoveLinks(IEnumerable<XElement> links)
    {
        var parents = new List<XElement>();
        foreach (var x in links.ToList())
        {
            if (x.Parent is { Name.LocalName: "from" } p) parents.Add(p);
            x.Remove();
        }
        return parents;
    }

    /// <summary>Drops the given &lt;from&gt; blocks if this edit left them empty (empty blocks already in the file are
    /// left alone, so untouched parts keep their bytes).</summary>
    private static void RemoveIfEmpty(List<XElement> froms)
    {
        foreach (var f in froms.Distinct())
            if (f.Parent is not null && !f.Elements("to").Any()) f.Remove();
    }
}
