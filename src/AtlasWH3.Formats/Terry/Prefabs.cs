using System.Collections.Concurrent;
using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>One prefab: a Terry prefab project whose file name is the key an ECPrefab instance refers to.</summary>
public sealed class PrefabDefinition
{
    public required string Key { get; init; }
    public required string Path { get; init; }
    public required string Database { get; init; }
    /// <summary>Every placeable entity of the prefab's layers (layer entities excluded), with its resolved meta tags.</summary>
    public required IReadOnlyList<(XElement Entity, string Tags)> Entities { get; init; }

    /// <summary>x/z bounds of the entities' positions (prefab space), or null when none has a transform.</summary>
    public (double X0, double Z0, double X1, double Z1)? Bounds
    {
        get
        {
            var pts = Entities.Select(e => e.Entity.Element("ECTransform")).Where(t => t is not null)
                .Select(t => TerryTransform.Read(t).Position).ToList();
            return pts.Count == 0 ? null : (pts.Min(p => p[0]), pts.Min(p => p[2]), pts.Max(p => p[0]), pts.Max(p => p[2]));
        }
    }
}

/// <summary>
/// The prefabs Terry can place in a project of one database: every *.terry under the database's prefab folder in the
/// kit's raw_data (configuration.xml: battle → art/prefabs/battle, campaign → art/campaign/prefabs). A key is the
/// project's file name; the kit has a few duplicate names, where the first path (ordinal order) wins, as reported by
/// <see cref="Duplicates"/>.
/// </summary>
public sealed class PrefabLibrary
{
    private readonly Lazy<Dictionary<string, List<string>>> _index;
    private readonly ConcurrentDictionary<string, PrefabDefinition?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; }
    public string Database { get; }

    public PrefabLibrary(string root, string database)
    {
        Root = System.IO.Path.GetFullPath(root);
        Database = database;
        _index = new Lazy<Dictionary<string, List<string>>>(() =>
        {
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!System.IO.Directory.Exists(Root)) return d;
            foreach (var f in System.IO.Directory.EnumerateFiles(Root, "*.terry", System.IO.SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var key = System.IO.Path.GetFileNameWithoutExtension(f);
                if (!d.TryGetValue(key, out var list)) d[key] = list = [];
                list.Add(f);
            }
            return d;
        });
    }

    /// <summary>The library Terry uses for a database, from the kit's configuration.xml (defaults if it is missing).</summary>
    public static PrefabLibrary ForKit(string akRoot, string database)
    {
        var relative = database == "campaign" ? "art/campaign/prefabs" : "art/prefabs/battle";
        var config = System.IO.Path.Combine(akRoot, "working_data", "Terry", "configuration.xml");
        if (File.Exists(config))
        {
            var path = XDocument.Load(config).Root?.Elements("item").FirstOrDefault(i => (string?)i.Attribute("key") == "path")
                ?.Elements("item").FirstOrDefault(i => (string?)i.Attribute("key") == database)
                ?.Elements("item").FirstOrDefault(i => (string?)i.Attribute("key") == "prefab")?.Value.Trim();
            if (!string.IsNullOrEmpty(path)) relative = path;
        }
        return new PrefabLibrary(System.IO.Path.Combine(akRoot, "raw_data", relative.Replace('/', System.IO.Path.DirectorySeparatorChar)), database);
    }

    public IReadOnlyCollection<string> Keys => _index.Value.Keys;

    public IEnumerable<(string Key, IReadOnlyList<string> Paths)> Duplicates =>
        _index.Value.Where(kv => kv.Value.Count > 1).Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value));

    public string? PathOf(string key) => _index.Value.TryGetValue(key, out var p) ? p[0] : null;

    /// <summary>Where a new prefab with this key (in an optional sub-folder) is written.</summary>
    public string NewPath(string key, string? folder = null) =>
        System.IO.Path.Combine(Root, (folder ?? "").Replace('/', System.IO.Path.DirectorySeparatorChar), key + ".terry");

    /// <summary>Forgets cached definitions and the key index (after a prefab was created or edited).</summary>
    public void Invalidate(string? key = null)
    {
        if (key is null) _cache.Clear();
        else _cache.TryRemove(key, out _);
    }

    public PrefabDefinition? Load(string key) => _cache.GetOrAdd(key, k =>
    {
        var path = PathOf(k);
        return path is null ? null : Read(k, path, Database);
    });

    /// <summary>Reads a prefab project's entities from every file layer it declares.</summary>
    public static PrefabDefinition Read(string key, string terryPath, string database)
    {
        var project = TerryProject.Load(terryPath);
        var entities = new List<(XElement, string)>();
        foreach (var layer in project.Layers())
        {
            if (layer.FilePath is not { } file || !File.Exists(file)) continue;
            XDocument doc;
            try { doc = TerryXml.Load(file); }
            catch (System.Xml.XmlException) { continue; } // one CA prefab layer is malformed XML
            var root = doc.Root!;
            var tags = LayerDocument.TagResolver(root);
            foreach (var e in root.Element("entities")?.Elements("entity") ?? [])
                if (e.Element("ECLayer") is null && e.Element("ECLayerFile") is null)
                    entities.Add((e, tags((string?)e.Attribute("id") ?? "")));
        }
        return new PrefabDefinition { Key = key, Path = terryPath, Database = project.Database, Entities = entities };
    }
}

