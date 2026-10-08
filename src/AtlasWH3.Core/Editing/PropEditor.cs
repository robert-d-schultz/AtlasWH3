using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Prop editing on the assembly kit's Terry layers (raw_data\terrain\campaigns\&lt;map&gt;\&lt;map&gt;.&lt;id&gt;.layer),
/// one region per layer. Edits are batched op lists; each batch snapshots the layers it touches first, so batches can
/// be undone, or rolled back to a named checkpoint. History lives in output\prop_edits\&lt;map&gt;.
///
/// Coordinates are campaign world units: x west→east, z south→north, y up; rotations in degrees.
/// The compiled global_props.bin only changes once the global_props build step runs again.
/// </summary>
public sealed class PropEditor
{
    public sealed record LayerInfo(string Name, string Id, string Path, bool Exported);
    public sealed record Found(LayerInfo Layer, LayerEntity Entity);

    private readonly ProjectPaths _paths;
    private readonly Dictionary<string, LayerDocument> _open = new(StringComparer.OrdinalIgnoreCase);
    private List<LayerInfo>? _layers;
    private Dictionary<string, LayerInfo>? _idIndex;
    private (Raster<ushort> Height, double WorldW, double WorldH)? _terrain;

    public PropEditor(ProjectPaths paths) => _paths = paths;

    public string TerryPath => Path.Combine(_paths.AkTerrainDir, _paths.MapName + ".terry");
    /// <summary>output\prop_edits\&lt;map&gt; (see <see cref="FileJournal.EditDir"/>).</summary>
    public string HistoryDir => FileJournal.EditDir(_paths, "prop_edits");
    private FileJournal Journal => new(HistoryDir);

    // ---------------------------------------------------------------- layers and queries

    public IReadOnlyList<LayerInfo> Layers()
    {
        if (_layers is not null) return _layers;
        if (!File.Exists(TerryPath)) throw new FileNotFoundException("Assembly kit .terry project not found.", TerryPath);
        var project = TerryProject.Load(TerryPath);
        var exported = Campaign.Props.GlobalPropsStep.ExportedLayers(TerryPath).Select(l => l.Id).ToHashSet();
        return _layers = project.LayerFiles()
            .Select(kv => new LayerInfo(kv.Key, kv.Value, project.LayerFilePath(kv.Value), exported.Contains(kv.Value)))
            .Where(l => File.Exists(l.Path))
            .OrderBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A layer by region name, layer id, or file name.</summary>
    public LayerInfo Layer(string nameOrId) =>
        Layers().FirstOrDefault(l => l.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase) || l.Id == nameOrId
                                     || Path.GetFileName(l.Path).Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"no layer '{nameOrId}' in {Path.GetFileName(TerryPath)} (see props-layers)");

    public LayerDocument Open(LayerInfo layer)
    {
        if (!_open.TryGetValue(layer.Path, out var doc)) _open[layer.Path] = doc = LayerDocument.Load(layer.Path);
        return doc;
    }

    /// <summary>Every entity of every layer (parsed in parallel; ~1-2 s for the full 3K map).</summary>
    public List<(LayerInfo Layer, List<LayerEntity> Entities)> ReadAll(IEnumerable<LayerInfo>? layers = null)
    {
        var list = (layers ?? Layers()).ToList();
        var result = new (LayerInfo, List<LayerEntity>)[list.Count];
        Parallel.For(0, list.Count, i =>
        {
            var doc = _open.GetValueOrDefault(list[i].Path) ?? LayerDocument.Load(list[i].Path);
            result[i] = (list[i], doc.Entities());
        });
        return result.ToList();
    }

    /// <summary>The layer holding entity <paramref name="id"/> (searches every layer the first time).</summary>
    public LayerInfo LayerOf(string id)
    {
        if (_idIndex is null)
        {
            _idIndex = new Dictionary<string, LayerInfo>();
            foreach (var (layer, entities) in ReadAll())
                foreach (var e in entities) _idIndex.TryAdd(e.Id, layer);
        }
        return _idIndex.TryGetValue(id, out var l) ? l : throw new KeyNotFoundException($"no entity {id} in any layer");
    }

