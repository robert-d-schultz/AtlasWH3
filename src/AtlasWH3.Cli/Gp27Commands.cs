using System.Collections;
using System.Reflection;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Props;

/// <summary>gp27-* commands: WH3 (BMD v27) global_props.bin research and parity tools.</summary>
static class Gp27Commands
{
    public static readonly string[] Names = ["gp27-roundtrip", "gp27-diff", "gp27-model", "gp27-cat"];

    public static int Run(ProjectPaths paths, string command, string[] a) => command switch
    {
        "gp27-roundtrip" => RoundTrip(a),
        "gp27-diff" => Diff(a),
        "gp27-model" => ModelBox(paths, a),
        "gp27-cat" => Cat(paths, a),
        _ => throw new ArgumentException($"unknown command {command}"),
    };

    /// <summary>gp27-roundtrip &lt;global_props.bin&gt;...: parse and re-encode every body, report the ones that differ.</summary>
    private static int RoundTrip(string[] files)
    {
        var failed = 0;
        foreach (var file in files)
        {
            var gp = GlobalProps.Load(file);
            int ok = 0, bad = 0;
            foreach (var (name, body) in gp.Bodies())
            {
                try
                {
                    if (Bmd27Body.Parse(body).ToBytes().AsSpan().SequenceEqual(body)) { ok++; continue; }
                    if (bad++ < 5) Console.WriteLine($"  differs: {name}");
                }
                catch (InvalidDataException e)
                {
                    if (bad++ < 5) Console.WriteLine($"  {name}: {e.Message}");
                }
            }
            Console.WriteLine($"{file}: {ok:N0} bodies byte-identical, {bad:N0} not");
            failed += bad;
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>gp27-model &lt;model path&gt;... [--pack p]: the box and flags the global_props builder derives (game packs plus
    /// the linked mod packs), and the RMV2's per-LOD mesh header bounds.</summary>
    private static int ModelBox(ProjectPaths paths, string[] models)
    {
        var packs = AtlasWH3.Core.GameSetup.OpenWithLinked(paths.GameDataDir, paths.ModPacks);
        var b = new AtlasWH3.Core.Campaign.Props.Wh3GlobalPropsBuilder(packs, new AtlasWH3.Formats.Terry.PrefabLibrary(".", "campaign"),
            new Dictionary<string, int>(), "", (0, 0, 1, 1), (_, _) => "");
        foreach (var m in models)
        {
            var d = b.ModelInfo(m);
            Console.WriteLine($"{m}: box [{string.Join(", ", d.Min)}] .. [{string.Join(", ", d.Max)}], add_terrain_height {d.TerrainHeight}, emissive mountain {d.DynamicShadows}, skeleton {d.Skinned}");
            var geometry = m;
            if (m.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase) && packs.TryRead(m.ToLowerInvariant()) is { } ws)
            {
                var f = AtlasWH3.Formats.Models.WsModelFile.Parse(ws);
                geometry = f.Geometry;
                Console.WriteLine($"  geometry {f.Geometry}; materials {string.Join(", ", f.Materials.Values.Distinct())}");
            }
            Console.WriteLine($"  pack: {packs.FindOwner(geometry.ToLowerInvariant())?.SourcePath ?? "none"}");
            if (packs.TryRead(geometry.ToLowerInvariant()) is { } bytes)
            {
                var rm = AtlasWH3.Formats.Models.RigidModel.Read(bytes);
                for (var l = 0; l < rm.Lods.Count; l++)
                    foreach (var mesh in rm.Lods[l].Meshes)
                        Console.WriteLine($"  v{rm.Version} skel '{rm.Skeleton}' lod {l}: fmt {mesh.VertexFormat} flags {mesh.RenderFlags} shader {mesh.Shader} mat {mesh.MaterialId} posOnly {mesh.PositionsOnly} [{string.Join(", ", mesh.BoundsMin)}]..[{string.Join(", ", mesh.BoundsMax)}]");
            }
            else Console.WriteLine($"  {geometry} not in the packs");
        }
        return 0;
    }

    /// <summary>gp27-cat &lt;pack path&gt; [--pack p]: a file from the game and linked packs, as UTF-8 text.</summary>
    private static int Cat(ProjectPaths paths, string[] a)
    {
        var packs = AtlasWH3.Core.GameSetup.OpenWithLinked(paths.GameDataDir, paths.ModPacks);
        var bytes = packs.TryRead(a[0].ToLowerInvariant());
        Console.WriteLine(bytes is null ? $"{a[0]}: not in the packs" : System.Text.Encoding.UTF8.GetString(bytes));
        return bytes is null ? 1 : 0;
    }

    private sealed record Placed(string Region, int Cell, int Bucket, string Kind, string Identity, float X, float Z, object Record, string Entry);

    /// <summary>gp27-diff &lt;native&gt; &lt;bob&gt; [--examples n]: entries, object placement and record fields.</summary>
    private static int Diff(string[] a)
    {
        var files = a.Where(s => !s.StartsWith("--")).ToList();
        var examples = a.SkipWhile(s => s != "--examples").Skip(1).Select(int.Parse).FirstOrDefault(5);
        var native = GlobalPropsParity.Load(files[0]);
        var bob = GlobalPropsParity.Load(files[1]);
        Console.WriteLine($"entries: native {native.Count:N0}, BOB {bob.Count:N0}");
        var same = native.Keys.Count(k => bob.TryGetValue(k, out var b) && b.AsSpan().SequenceEqual(native[k]));
        Console.WriteLine($"  byte-identical bodies: {same:N0}");
        Report("  only native", native.Keys.Except(bob.Keys).ToList(), examples);
        Report("  only BOB", bob.Keys.Except(native.Keys).ToList(), examples);
        var orderSame = native.Keys.Where(bob.ContainsKey).SequenceEqual(bob.Keys.Where(native.ContainsKey));
        Console.WriteLine($"  common entries in the same order: {orderSame}");

        // placement: every record of a bucket body, matched by kind, identity and position
        var np = Placements(native);
        var bp = Placements(bob);
        Console.WriteLine($"objects: native {np.Count:N0}, BOB {bp.Count:N0}");
        static string Key(Placed p) => $"{p.Kind}|{p.Identity}|{p.X:F3}|{p.Z:F3}";
        var nByKey = np.ToLookup(Key);
        var bByKey = bp.ToLookup(Key);
        var outcome = new Dictionary<string, List<string>>();
        void Note(string what, string example) { if (!outcome.TryGetValue(what, out var l)) outcome[what] = l = []; l.Add(example); }
        foreach (var key in nByKey.Select(g => g.Key).Union(bByKey.Select(g => g.Key)))
        {
            var ns = nByKey[key].Select(p => (p.Region, p.Cell, p.Bucket)).OrderBy(t => t).ToList();
            var bs = bByKey[key].Select(p => (p.Region, p.Cell, p.Bucket)).OrderBy(t => t).ToList();
            if (ns.SequenceEqual(bs)) { Note("same placement", key); continue; }
            if (ns.Count == 0) { Note("missing in native", $"{key} BOB {Show(bs)}"); continue; }
            if (bs.Count == 0) { Note("extra in native", $"{key} native {Show(ns)}"); continue; }
            var what = new List<string>();
            if (!ns.Select(t => t.Region).Distinct().Order().SequenceEqual(bs.Select(t => t.Region).Distinct().Order())) what.Add("region");
            if (!ns.Select(t => t.Cell).Distinct().Order().SequenceEqual(bs.Select(t => t.Cell).Distinct().Order())) what.Add("cell");
            if (!ns.Select(t => t.Bucket).Distinct().Order().SequenceEqual(bs.Select(t => t.Bucket).Distinct().Order())) what.Add("bucket");
            if (ns.Count != bs.Count) what.Add("count");
            Note("differs: " + string.Join("+", what), $"{key} native {Show(ns)} BOB {Show(bs)}");
        }
        foreach (var (what, list) in outcome.OrderByDescending(kv => kv.Value.Count))
        {
            Console.WriteLine($"  {what}: {list.Count:N0}");
            if (what != "same placement") foreach (var ex in list.Take(examples)) Console.WriteLine($"      {ex}");
        }
        var kinds = np.Concat(bp).Select(p => p.Kind).Distinct().Order();
        foreach (var k in kinds)
            Console.WriteLine($"    {k}: native {np.Count(p => p.Kind == k):N0}, BOB {bp.Count(p => p.Kind == k):N0}");

        // record fields of objects matched by key (first of each), field by field
        var fields = new Dictionary<string, (int Count, List<string> Examples)>();
        foreach (var g in bByKey)
        {
            var n = nByKey[g.Key].FirstOrDefault();
            if (n is null) continue;
            foreach (var (field, nv, bv) in Compare(n.Record, g.First().Record))
            {
                var f = $"{n.Kind}.{field}";
                fields.TryGetValue(f, out var e);
                e.Examples ??= [];
                if (e.Examples.Count < examples) e.Examples.Add($"{g.Key}: native {nv} BOB {bv}");
                fields[f] = (e.Count + 1, e.Examples);
            }
        }
        Console.WriteLine("record fields that differ (matched objects):");
        foreach (var (f, (count, ex)) in fields.OrderByDescending(kv => kv.Value.Count))
        {
            Console.WriteLine($"  {f}: {count:N0}");
            foreach (var e in ex) Console.WriteLine($"      {e}");
        }

        // record order inside common bucket bodies with the same records
        int bodies = 0, orderOnly = 0, other = 0;
        var orderEx = new List<string>();
        foreach (var name in native.Keys.Where(bob.ContainsKey))
        {
            if (GlobalPropsParity.Parts(name).Bucket < 0 || native[name].AsSpan().SequenceEqual(bob[name])) continue;
            bodies++;
            var nb = Bmd27Body.Parse(native[name]);
            var bb = Bmd27Body.Parse(bob[name]);
            var nk = Records(nb).Select(r => r.Kind + r.Identity + r.X + r.Z).ToList();
            var bk = Records(bb).Select(r => r.Kind + r.Identity + r.X + r.Z).ToList();
            if (nk.Order().SequenceEqual(bk.Order()) && !nk.SequenceEqual(bk)) { orderOnly++; if (orderEx.Count < examples) orderEx.Add(name); }
            else other++;
        }
        Console.WriteLine($"common bucket bodies that differ: {bodies:N0} ({orderOnly:N0} only in record order, {other:N0} otherwise)");
        foreach (var e in orderEx) Console.WriteLine($"      {e}");
        var parity = GlobalPropsParity.Compare(native, bob, examples);
        Console.WriteLine($"content parity (record order and one-prop numbering ignored): {parity.Same:N0} groups the same, {parity.Differ:N0} differ ({parity.Share:P2})");
        foreach (var e in parity.Examples) Console.WriteLine($"      {e}");
        return 0;
    }

    private static string Show(List<(string Region, int Cell, int Bucket)> l) =>
        string.Join(" ", l.Select(t => $"{t.Region}.{t.Cell}.{t.Bucket}"));

    private static void Report(string label, List<string> names, int examples)
    {
        Console.WriteLine($"{label}: {names.Count:N0}");
        foreach (var n in names.Take(examples)) Console.WriteLine($"      {n}");
    }

    private static List<Placed> Placements(Dictionary<string, byte[]> entries)
    {
        var result = new List<Placed>();
        foreach (var (name, data) in entries)
        {
            var (region, cell, bucket) = GlobalPropsParity.Parts(name);
            if (bucket < 0) continue;
            foreach (var r in Records(Bmd27Body.Parse(data)))
                result.Add(r with { Region = region, Cell = cell, Bucket = bucket, Entry = name });
        }
        return result;
    }

    private static IEnumerable<Placed> Records(Bmd27Body b)
    {
        Placed P(string kind, string id, float x, float z, object r) => new("", 0, 0, kind, id, x, z, r, "");
        foreach (var p in b.Props) yield return P(p.Decal == 1 ? "decal" : "prop", b.PropPaths[(int)p.PathIndex], p.Transform[9], p.Transform[11], p);
        foreach (var v in b.Vfx) yield return P("vfx", v.Name, v.Transform[9], v.Transform[11], v);
        foreach (var l in b.Lights) yield return P("light", "", l.X, l.Z, l);
        foreach (var s in b.Scenes) yield return P("scene", s.Path, s.Transform[9], s.Transform[11], s);
        foreach (var p in b.Probes) yield return P("probe", "", p.X, p.Z, p);
        foreach (var h in b.Holes) yield return P("hole", "", h.Vertices[0], h.Vertices[2], h);
        foreach (var p in b.Polys) yield return P("poly", p.Material, p.Transform[9], p.Transform[11], p);
        foreach (var s in b.Spots) yield return P("spot", "", s.X, s.Z, s);
        foreach (var s in b.Sounds) yield return P("sound", s.Name, s.Points.Count > 0 ? s.Points[0].X : 0, s.Points.Count > 0 ? s.Points[0].Z : 0, s);
    }

    /// <summary>Public fields that differ, as (name, native, BOB) text; arrays and lists element-wise.</summary>
    private static IEnumerable<(string, string, string)> Compare(object n, object b)
    {
        foreach (var f in n.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.Name == "PathIndex") continue;
            var nv = f.GetValue(n);
            var bv = f.GetValue(b);
            var ns = GlobalPropsParity.Text(nv);
            var bs = GlobalPropsParity.Text(bv);
            if (ns != bs) yield return (f.Name, ns, bs);
        }
    }

}
