using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;

/// <summary>
/// Terry schema and generic entity commands (see <see cref="EntityEditor"/>). Every entity-* command prints JSON (the
/// terry MCP server wraps them); errors print {"error": ...} and exit 1. --project &lt;.terry&gt; selects any Terry
/// project (default: the campaign map's).
/// </summary>
static class TerryCommands
{
    public static readonly HashSet<string> Names =
    [
        "terry-schema", "entity-types", "entity-type", "entity-project", "entity-layers", "entity-query", "entity-get",
        "entity-edit", "entity-undo", "entity-checkpoint", "entity-rollback", "entity-history", "prefab-list", "prefab-get",
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly string[] Flags = ["--force", "--full", "--all", "--json"];

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        if (command == "terry-schema") return Schema(paths, a);
        try
        {
            var project = Option(a, "--project");
            var editor = new EntityEditor(paths, project);
            JsonNode result = command switch
            {
                "entity-types" => Types(editor),
                "entity-type" => TypeInfo(editor, Positional(a, 0, "entity type")),
                "entity-project" => ProjectInfo(editor),
                "entity-layers" => Layers(editor, Flag(a, "--all")),
                "entity-query" => Query(editor, a),
                "entity-get" => Get(editor, a),
                "entity-edit" => Edit(editor, a),
                "entity-undo" => Undone(editor.Undo(int.Parse(Option(a, "--steps") ?? "1", CultureInfo.InvariantCulture), Flag(a, "--force"))),
                "entity-checkpoint" => new JsonObject { ["checkpoint"] = Positional(a, 0, "label"), ["seq"] = editor.Checkpoint(Positional(a, 0, "label")) },
                "entity-rollback" => Undone(editor.Rollback(Positional(a, 0, "label"), Flag(a, "--force"))),
                "entity-history" => History(editor),
                "prefab-list" => PrefabList(editor, Option(a, "--filter"), int.Parse(Option(a, "--limit") ?? "300", CultureInfo.InvariantCulture)),
                "prefab-get" => PrefabGet(editor, Positional(a, 0, "prefab key")),
                _ => throw new ArgumentException($"unknown command {command}"),
            };
            Console.WriteLine(result.ToJsonString(Indented));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(new JsonObject { ["error"] = ex.Message, ["type"] = ex.GetType().Name }.ToJsonString(Indented));
            return 1;
        }
    }

    // ---------------------------------------------------------------- schema

    private static JsonNode Types(EntityEditor editor)
    {
        var project = File.Exists(editor.TerryPath) ? editor.Project : null;
        var rows = new JsonArray();
        foreach (var t in editor.Config.Types.OrderBy(t => t.Type, StringComparer.Ordinal))
        {
            var template = editor.Schema.EntityTemplates.GetValueOrDefault(t.Type);
            var row = new JsonObject
            {
                ["type"] = t.Type,
                ["components"] = new JsonArray((template?.Components ?? t.Components.Select(c => c.Type).ToList()).Select(c => (JsonNode)c).ToArray()),
                ["seen_in_kit"] = template?.Count ?? 0,
            };
            if (project is not null) row["allowed_here"] = t.AllowedIn(project.ProjectType, project.Database);
            if (t.AllowIfChildOf.Count > 0) row["child_of"] = string.Join(",", t.AllowIfChildOf);
            rows.Add(row);
        }
        return new JsonObject
        {
            ["project"] = project is null ? null : $"{project.Database} {project.ProjectType}",
            ["types"] = rows,
            ["builtin"] = new JsonArray("LayerFile", "Layer", "TagLayer", "Group", "Signature"),
        };
    }