    public sealed record Query(string? Layer = null, string[]? Kinds = null, string? Asset = null, string? Tag = null,
                               double[]? Rect = null, double[]? Circle = null);

    public IEnumerable<Found> Find(Query q)
    {
        var layers = q.Layer is null ? Layers() : [Layer(q.Layer)];
        foreach (var (layer, entities) in ReadAll(layers))
            foreach (var e in entities)
                if (Matches(e, q)) yield return new Found(layer, e);
    }

    private static bool Matches(LayerEntity e, Query q)
    {
        if (q.Kinds is { Length: > 0 } ? !q.Kinds.Contains(e.Kind, StringComparer.OrdinalIgnoreCase) : e.Kind == "tag_layer") return false;
        if (q.Asset is not null && !e.Asset.Contains(q.Asset, StringComparison.OrdinalIgnoreCase)) return false;
        if (q.Tag is not null && !e.Tags.Split(',').Contains(q.Tag, StringComparer.OrdinalIgnoreCase)) return false;
        double x = e.Position[0], z = e.Position[2];
        if (q.Rect is [var x0, var z0, var x1, var z1]
            && (x < Math.Min(x0, x1) || x > Math.Max(x0, x1) || z < Math.Min(z0, z1) || z > Math.Max(z0, z1))) return false;
        if (q.Circle is [var cx, var cz, var r] && (x - cx) * (x - cx) + (z - cz) * (z - cz) > r * r) return false;
        return true;
    }

    /// <summary>Distinct assets (model paths, VFX, scenes...) used on the map, most used first.</summary>
    public List<(string Kind, string Asset, int Count, double MeanScale, string ExampleLayer)> Assets(string? filter = null, string[]? kinds = null)
    {
        return ReadAll()
            .SelectMany(l => l.Entities.Select(e => (l.Layer, e)))
            .Where(x => x.e.Kind is not ("tag_layer" or "river" or "other") && x.e.Asset.Length > 0)
            .Where(x => kinds is not { Length: > 0 } || kinds.Contains(x.e.Kind, StringComparer.OrdinalIgnoreCase))
            .Where(x => filter is null || x.e.Asset.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => (x.e.Kind, x.e.Asset))
            .Select(g => (g.Key.Kind, g.Key.Asset, g.Count(), g.Average(x => x.e.Scale[0]),
                          g.GroupBy(x => x.Layer.Name).OrderByDescending(n => n.Count()).First().Key))
            .OrderByDescending(a => a.Item3)
            .ToList();
    }

    /// <summary>Region layer for a world point: the exported layer with the most objects within 4 units, else the one
    /// whose objects come closest.</summary>
    public LayerInfo LayerAt(double x, double z)
    {
        LayerInfo? best = null;
        double bestDist = double.MaxValue;
        var bestNear = 0;
        foreach (var (layer, entities) in ReadAll(Layers().Where(l => l.Exported)))
        {
            var near = 0;
            var closest = double.MaxValue;
            foreach (var e in entities)
            {
                if (e.Kind is "tag_layer" or "river" or "poly" or "other") continue;
                var d = Math.Sqrt((e.Position[0] - x) * (e.Position[0] - x) + (e.Position[2] - z) * (e.Position[2] - z));
                if (d < 4) near++;
                closest = Math.Min(closest, d);
            }
            if (near > bestNear || (bestNear == 0 && near == 0 && closest < bestDist))
            {
                best = layer;
                bestNear = near;
                bestDist = closest;
            }
        }
        return best ?? throw new InvalidOperationException("no exported layers with objects");
    }

    // ---------------------------------------------------------------- terrain

    /// <summary>Ground height (world y) at a world point, from the AK LowFrequencyHeight TIF.</summary>
    public double GroundY(double x, double z)
    {
        var (h, worldW, worldH) = Terrain();
        var col = Math.Clamp(x / worldW * h.Width - 0.5, 0, h.Width - 1.001);
        var row = Math.Clamp((1 - z / worldH) * h.Height - 0.5, 0, h.Height - 1.001);
        int c0 = (int)col, r0 = (int)row;
        double fx = col - c0, fz = row - r0;
        double v = h[c0, r0] * (1 - fx) * (1 - fz) + h[c0 + 1, r0] * fx * (1 - fz)
                 + h[c0, r0 + 1] * (1 - fx) * fz + h[c0 + 1, r0 + 1] * fx * fz;
        return v * CameraHeightmapStep.HeightStep + CameraHeightmapStep.HeightOffset;
    }

