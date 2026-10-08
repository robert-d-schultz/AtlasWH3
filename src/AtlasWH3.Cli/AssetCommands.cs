using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Assets;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Models;

/// <summary>
/// asset-* commands: game models and textures through <see cref="ModelLibrary"/> (vanilla packs, plus
/// --mod-pack &lt;pack&gt; and --asset-root &lt;loose folder&gt; overrides, both repeatable). JSON out.
/// </summary>
static class AssetCommands
{
    public static readonly HashSet<string> Names = ["asset-info", "asset-find", "asset-preview", "asset-census", "asset-check"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            var library = ModelLibrary.ForGame(paths, Options(a, "--asset-root"), Options(a, "--mod-pack"));
            JsonNode result = command switch
            {
                "asset-info" => Info(library, Positional(a, 0, "asset path"), int.Parse(Option(a, "--lod") ?? "0", CultureInfo.InvariantCulture)),
                "asset-find" => Find(library, Option(a, "--filter") ?? Positional(a, 0, "filter"), Option(a, "--ext"), int.Parse(Option(a, "--limit") ?? "100", CultureInfo.InvariantCulture)),
                "asset-preview" => Preview(library, Positional(a, 0, "model path"), a),
                "asset-census" => Census(library, Option(a, "--ext") ?? ".rigid_model_v2", Option(a, "--limit")),
                "asset-check" => Check(library, new EntityEditor(paths, Option(a, "--project"))),
                _ => throw new ArgumentException(command),
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

    /// <summary>What a model / material / texture path resolves to.</summary>
    private static JsonNode Info(ModelLibrary lib, string path, int lod)
    {
        var where = lib.Source.Locate(path);
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = lib.Source.Read(path);
            var h = DdsHeader.Read(bytes);
            return new JsonObject
            {
                ["path"] = path, ["source"] = where, ["width"] = h.Width, ["height"] = h.Height, ["mips"] = h.MipCount,
                ["format"] = h.IsDx10 ? $"DXGI {h.DxgiFormat}" : h.FourCC.Length > 0 ? h.FourCC : $"{h.RgbBitCount} bpp", ["bytes"] = bytes.Length,
            };
        }
        if (path.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
        {
            var m = lib.Material(path) ?? throw new FileNotFoundException($"material {path} not found or unreadable");
            return new JsonObject
            {
                ["path"] = path, ["source"] = where, ["name"] = m.Name, ["shader"] = m.Shader, ["alpha_test"] = m.AlphaTest,
                ["textures"] = new JsonObject(m.Textures.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)$"{kv.Value}{(lib.Source.Exists(kv.Value) ? "" : "  (MISSING)")}"))),
                ["params"] = new JsonObject(m.Params.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.Value))),
            };
        }
        var (model, error) = lib.TryLoad(path);
        if (model is null) throw new InvalidDataException(error ?? "unreadable");
        return new JsonObject
        {
            ["path"] = path, ["source"] = where, ["geometry"] = model.Geometry, ["geometry_source"] = lib.Source.Locate(model.Geometry),
            ["bounds"] = new JsonArray(model.Bounds.Select(v => (JsonNode)Math.Round(v, 4)).ToArray()),
            ["lods"] = new JsonArray(model.Lods.Select((l, i) => (JsonNode)new JsonObject
            {
                ["lod"] = i, ["distance"] = model.LodDistances.ElementAtOrDefault(i), ["meshes"] = l.Count,
                ["vertices"] = model.Vertices(i), ["triangles"] = model.Triangles(i),
            }).ToArray()),
            ["meshes"] = new JsonArray(model.Lods.ElementAtOrDefault(lod)?.Select((m, i) => (JsonNode)new JsonObject
            {
                ["part"] = i, ["vertices"] = m.Positions.Length / 3, ["triangles"] = m.Indices.Length / 3, ["material"] = m.Material,
                ["shader"] = m.Shader, ["base_colour"] = m.BaseColour, ["alpha_test"] = m.AlphaTest,
            }).ToArray() ?? []),
            ["problems"] = new JsonArray(model.Problems.Select(p => (JsonNode)p).ToArray()),
        };
    }

    private static JsonNode Find(ModelLibrary lib, string filter, string? ext, int limit)
    {
        string[] exts = ext is null ? [".wsmodel", ".rigid_model_v2"] : ext.Split(',').Select(e => e.StartsWith('.') ? e : "." + e).ToArray();
        var hits = lib.Source.Enumerate(exts).Where(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new JsonObject
        {
            ["count"] = hits.Count,
            ["assets"] = new JsonArray(hits.Take(limit).Select(h => (JsonNode)h).ToArray()),
            ["truncated"] = hits.Count > limit,
        };
    }

    private static JsonNode Preview(ModelLibrary lib, string path, string[] a)
    {
        var (model, error) = lib.TryLoad(path);
        if (model is null) throw new InvalidDataException(error ?? "unreadable");
        double D(string name, double def) => Option(a, name) is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : def;
        var options = new ModelPreview.Options((int)D("--size", 384), D("--yaw", 35), D("--pitch", 30), (int)D("--lod", 0), !Flag(a, "--untextured"));
        var outPath = Option(a, "--out") ?? Path.Combine(Path.GetTempPath(), "terry_asset_" + Path.GetFileNameWithoutExtension(path) + ".png");
        File.WriteAllBytes(outPath, ModelPreview.RenderPng(model, lib, options));
        return new JsonObject
        {
            ["png"] = outPath, ["path"] = path, ["triangles"] = model.Triangles(options.Lod),
            ["textures"] = new JsonArray(model.Lods[Math.Min(options.Lod, model.Lods.Count - 1)].Select(m => m.BaseColour).Distinct().Select(t => (JsonNode?)t).ToArray()),
            ["problems"] = new JsonArray(model.Problems.Select(p => (JsonNode)p).ToArray()),
        };
    }

    /// <summary>Parses every asset of a kind across all sources and reports failures (the reader's coverage check).</summary>
    private static JsonNode Census(ModelLibrary lib, string ext, string? limit)
    {
        var sw = Stopwatch.StartNew();
        var paths = lib.Source.Enumerate(ext).ToList();
        if (limit is not null) paths = paths.Take(int.Parse(limit, CultureInfo.InvariantCulture)).ToList();
        var failures = new ConcurrentBag<(string Path, string Error)>();
        var problems = new ConcurrentBag<string>();
        var stats = new ConcurrentDictionary<string, int>();
        long triangles = 0;
        Parallel.ForEach(paths, p =>
        {
            try
            {
                switch (ext.ToLowerInvariant())
                {
                    case ".rigid_model_v2":
                        var m = RigidModel.Read(lib.Source.Read(p));
                        stats.AddOrUpdate($"v{m.Version}", 1, (_, n) => n + 1);
                        foreach (var mesh in m.Lods.SelectMany(l => l.Meshes)) stats.AddOrUpdate($"format {mesh.VertexFormat}", 1, (_, n) => n + 1);
                        Interlocked.Add(ref triangles, m.Lods.FirstOrDefault()?.Meshes.Sum(x => x.TriangleCount) ?? 0);
                        break;
                    case ".wsmodel":
                        var (model, error) = lib.TryLoad(p);
                        if (model is null) throw new InvalidDataException(error);
                        foreach (var pr in model.Problems) problems.Add($"{p}: {pr}");
                        Interlocked.Add(ref triangles, model.Triangles());
                        break;
                    case ".material":
                        _ = MaterialFile.Parse(lib.Source.Read(p));
                        break;
                    case ".dds":
                        var h = DdsHeader.Read(lib.Source.Read(p));
                        stats.AddOrUpdate(h.IsDx10 ? $"dxgi {h.DxgiFormat}" : h.FourCC.Length > 0 ? h.FourCC : $"{h.RgbBitCount}bpp", 1, (_, n) => n + 1);
                        break;
                    default:
                        throw new ArgumentException($"census supports .rigid_model_v2, .wsmodel, .material, .dds (not {ext})");
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception ex) { failures.Add((p, ex.Message)); }
        });
        return new JsonObject
        {
            ["ext"] = ext, ["files"] = paths.Count, ["ok"] = paths.Count - failures.Count, ["failed"] = failures.Count,
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1), ["lod0_triangles"] = triangles,
            ["stats"] = new JsonObject(stats.OrderByDescending(kv => kv.Value).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["failures"] = new JsonArray(failures.Take(40).Select(f => (JsonNode)$"{f.Path}: {f.Error}").ToArray()),
            ["failure_kinds"] = new JsonObject(failures.GroupBy(f => f.Error.Split(':')[0]).OrderByDescending(g => g.Count()).Take(15)
                .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["problems"] = problems.Count,
            ["problem_examples"] = new JsonArray(problems.Take(25).Select(p => (JsonNode)p).ToArray()),
        };
    }

    /// <summary>Every model a project's entities use (ECMesh / ECDecal model_path): does it load, and what is missing.</summary>
    private static JsonNode Check(ModelLibrary lib, EntityEditor editor)
    {
        var sw = Stopwatch.StartNew();
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, entities) in editor.ReadAll())
            foreach (var e in entities)
                foreach (var path in new[] { e.Component("ECMesh")?["model_path"], e.Component("ECDecal")?["model_path"] })
                    if (!string.IsNullOrEmpty(path)) uses[path] = uses.GetValueOrDefault(path) + 1;
        var failed = new ConcurrentBag<(string Path, int Uses, string Error)>();
        var problems = new ConcurrentBag<(string Path, string Problem)>();
        Parallel.ForEach(uses, kv =>
        {
            var (model, error) = lib.TryLoad(kv.Key);
            if (model is null) failed.Add((kv.Key, kv.Value, error ?? "unreadable"));
            else foreach (var pr in model.Problems) problems.Add((kv.Key, pr));
        });
        return new JsonObject
        {
            ["project"] = editor.TerryPath, ["models"] = uses.Count, ["entities"] = uses.Values.Sum(),
            ["loaded"] = uses.Count - failed.Count, ["failed"] = failed.Count, ["entities_with_failed_models"] = failed.Sum(f => f.Uses),
            ["with_problems"] = problems.Select(p => p.Path).Distinct().Count(), ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["failures"] = new JsonArray(failed.OrderByDescending(f => f.Uses).Take(40).Select(f => (JsonNode)$"{f.Path} (x{f.Uses}): {f.Error}").ToArray()),
            ["problems"] = new JsonArray(problems.GroupBy(p => p.Problem).OrderByDescending(g => g.Count()).Take(30)
                .Select(g => (JsonNode)$"{g.Key}  ({g.Count()} models, e.g. {g.First().Path})").ToArray()),
        };
    }

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static List<string> Options(string[] a, string name)
    {
        var values = new List<string>();
        for (var i = 0; i + 1 < a.Length; i++)
            if (a[i].Equals(name, StringComparison.OrdinalIgnoreCase)) values.Add(a[++i]);
        return values;
    }

    private static bool Flag(string[] a, string name) => a.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string Positional(string[] a, int index, string what)
    {
        var positional = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) { positional.Add(a[i]); continue; }
            if (a[i] is not ("--untextured" or "--json")) i++;
        }
        return index < positional.Count ? positional[index] : throw new ArgumentException($"missing {what}");
    }
}