/// <summary>One entity produced by expanding a prefab instance, already in the instance's parent space.</summary>
public sealed record ExpandedEntity(XElement Entity, string Tags, string FromKey, int Depth);

/// <summary>
/// Expands ECPrefab instances into the entities they stand for, the way the game sees them: each inner entity gets the
/// instance transform composed with its own (<see cref="TerryTransform.Compose"/>), &lt;override id=…&gt; blocks of the
/// instance replace attributes of the inner entity with that id, and nested prefab instances are expanded in turn
/// (recursive) or kept as instances.
/// </summary>
public static class PrefabExpander
{
    public const int MaxDepth = 8;

    public static string? KeyOf(XElement entity) => (string?)entity.Element("ECPrefab")?.Attribute("key");

    public static List<ExpandedEntity> Expand(XElement instance, PrefabLibrary library, bool recursive = true, ICollection<string>? missing = null) =>
        Expand(instance, library, recursive, missing, 1, []);

    private static List<ExpandedEntity> Expand(XElement instance, PrefabLibrary library, bool recursive, ICollection<string>? missing,
                                               int depth, HashSet<string> stack)
    {
        var key = KeyOf(instance);
        var result = new List<ExpandedEntity>();
        if (key is null) return result;
        if (depth > MaxDepth || !stack.Add(key))
        {
            missing?.Add($"{key} (recursive)");
            return result;
        }
        var def = library.Load(key);
        if (def is null)
        {
            missing?.Add(key);
            stack.Remove(key);
            return result;
        }
        var parent = TerryTransform.Read(instance.Element("ECTransform"));
        var overrides = instance.Element("ECPrefab")!.Elements("override")
            .Where(o => o.Attribute("id") is not null)
            .GroupBy(o => (string)o.Attribute("id")!).ToDictionary(g => g.Key, g => g.Last());

        foreach (var (inner, tags) in def.Entities)
        {
            var copy = new XElement(inner);
            if (overrides.TryGetValue((string?)inner.Attribute("id") ?? "", out var o))
                foreach (var oc in o.Elements())
                    if (copy.Element(oc.Name) is { } target)
                        foreach (var a in oc.Attributes()) target.SetAttributeValue(a.Name, a.Value);
            // Entities without a transform (deployment zones, ...) are not placed and stay as they are.
            if (copy.Element("ECTransform") is { } t) parent.Compose(TerryTransform.Read(t)).WriteTo(t);

            if (recursive && KeyOf(copy) is not null)
                foreach (var nested in Expand(copy, library, true, missing, depth + 1, stack))
                    result.Add(nested with { Tags = Union(tags, nested.Tags) });
            else
                result.Add(new ExpandedEntity(copy, tags, key, depth));
        }
        stack.Remove(key);
        return result;
    }

    public static string Union(string a, string b) =>
        string.Join(",", a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(b.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct());
}