    public (Raster<ushort> Height, double WorldW, double WorldH) Terrain()
    {
        if (_terrain is { } t) return t;
        var project = TerryProject.Load(TerryPath);
        var map = project.Find("LowFrequencyHeight") ?? throw new InvalidDataException("no LowFrequencyHeight map in the .terry");
        var raster = TiffMap.ReadGray16(project.LayerTifPath(map));
        _terrain = (raster, raster.Width * CameraHeightmapStep.PixelSizeX, raster.Height * CameraHeightmapStep.PixelSizeZ);
        return _terrain.Value;
    }

    // ---------------------------------------------------------------- edits

    /// <summary>
    /// Applies a batch of ops (JSON array) and saves the touched layers; the whole batch is one undo step.
    /// Each op: {"op": add|move|rotate|scale|set|tags|duplicate|delete, ...}; see <see cref="ApplyOne"/>.
    /// Returns one result object per op. Nothing is written if any op fails.
    /// </summary>
    public JsonArray Apply(JsonArray ops, string? label = null)
    {
        var touched = new Dictionary<string, LayerInfo>(StringComparer.OrdinalIgnoreCase);
        var results = new JsonArray();
        foreach (var node in ops)
        {
            var op = node as JsonObject ?? throw new ArgumentException("each op must be a JSON object");
            try { results.Add(ApplyOne(op, touched)); }
            catch (Exception ex)
            {
                _open.Clear(); // drop the half-edited documents; nothing has been saved
                throw new InvalidOperationException($"op {results.Count} ({op["op"]}): {ex.Message}", ex);
            }
        }
        if (touched.Count > 0) Commit(touched.Values, label ?? Summary(ops));
        return results;
    }

    private JsonNode ApplyOne(JsonObject op, Dictionary<string, LayerInfo> touched)
    {
        var kind = Str(op, "op") ?? throw new ArgumentException("missing \"op\"");
        if (kind == "add") return Add(op, touched);

        var ids = Ids(op);
        if (op["group"] is JsonValue g && g.GetValue<bool>()) ids = WithTwins(ids, Str(op, "layer"));
        var entities = new JsonArray();
        var created = new JsonArray();
        foreach (var id in ids)
        {
            var layer = Str(op, "layer") is { } ln ? Layer(ln) : LayerOf(id);
            var doc = Open(layer);
            touched[layer.Path] = layer;
            var e = doc.Get(id);
            switch (kind)
            {
                case "move":
                    if (Vec(op, "position") is { } p)
                        doc.SetTransform(id, position: p);
                    if (Vec(op, "by") is { } by)
                        doc.MoveBy(id, by[0] ?? 0, by[1] ?? 0, by[2] ?? 0);
                    Snap(op, doc, id, e.Position);
                    break;
                case "rotate":
                    if (Vec(op, "rotation") is { } r) doc.SetTransform(id, rotation: r);
                    if (Vec(op, "by") is { } rb)
                        doc.SetTransform(id, rotation: [e.Rotation[0] + (rb[0] ?? 0), e.Rotation[1] + (rb[1] ?? 0), e.Rotation[2] + (rb[2] ?? 0)]);
                    break;
                case "scale":
                    if (Vec(op, "scale") is { } s) doc.SetTransform(id, scale: s);
                    if (op["factor"] is { } f)
                    {
                        var k = f.GetValue<double>();
                        doc.SetTransform(id, scale: [e.Scale[0] * k, e.Scale[1] * k, e.Scale[2] * k]);
                    }
                    break;
                case "set":
                    if (Str(op, "model") is { } model)
                    {
                        if (e.Kind is not ("prop" or "decal")) throw new InvalidOperationException($"{id} is a {e.Kind}, not a prop");
                        doc.SetAttribute(id, e.Kind == "decal" ? "ECDecal" : "ECMesh", "model_path", model);
                    }
                    if (Str(op, "seasons") is { } seasons) doc.SetAttribute(id, "ECCampaignProperties", "season_mask", seasons);
                    if (op["attributes"] is JsonObject attrs) // {"ECComponent.attribute": "value", ...}
                        foreach (var (key, value) in attrs)
                        {
                            var dot = key.IndexOf('.');
                            if (dot < 0) throw new ArgumentException($"attribute key '{key}' must be Component.attribute");
                            doc.SetAttribute(id, key[..dot], key[(dot + 1)..], value?.ToString() ?? "");
                        }
                    break;
                case "tags":
                    doc.SetTags(id, Str(op, "tags") ?? "");
                    break;
                case "duplicate":
                    var d = Vec(op, "by") ?? [0, 0, 0];
                    var copy = doc.Duplicate(id, d[0] ?? 0, d[1] ?? 0, d[2] ?? 0);
                    Snap(op, doc, copy, e.Position);
                    _idIndex?.TryAdd(copy, layer);
                    created.Add(Describe(layer, doc.Get(copy)));
                    break;
                case "delete":
                    doc.Delete(id);
                    _idIndex?.Remove(id);
                    break;
                default:
                    throw new ArgumentException($"unknown op '{kind}' (add, move, rotate, scale, set, tags, duplicate, delete)");
            }
            if (kind is not ("delete" or "duplicate"))
                entities.Add(Describe(layer, doc.Get(id)));
        }
        return kind switch
        {
            "delete" => new JsonObject { ["op"] = kind, ["deleted"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) },
            "duplicate" => new JsonObject { ["op"] = kind, ["created"] = created },
            _ => new JsonObject { ["op"] = kind, ["entities"] = entities },
        };
    }

