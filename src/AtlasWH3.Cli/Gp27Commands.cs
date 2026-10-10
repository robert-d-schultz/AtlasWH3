using System.Collections;
using System.Reflection;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Props;

/// <summary>gp27-* commands: WH3 (BMD v27) global_props.bin research and parity tools.</summary>
static class Gp27Commands
{
    public static readonly string[] Names = ["gp27-roundtrip", "gp27-diff", "gp27-model", "gp27-cat", "gp27-pieces", "gp27-pieces-diff", "gp27-pieces-order", "gp27-find"];

    public static int Run(ProjectPaths paths, string command, string[] a) => command switch
    {
        "gp27-roundtrip" => RoundTrip(a),
        "gp27-pieces" => Pieces(a),
        "gp27-pieces-diff" => PiecesDiff(a),
        "gp27-pieces-order" => PiecesOrder(a),
        "gp27-find" => Find(a),
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

    /// <summary>gp27-pieces &lt;pieces dir&gt;...: every piece's objects / sound .bin parsed and re-encoded, its .culture read
    /// and its section counts set against the body's, and the framing bytes and placeholder masks that occur.</summary>
    private static int Pieces(string[] dirs)
    {
        var failed = 0;
        foreach (var dir in dirs)
        {
            int ok = 0, bad = 0, cultureBad = 0;
            var kinds = new Dictionary<string, int>();
            var framing = new Dictionary<string, int>();
            var tagOrders = new Dictionary<string, int>();
            void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
            foreach (var bin in Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories).Order())
            {
                var stem = Path.GetFileNameWithoutExtension(bin);
                var body = File.ReadAllBytes(bin);
                Bmd27Body b;
                try { b = Bmd27Body.Parse(body); }
                catch (InvalidDataException e) { if (bad++ < 5) Console.WriteLine($"  {bin}: {e.Message}"); continue; }
                if (b.ToBytes().AsSpan().SequenceEqual(body)) ok++;
                else if (bad++ < 5) Console.WriteLine($"  differs: {bin}");
                Count(kinds, stem);
                Count(framing, $"{Convert.ToHexString(b.Preamble)} {Convert.ToHexString(b.AfterNested)} {Convert.ToHexString(b.AfterVfx)} {Convert.ToHexString(b.AfterLights)} {Convert.ToHexString(b.AfterPoly)} {Convert.ToHexString(b.Trailing)}");
                var masks = b.Props.Select(p => p.CultureMask).Concat(b.Vfx.Select(v => v.CultureMask)).Concat(b.Lights.Select(l => l.CultureMask))
                    .Concat(b.Spots.Select(s => s.CultureMask)).Concat(b.Scenes.Select(s => s.CultureMask)).Concat(b.Sounds.Select(s => s.CultureMask)).Distinct();
                Count(framing, "masks in .bin: " + string.Join(",", masks.Order()));
                if (b.Nested.Count > 0) Count(framing, "has nested");

                var culturePath = Path.ChangeExtension(bin, ".culture");
                if (!File.Exists(culturePath)) { Count(framing, "no .culture"); continue; }
                var sections = PieceCulture.Read(File.ReadAllBytes(culturePath));
                if (!PieceCulture.Write(sections.Select(s => (s.Tag, (IReadOnlyList<ulong>)s.Masks))).AsSpan().SequenceEqual(File.ReadAllBytes(culturePath)))
                    Count(framing, ".culture does not round-trip");
                Count(tagOrders, string.Join(" ", sections.Select(s => s.Tag)));
                var expected = new Dictionary<string, int>
                {
                    ["p"] = b.Props.Count, ["v"] = b.Vfx.Count, ["lp"] = b.Lights.Count, ["ls"] = b.Spots.Count, ["m"] = b.Polys.Count,
                    ["sc"] = b.Scenes.Count, ["ss"] = b.Sounds.Count,
                };
                var got = sections.ToDictionary(s => s.Tag, s => s.Masks.Count);
                foreach (var (tag, n) in expected)
                    if (got.GetValueOrDefault(tag) != n)
                    {
                        if (cultureBad++ < 5) Console.WriteLine($"  {culturePath}: '{tag}' {got.GetValueOrDefault(tag)}, the .bin has {n}");
                    }
                if (b.Probes.Count > 0) Count(framing, "has light probes");
                if (b.Holes.Count > 0) Count(framing, $"has holes (ht {got.GetValueOrDefault("ht")})");
            }
            Console.WriteLine($"{dir}: {ok:N0} bodies byte-identical after re-encoding, {bad:N0} not; {cultureBad:N0} .culture count mismatches");
            foreach (var (k, n) in kinds.OrderBy(kv => kv.Key)) Console.WriteLine($"  {k}: {n:N0}");
            Console.WriteLine("  .culture section orders:");
            foreach (var (k, n) in tagOrders.OrderByDescending(kv => kv.Value)) Console.WriteLine($"    {n,5:N0}  {k}");
            Console.WriteLine("  framing / notes:");
            foreach (var (k, n) in framing.OrderByDescending(kv => kv.Value)) Console.WriteLine($"    {n,5:N0}  {k}");
            failed += bad;
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>gp27-pieces-diff &lt;native pieces dir&gt; &lt;BOB pieces dir&gt; [--examples n]: the pieces' objects / sound files
    /// (.bin + .culture): which exist, which are byte-identical, and their records (with their .culture masks) as
    /// multisets, then the fields of matched records. --ignore f,g leaves record fields out of the comparison (Animated,
    /// DynamicShadows, UsesTerrainVertexOffset: what BOB derives only from models loose in its kit).</summary>
    private static int PiecesDiff(string[] a)
    {
        var examples = a.SkipWhile(s => s != "--examples").Skip(1).Select(int.Parse).FirstOrDefault(5);
        _ignore = a.SkipWhile(s => s != "--ignore").Skip(1).FirstOrDefault()?.Split(',').ToHashSet() ?? [];
        var dirs = a.Where((s, i) => !s.StartsWith("--") && (i == 0 || a[i - 1] is not ("--examples" or "--ignore"))).ToList();
        var outcome = new Dictionary<string, List<string>>();
        void Note(string what, string example) { if (!outcome.TryGetValue(what, out var l)) outcome[what] = l = []; l.Add(example); }
        int same = 0, binSame = 0, cultureSame = 0, files = 0;
        long bobRecords = 0, exactRecords = 0;
        var fields = new Dictionary<string, (int Count, List<string> Examples)>();
        static IEnumerable<string> Stems(string dir) => Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.bin").Select(f => Path.GetFileNameWithoutExtension(f)!) : [];
        foreach (var piece in Directory.GetDirectories(dirs[0]).Select(Path.GetFileName).Union(Directory.GetDirectories(dirs[1]).Select(Path.GetFileName)).Order())
        {
            var nDir = Path.Combine(dirs[0], piece!);
            var bDir = Path.Combine(dirs[1], piece!);
            foreach (var stem in Stems(nDir).Union(Stems(bDir)).Order())
            {
                var nBin = Path.Combine(nDir, stem + ".bin");
                var bBin = Path.Combine(bDir, stem + ".bin");
                if (!File.Exists(nBin)) { Note("file only BOB", $"{piece}/{stem}"); continue; }
                if (!File.Exists(bBin)) { Note("file only native", $"{piece}/{stem}"); continue; }
                files++;
                var nb = File.ReadAllBytes(nBin);
                var bb = File.ReadAllBytes(bBin);
                var nc = File.ReadAllBytes(Path.ChangeExtension(nBin, ".culture"));
                var bc = File.Exists(Path.ChangeExtension(bBin, ".culture")) ? File.ReadAllBytes(Path.ChangeExtension(bBin, ".culture")) : [];
                var binEq = nb.AsSpan().SequenceEqual(bb);
                var cultureEq = nc.AsSpan().SequenceEqual(bc);
                if (binEq) binSame++;
                if (cultureEq) cultureSame++;
                var n = PieceRecords(nb, nc);
                var b = PieceRecords(bb, bc);
                bobRecords += b.Count;
                if (binEq && cultureEq) { same++; exactRecords += b.Count; continue; }
                var nKeys = n.Select(r => r.Key).ToList();
                var bKeys = b.Select(r => r.Key).ToList();
                var nFull = n.Select(r => r.Key + "|" + Full(r.P.Record)).ToList();
                var bFull = b.Select(r => r.Key + "|" + Full(r.P.Record)).ToList();
                if (nFull.Order().SequenceEqual(bFull.Order()))
                {
                    if (nFull.SequenceEqual(bFull)) Note("same records, same order", $"{piece}/{stem}");
                    else
                    {
                        // BOB's records as native indices (each duplicate taken once, in order)
                        var used = new bool[nFull.Count];
                        var seq = bFull.Select(k => { var i = Enumerable.Range(0, nFull.Count).First(j => !used[j] && nFull[j] == k); used[i] = true; return i; });
                        Note("same records, other order", $"{piece}/{stem}: {string.Join(" ", seq.Take(60))}");
                    }
                }
                else
                {
                    var missing = bKeys.GroupBy(k => k).SelectMany(g => g.Skip(nKeys.Count(k => k == g.Key))).ToList();
                    var extra = nKeys.GroupBy(k => k).SelectMany(g => g.Skip(bKeys.Count(k => k == g.Key))).ToList();
                    Note("records differ", $"{piece}/{stem}: native {nKeys.Count} BOB {bKeys.Count}; missing {string.Join("; ", missing.Take(3))}; extra {string.Join("; ", extra.Take(3))}");
                    foreach (var m in missing) Note("  missing record kind " + m.Split('|')[0], $"{piece}/{stem} {m}");
                    foreach (var m in extra) Note("  extra record kind " + m.Split('|')[0], $"{piece}/{stem} {m}");
                }
                // fields of records that are not identical, paired by key
                var exact = nFull.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
                foreach (var k in bFull) if (exact.TryGetValue(k, out var c) && c > 0) { exact[k] = c - 1; exactRecords++; }
                var bRest = b.Where((r, i) => !nFull.Contains(bFull[i])).ToList();
                var bByKey = bRest.ToLookup(r => r.Key);
                foreach (var r in n.Where((r, i) => !bFull.Contains(nFull[i])))
                    if (bByKey[r.Key].Select(m => m.P).FirstOrDefault() is { } match)
                        foreach (var (field, nv, bv) in Compare(r.P.Record, match.Record))
                        {
                            var f = $"{r.P.Kind}.{field}";
                            fields.TryGetValue(f, out var e);
                            e.Examples ??= [];
                            if (e.Examples.Count < examples) e.Examples.Add($"{piece}/{stem} {r.Key}: native {nv} BOB {bv}");
                            fields[f] = (e.Count + 1, e.Examples);
                        }
            }
        }
        Console.WriteLine($"{files:N0} files in both: {same:N0} identical (.bin {binSame:N0}, .culture {cultureSame:N0})");
        Console.WriteLine($"records: BOB {bobRecords:N0}, matched exactly (fields and culture mask) {exactRecords:N0} ({(double)exactRecords / Math.Max(1, bobRecords):P2})");
        foreach (var (what, list) in outcome.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {what}: {list.Count:N0}");
            foreach (var ex in list.Take(examples)) Console.WriteLine($"      {ex}");
        }
        Console.WriteLine("record fields that differ (matched records):");
        foreach (var (f, (count, ex)) in fields.OrderByDescending(kv => kv.Value.Count))
        {
            Console.WriteLine($"  {f}: {count:N0}");
            foreach (var e in ex) Console.WriteLine($"      {e}");
        }
        return 0;
    }

    /// <summary>gp27-pieces-order &lt;piece objects.bin&gt; &lt;global_props.bin of the same run&gt;: the piece's records as positions
    /// in a walk of global_props.bin's bucket bodies in file order (first occurrence, culture mask ignored).</summary>
    private static int PiecesOrder(string[] a)
    {
        var piece = Records(Bmd27Body.Parse(File.ReadAllBytes(a[0]))).Select(r => r.Kind + "|" + Full(r.Record).Replace("|1|", "|")).ToList();
        var walk = new List<string>();
        foreach (var (name, data) in GlobalProps.Load(a[1]).Bodies())
            if (GlobalPropsParity.Parts(name).Bucket >= 0)
                walk.AddRange(Records(Bmd27Body.Parse(data)).Select(r => r.Kind + "|" + Full(r.Record).Replace("|1|", "|")));
        var first = new Dictionary<string, int>();
        for (var i = 0; i < walk.Count; i++) first.TryAdd(walk[i], i);
        var pos = piece.Select(k => first.TryGetValue(k, out var i) ? i : -1).ToList();
        Console.WriteLine($"{piece.Count} records, {pos.Count(p => p < 0)} not in global_props; walk positions ascending: {pos.Where(p => p >= 0).Zip(pos.Where(p => p >= 0).Skip(1)).All(t => t.First < t.Second)}");
        Console.WriteLine(string.Join(" ", pos.Take(80)));
        return 0;
    }

    /// <summary>gp27-find &lt;global_props.bin | piece .bin&gt; &lt;x&gt; &lt;z&gt; [radius]: the records within radius (default 1) of
    /// (x, z), with the body they are in.</summary>
    private static int Find(string[] a)
    {
        float x = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), z = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
        var radius = a.Length > 3 ? float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
        var bodies = a[0].EndsWith("global_props.bin", StringComparison.OrdinalIgnoreCase) || a[0].EndsWith("global_props_sound.bin", StringComparison.OrdinalIgnoreCase)
            ? GlobalProps.Load(a[0]).Bodies().ToList() : [(Path.GetFileName(a[0]), File.ReadAllBytes(a[0]))];
        foreach (var (name, data) in bodies)
            foreach (var r in Records(Bmd27Body.Parse(data)))
                if (MathF.Abs(r.X - x) <= radius && MathF.Abs(r.Z - z) <= radius)
                    Console.WriteLine($"{name}: {r.Kind} {r.Identity} {r.X} {r.Z}");
        return 0;
    }

    private static HashSet<string> _ignore = [];

    private static string Full(object record) =>
        string.Join("|", record.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.Name != "PathIndex" && !_ignore.Contains(f.Name))
            .Select(f => GlobalPropsParity.Text(f.GetValue(record))));

