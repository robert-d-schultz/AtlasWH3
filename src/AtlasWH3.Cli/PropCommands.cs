using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Rendering;

/// <summary>
/// props-* commands: query and edit the props in the assembly kit's Terry layers (see <see cref="PropEditor"/>).
/// Every command prints JSON (the terry MCP server wraps them); errors print {"error": ...} and exit 1.
/// </summary>
static class PropCommands
{
    public static readonly string[] Names =
    [
        "props-layers", "props-list", "props-get", "props-models", "props-ground", "props-edit",
        "props-undo", "props-checkpoint", "props-rollback", "props-history", "props-preview",
    ];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        var args = new Args(a.Where(s => !s.Equals("--json", StringComparison.OrdinalIgnoreCase)));
        var editor = new PropEditor(paths);
        try
        {
            JsonNode result = command switch
            {
                "props-layers" => Layers(editor),
                "props-list" => List(editor, args),
                "props-get" => Get(editor, args),
                "props-models" => Models(editor, args),
                "props-ground" => Ground(editor, args),
                "props-edit" => Edit(editor, args),
                "props-undo" => Undone(editor.Undo(int.Parse(args.Option("--steps") ?? "1"), args.Flag("--force"))),
                "props-checkpoint" => new JsonObject { ["checkpoint"] = args.Positional(0, "label"), ["seq"] = editor.Checkpoint(args.Positional(0, "label")) },
                "props-rollback" => Undone(editor.Rollback(args.Positional(0, "label"), args.Flag("--force"))),
                "props-history" => History(editor),
                "props-preview" => Preview(editor, paths, args),
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

    private static JsonNode Layers(PropEditor editor)
    {
        var rows = new JsonArray();
        foreach (var (layer, entities) in editor.ReadAll())
        {
            var objects = entities.Where(e => e.Kind is not ("tag_layer" or "river" or "poly")).ToList();
            var row = new JsonObject
            {
                ["name"] = layer.Name, ["id"] = layer.Id, ["exported"] = layer.Exported,
                ["counts"] = new JsonObject(entities.GroupBy(e => e.Kind).OrderBy(g => g.Key)
                    .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            };
            if (objects.Count > 0)
                row["bounds"] = new JsonArray(R(objects.Min(e => e.Position[0])), R(objects.Min(e => e.Position[2])),
                                              R(objects.Max(e => e.Position[0])), R(objects.Max(e => e.Position[2])));
            rows.Add(row);
        }
        return new JsonObject { ["terry"] = editor.TerryPath, ["layers"] = rows, ["note"] = "bounds = [x0, z0, x1, z1] of the layer's objects" };
    }

    private static JsonNode List(PropEditor editor, Args a)
    {
        var limit = int.Parse(a.Option("--limit") ?? "200");
        var found = editor.Find(Query(a)).ToList();
        var center = a.Doubles("--near");
        if (center is not null)
            found = found.OrderBy(f => Math.Pow(f.Entity.Position[0] - center[0], 2) + Math.Pow(f.Entity.Position[2] - center[1], 2)).ToList();
        return new JsonObject
        {
            ["total"] = found.Count, ["returned"] = Math.Min(limit, found.Count),
            ["objects"] = new JsonArray(found.Take(limit).Select(f => (JsonNode)PropEditor.Describe(f.Layer, f.Entity)).ToArray()),
        };
    }

    private static PropEditor.Query Query(Args a) => new(
        Layer: a.Option("--layer"),
        Kinds: a.Option("--kind")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Asset: a.Option("--asset"),
        Tag: a.Option("--tag"),
        Rect: a.Doubles("--rect"),
        Circle: a.Doubles("--near"));

    private static JsonNode Get(PropEditor editor, Args a)
    {
        var id = a.Positional(0, "entity id");
        var layer = a.Option("--layer") is { } l ? editor.Layer(l) : editor.LayerOf(id);
        var doc = editor.Open(layer);
        var e = doc.Get(id);
        var o = PropEditor.Describe(layer, e);
        o["layer_file"] = layer.Path;
        o["ground_y"] = R(editor.GroundY(e.Position[0], e.Position[2]));
        o["xml"] = doc.EntityXml(id);
        return o;
    }

    private static JsonNode Models(PropEditor editor, Args a)
    {
        var limit = int.Parse(a.Option("--limit") ?? "100");
        var assets = editor.Assets(a.Option("--filter"), a.Option("--kind")?.Split(',', StringSplitOptions.RemoveEmptyEntries));
        return new JsonObject
        {
            ["total"] = assets.Count,
            ["assets"] = new JsonArray(assets.Take(limit).Select(m => (JsonNode)new JsonObject
            {
                ["kind"] = m.Kind, ["asset"] = m.Asset, ["count"] = m.Count, ["mean_scale"] = R(m.MeanScale), ["example_layer"] = m.ExampleLayer,
            }).ToArray()),
        };
    }

    private static JsonNode Ground(PropEditor editor, Args a)
    {
        var p = a.Doubles("--at") ?? throw new ArgumentException("--at x,z[,x,z...]");
        if (p.Length < 2 || p.Length % 2 != 0) throw new ArgumentException("--at takes x,z pairs");
        if (p.Length == 2)
            return new JsonObject { ["x"] = p[0], ["z"] = p[1], ["ground_y"] = R(editor.GroundY(p[0], p[1])), ["layer"] = editor.LayerAt(p[0], p[1]).Name };
        return new JsonArray(Enumerable.Range(0, p.Length / 2)
            .Select(i => (JsonNode)new JsonObject { ["x"] = p[2 * i], ["z"] = p[2 * i + 1], ["ground_y"] = R(editor.GroundY(p[2 * i], p[2 * i + 1])) }).ToArray());
    }

    private static JsonNode Edit(PropEditor editor, Args a)
    {
        var source = a.Option("--ops") ?? throw new ArgumentException("--ops <file | - | inline JSON array>");
        var text = source == "-" ? Console.In.ReadToEnd() : source.TrimStart().StartsWith('[') || source.TrimStart().StartsWith('{') ? source : File.ReadAllText(source);
        var node = JsonNode.Parse(text);
        var ops = node as JsonArray ?? new JsonArray(node!.DeepClone());
        var results = editor.Apply(ops, a.Option("--label"));
        var history = editor.History();
        return new JsonObject
        {
            ["seq"] = history.Count == 0 ? 0 : history[^1].Seq,
            ["results"] = results,
            ["note"] = "AK layers saved; run the global_props build step for global_props.bin",
        };
    }

    private static JsonNode Undone(List<FileJournal.HistoryEntry> undone) => new JsonObject
    {
        ["undone"] = new JsonArray(undone.Select(h => (JsonNode)new JsonObject
        {
            ["seq"] = h.Seq, ["label"] = h.Label, ["files"] = new JsonArray(h.Files.Select(f => (JsonNode)Path.GetFileName(f.Path)).ToArray()),
        }).ToArray()),
    };

    private static JsonNode History(PropEditor editor)
    {
        var checkpoints = editor.Checkpoints();
        return new JsonObject
        {
            ["edits"] = new JsonArray(editor.History().Select(h => (JsonNode)new JsonObject
            {
                ["seq"] = h.Seq, ["time"] = h.Time.ToString("s", CultureInfo.InvariantCulture), ["label"] = h.Label,
                ["files"] = new JsonArray(h.Files.Select(f => (JsonNode)Path.GetFileName(f.Path)).ToArray()),
            }).ToArray()),
            ["checkpoints"] = new JsonObject(checkpoints.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
        };
    }

    private static JsonNode Preview(PropEditor editor, ProjectPaths paths, Args a)
    {
        double x0, z0, x1, z1;
        if (a.Doubles("--rect") is [var ax, var az, var bx, var bz]) (x0, z0, x1, z1) = (ax, az, bx, bz);
        else if (a.Doubles("--center") is [var cx, var cz])
        {
            var half = double.Parse(a.Option("--size") ?? "20", CultureInfo.InvariantCulture) / 2;
            (x0, z0, x1, z1) = (cx - half, cz - half, cx + half, cz + half);
        }
        else throw new ArgumentException("--rect x0,z0,x1,z1 or --center x,z [--size s]");
        var highlight = a.Option("--highlight")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var outPath = a.Option("--out") ?? Path.Combine(paths.OutputRoot, "previews", "props",
            FormattableString.Invariant($"{paths.MapName}_{x0:0.#}_{z0:0.#}_{x1:0.#}_{z1:0.#}_{DateTime.Now:HHmmss}.png"));
        PropPreview.Render(editor, x0, z0, x1, z1, outPath, int.Parse(a.Option("--width") ?? "1024"), highlight, a.Option("--layer"));
        return new JsonObject { ["png"] = outPath, ["rect"] = new JsonArray(R(x0), R(z0), R(x1), R(z1)) };
    }

    private static JsonNode R(double v) => Math.Round(v, 4);

    /// <summary>"--name value" options, "--flag" switches and positional arguments.</summary>
    private sealed class Args(IEnumerable<string> raw)
    {
        private static readonly string[] Flags = ["--force"];
        private readonly List<string> _a = raw.ToList();

        public string? Option(string name)
        {
            var i = _a.FindIndex(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            return i + 1 < _a.Count ? _a[i + 1] : throw new ArgumentException($"{name} needs a value");
        }

        public bool Flag(string name) => _a.Contains(name, StringComparer.OrdinalIgnoreCase);

        public double[]? Doubles(string name) =>
            Option(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();

        public string Positional(int index, string what)
        {
            var positional = new List<string>();
            for (var i = 0; i < _a.Count; i++)
            {
                if (!_a[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(_a[i]); continue; }
                if (!Flags.Contains(_a[i], StringComparer.OrdinalIgnoreCase)) i++; // skip the option's value
            }
            return index < positional.Count ? positional[index] : throw new ArgumentException($"missing {what}");
        }
    }
}