    /// <summary>The ids plus every other object at the same spot in the same layer (season variants of one tree are
    /// separate props sharing a position).</summary>
    private List<string> WithTwins(List<string> ids, string? layerName)
    {
        var result = new List<string>();
        foreach (var id in ids)
        {
            var layer = layerName is not null ? Layer(layerName) : LayerOf(id);
            var all = Open(layer).Entities();
            var p = all.Single(e => e.Id == id).Position;
            foreach (var e in all)
                if (e.Kind != "tag_layer" && Math.Abs(e.Position[0] - p[0]) < 1e-3 && Math.Abs(e.Position[1] - p[1]) < 1e-3
                    && Math.Abs(e.Position[2] - p[2]) < 1e-3 && !result.Contains(e.Id))
                    result.Add(e.Id);
        }
        return result;
    }

    private JsonNode Add(JsonObject op, Dictionary<string, LayerInfo> touched)
    {
        var model = Str(op, "model") ?? throw new ArgumentException("add needs \"model\" (a path from props-models)");
        var pos = Vec(op, "position") ?? throw new ArgumentException("add needs \"position\": [x, y, z] (y null = ground)");
        double x = pos[0] ?? throw new ArgumentException("position x missing"), z = pos[2] ?? throw new ArgumentException("position z missing");
        var y = pos[1] ?? GroundY(x, z);
        var rot = Vec(op, "rotation") ?? [0, 0, 0];
        var scale = op["scale"] is JsonValue sv ? [sv.GetValue<double>(), sv.GetValue<double>(), sv.GetValue<double>()] : Vec(op, "scale") ?? [1, 1, 1];
        var layer = Str(op, "layer") is { } ln ? Layer(ln) : LayerAt(x, z);
        var doc = Open(layer);
        touched[layer.Path] = layer;
        bool F(string name, bool def) => op[name] is { } n ? n.GetValue<bool>() : def;
        var record = new PropRecord(model,
            new PropTransform((float)x, (float)y, (float)z, rot[0] ?? 0, rot[1] ?? 0, rot[2] ?? 0, scale[0] ?? 1, scale[1] ?? 1, scale[2] ?? 1),
            Str(op, "tags") ?? "", Str(op, "seasons") ?? "", F("decal", false), F("apply_to_terrain", true), F("apply_to_objects", false),
            F("has_height_patch", false), F("apply_height_patch", false), F("visible_inside_snow", true), F("visible_outside_snow", true),
            F("visible_inside_destruction", true), F("visible_outside_destruction", true), F("visible_in_unseen_shroud", false),
            F("visible_in_seen_shroud", true));
        var id = doc.AddProp(record);
        _idIndex?.TryAdd(id, layer);
        return new JsonObject { ["op"] = "add", ["created"] = new JsonArray(Describe(layer, doc.Get(id))) };
    }

