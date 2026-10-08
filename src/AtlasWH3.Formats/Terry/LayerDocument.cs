using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AtlasWH3.Formats.Props;

namespace AtlasWH3.Formats.Terry;

/// <summary>One entity of a layer, as read for listing and querying.</summary>
public sealed record LayerEntity(string Id, string? Name, string Kind, string Asset, double[] Position, double[] Rotation,
                                 double[] Scale, string Tags, string Seasons);

/// <summary>
/// A Terry .layer opened for editing single entities. Untouched entities keep their exact text: the document is written
/// back in Terry's own layout (two-space indent, self-closed empty elements, LF, no escaping beyond XML's), so loading
/// and saving an unedited layer gives the same bytes.
///
/// Tags work the way Terry and <see cref="LayerWriter"/> store them: tag-layer entities (ECLayer + ECLayerExportTags)
/// own their members through &lt;associations&gt;&lt;Logical&gt;&lt;from id=layer&gt;&lt;to id=member/&gt;.
/// </summary>
public sealed partial class LayerDocument
{
    private readonly XDocument _doc;
    private readonly string _idSeed;
    private readonly string _newline;
    private int _idCounter;

    public string? Path { get; }

    private LayerDocument(XDocument doc, string? path, string newline)
    {
        _doc = doc;
        Path = path;
        _newline = newline;
        _idSeed = System.IO.Path.GetFileName(path) ?? "layer";
    }

    public static LayerDocument Load(string path) => Parse(File.ReadAllText(path), path);
    public static LayerDocument Parse(string text, string? path = null) => new(TerryXml.Parse(text), path, TerryXml.NewlineOf(text));

    /// <summary>A new, empty layer in Terry's layout.</summary>
    public static LayerDocument Empty(string? path = null, int version = 35) =>
        Parse($"""
               <?xml version="1.0" encoding="UTF-8"?>
               <layer version="{version}">
                 <entities/>
                 <associations>
                   <Logical/>
                   <Transform/>
                 </associations>
               </layer>

               """.ReplaceLineEndings("\n"), path);

    private XElement Root => _doc.Root!;
    private XElement EntitiesElement => Root.Element("entities") ?? throw new InvalidDataException("layer has no <entities>");

    private XElement Logical
    {
        get
        {
            var assoc = Root.Element("associations");
            if (assoc is null) Root.Add(assoc = new XElement("associations", new XElement("Logical"), new XElement("Transform")));
            var logical = assoc.Element("Logical");
            if (logical is null) assoc.AddFirst(logical = new XElement("Logical"));
            return logical;
        }
    }

    public IEnumerable<XElement> EntityElements => EntitiesElement.Elements("entity");

    /// <summary>Every entity, including those nested in ECGroup groups.</summary>
    public IEnumerable<XElement> AllEntityElements => EntitiesElement.Descendants("entity");

    public XElement? Find(string id) => AllEntityElements.FirstOrDefault(e => (string?)e.Attribute("id") == id);

    private XElement Require(string id) => Find(id) ?? throw new KeyNotFoundException($"no entity {id} in {_idSeed}");

    /// <summary>Every entity, tags resolved through the tag layers (including tag layers nested in tag layers).</summary>
    public List<LayerEntity> Entities()
    {
        var tagsOf = TagResolver(Root);
        return EntityElements.Select(e => Describe(e, tagsOf)).ToList();
    }

    public LayerEntity Get(string id) => Describe(Require(id), TagResolver(Root));

