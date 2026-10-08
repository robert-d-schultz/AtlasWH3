using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Generic Terry editing: any entity type, component and field in any Terry project (the campaign map by default,
/// or any .terry such as a prefab). Edits are batched op lists, checked against the corpus schema and Terry's entity
/// configuration; each batch is one undo step in a <see cref="FileJournal"/>. For the campaign map the journal is the
/// one <see cref="PropEditor"/> uses, so prop and entity edits share one history.
///
/// Hierarchy: the project's scene lists the top-level (file) layers; inside each .layer, folder and tag layers hold
/// their members through Logical associations. Visibility, locking and the active layer are per-user state
/// (.terry.user), as in Terry.
/// </summary>
public sealed class EntityEditor
{
    public sealed record Found(ProjectLayer Layer, TerryEntityData Entity);

    private readonly ProjectPaths _paths;
    private readonly Dictionary<string, LayerDocument> _open = new(StringComparer.OrdinalIgnoreCase);
    private TerryProject? _project;
    private TerryUserFile? _user;
    private Dictionary<string, ProjectLayer>? _idIndex;
    private bool _projectDirty, _userDirty;
    private readonly HashSet<string> _deletedFiles = new(StringComparer.OrdinalIgnoreCase);

    public EntityConfiguration Config { get; }
    public ComponentSchema Schema { get; }

    private PrefabLibrary? _prefabs;
    /// <summary>The prefabs Terry offers for this project's database (raw_data/art/prefabs/battle, art/campaign/prefabs).</summary>
    public PrefabLibrary Prefabs => _prefabs ??= PrefabLibrary.ForKit(_paths.AssemblyKitRoot, File.Exists(TerryPath) ? Project.Database : "campaign");

    /// <summary>Files an op batch creates outside the project folder (new prefabs), written at commit.</summary>
    private readonly Dictionary<string, Action<string>> _extraFiles = new(StringComparer.OrdinalIgnoreCase);
    public string TerryPath { get; }

    public EntityEditor(ProjectPaths paths, string? terryPath = null)
    {
        _paths = paths;
        TerryPath = Path.GetFullPath(terryPath ?? CampaignTerryPath(paths));
        Config = EntityConfiguration.ForKit(paths.AssemblyKitRoot);
        Schema = ComponentSchema.Embedded;
    }

    public static string CampaignTerryPath(ProjectPaths paths) => Path.Combine(paths.AkTerrainDir, paths.MapName + ".terry");

    private bool IsCampaignMap => TerryPath.Equals(Path.GetFullPath(CampaignTerryPath(_paths)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The campaign map shares output\prop_edits\&lt;map&gt; with <see cref="PropEditor"/>; other projects get
    /// output\entity_edits\&lt;project&gt;_&lt;hash of path&gt;.</summary>
    public string HistoryDir => IsCampaignMap
        ? FileJournal.EditDir(_paths, "prop_edits")
        : Path.Combine(_paths.OutputRoot, "entity_edits",
            $"{Path.GetFileNameWithoutExtension(TerryPath)}_{Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(TerryPath.ToLowerInvariant())))[..8].ToLowerInvariant()}");

    private FileJournal Journal => new(HistoryDir);

    public TerryProject Project => _project ??= File.Exists(TerryPath) ? TerryProject.Load(TerryPath)
        : throw new FileNotFoundException("Terry project not found.", TerryPath);

    public TerryUserFile User => _user ??= TerryUserFile.Load(TerryPath);

    // ---------------------------------------------------------------- layers

    public IReadOnlyList<ProjectLayer> Layers() => Project.Layers();

    /// <summary>A top-level layer by name or id.</summary>
    public ProjectLayer Layer(string nameOrId) =>
        Project.FindLayer(nameOrId)
        ?? Layers().FirstOrDefault(l => l.FilePath is { } p && Path.GetFileName(p).Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"no layer '{nameOrId}' in {Path.GetFileName(TerryPath)}");

    public LayerDocument Open(ProjectLayer layer)
    {
        var path = layer.FilePath ?? throw new InvalidOperationException($"layer {layer.Name} is not stored in a file");
        if (!_open.TryGetValue(path, out var doc))
        {
            doc = File.Exists(path) ? LayerDocument.Load(path) : throw new FileNotFoundException($"layer file missing for {layer.Name}", path);
            doc.Config = Config;
            doc.Schema = Schema;
            _open[path] = doc;
        }
        return doc;
    }

    /// <summary>Every entity of the given (default: all) file layers, parsed in parallel.</summary>
    public List<(ProjectLayer Layer, List<TerryEntityData> Entities)> ReadAll(IEnumerable<ProjectLayer>? layers = null)
    {
        var list = (layers ?? Layers()).Where(l => l.FilePath is { } p && (File.Exists(p) || _open.ContainsKey(p))).ToList();
        var result = new (ProjectLayer, List<TerryEntityData>)[list.Count];
        Parallel.For(0, list.Count, i =>
        {
            var doc = _open.GetValueOrDefault(list[i].FilePath!) ?? LayerDocument.Load(list[i].FilePath!);
            doc.Config = Config;
            doc.Schema = Schema;
            result[i] = (list[i], doc.ReadEntities());
        });
        return [.. result];
    }

    public ProjectLayer LayerOf(string id)
    {
        if (_idIndex is null)
        {
            _idIndex = new Dictionary<string, ProjectLayer>();
            foreach (var (layer, entities) in ReadAll())
                foreach (var e in entities) _idIndex.TryAdd(e.Id, layer);
        }
        return _idIndex.TryGetValue(id, out var l) ? l : throw new KeyNotFoundException($"no entity {id} in any layer");
    }

    public Found Get(string id)
    {
        var layer = LayerOf(id);
        return new Found(layer, Open(layer).Read(id));
    }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// Entity filter. <paramref name="Fields"/> entries are "ECComponent.field=value" (exact), "ECComponent.field~text"
    /// (contains, case-insensitive), "ECComponent.field!=value", or just "ECComponent" (has the component).
    /// Rect is [x0, z0, x1, z1] on ECTransform position. Layers (folder/tag) are excluded unless asked for by type.
    /// </summary>
    public sealed record Query(string? Layer = null, string[]? Types = null, string? Name = null, string[]? Fields = null,
                               double[]? Rect = null, string? Parent = null, string[]? Ids = null);

    public IEnumerable<Found> Find(Query q)
    {
        var layers = q.Layer is null ? Layers() : [Layer(q.Layer)];
        var filters = (q.Fields ?? []).Select(FieldFilter).ToList();
        var ids = q.Ids?.ToHashSet();
        foreach (var (layer, entities) in ReadAll(layers))
            foreach (var e in entities)
            {
                if (ids is not null && !ids.Contains(e.Id)) continue;
                if (q.Types is { Length: > 0 } ? !q.Types.Contains(e.Type, StringComparer.OrdinalIgnoreCase)
                    : TerryEntityTypes.IsLayerType(e.Type) && ids is null) continue;
                if (q.Name is not null && !(e.Name ?? "").Contains(q.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (q.Parent is not null && !e.Parents.Contains(q.Parent)) continue;
                if (!filters.All(f => f(e))) continue;
                if (q.Rect is [var x0, var z0, var x1, var z1])
                {
                    if (Position(e) is not { } p) continue;
                    if (p[0] < Math.Min(x0, x1) || p[0] > Math.Max(x0, x1) || p[2] < Math.Min(z0, z1) || p[2] > Math.Max(z0, z1)) continue;
                }
                yield return new Found(layer, e);
            }
    }

    /// <summary>A field filter as used by <see cref="Query.Fields"/>: "ECX.f=v", "ECX.f~v", "ECX.f!=v" or "ECX".</summary>
    public static Func<TerryEntityData, bool> FieldFilter(string f)
    {
        foreach (var op in new[] { "!=", "~", "=" })
        {
            var i = f.IndexOf(op, StringComparison.Ordinal);
            if (i < 0) continue;
            var (comp, field) = SplitKey(f[..i]);
            var value = f[(i + op.Length)..];
            return e => e.Component(comp)?[field] is { } v && op switch
            {
                "=" => v == value,
                "!=" => v != value,
                _ => v.Contains(value, StringComparison.OrdinalIgnoreCase),
            };
        }
        return e => e.Component(f.Trim()) is not null;
    }

    public static double[]? Position(TerryEntityData e) =>
        e.Component("ECTransform")?["position"] is { } p ? LayerDocument.ReadVector(p) : null;

    // ---------------------------------------------------------------- edits

    /// <summary>
    /// Applies a batch of ops and saves every touched file (layers, .terry, .terry.user) as one undo step. Nothing is
    /// written if any op fails. Ops (JSON objects with "op"):
    ///   set              {ids|query, fields: {"ECComponent.field": value}, name?}
    ///   add_component    {ids|query, component, fields?: {field: value}}
    ///   remove_component {ids|query, component}
    ///   create           {type, layer, parent?, name?, fields?, components?, position?: [x,y,z]}
    ///   delete           {ids|query, with_members?}
    ///   move_to_layer    {ids|query, layer, parent?}     (another file layer; ids are kept)
    ///   set_parent       {ids|query, parent: id|null}   (folder/tag layer in the same file)
    ///   duplicate        {ids|query, by?: [dx,dy,dz]}  (copies keep the original's layers/tags; returns the copies)
    ///   place_prefab     {key, layer, parent?, position?, rotation?, scale?}
    ///   expand_prefab    {ids|query, recursive?: true, into_folder?: true}   (instance → its entities, overrides applied)
    ///   make_prefab      {ids|query, key, folder?, replace?: true}         (new prefab project; originals → one instance)
    ///   create_layer     {name, layer?, tags?, parent?} (no "layer": a new file layer in the project)
    ///   rename           {id, name}                     (entity, nested layer, or file layer)
    ///   layer_state      {id, visible?, frozen?, export?, active?}
    ///   delete_layer     {id, with_members?}            (a file layer also loses its .layer file; undo restores it)
    /// "query" takes the <see cref="Query"/> fields: {layer, types, name, fields, rect, parent}.
    /// </summary>
    public JsonArray Apply(JsonArray ops, string? label = null)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new JsonArray();
        foreach (var node in ops)
        {
            var op = node as JsonObject ?? throw new ArgumentException("each op must be a JSON object");
            try { results.Add(ApplyOne(op, touched)); }
            catch (Exception ex)
            {
                Reset();
                throw new InvalidOperationException($"op {results.Count} ({op["op"]}): {ex.Message}", ex);
            }
        }
        if (touched.Count > 0 || _projectDirty || _userDirty || _extraFiles.Count > 0) Commit(touched, label ?? Summary(ops));
        return results;
    }

    private void Reset()
    {
        _open.Clear();
        _project = null;
        _user = null;
        _idIndex = null;
        _projectDirty = _userDirty = false;
        _deletedFiles.Clear();
        _extraFiles.Clear();
        _prefabs = null;
    }

    private JsonNode ApplyOne(JsonObject op, HashSet<string> touched)
    {
        var kind = Str(op, "op") ?? throw new ArgumentException("missing \"op\"");
        switch (kind)
        {
            case "create": return Create(op, touched);
            case "place_prefab": return PlacePrefab(op, touched);
            case "make_prefab": return MakePrefab(op, touched);
            case "create_layer": return CreateLayer(op, touched);
            case "rename" when Layers().Any(l => l.Id == Str(op, "id")):
                Project.RenameLayer(Str(op, "id")!, Str(op, "name") ?? throw new ArgumentException("rename needs \"name\""));
                _projectDirty = true;
                return new JsonObject { ["op"] = kind, ["layer"] = Str(op, "id") };
            case "layer_state": return LayerState(op, touched);
            case "delete_layer" when Layers().Any(l => l.Id == Str(op, "id") || l.Name == Str(op, "id")):
                return DeleteFileLayer(Layer(Str(op, "id")!), touched);
        }

        var targets = Targets(op);
        var results = new JsonArray();
        var warnings = new JsonArray();
        foreach (var (layer, id) in targets)
        {
            var doc = Open(layer);
            touched.Add(layer.FilePath!);
            switch (kind)
            {
                case "set":
                    if (op["fields"] is JsonObject fields)
                        foreach (var (key, value) in fields)
                        {
                            var (comp, field) = SplitKey(key);
                            if (doc.SetField(id, comp, field, Text(value)) is { } w) warnings.Add(w);
                        }
                    if (op.ContainsKey("name")) doc.SetName(id, Str(op, "name"));
                    break;
                case "rename":
                    doc.SetName(id, Str(op, "name"));
                    break;
                case "add_component":
                    doc.AddComponent(id, Str(op, "component") ?? throw new ArgumentException("needs \"component\""), StrMap(op["fields"]));
                    break;
                case "remove_component":
                    doc.RemoveComponent(id, Str(op, "component") ?? throw new ArgumentException("needs \"component\""));
                    break;
                case "delete":
                case "delete_layer":
                    if (doc.Find(id) is null) continue; // removed with an earlier target
                    foreach (var gone in doc.DeleteEntity(id, Bool(op, "with_members") ?? false))
                    {
                        _idIndex?.Remove(gone);
                        results.Add(gone);
                    }
                    continue;
                case "set_parent":
                    doc.SetParent(id, Str(op, "parent"));
                    break;
                case "expand_prefab":
                    var missing = new List<string>();
                    var made = doc.ExpandPrefab(id, Prefabs, Bool(op, "recursive") ?? true, Bool(op, "into_folder") ?? true, missing);
                    _idIndex?.Remove(id);
                    foreach (var m in made)
                    {
                        _idIndex?.TryAdd(m, layer);
                        results.Add(Summary(layer, doc.Read(m)));
                    }
                    foreach (var m in missing.Distinct()) warnings.Add($"prefab '{m}' not found; kept out of the expansion");
                    continue;
                case "duplicate":
                    var by = op["by"] is JsonArray b ? b.Select(n => Num(n)).ToArray() : [0.0, 0, 0];
                    var copy = doc.Duplicate(id, by.ElementAtOrDefault(0), by.ElementAtOrDefault(1), by.ElementAtOrDefault(2));
                    _idIndex?.TryAdd(copy, layer);
                    results.Add(Describe(layer, doc.Read(copy)));
                    continue;
                case "move_to_layer":
                    var dest = Layer(Str(op, "layer") ?? throw new ArgumentException("move_to_layer needs \"layer\""));
                    if (dest.Id == layer.Id)
                    {
                        doc.SetParent(id, Str(op, "parent"));
                        break;
                    }
                    var element = doc.DetachEntity(id);
                    var destDoc = Open(dest);
                    destDoc.ImportEntity(element, Str(op, "parent"));
                    touched.Add(dest.FilePath!);
                    _idIndex?.Remove(id);
                    _idIndex?.TryAdd(id, dest);
                    results.Add(Describe(dest, destDoc.Read(id)));
                    continue;
                default:
                    throw new ArgumentException($"unknown op '{kind}'");
            }
            results.Add(Describe(layer, doc.Read(id)));
        }
        var result = new JsonObject { ["op"] = kind, [kind is "delete" or "delete_layer" ? "deleted" : "entities"] = results };
        if (warnings.Count > 0) result["warnings"] = warnings;
        return result;
    }

    /// <summary>The (layer, id) pairs an op addresses: "ids"/"id", or a "query".</summary>
    private List<(ProjectLayer Layer, string Id)> Targets(JsonObject op)
    {
        if (op["query"] is JsonObject q)
        {
            var found = Find(ParseQuery(q)).Select(f => (f.Layer, f.Entity.Id)).ToList();
            if (found.Count == 0 && Bool(op, "allow_empty") != true) throw new InvalidOperationException("query matched no entities");
            return found;
        }
        var ids = op["ids"] is JsonArray a ? a.Select(n => n!.ToString()).ToList()
            : op["id"] is JsonValue v ? [v.ToString()]
            : throw new ArgumentException("needs \"ids\": [...], \"id\", or \"query\"");
        return ids.Select(i => (Str(op, "in_layer") is { } ln ? Layer(ln) : LayerOf(i), i)).ToList();
    }

    public static Query ParseQuery(JsonObject q) => new(
        Str(q, "layer"),
        q["types"] is JsonArray t ? t.Select(n => n!.ToString()).ToArray() : Str(q, "type") is { } one ? [one] : null,
        Str(q, "name"),
        q["fields"] is JsonArray f ? f.Select(n => n!.ToString()).ToArray() : null,
        q["rect"] is JsonArray r ? r.Select(n => Num(n)).ToArray() : null,
        Str(q, "parent"),
        q["ids"] is JsonArray ids ? ids.Select(n => n!.ToString()).ToArray() : null);

    private JsonNode Create(JsonObject op, HashSet<string> touched)
    {
        var type = Str(op, "type") ?? throw new ArgumentException("create needs \"type\" (see entity-types)");
        var layer = Layer(Str(op, "layer") ?? throw new ArgumentException("create needs \"layer\""));
        if (Config.Find(type) is { } def && !def.AllowedIn(Project.ProjectType, Project.Database))
            throw new InvalidOperationException($"Terry does not allow {type} in a {Project.Database} {Project.ProjectType} project");
        var fields = StrMap(op["fields"]) ?? [];
        if (op["position"] is JsonArray pos)
            fields["ECTransform.position"] = string.Join(' ', pos.Select(n => F(Num(n))));
        var components = op["components"] is JsonArray c ? c.Select(n => n!.ToString()).ToList() : null;
        var doc = Open(layer);
        var id = doc.CreateEntity(type, fields, Str(op, "name"), Str(op, "parent"), components);
        touched.Add(layer.FilePath!);
        _idIndex?.TryAdd(id, layer);
        return new JsonObject { ["op"] = "create", ["created"] = new JsonArray(Describe(layer, doc.Read(id))) };
    }

    /// <summary>{"op":"place_prefab", "key", "layer", "parent"?, "position"?, "rotation"?, "scale"?}: a Prefab instance.</summary>
    private JsonNode PlacePrefab(JsonObject op, HashSet<string> touched)
    {
        var key = Str(op, "key") ?? throw new ArgumentException("place_prefab needs \"key\" (see prefab-list)");
        if (Prefabs.PathOf(key) is null) throw new KeyNotFoundException($"no prefab '{key}' in {Prefabs.Root}");
        var create = new JsonObject { ["op"] = "create", ["type"] = "Prefab", ["layer"] = op["layer"]?.DeepClone() };
        foreach (var k in new[] { "parent", "name", "position" }) if (op[k] is { } v) create[k] = v.DeepClone();
        var fields = op["fields"]?.DeepClone() as JsonObject ?? new JsonObject();
        fields["ECPrefab.key"] = key;
        if (op["rotation"] is JsonArray r) fields["ECTransform.rotation"] = string.Join(' ', r.Select(n => F(Num(n))));
        if (op["scale"] is JsonArray sc) fields["ECTransform.scale"] = string.Join(' ', sc.Select(n => F(Num(n))));
        else if (op["scale"] is JsonValue sv) fields["ECTransform.scale"] = string.Join(' ', Enumerable.Repeat(F(Num(sv)), 3));
        create["fields"] = fields;
        var result = (JsonObject)Create(create, touched);
        result["op"] = "place_prefab";
        return result;
    }

    /// <summary>
    /// {"op":"make_prefab", ids|query, "key", "folder"?, "replace"?: true}: writes the entities as a new prefab project
    /// (&lt;prefab root&gt;/&lt;folder&gt;/&lt;key&gt;.terry + its Default layer), positioned around their x/z centre at the
    /// lowest y; with replace (default) the originals become one Prefab instance at that pivot.
    /// </summary>
    private JsonNode MakePrefab(JsonObject op, HashSet<string> touched)
    {
        var key = Str(op, "key") ?? throw new ArgumentException("make_prefab needs \"key\"");
        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains('.')) throw new ArgumentException($"'{key}' is not a valid prefab name");
        if (Prefabs.PathOf(key) is { } existing) throw new InvalidOperationException($"prefab '{key}' already exists: {existing}");
        var targets = Targets(op);
        if (targets.Count == 0) throw new InvalidOperationException("make_prefab needs entities");

        var entities = new List<(XElement Entity, string Tags)>();
        foreach (var group in targets.GroupBy(t => t.Layer.Id))
            entities.AddRange(Open(group.First().Layer).ExportEntities(group.Select(g => g.Id)));
        var placed = entities.Select(e => e.Entity.Element("ECTransform")).Where(t => t is not null).Select(t => TerryTransform.Read(t).Position).ToList();
        if (placed.Count == 0) throw new InvalidOperationException("none of the entities has a transform");
        double cx = (placed.Min(p => p[0]) + placed.Max(p => p[0])) / 2, cz = (placed.Min(p => p[2]) + placed.Max(p => p[2])) / 2;
        var cy = placed.Min(p => p[1]);
        foreach (var (e, _) in entities)
            if (e.Element("ECTransform") is { } t)
            {
                var tr = TerryTransform.Read(t);
                new TerryTransform([tr.Position[0] - cx, tr.Position[1] - cy, tr.Position[2] - cz], tr.Rotation, tr.Scale).WriteTo(t);
            }

        var terryPath = Prefabs.NewPath(key, Str(op, "folder"));
        var layerId = NewPrefabId(key, 0);
        var projectId = NewPrefabId(key, 1);
        var layerPath = Path.Combine(Path.GetDirectoryName(terryPath)!, $"{key}.{layerId}.layer");
        var layerDoc = LayerDocument.Empty(layerPath, Project.SceneVersion);
        layerDoc.Config = Config;
        layerDoc.Schema = Schema;
        layerDoc.AddEntities(entities);
        var terryText = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project version="20" id="{projectId}">
              <pc type="QTU::ProjectPrefab">
                <data database="{Project.Database}"/>
              </pc>
              <pc type="QTU::Scene">
                <data version="{Project.SceneVersion}">
                  <entity id="{layerId}" name="Default">
                    <ECLayerFile/>
                    <ECLayer/>
                    <ECLayerExport export="true" export_as_separate_file_if_not_meta_tagged="false" buildings_have_linked_destruction="false"/>
                  </entity>
                </data>
              </pc>
              <pc type="QTU::Terrain"/>
            </project>

            """.ReplaceLineEndings("\n");
        _extraFiles[terryPath] = p =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, terryText, new System.Text.UTF8Encoding(false));
        };
        _extraFiles[layerPath] = p =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            layerDoc.Save(p);
        };

        var result = new JsonObject
        {
            ["op"] = "make_prefab", ["key"] = key, ["terry"] = terryPath, ["entities"] = entities.Count,
            ["pivot"] = new JsonArray(cx, cy, cz),
        };
        if (Bool(op, "replace") ?? true)
        {
            var first = targets[0];
            var parent = Open(first.Layer).Read(first.Id).Parents.FirstOrDefault();
            foreach (var group in targets.GroupBy(t => t.Layer.Id))
            {
                var doc = Open(group.First().Layer);
                foreach (var (_, id) in group)
                    if (doc.Find(id) is not null)
                        foreach (var gone in doc.DeleteEntity(id)) _idIndex?.Remove(gone);
                touched.Add(group.First().Layer.FilePath!);
            }
            // The new prefab is not in the library until the batch is written, so create the instance directly.
            var fields = new Dictionary<string, string>
            {
                ["ECPrefab.key"] = key,
                ["ECTransform.position"] = $"{F(cx)} {F(cy)} {F(cz)}",
            };
            var firstDoc = Open(first.Layer);
            var instance = firstDoc.CreateEntity("Prefab", fields, null, parent is not null && firstDoc.Find(parent) is not null ? parent : null);
            _idIndex?.TryAdd(instance, first.Layer);
            result["instance"] = Summary(first.Layer, firstDoc.Read(instance));
        }
        return result;
    }

    private string NewPrefabId(string key, int salt)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in $"{key}/{salt}/{DateTime.UtcNow.Ticks}")
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return "1" + (hash & 0x00FF_FFFF_FFFF_FFFFUL).ToString("x14");
    }

    private JsonNode CreateLayer(JsonObject op, HashSet<string> touched)
    {
        var name = Str(op, "name") ?? throw new ArgumentException("create_layer needs \"name\"");
        if (Str(op, "layer") is { } inLayer)
        {
            var layer = Layer(inLayer);
            var doc = Open(layer);
            var id = doc.CreateLayer(name, Str(op, "tags"), Str(op, "parent"));
            touched.Add(layer.FilePath!);
            _idIndex?.TryAdd(id, layer);
            return new JsonObject { ["op"] = "create_layer", ["created"] = new JsonArray(Describe(layer, doc.Read(id))) };
        }
        var fileLayer = Project.AddFileLayer(name, Bool(op, "export") ?? true);
        _projectDirty = true;
        var empty = LayerDocument.Empty(fileLayer.FilePath, Project.SceneVersion);
        empty.Config = Config;
        empty.Schema = Schema;
        _open[fileLayer.FilePath!] = empty;
        _deletedFiles.Remove(fileLayer.FilePath!);
        touched.Add(fileLayer.FilePath!);
        return new JsonObject { ["op"] = "create_layer", ["layer"] = LayerJson(fileLayer) };
    }

    private JsonNode LayerState(JsonObject op, HashSet<string> touched)
    {
        var id = Str(op, "id") ?? throw new ArgumentException("layer_state needs \"id\"");
        var top = Layers().FirstOrDefault(l => l.Id == id || l.Name == id);
        var entityId = top?.Id ?? id;
        if (top is null) LayerOf(entityId); // nested layers and plain entities are valid targets too
        if (Bool(op, "visible") is { } visible) { User.SetVisible(entityId, visible); _userDirty = true; }
        if (Bool(op, "frozen") is { } frozen) { User.SetFrozen(entityId, frozen); _userDirty = true; }
        if (Bool(op, "active") == true) { User.ActiveLayer = entityId; _userDirty = true; }
        if (Bool(op, "export") is { } export)
        {
            if (top is not null) { Project.SetLayerExport(entityId, export); _projectDirty = true; }
            else
            {
                var layer = LayerOf(entityId);
                Open(layer).SetField(entityId, "ECLayerExport", "export", export ? "true" : "false");
                touched.Add(layer.FilePath!);
            }
        }
        return new JsonObject
        {
            ["op"] = "layer_state", ["id"] = entityId,
            ["visible"] = !User.Invisible.Contains(entityId), ["frozen"] = User.Frozen.Contains(entityId),
        };
    }

    private JsonNode DeleteFileLayer(ProjectLayer layer, HashSet<string> touched)
    {
        Project.RemoveLayer(layer.Id);
        _projectDirty = true;
        if (layer.FilePath is { } path)
        {
            _open.Remove(path);
            _deletedFiles.Add(path);
            touched.Add(path);
        }
        _idIndex = null;
        return new JsonObject { ["op"] = "delete_layer", ["layer"] = LayerJson(layer) };
    }

    // ---------------------------------------------------------------- output

    public JsonObject LayerJson(ProjectLayer l) => new()
    {
        ["id"] = l.Id, ["name"] = l.Name, ["file"] = l.IsFile, ["export"] = l.Export,
        ["visible"] = !User.Invisible.Contains(l.Id), ["frozen"] = User.Frozen.Contains(l.Id),
        ["active"] = User.ActiveLayer == l.Id,
    };

    /// <summary>Full entity: every component with its fields.</summary>
    public static JsonObject Describe(ProjectLayer layer, TerryEntityData e)
    {
        var components = new JsonObject();
        foreach (var c in e.Components)
        {
            var fields = new JsonObject();
            foreach (var (k, v) in c.Fields) fields[k] = v;
            if (c.ChildElements > 0) fields["#children"] = c.ChildElements;
            components[c.Name] = fields;
        }
        var o = new JsonObject { ["id"] = e.Id, ["type"] = e.Type, ["layer"] = layer.Name };
        if (e.Name is not null) o["name"] = e.Name;
        if (e.Parents.Count > 0) o["parents"] = new JsonArray(e.Parents.Select(p => (JsonNode)p).ToArray());
        if (e.Group is not null) o["group"] = e.Group;
        o["components"] = components;
        return o;
    }

    /// <summary>One-line summary: type, name, position and the main asset.</summary>
    public static JsonObject Summary(ProjectLayer layer, TerryEntityData e)
    {
        var o = new JsonObject { ["id"] = e.Id, ["type"] = e.Type, ["layer"] = layer.Name };
        if (e.Name is not null) o["name"] = e.Name;
        if (Position(e) is { } p) o["position"] = new JsonArray(p.Select(x => (JsonNode)(Math.Round(x, 4) + 0.0)).ToArray());
        var asset = e.Component("ECMesh")?["model_path"] ?? e.Component("ECDecal")?["model_path"] ?? e.Component("ECVFX")?["vfx"]
                    ?? e.Component("ECCompositeScene")?["path"] ?? e.Component("ECBuilding")?["key"] ?? e.Component("ECPrefab")?["key"]
                    ?? e.Component("ECVegetation")?["key"] ?? e.Component("ECSoundMarker")?["key"] ?? e.Component("ECLayerExportTags")?["tags"];
        if (!string.IsNullOrEmpty(asset)) o["asset"] = asset;
        if (e.Parents.Count > 0) o["parents"] = new JsonArray(e.Parents.Select(x => (JsonNode)x).ToArray());
        return o;
    }

    // ---------------------------------------------------------------- history

    public List<FileJournal.HistoryEntry> History() => Journal.History();

    private void Commit(HashSet<string> touchedLayers, string label)
    {
        var files = new List<(string, Action<string>)>();
        foreach (var path in touchedLayers)
            files.Add((path, _deletedFiles.Contains(path) ? p => File.Delete(p) : p => _open[p].Save(p)));
        foreach (var (path, save) in _extraFiles) files.Add((path, save));
        if (_projectDirty) files.Add((Project.Path, p => Project.Save(p)));
        if (_userDirty) files.Add((User.Path, p => User.Save(p)));
        Journal.Commit(files, label);
        _projectDirty = _userDirty = false;
        _deletedFiles.Clear();
        if (_extraFiles.Count > 0) _prefabs = null; // new prefab keys
        _extraFiles.Clear();
    }

    public List<FileJournal.HistoryEntry> Undo(int steps = 1, bool force = false)
    {
        var undone = Journal.Undo(steps, force);
        Reset();
        return undone;
    }

    public Dictionary<string, int> Checkpoints() => Journal.Checkpoints();
    public int Checkpoint(string label) => Journal.Checkpoint(label);

    public List<FileJournal.HistoryEntry> Rollback(string label, bool force = false)
    {
        var undone = Journal.Rollback(label, force);
        Reset();
        return undone;
    }

    // ---------------------------------------------------------------- json helpers

    private static string Summary(JsonArray ops) =>
        string.Join(", ", ops.OfType<JsonObject>().GroupBy(o => Str(o, "op")).Select(g => $"{g.Key} x{g.Count()}"));

    private static (string Component, string Field) SplitKey(string key)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0) throw new ArgumentException($"'{key}' must be Component.field (e.g. ECMesh.model_path)");
        return (key[..dot].Trim(), key[(dot + 1)..].Trim());
    }

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;

    /// <summary>A JSON number of any CLR type (ops built in code hold ints as well as doubles).</summary>
    private static double Num(JsonNode? n) =>
        n is null ? 0 : double.Parse(n.ToJsonString().Trim('"'), CultureInfo.InvariantCulture);
    private static bool? Bool(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>Field values as Terry text: booleans lower-case, numbers invariant, arrays space-separated.</summary>
    private static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonArray a => string.Join(' ', a.Select(Text)),
        JsonValue v when v.TryGetValue(out bool b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue(out double d) => F(d),
        _ => n.ToString(),
    };

    private static Dictionary<string, string>? StrMap(JsonNode? n) =>
        n is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => Text(kv.Value)) : null;

    private static string F(double v) => LayerWriter.F(v);
}