    /// <summary>One entity type with every component's fields (type, default, known values).</summary>
    private static JsonNode TypeInfo(EntityEditor editor, string type)
    {
        var def = editor.Config.Find(type);
        var template = editor.Schema.EntityTemplates.GetValueOrDefault(type);
        if (def is null && template is null) throw new KeyNotFoundException($"unknown entity type '{type}' (see entity-types)");
        var components = new JsonArray();
        var names = (template?.Components ?? []).Concat(def?.Components.Select(c => c.Type) ?? []).Distinct();
        foreach (var name in names)
        {
            var slot = def?.Components.FirstOrDefault(c => c.Type == name);
            var schema = editor.Schema.Find(name);
            var c = new JsonObject { ["component"] = name, ["in_default_layout"] = template?.Components.Contains(name) ?? false };
            if (slot is { Conditional: true }) c["when"] = $"{slot.Parameter}={slot.Value}";
            if (slot is { ReadOnly: true }) c["readonly"] = true;
            c["fields"] = new JsonArray((schema?.Fields ?? []).Select(f =>
            {
                var o = new JsonObject { ["name"] = f.Name, ["type"] = f.Type.ToString().ToLowerInvariant() };
                if (f.Default is not null) o["default"] = f.Default;
                if (f.Values is { Count: <= 40 } v && f.Type is FieldType.Enum or FieldType.Bool or FieldType.Int)
                    o["values"] = new JsonArray(v.Select(x => (JsonNode)x).ToArray());
                return (JsonNode)o;
            }).ToArray());
            if (schema is { Count: 0 }) c["note"] = "never seen in the kit: fields unknown";
            if (schema?.Children.Count > 0) c["children"] = string.Join(",", schema.Children.Keys);
            components.Add(c);
        }
        var result = new JsonObject { ["type"] = type, ["seen_in_kit"] = template?.Count ?? 0, ["components"] = components };
        if (def?.DefaultName is { } dn) result["default_name"] = dn;
        if (def is not null)
            result["allowed_in"] = new JsonArray(def.AllowIf.Select(r => (JsonNode)$"{r.TileDatabase ?? "any"}: {(r.ProjectTypes.Count == 0 ? "any" : string.Join(",", r.ProjectTypes))}").ToArray());
        return result;
    }

    // ---------------------------------------------------------------- project and layers

    private static JsonNode ProjectInfo(EntityEditor editor)
    {
        var p = editor.Project;
        return new JsonObject
        {
            ["terry"] = p.Path, ["project_type"] = p.ProjectType, ["database"] = p.Database, ["scene_version"] = p.SceneVersion,
            ["layers"] = p.Layers().Count, ["active_layer"] = editor.User.ActiveLayer,
            ["terrain_maps"] = new JsonArray(p.Maps.Select(m => (JsonNode)new JsonObject
            {
                ["type"] = m.Type, ["size"] = $"{m.Size.Width}x{m.Size.Height}", ["tif"] = Path.GetFileName(p.LayerTifPath(m)),
            }).ToArray()),
            ["history"] = editor.HistoryDir,
        };
    }

