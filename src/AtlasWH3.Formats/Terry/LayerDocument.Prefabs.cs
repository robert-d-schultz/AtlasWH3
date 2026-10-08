using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>Prefab instances inside a layer: expanding ("breaking") one into its entities.</summary>
public sealed partial class LayerDocument
{
    /// <summary>
    /// Replaces a Prefab instance by the entities it stands for (see <see cref="PrefabExpander"/>), placed with the
    /// instance's transform and overrides. The copies get fresh ids (references between them, e.g. a building's
    /// capture_location, are remapped) and sit where the instance sat: in a new folder layer named after the prefab
    /// (<paramref name="intoFolder"/>), or in the instance's own layer. Copies that had meta tags inside the prefab,
    /// and all copies of an instance sitting in a tag layer, go into tag layers with the combined tags (folders do
    /// not carry tags). Returns the new ids; unresolved keys go to <paramref name="missing"/>.
    /// </summary>
    public List<string> ExpandPrefab(string id, PrefabLibrary library, bool recursive = true, bool intoFolder = true,
                                     ICollection<string>? missing = null)
    {
        var instance = Require(id);
        var key = PrefabExpander.KeyOf(instance) ?? throw new InvalidOperationException($"{id} is not a prefab instance");
        var notFound = new List<string>();
        var expanded = PrefabExpander.Expand(instance, library, recursive, notFound);
        foreach (var m in notFound) missing?.Add(m);
        if (expanded.Count == 0)
            throw new InvalidOperationException(notFound.Count > 0
                ? $"prefab '{key}' not found in {library.Root}" : $"prefab '{key}' has no entities");

        var parent = ParentMap().GetValueOrDefault(id)?.FirstOrDefault();
        var instanceTags = TagResolver(Root)(id);
        var folder = intoFolder && instanceTags.Length == 0 ? CreateLayer(key, null, parent) : parent;

        // Fresh ids for every copy (and group members inside copies). A nested prefab used twice repeats inner ids, so
        // ids are assigned per element; references between copies (a building's capture_location, ...) then resolve
        // to the latest copy with that old id, which is the one from the same expansion.
        var map = new Dictionary<string, string>();
        var assigned = new HashSet<string>();
        foreach (var e in expanded.SelectMany(x => x.Entity.DescendantsAndSelf("entity")))
        {
            var fresh = NewIdExcluding(assigned);
            assigned.Add(fresh);
            if ((string?)e.Attribute("id") is { } old) map[old] = fresh;
            e.SetAttributeValue("id", fresh);
        }
        var created = new List<string>();
        var after = instance;
        foreach (var x in expanded)
        {
            foreach (var a in x.Entity.Descendants().SelectMany(d => d.Attributes()).Where(a => a.Name != "id" && map.ContainsKey(a.Value)))
                a.Value = map[a.Value];
            after.AddAfterSelf(x.Entity);
            after = x.Entity;
            var newId = (string)x.Entity.Attribute("id")!;
            created.Add(newId);
            var tags = PrefabExpander.Union(instanceTags, x.Tags);
            if (tags.Length > 0) SetTags(newId, tags);
            else if (folder is not null) SetParent(newId, folder);
        }
        DeleteEntity(id);
        return created;
    }

    /// <summary>A fresh id also distinct from ids handed out but not yet inserted.</summary>
    private string NewIdExcluding(IEnumerable<string> pending)
    {
        var taken = pending.ToHashSet();
        while (true)
        {
            var id = NewId();
            if (!taken.Contains(id)) return id;
        }
    }

    /// <summary>Copies of entities (with group members) and their resolved tags, for writing into another document.</summary>
    public List<(XElement Entity, string Tags)> ExportEntities(IEnumerable<string> ids)
    {
        var tags = TagResolver(Root);
        return ids.Select(i => (new XElement(Require(i)), tags(i))).ToList();
    }

    /// <summary>Adds copies of entities (from <see cref="ExportEntities"/>) with fresh ids, tags re-created as tag layers.
    /// Returns the new ids.</summary>
    public List<string> AddEntities(IEnumerable<(XElement Entity, string Tags)> entities)
    {
        var created = new List<string>();
        foreach (var (e, tags) in entities)
        {
            var copy = new XElement(e);
            foreach (var x in copy.DescendantsAndSelf("entity")) x.SetAttributeValue("id", NewId());
            InsertObject(copy, after: null);
            var id = (string)copy.Attribute("id")!;
            if (tags.Length > 0) SetTags(id, tags);
            created.Add(id);
        }
        return created;
    }
}