    /// <summary>A piece body's records keyed by kind, identity, position and their .culture mask (the ht section's
    /// (triangle count, mask) runs expanded to a mask per triangle).</summary>
    private static List<(string Key, Placed P)> PieceRecords(byte[] bin, byte[] culture)
    {
        var body = Bmd27Body.Parse(bin);
        var sections = PieceCulture.Read(culture).ToDictionary(s => s.Tag, s => s.Masks);
        if (sections.TryGetValue("ht", out var runs))
            sections["ht"] = Enumerable.Range(0, runs.Count / 2).SelectMany(i => Enumerable.Repeat(runs[2 * i + 1], (int)runs[2 * i])).ToList();
        var next = new Dictionary<string, int>();
        string Mask(string tag)
        {
            var i = next.GetValueOrDefault(tag);
            next[tag] = i + 1;
            return sections.TryGetValue(tag, out var m) && i < m.Count ? m[i].ToString("x") : "?";
        }
        var result = new List<(string, Placed)>();
        foreach (var r in Records(body))
        {
            var tag = r.Kind switch
            {
                "prop" or "decal" => "p", "poly" => "m", "vfx" => "v", "light" => "lp", "spot" => "ls", "scene" => "sc", "sound" => "ss",
                "hole" => "ht", _ => null,
            };
            result.Add(($"{r.Kind}|{r.Identity}|{r.X:F4}|{r.Z:F4}|{(tag is null ? "" : Mask(tag))}", r));
        }
        return result;
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
            if (f.Name == "PathIndex" || _ignore.Contains(f.Name)) continue;
            var nv = f.GetValue(n);
            var bv = f.GetValue(b);
            var ns = GlobalPropsParity.Text(nv);
            var bs = GlobalPropsParity.Text(bv);
            if (ns != bs) yield return (f.Name, ns, bs);
        }
    }

}