    /// <summary>The layer tree: file layers with entity counts by type, and (with --all) their nested layers.</summary>
    private static JsonNode Layers(EntityEditor editor, bool nested)
    {
        var all = editor.ReadAll().ToDictionary(x => x.Layer.Id, x => x.Entities);
        var invisible = editor.User.Invisible;
        var frozen = editor.User.Frozen;
        var rows = new JsonArray();
        foreach (var l in editor.Layers())
        {
            var row = editor.LayerJson(l);
            if (all.TryGetValue(l.Id, out var entities))
            {
                row["counts"] = new JsonObject(entities.Where(e => !TerryEntityTypes.IsLayerType(e.Type)).GroupBy(e => e.Type)
                    .OrderByDescending(g => g.Count()).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));
                if (nested)
                    row["layers"] = new JsonArray(entities.Where(e => TerryEntityTypes.IsLayerType(e.Type)).Select(e => (JsonNode)new JsonObject
                    {
                        ["id"] = e.Id, ["name"] = e.Name, ["type"] = e.Type,
                        ["parents"] = new JsonArray(e.Parents.Select(x => (JsonNode)x).ToArray()),
                        ["members"] = entities.Count(m => m.Parents.Contains(e.Id)),
                        ["visible"] = !invisible.Contains(e.Id), ["frozen"] = frozen.Contains(e.Id),
                    }).ToArray());
            }
            else row["missing_file"] = l.FilePath is not null;
            rows.Add(row);
        }
        return new JsonObject { ["terry"] = editor.TerryPath, ["layers"] = rows };
    }

    // ---------------------------------------------------------------- entities

    private static EntityEditor.Query ParseQuery(string[] a)
    {
        var q = Option(a, "--query") is { } json ? EntityEditor.ParseQuery(JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("--query must be a JSON object"))
            : new EntityEditor.Query();
        return q with
        {
            Layer = Option(a, "--layer") ?? q.Layer,
            Types = Option(a, "--type")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? q.Types,
            Name = Option(a, "--name") ?? q.Name,
            Fields = Options(a, "--where") is { Count: > 0 } w ? [.. w] : q.Fields,
            Rect = Option(a, "--rect")?.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray() ?? q.Rect,
            Parent = Option(a, "--parent") ?? q.Parent,
        };
    }

    private static JsonNode Query(EntityEditor editor, string[] a)
    {
        var limit = int.Parse(Option(a, "--limit") ?? "200", CultureInfo.InvariantCulture);
        var full = Flag(a, "--full");
        var found = editor.Find(ParseQuery(a)).ToList();
        return new JsonObject
        {
            ["count"] = found.Count,
            ["by_type"] = new JsonObject(found.GroupBy(f => f.Entity.Type).OrderByDescending(g => g.Count())
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["entities"] = new JsonArray(found.Take(limit).Select(f => (JsonNode)(full ? EntityEditor.Describe(f.Layer, f.Entity)
                : EntityEditor.Summary(f.Layer, f.Entity))).ToArray()),
            ["truncated"] = found.Count > limit,
        };
    }

    private static JsonNode Get(EntityEditor editor, string[] a)
    {
        var ids = Positionals(a);
        if (ids.Count == 0) throw new ArgumentException("entity-get <id> [id...]");
        return new JsonArray(ids.Select(id => (JsonNode)EntityEditor.Describe(editor.Get(id).Layer, editor.Get(id).Entity)).ToArray());
    }

    private static JsonNode Edit(EntityEditor editor, string[] a)
    {
        var source = Option(a, "--ops") ?? throw new ArgumentException("--ops <file | - | inline JSON array>");
        var text = source == "-" ? Console.In.ReadToEnd()
            : source.TrimStart().StartsWith('[') || source.TrimStart().StartsWith('{') ? source : File.ReadAllText(source);
        var node = JsonNode.Parse(text);
        var ops = node as JsonArray ?? new JsonArray(node!.DeepClone());
        var results = editor.Apply(ops, Option(a, "--label"));
        var history = editor.History();
        return new JsonObject
        {
            ["seq"] = history.Count == 0 ? 0 : history[^1].Seq,
            ["results"] = results,
        };
    }

    private static JsonNode Undone(List<FileJournal.HistoryEntry> undone) => new JsonObject
    {
        ["undone"] = new JsonArray(undone.Select(h => (JsonNode)new JsonObject
        {
            ["seq"] = h.Seq, ["label"] = h.Label, ["files"] = new JsonArray(h.Files.Select(f => (JsonNode)Path.GetFileName(f.Path)).ToArray()),
        }).ToArray()),
    };

    private static JsonNode History(EntityEditor editor) => new JsonObject
    {
        ["dir"] = editor.HistoryDir,
        ["edits"] = new JsonArray(editor.History().Select(h => (JsonNode)new JsonObject
        {
            ["seq"] = h.Seq, ["time"] = h.Time.ToString("s", CultureInfo.InvariantCulture), ["label"] = h.Label,
            ["files"] = new JsonArray(h.Files.Select(f => (JsonNode)Path.GetFileName(f.Path)).ToArray()),
        }).ToArray()),
        ["checkpoints"] = new JsonObject(editor.Checkpoints().Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
    };

    // ---------------------------------------------------------------- prefabs

    private static JsonNode PrefabList(EntityEditor editor, string? filter, int limit)
    {
        var lib = editor.Prefabs;
        var keys = lib.Keys.Where(k => filter is null || k.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        return new JsonObject
        {
            ["root"] = lib.Root, ["database"] = lib.Database, ["count"] = keys.Count,
            ["prefabs"] = new JsonArray(keys.Take(limit).Select(k => (JsonNode)new JsonObject
            {
                ["key"] = k, ["path"] = Path.GetRelativePath(lib.Root, lib.PathOf(k)!),
            }).ToArray()),
            ["truncated"] = keys.Count > limit,
            ["duplicates"] = new JsonArray(lib.Duplicates.Select(d => (JsonNode)d.Key).ToArray()),
            ["note"] = Directory.Exists(lib.Root) ? null : $"{lib.Root} does not exist: no {lib.Database} prefabs yet (make_prefab creates it)",
        };
    }

    private static JsonNode PrefabGet(EntityEditor editor, string key)
    {
        var def = editor.Prefabs.Load(key) ?? throw new KeyNotFoundException($"no prefab '{key}' in {editor.Prefabs.Root}");
        var types = def.Entities.GroupBy(e => TerryEntityTypes.Classify(e.Entity, editor.Config)).OrderByDescending(g => g.Count());
        var nested = def.Entities.Select(e => PrefabExpander.KeyOf(e.Entity)).Where(k => k is not null).Distinct().ToList();
        var missing = new List<string>();
        var flat = def.Entities.Sum(e => PrefabExpander.KeyOf(e.Entity) is null ? 1
            : PrefabExpander.Expand(e.Entity, editor.Prefabs, true, missing).Count);
        return new JsonObject
        {
            ["key"] = key, ["terry"] = def.Path, ["database"] = def.Database, ["entities"] = def.Entities.Count,
            ["entities_expanded"] = flat,
            ["by_type"] = new JsonObject(types.Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["bounds"] = def.Bounds is var (x0, z0, x1, z1) ? new JsonArray(x0, z0, x1, z1) : null,
            ["nested_prefabs"] = new JsonArray(nested.Select(n => (JsonNode)n!).ToArray()),
            ["missing_nested"] = missing.Count > 0 ? new JsonArray(missing.Distinct().Select(m => (JsonNode)m).ToArray()) : null,
            ["assets"] = new JsonArray(def.Entities.Select(e => (string?)e.Entity.Element("ECMesh")?.Attribute("model_path")
                    ?? (string?)e.Entity.Element("ECBuilding")?.Attribute("key") ?? (string?)e.Entity.Element("ECDecal")?.Attribute("model_path"))
                .Where(x => !string.IsNullOrEmpty(x)).GroupBy(x => x).OrderByDescending(g => g.Count()).Take(30)
                .Select(g => (JsonNode)$"{g.Key} x{g.Count()}").ToArray()),
        };
    }

    // ---------------------------------------------------------------- terry-schema

    // terry-schema [--out <json>] [--root <dir>]...: scans every .layer/.terry under the roots (default: the kit's
    // raw_data, which holds the campaign/battle projects and the prefab .terry files) and writes the component schema.
    private static int Schema(ProjectPaths paths, string[] a)
    {
        var roots = Options(a, "--root");
        if (roots.Count == 0) roots.Add(Path.Combine(paths.AssemblyKitRoot, "raw_data"));
        var outPath = Option(a, "--out")
                      ?? Path.GetFullPath(Path.Combine("src", "AtlasWH3.Formats", "Terry", "Data", "component_schema.json"));   // run from the repo root

        var config = EntityConfiguration.LoadFromKit(paths.AssemblyKitRoot);
        var files = roots.SelectMany(r => Directory.EnumerateFiles(r, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".layer", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".terry", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var schema = ComponentSchema.Scan(files, config, Console.Error.WriteLine);
        schema.Save(outPath);

        var seen = schema.Components.Values.Where(c => c.Count > 0).ToList();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            output = outPath,
            files = schema.FilesScanned,
            entities = schema.EntitiesScanned,
            entity_types = config.Types.Count,
            components_configured = config.ComponentTypes.Count,
            components_seen = seen.Count,
            components_seen_not_configured = seen.Where(c => !c.InConfiguration).Select(c => c.Name),
            components_configured_never_seen = schema.Components.Values.Count(c => c.Count == 0),
            fields = seen.Sum(c => c.Fields.Count),
            unclassified_signatures = schema.UnclassifiedSignatures.Take(15),
        }, Indented));
        return 0;
    }

    // ---------------------------------------------------------------- args

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        return i + 1 < a.Length ? a[i + 1] : throw new ArgumentException($"{name} needs a value");
    }

    /// <summary>Every value of a repeatable option.</summary>
    private static List<string> Options(string[] a, string name)
    {
        var values = new List<string>();
        for (var i = 0; i + 1 < a.Length; i++)
            if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) values.Add(a[++i]);
        return values;
    }

    private static bool Flag(string[] a, string name) => a.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static List<string> Positionals(string[] a)
    {
        var positional = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(a[i]); continue; }
            if (!Flags.Contains(a[i], StringComparer.OrdinalIgnoreCase)) i++; // skip the option's value
        }
        return positional;
    }

    private static string Positional(string[] a, int index, string what) =>
        Positionals(a) is var p && index < p.Count ? p[index] : throw new ArgumentException($"missing {what}");
}