    /// <summary>The entity's XML exactly as it is written to the file.</summary>
    public string EntityXml(string id)
    {
        var sb = new StringBuilder();
        TerryXml.Write(sb, Require(id), 2, _newline);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- edits

    /// <summary>Sets any of position / rotation (degrees) / scale; components passed as null are kept.</summary>
    public void SetTransform(string id, double?[]? position = null, double?[]? rotation = null, double?[]? scale = null)
    {
        var t = Require(id).Element("ECTransform") ?? throw new InvalidOperationException($"entity {id} has no ECTransform");
        Apply(t, "position", position, "0 0 0");
        Apply(t, "rotation", rotation, "0 0 0");
        Apply(t, "scale", scale, "1 1 1");

        static void Apply(XElement t, string attr, double?[]? values, string fallback)
        {
            if (values is null) return;
            var current = ReadVector((string?)t.Attribute(attr) ?? fallback);
            for (var i = 0; i < 3 && i < values.Length; i++)
                if (values[i] is { } v) current[i] = v;
            t.SetAttributeValue(attr, string.Join(' ', current.Select(LayerWriter.F)));
        }
    }

    /// <summary>Sets one attribute of one component element (e.g. ECCampaignProperties season_mask).</summary>
    public void SetAttribute(string id, string component, string attribute, string value)
    {
        var c = Require(id).Element(component) ?? throw new InvalidOperationException($"entity {id} has no {component}");
        if (c.Attribute(attribute) is null)
            throw new InvalidOperationException($"{component} of {id} has no attribute {attribute} (Terry would drop unknown attributes)");
        c.SetAttributeValue(attribute, value);
    }

    /// <summary>Adds a prop or decal entity (laid out as <see cref="LayerWriter"/> writes them) and returns its id.</summary>
    public string AddProp(PropRecord prop)
    {
        var id = NewId();
        var e = new XElement("entity", new XAttribute("id", id));
        foreach (var line in LayerWriter.PropLines(prop)) e.Add(XElement.Parse(line));
        InsertObject(e, after: null);
        if (prop.Tags.Length > 0) SetTags(id, prop.Tags);
        return id;
    }

    /// <summary>Copies an entity (with its tag-layer membership) next to the original, moved by the offset.</summary>
    public string Duplicate(string id, double dx = 0, double dy = 0, double dz = 0)
    {
        var source = Require(id);
        if (IsTagLayer(source)) throw new InvalidOperationException($"{id} is a tag layer; duplicate its members instead");
        var copy = new XElement(source);
        var newId = NewId();
        copy.SetAttributeValue("id", newId);
        InsertObject(copy, after: source);
        if (copy.Element("ECTransform") is not null) MoveBy(newId, dx, dy, dz);
        foreach (var from in Logical.Elements("from").Where(f => f.Elements("to").Any(t => (string?)t.Attribute("id") == id)).ToList())
            from.Add(new XElement("to", new XAttribute("id", newId)));
        return newId;
    }

    public void MoveBy(string id, double dx, double dy, double dz)
    {
        var p = ReadVector((string?)Require(id).Element("ECTransform")?.Attribute("position") ?? "0 0 0");
        SetTransform(id, position: [p[0] + dx, p[1] + dy, p[2] + dz]);
    }

    /// <summary>Removes an object entity and its association links; tag layers left empty are removed too.</summary>
    public void Delete(string id)
    {
        var e = Require(id);
        if (IsTagLayer(e)) throw new InvalidOperationException($"{id} is a tag layer; delete or re-tag its members instead");
        e.Remove();
        foreach (var to in Root.Element("associations")?.Descendants("to").Where(t => (string?)t.Attribute("id") == id).ToList() ?? [])
            to.Remove();
        RemoveEmptyTagLayers();
    }

    /// <summary>Makes the entity's direct tag set exactly <paramref name="tags"/> (comma separated; "" = untagged),
    /// reusing a top-level tag layer with the same tags or creating one.</summary>
    public void SetTags(string id, string tags)
    {
        var e = Require(id);
        if (IsTagLayer(e)) throw new InvalidOperationException($"{id} is a tag layer");
        var wanted = SplitTags(tags);
        foreach (var to in Logical.Elements("from").SelectMany(f => f.Elements("to")).Where(t => (string?)t.Attribute("id") == id).ToList())
            to.Remove();
        if (wanted.Count > 0)
        {
            var nested = Logical.Elements("from").SelectMany(f => f.Elements("to")).Select(t => (string?)t.Attribute("id")).ToHashSet();
            var layer = EntityElements.FirstOrDefault(l => IsTagLayer(l) && !nested.Contains((string?)l.Attribute("id"))
                                                           && SplitTags(LayerTagsOf(l)).SetEquals(wanted));
            var joined = string.Join(",", wanted.Order(Comparer<string>.Create(MetaTags.NaturalCompare)));
            if (layer is null)
            {
                layer = new XElement("entity", new XAttribute("id", NewId()), new XAttribute("name", joined));
                foreach (var line in LayerWriter.TagLayerLines(joined)) layer.Add(XElement.Parse(line));
                EntitiesElement.Add(layer);
            }
            var layerId = (string)layer.Attribute("id")!;
            var from = Logical.Elements("from").FirstOrDefault(f => (string?)f.Attribute("id") == layerId);
            if (from is null) Logical.Add(from = new XElement("from", new XAttribute("id", layerId)));
            from.Add(new XElement("to", new XAttribute("id", id)));
        }
        RemoveEmptyTagLayers();
    }

    // ---------------------------------------------------------------- output

    /// <summary>The layer text in Terry's layout.</summary>
    public string ToText() => TerryXml.ToText(Root, _newline);

    public void Save(string? path = null) =>
        File.WriteAllText(path ?? Path ?? throw new InvalidOperationException("no path"), ToText(), new UTF8Encoding(false));

    // ---------------------------------------------------------------- helpers

    /// <summary>Resolved (comma-joined) tags per entity id: the tags of every tag layer that owns it, transitively.</summary>
    public static Func<string, string> TagResolver(XElement root)
    {
        var entities = root.Element("entities")?.Elements("entity").ToList() ?? [];
        var layerTags = entities.Where(e => e.Element("ECLayerExportTags") is not null)
            .ToDictionary(e => (string)e.Attribute("id")!, e => (string?)e.Element("ECLayerExportTags")!.Attribute("tags") ?? "");
        var parents = new Dictionary<string, List<string>>();
        foreach (var from in root.Element("associations")?.Element("Logical")?.Elements("from") ?? [])
        {
            var id = (string)from.Attribute("id")!;
            if (!layerTags.ContainsKey(id)) continue;
            foreach (var to in from.Elements("to"))
            {
                var member = (string)to.Attribute("id")!;
                if (!parents.TryGetValue(member, out var list)) parents[member] = list = [];
                list.Add(id);
            }
        }
        var memo = new Dictionary<string, HashSet<string>>();
        HashSet<string> TagSet(string id, int depth = 0)
        {
            if (memo.TryGetValue(id, out var cached)) return cached;
            var set = new HashSet<string>();
            if (depth < 16 && parents.TryGetValue(id, out var ps))
                foreach (var layer in ps)
                {
                    foreach (var t in layerTags[layer].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) set.Add(t);
                    set.UnionWith(TagSet(layer, depth + 1));
                }
            return memo[id] = set;
        }
        return id => string.Join(",", TagSet(id));
    }

    public static string KindOf(XElement e) =>
        e.Element("ECLayer") is not null ? "tag_layer"
        : e.Element("ECRiver") is not null ? "river"
        : e.Element("ECDecal") is not null ? "decal"
        : e.Element("ECPropMesh") is not null ? "prop"
        : e.Element("ECVFX") is not null ? "vfx"
        : e.Element("ECPointLight") is not null ? "light"
        : e.Element("ECCompositeScene") is not null ? "scene"
        : e.Element("ECSoundMarker") is not null ? "sound"
        : e.Element("ECLightProbe") is not null ? "probe"
        : e.Element("ECPolygonMesh") is not null ? "poly"
        : "other";

    private static LayerEntity Describe(XElement e, Func<string, string> tagsOf)
    {
        var id = (string?)e.Attribute("id") ?? "";
        var kind = KindOf(e);
        var asset = kind switch
        {
            "prop" => (string?)e.Element("ECMesh")?.Attribute("model_path"),
            "decal" => (string?)e.Element("ECDecal")?.Attribute("model_path"),
            "vfx" => (string?)e.Element("ECVFX")?.Attribute("vfx"),
            "scene" => (string?)e.Element("ECCompositeScene")?.Attribute("path"),
            "sound" => (string?)e.Element("ECSoundMarker")?.Attribute("key"),
            "poly" => (string?)e.Element("ECPolygonMesh")?.Attribute("material"),
            "river" => (string?)e.Element("ECRiverSpline")?.Attribute("material"),
            "tag_layer" => (string?)e.Element("ECLayerExportTags")?.Attribute("tags"),
            _ => null,
        } ?? "";
        var t = e.Element("ECTransform");
        return new LayerEntity(id, (string?)e.Attribute("name"), kind, asset,
            ReadVector((string?)t?.Attribute("position") ?? "0 0 0"),
            ReadVector((string?)t?.Attribute("rotation") ?? "0 0 0"),
            ReadVector((string?)t?.Attribute("scale") ?? "1 1 1"),
            kind == "tag_layer" ? "" : tagsOf(id),
            (string?)e.Element("ECCampaignProperties")?.Attribute("season_mask") ?? "");
    }

    public static double[] ReadVector(string s)
    {
        var v = s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return v.Length >= 3 ? v[..3] : [.. v, .. Enumerable.Repeat(0.0, 3 - v.Length)];
    }

    private static bool IsTagLayer(XElement e) => e.Element("ECLayer") is not null;
    private static string LayerTagsOf(XElement e) => (string?)e.Element("ECLayerExportTags")?.Attribute("tags") ?? "";

    private static HashSet<string> SplitTags(string tags) =>
        tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    /// <summary>Object entities go after <paramref name="after"/>, or after the last object (before the tag layers
    /// that close the list, as Terry and LayerWriter order them).</summary>
    private void InsertObject(XElement e, XElement? after)
    {
        after ??= EntityElements.LastOrDefault(x => !IsTagLayer(x));
        if (after is not null) after.AddAfterSelf(e);
        else EntitiesElement.AddFirst(e);
    }

    /// <summary>Removes tag layers (layers carrying ECLayerExportTags) left without members. Plain folder layers are
    /// kept even when empty: they are created on purpose.</summary>
    private void RemoveEmptyTagLayers()
    {
        while (true)
        {
            var froms = Logical.Elements("from").ToDictionary(f => (string)f.Attribute("id")!, f => f);
            var empty = EntityElements.Where(l => IsTagLayer(l) && l.Element("ECLayerExportTags") is not null)
                .Where(l => !froms.TryGetValue((string)l.Attribute("id")!, out var f) || !f.Elements("to").Any())
                .ToList();
            if (empty.Count == 0) return;
            foreach (var l in empty)
            {
                var lid = (string)l.Attribute("id")!;
                l.Remove();
                if (froms.TryGetValue(lid, out var f)) f.Remove();
                foreach (var to in Logical.Descendants("to").Where(t => (string?)t.Attribute("id") == lid).ToList()) to.Remove();
            }
        }
    }

    /// <summary>A fresh 15-hex-digit id ("1" + 14 digits, like Terry's) not used anywhere in the layer.</summary>
    private string NewId()
    {
        var used = Root.Descendants().Select(e => (string?)e.Attribute("id")).Where(i => i is not null).ToHashSet();
        while (true)
        {
            var hash = 14695981039346656037UL;
            foreach (var ch in $"{_idSeed}/edit/{used.Count}/{_idCounter++}")
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            var id = "1" + (hash & 0x00FF_FFFF_FFFF_FFFFUL).ToString("x14");
            if (!used.Contains(id)) return id;
        }
    }
}