    /// <summary>"snap": "terrain" (or true) puts the object on the lf terrain; "relative" keeps its height above the
    /// terrain from where it was (for objects standing on mountain props, whose relief is not in the terrain).</summary>
    private void Snap(JsonObject op, LayerDocument doc, string id, double[] from)
    {
        var mode = op["snap"] switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out bool b) => b ? "terrain" : null,
            var v => v.ToString(),
        };
        if (mode is null) return;
        var p = doc.Get(id).Position;
        var y = mode switch
        {
            "terrain" => GroundY(p[0], p[2]),
            "relative" => GroundY(p[0], p[2]) + from[1] - GroundY(from[0], from[2]),
            _ => throw new ArgumentException($"snap must be \"terrain\" or \"relative\", not '{mode}'"),
        };
        doc.SetTransform(id, position: [null, y, null]);
    }

    public static JsonObject Describe(LayerInfo layer, LayerEntity e) => new()
    {
        ["id"] = e.Id, ["layer"] = layer.Name, ["kind"] = e.Kind, ["asset"] = e.Asset,
        ["position"] = Arr(e.Position), ["rotation"] = Arr(e.Rotation), ["scale"] = Arr(e.Scale),
        ["tags"] = e.Tags, ["seasons"] = e.Seasons,
    };

    private static JsonArray Arr(double[] v) => new(v.Select(x => (JsonNode)(Math.Round(x, 5) + 0.0)).ToArray()); // + 0.0: no "-0"

    // ---------------------------------------------------------------- history

    public List<FileJournal.HistoryEntry> History() => Journal.History();

    private void Commit(IEnumerable<LayerInfo> layers, string label) =>
        Journal.Commit(layers.Select(l => (l.Path, (Action<string>)(path => _open[path].Save(path)))), label);

    /// <summary>Restores the layers of the last <paramref name="steps"/> batches. Refuses when a layer changed since
    /// (another tool or a hand edit) unless forced.</summary>
    public List<FileJournal.HistoryEntry> Undo(int steps = 1, bool force = false)
    {
        var undone = Journal.Undo(steps, force);
        _open.Clear();
        _idIndex = null;
        return undone;
    }

    public Dictionary<string, int> Checkpoints() => Journal.Checkpoints();

    public int Checkpoint(string label) => Journal.Checkpoint(label);

    public List<FileJournal.HistoryEntry> Rollback(string label, bool force = false)
    {
        var undone = Journal.Rollback(label, force);
        _open.Clear();
        _idIndex = null;
        return undone;
    }

    // ---------------------------------------------------------------- json helpers

    private static string Summary(JsonArray ops) =>
        string.Join(", ", ops.OfType<JsonObject>().GroupBy(o => Str(o, "op")).Select(g => $"{g.Key} x{g.Count()}"));

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;

    private static List<string> Ids(JsonObject o) =>
        o["ids"] is JsonArray a ? a.Select(n => n!.ToString()).ToList()
        : o["id"] is JsonValue v ? [v.ToString()]
        : throw new ArgumentException("needs \"ids\": [...] or \"id\"");

    /// <summary>[x, y, z] with nulls for "keep"; a 2-element array is [x, z].</summary>
    private static double?[]? Vec(JsonObject o, string name)
    {
        if (o[name] is not JsonArray a) return null;
        var v = a.Select(n => n is null ? (double?)null : n.GetValue<double>()).ToArray();
        return v.Length switch
        {
            2 => [v[0], null, v[1]],
            3 => v,
            _ => throw new ArgumentException($"\"{name}\" must be [x, y, z] or [x, z]"),
        };
    }
}
