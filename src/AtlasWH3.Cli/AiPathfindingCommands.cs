using System.Diagnostics;
using System.Globalization;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.AiPathfinding;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;

/// <summary>
/// hlp-spd: builds a campaign map's hlp_data.esf / spd_data.esf from its pathfinding.ppd + map_data.esf (the native
/// replacement for the game's reprocess_hlp_data / reprocess_spd_data) and optionally compares them with reference files.
///   hlp-spd --in &lt;dir with pathfinding.ppd, map_data.esf&gt; [--out &lt;dir&gt;] [--compare &lt;dir with reference esf&gt;]
///           [--only hlp|spd] [--legacy-stl] [--threshold f] [--centre-blocked] [--threads n]
///           [--wrap-like-game] (hlp region tables with the game's wrapping u32 sums, for parity; default: corrected)
/// Research (with --compare, spd): --diag-slot k [--diag-count n] (cheapest diverging cells), --edge-costs (edge costs
/// the reference implies), --slot-edges [--all-edges], --show x,y;.., --hexmap-flags, --area-diffs.
///   esf-roundtrip &lt;file.esf&gt;...: CAAB/CBAB files through EsfTree and CaabWriter, byte for byte.
/// </summary>
static class AiPathfindingCommands
{
    public static readonly HashSet<string> Names = ["hlp-spd", "esf-roundtrip"];

    static string? Option(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    /// <summary>esf-roundtrip &lt;file.esf&gt;...: reads each CAAB file as a tree and writes it back with
    /// <see cref="CaabWriter"/>; reports the first differing byte.</summary>
    static int RoundTrip(string[] files)
    {
        var exit = 0;
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            var tree = EsfTree.Read(bytes);
            var ts = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            var w = new CaabWriter(tree.Root.Name, (byte)tree.Root.Version, bytes.Length + 64);
            foreach (var c in tree.Root.Children) w.Node(c);
            var outBytes = w.ToFile(ts, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));
            if (Environment.GetEnvironmentVariable("ESF_ROUNDTRIP_OUT") is { } dump) File.WriteAllBytes(Path.Combine(dump, Path.GetFileName(f)), outBytes);
            var n = Math.Min(bytes.Length, outBytes.Length);
            var at = 0;
            while (at < n && bytes[at] == outBytes[at]) at++;
            var same = at == n && bytes.Length == outBytes.Length;
            if (!same) exit = 1;
            Console.WriteLine($"{(same ? "same" : $"DIFFERENT at {at} (sizes {bytes.Length} / {outBytes.Length}): {Convert.ToHexString(bytes.AsSpan(Math.Max(0, at - 6), Math.Min(16, bytes.Length - Math.Max(0, at - 6))))} vs {Convert.ToHexString(outBytes.AsSpan(Math.Max(0, at - 6), Math.Min(16, outBytes.Length - Math.Max(0, at - 6))))}")}  {f}");
        }
        return exit;
    }

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        if (command == "esf-roundtrip") return RoundTrip(a);
        var inDir = Option(a, "--in") ?? paths.AkWorkingCampaignMapDir;
        var outDir = Option(a, "--out");
        var compare = Option(a, "--compare");
        var only = Option(a, "--only");
        var threads = int.Parse(Option(a, "--threads") ?? "0");
        var sw = Stopwatch.StartNew();
        var ppd = PathfindingPpd.Read(Path.Combine(inDir, "pathfinding.ppd"));
        var regions = MapDataRegions.Read(Path.Combine(inDir, "map_data.esf"));
        var dbNotes = new List<string>();
        var settings = AiPathfindingStep.DbSettings(paths, dbNotes);
        foreach (var note in dbNotes) Console.WriteLine(note);
        Console.WriteLine($"read {ppd.Width}x{ppd.Height}, {regions.Regions.Count} regions in {sw.Elapsed.TotalSeconds:F2} s; road {settings.RoadCost} beach {settings.LandToSeaCost}/{settings.SeaToLandCost}");
        var ts = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exit = 0;
        if (only is null or "spd")
        {
            var t = Stopwatch.StartNew();
            var grid = new CampaignPathGrid(ppd, regions, settings);
            if (Option(a, "--grid-cost") is { } gc) // research: the spd grid's shortest cost between hex pairs
                foreach (var pair in gc.Split(';'))
                {
                    var v = pair.Split(',').Select(int.Parse).ToArray();
                    var dist = grid.Search(grid.Index(v[0], v[1]), false);
                    Console.WriteLine($"grid cost ({v[0]},{v[1]})->({v[2]},{v[3]}) {dist[grid.Index(v[2], v[3])]}");
                }
            if (Option(a, "--dump-grid") is { } dump) // research: forward edge costs (u32 per hex·6) + types
            {
                File.WriteAllBytes(dump + ".fwd", System.Runtime.InteropServices.MemoryMarshal.AsBytes(grid.Forward.AsSpan()).ToArray());
                File.WriteAllBytes(dump + ".types", grid.Types);
                File.WriteAllBytes(dump + ".edges", grid.EdgeBytes);
                File.WriteAllBytes(dump + ".areas", System.Runtime.InteropServices.MemoryMarshal.AsBytes(regions.AreaMap.AsSpan()).ToArray());
                File.WriteAllLines(dump + ".regions.txt", regions.Regions.Select((r, i) =>
                    $"{i}\t{r.Key}\t{r.IsSea}\t{r.Settlement}\t{r.Port}\t{string.Join(";", r.PrimarySlot)}\t{string.Join(";", r.PortSlot)}"));
            }
            var refPath = compare is null ? null : Path.Combine(compare, "spd_data.esf");
            var refSpd = refPath is not null && File.Exists(refPath) ? SpdData.Read(refPath) : null;
            var spd = SpdBuilder.Build(grid, regions, refSpd?.Timestamp ?? ts, Console.WriteLine,
                new SpdBuilder.Options { Inwards = a.Contains("--spd-inwards"), MaxThreads = threads });
            if (refSpd is not null) spd.Magic = refSpd.Magic;
            var bytes = spd.ToBytes();
            Console.WriteLine($"spd: {spd.Width}x{spd.Height} cells, {spd.SetCount} sets, {spd.AreaLandmarks.Count} areas, {bytes.Length:N0} bytes in {t.Elapsed.TotalSeconds:F2} s");
            if (outDir is not null) { Directory.CreateDirectory(outDir); File.WriteAllBytes(Path.Combine(outDir, "spd_data.esf"), bytes); }
            if (refSpd is not null)
            {
                var same = File.ReadAllBytes(refPath!).AsSpan().SequenceEqual(bytes);
                Console.WriteLine($"spd vs reference: {(same ? "byte-identical" : "DIFFERENT")}");
                if (!same) { Console.WriteLine(SpdCompare.Report(spd, refSpd, a.Contains("--verbose") ? 40 : 8)); exit = 1; }
                if (!same && Option(a, "--diag-slot") is { } ds) SpdCompare.FirstDiffs(spd, refSpd, grid, int.Parse(ds), int.Parse(Option(a, "--diag-count") ?? "6"), (x, y) =>
                    string.Join(",", regions.Regions.Where(r => r.PrimarySlot.Contains((x, y))).Select(r => "primary:" + r.Key)
                        .Concat(regions.Regions.Where(r => r.PortSlot.Contains((x, y))).Select(r => "port:" + r.Key))
                        .Concat(regions.Regions.Where(r => r.Settlement == (x, y)).Select(r => "settlement:" + r.Key))
                        .Concat(regions.Regions.Where(r => r.Port == (x, y)).Select(r => "portpos:" + r.Key))));
                if (a.Contains("--edge-costs")) SpdCompare.EdgeCosts(refSpd, grid);
                if (a.Contains("--hexmap-flags"))
                {
                    var hm = EsfTree.Read(Path.Combine(inDir, "map_data.esf")).Root.Descendants("HEX_MAP_DATA").First().Children.OfType<EsfArray>().First().Data;
                    var piece = SpdBuilder.Pieces(grid, out var pc);
                    var refSets = new HashSet<(int, int)>(refSpd.SetLandmarks.Select(p => ((int)p.X, (int)p.Y)));
                    var stats = new Dictionary<string, int>();
                    for (var h = 0; h < grid.Width * grid.Height; h++)
                    {
                        if (piece[h] < 0) continue;
                        int x = h % grid.Width, y = h / grid.Width;
                        var inRef = x >= refSpd.X0 && x <= refSpd.X1 && y >= refSpd.Y0 && y <= refSpd.Y1
                            && refSpd.Sets[(y - refSpd.Y0) * refSpd.Width + (x - refSpd.X0)] != SpdData.NoSet;
                        var ak = regions.AreaMap[h];
                        var k = inRef ? "ref set yes" : $"ref set no: region {regions.Regions[ak & MapDataRegions.RegionMask].Key} area {ak >> MapDataRegions.AreaShift} type {regions.AreaOf(ak).Type} hex type {grid.Types[h]} x {h % grid.Width} tile group {ppd.TileGroup(x, y)} of {ppd.TileGroups.Count}";
                        stats[k] = stats.GetValueOrDefault(k) + 1;
                    }
                    foreach (var (k, v) in stats.OrderBy(p => p.Key)) Console.WriteLine($"  {v,8} {k}");
                    var hl = new Dictionary<int, HashSet<int>>();
                    for (var h = 0; h < grid.Width * grid.Height; h++)
                    {
                        if (piece[h] < 0) continue;
                        var tg = ppd.TileGroup(h % grid.Width, h / grid.Width);
                        var hlci = tg < ppd.TileGroups.Count ? ppd.TileGroups[tg].Hlci : -1;
                        if (!hl.TryGetValue(piece[h], out var set)) hl[piece[h]] = set = [];
                        set.Add(hlci);
                    }
                    Console.WriteLine($"  pieces {pc}; HLCIs per piece: {string.Join(" ", hl.OrderBy(p => p.Key).Select(p => $"{p.Key}:[{string.Join(",", p.Value.Take(6))}{(p.Value.Count > 6 ? $"..+{p.Value.Count - 6}" : "")}]"))}");
                }
                if (a.Contains("--area-diffs"))
                {
                    var byKind = new Dictionary<string, int>();
                    for (var y = refSpd.Y0; y <= refSpd.Y1; y++)
                    for (var x = refSpd.X0; x <= refSpd.X1; x++)
                    {
                        var c = (y - refSpd.Y0) * refSpd.Width + (x - refSpd.X0);
                        if (spd.Areas[c] == refSpd.Areas[c]) continue;
                        var key = regions.AreaMap[y * grid.Width + x];
                        var area = regions.AreaOf(key);
                        var k = $"area type {area.Type} hex type {grid.Types[y * grid.Width + x]} set {(refSpd.Sets[c] == SpdData.NoSet ? "none" : "yes")} ref {(refSpd.Areas[c].Region == SpdData.None ? "none" : "other")} hexcount {Math.Min(area.HexCount, 9)}";
                        byKind[k] = byKind.GetValueOrDefault(k) + 1;
                    }
                    foreach (var (k, v) in byKind.OrderByDescending(p => p.Value)) Console.WriteLine($"  {v,6} {k}");
                }
                if (Option(a, "--show") is { } show)
                    foreach (var cell in show.Split(';'))
                    {
                        var xy = cell.Split(',').Select(int.Parse).ToArray();
                        uint[] Cell(SpdData d) => Enumerable.Range(0, 16).Select(j => d.Values[(long)((xy[1] - d.Y0) * d.Width + (xy[0] - d.X0)) * SpdData.Stride + j]).ToArray();
                        Console.WriteLine($"  ({xy[0]},{xy[1]}) {string.Join(",", regions.Regions.Where(r => r.PrimarySlot.Contains((xy[0], xy[1]))).Select(r => "primary:" + r.Key).Concat(regions.Regions.Where(r => r.PortSlot.Contains((xy[0], xy[1]))).Select(r => "port:" + r.Key)))} type {grid.Types[xy[1] * grid.Width + xy[0]]} mine {string.Join(" ", Cell(spd).Take(8))} | ref {string.Join(" ", Cell(refSpd).Take(8))}");
                    }
                if (a.Contains("--slot-edges"))
                {
                    var kind = new byte[grid.Width * grid.Height];
                    foreach (var r in regions.Regions)
                    {
                        foreach (var (x, y) in r.PrimarySlot) kind[y * grid.Width + x] |= 1;
                        foreach (var (x, y) in r.PortSlot) kind[y * grid.Width + x] |= 2;
                    }
                    var ppdTypes = ppd.Cells.Where((_, i) => i % 8 == 7).Select(b => (byte)(b >> 4)).ToArray();
                    SpdCompare.SlotEdges(refSpd, grid, kind, ppdTypes, a.Contains("--all-edges"));
                }
            }
        }
        if (only is null or "hlp")
        {
            var t = Stopwatch.StartNew();
            var refPath = compare is null ? null : Path.Combine(compare, "hlp_data.esf");
            var refHlp = refPath is not null && File.Exists(refPath) ? HlpData.Read(refPath) : null;
            var opt = new HlpBuilder.Options
            {
                LegacyStlOrder = a.Contains("--legacy-stl"),
                CentrePathZero = !a.Contains("--centre-blocked"),
                RefineThreshold = float.Parse(Option(a, "--threshold") ?? "0", CultureInfo.InvariantCulture),
                MaxThreads = threads,
                WrapLikeGame = a.Contains("--wrap-like-game"),
            };
            if (Option(a, "--edges") is { } eh) // research: the A* grid's edges around hexes
            {
                var ag = new AiPathGrid(ppd, regions, settings);
                foreach (var cell in eh.Split(';'))
                {
                    var v = cell.Split(',').Select(int.Parse).ToArray();
                    var h = ag.Index(v[0], v[1]);
                    Console.WriteLine($"({v[0]},{v[1]}) type {ag.Types[h]} slot {ag.Slot[h]} area {regions.AreaMap[h] & MapDataRegions.RegionMask},{regions.AreaMap[h] >> MapDataRegions.AreaShift}: " + string.Join("  ", Enumerable.Range(0, 6).Select(d =>
                    {
                        var nb = ag.Neighbour[h * 6 + d];
                        var e = ag.EdgesPlain[h * 6 + d];
                        var nav = (e & 0x80) != 0 ? "nav" : "---";
                        return nb < 0 ? $"d{d} -" : $"d{d}->({nb % ag.Width},{nb / ag.Width}) {nav} {ag.CostTable[e & 0x7F]} [{e:x2}]";
                    })));
                }
            }
            var hlp = HlpBuilder.Build(ppd, regions, settings, refHlp?.Timestamp ?? ts, Console.WriteLine, opt);
            if (refHlp is not null) hlp.Magic = refHlp.Magic;
            if (refHlp is not null && a.Contains("--categories")) Console.WriteLine(HlpCompare.AreaCategories(hlp, refHlp, 12));
            if (refHlp is not null && a.Contains("--tables-from-ref")) // research: the region tables from CA's own transitions
            {
                var copy = HlpData.Read(refPath!);
                HlpRegionTables.Fill(copy, wrapLikeGame: true);
                Console.WriteLine("from CA's transitions: " + HlpCompare.RegionTables(copy, refHlp));
                var shown = 0;
                for (var i = 0; i < copy.RegionCosts.Length && shown < 12; i++)
                    if (copy.RegionCosts[i] != refHlp.RegionCosts[i])
                    {
                        shown++;
                        Console.WriteLine($"  ({i / 1024},{i % 1024}) mine {copy.RegionCosts[i]} ref {refHlp.RegionCosts[i]} hops mine {copy.RegionHops[i]} ref {refHlp.RegionHops[i]}");
                    }
                var byRow = Enumerable.Range(0, 1024).Select(r => Enumerable.Range(0, 1024).Count(c => copy.RegionCosts[r * 1024 + c] != refHlp.RegionCosts[r * 1024 + c])).ToArray();
                Console.WriteLine($"  rows with mismatches: {byRow.Count(x => x > 0)}; worst {string.Join(" ", byRow.Select((x, r) => (x, r)).OrderByDescending(p => p.x).Take(8).Select(p => $"{p.r}:{p.x}"))}");
                Console.WriteLine($"  columns with mismatches: {string.Join(" ", Enumerable.Range(0, 1024).Select(c => (Enumerable.Range(0, 1024).Count(r => copy.RegionCosts[r * 1024 + c] != refHlp.RegionCosts[r * 1024 + c]), c)).OrderByDescending(p => p.Item1).Take(8).Select(p => $"{p.c}:{p.Item1}"))}");
            }
            var bytes = hlp.ToBytes();
            Console.WriteLine($"hlp: {hlp.Nodes.Count} nodes, {bytes.Length:N0} bytes in {t.Elapsed.TotalSeconds:F2} s; peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 >> 20} MB");
            if (outDir is not null) { Directory.CreateDirectory(outDir); File.WriteAllBytes(Path.Combine(outDir, "hlp_data.esf"), bytes); }
            if (refHlp is not null)
            {
                var same = File.ReadAllBytes(refPath!).AsSpan().SequenceEqual(bytes);
                Console.WriteLine($"hlp vs reference: {(same ? "byte-identical" : "DIFFERENT")}");
                Console.WriteLine(HlpCompare.Report(hlp, refHlp, a.Contains("--verbose") ? 40 : 8));
                if (!same) exit = 1;
            }
        }
        return exit;
    }
}

/// <summary>Field-level comparison of two spd_data.esf files.</summary>
public static class SpdCompare
{
    /// <summary>Research: the edge costs a reference spd implies. On every edge nb→h, value(h) − value(nb) ≤ cost, with
    /// equality on the shortest-path tree; so the largest difference per (cost index, direction) is the cost.</summary>
    public static void EdgeCosts(SpdData reference, CampaignPathGrid g)
    {
        var max = new Dictionary<(int Index, int Dir), (long Max, long Count, uint Table)>();
        for (var y = reference.Y0; y <= reference.Y1; y++)
        for (var x = reference.X0; x <= reference.X1; x++)
        {
            var h = y * g.Width + x;
            var c = (long)((y - reference.Y0) * reference.Width + (x - reference.X0));
            for (var d = 0; d < 6; d++)
            {
                var nb = g.Neighbour[h * 6 + d];
                if (nb < 0) continue;
                int nx = nb % g.Width, ny = nb / g.Width;
                if (nx < reference.X0 || nx > reference.X1 || ny < reference.Y0 || ny > reference.Y1) continue;
                var back = (d + 3) % 6;
                if (g.Forward[nb * 6 + back] == CampaignPathGrid.NoEdge) continue;
                var cn = (long)((ny - reference.Y0) * reference.Width + (nx - reference.X0));
                var key = (g.EdgeBytes[nb * 6 + back] & 0x7F, back);
                for (var j = 0; j < 8; j++)
                {
                    var vh = reference.Values[c * SpdData.Stride + j];
                    var vn = reference.Values[cn * SpdData.Stride + j];
                    if (vh == SpdData.NoPath || vn == SpdData.NoPath) continue;
                    var diff = (long)vh - vn;
                    var cur = max.GetValueOrDefault(key, (long.MinValue, 0, g.Forward[nb * 6 + back]));
                    max[key] = (Math.Max(cur.Max, diff), cur.Count + 1, cur.Table);
                }
            }
        }
        foreach (var ((index, dir), (m, n, table)) in max.OrderBy(k => k.Key.Index).ThenBy(k => k.Key.Dir))
            Console.WriteLine($"  index {index,3} dir {dir}: table {table,6}  implied {m,6}  ({n} samples)");
    }

    /// <summary>Research: per (slot kind of h, ppd type of h, slot kind of nb, ppd type of nb) the largest
    /// value(nb) − value(h) over the reference's set values: about 0 = a free edge h→nb, the table cost = a normal edge,
    /// much more = no edge (kind: 1 primary slot, 2 port slot, 3 both).</summary>
    public static void SlotEdges(SpdData reference, CampaignPathGrid g, byte[] kind, byte[] ppdTypes, bool all = false)
    {
        var stats = new Dictionary<(int, int, int, int), (long Max, long Min, long N, long Table)>();
        for (var y = reference.Y0; y <= reference.Y1; y++)
        for (var x = reference.X0; x <= reference.X1; x++)
        {
            var h = y * g.Width + x;
            var c = (long)((y - reference.Y0) * reference.Width + (x - reference.X0));
            for (var d = 0; d < 6; d++)
            {
                var nb = g.Neighbour[h * 6 + d];
                if (nb < 0 || !all && kind[h] == 0 && kind[nb] == 0) continue;
                int nx = nb % g.Width, ny = nb / g.Width;
                if (nx < reference.X0 || nx > reference.X1 || ny < reference.Y0 || ny > reference.Y1) continue;
                var cn = (long)((ny - reference.Y0) * reference.Width + (nx - reference.X0));
                var key = (kind[h], ppdTypes[h], kind[nb], ppdTypes[nb]);
                for (var j = 0; j < 8; j++)
                {
                    var vh = reference.Values[c * SpdData.Stride + j];
                    var vn = reference.Values[cn * SpdData.Stride + j];
                    if (vh == SpdData.NoPath || vn == SpdData.NoPath) continue;
                    var diff = (long)vn - vh;
                    var cur = stats.GetValueOrDefault(key, (long.MinValue, long.MaxValue, 0, (long)g.Forward[h * 6 + d]));
                    stats[key] = (Math.Max(cur.Max, diff), Math.Min(cur.Min, diff), cur.N + 1, cur.Table);
                }
            }
        }
        foreach (var (k, v) in stats.OrderBy(k => k.Key))
            Console.WriteLine($"  slot {k.Item1} type {k.Item2} -> slot {k.Item3} type {k.Item4}: max diff {v.Max,6} min {v.Min,7} n {v.N,7}  (my edge {(v.Table == CampaignPathGrid.NoEdge ? "none" : v.Table.ToString())})");
    }

    /// <summary>Research: the cheapest (by the reference) cells whose value <paramref name="slot"/> differs, with their
    /// neighbours' values and the grid's edge costs into them.</summary>
    public static void FirstDiffs(SpdData mine, SpdData reference, CampaignPathGrid g, int slot, int count, Func<int, int, string>? note = null)
    {
        uint V(SpdData d, int x, int y) => x < d.X0 || x > d.X1 || y < d.Y0 || y > d.Y1 ? 0xEEEEEEEE
            : d.Values[(long)((y - d.Y0) * d.Width + (x - d.X0)) * SpdData.Stride + slot];
        var diffs = new List<(uint R, int X, int Y)>();
        for (var y = reference.Y0; y <= reference.Y1; y++)
        for (var x = reference.X0; x <= reference.X1; x++)
            if (V(mine, x, y) != V(reference, x, y)) diffs.Add((V(reference, x, y), x, y));
        foreach (var (r, x, y) in diffs.OrderBy(d => d.R).Take(count))
        {
            var h = y * g.Width + x;
            Console.WriteLine($"  ({x},{y}) type {g.Types[h]} mine {V(mine, x, y)} ref {r} {note?.Invoke(x, y)}");
            for (var d = 0; d < 6; d++)
            {
                var nb = g.Neighbour[h * 6 + d];
                if (nb < 0) continue;
                int nx = nb % g.Width, ny = nb / g.Width;
                var back = (d + 3) % 6;
                Console.WriteLine($"     d{d} ({nx},{ny}) {note?.Invoke(nx, ny)} type {g.Types[nb]} mine {V(mine, nx, ny)} ref {V(reference, nx, ny)}  nb->h {g.Forward[nb * 6 + back]} (edge {g.EdgeBytes[nb * 6 + back]:x2})  h->nb {g.Forward[h * 6 + d]} (edge {g.EdgeBytes[h * 6 + d]:x2})");
            }
            for (var l = g.LinkStart[h]; l < g.LinkStart[h + 1]; l++) Console.WriteLine($"     bridge to ({g.Links[l] % g.Width},{g.Links[l] / g.Width})");
        }
    }

    public static string Report(SpdData mine, SpdData reference, int examples)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  box mine {mine.X0},{mine.Y0}-{mine.X1},{mine.Y1} ref {reference.X0},{reference.Y0}-{reference.X1},{reference.Y1}");
        sb.AppendLine($"  sets mine {mine.SetCount} ref {reference.SetCount}; landmarks equal {mine.SetLandmarks.Zip(reference.SetLandmarks).Count(p => p.First == p.Second)} of {reference.SetLandmarks.Count}");
        if (examples > 8)
            for (var i = 0; i < Math.Max(mine.SetCount, reference.SetCount); i++)
                sb.AppendLine($"    set {i}: mine {string.Join(" ", mine.SetLandmarks.Skip(8 * i).Take(8))} ref {string.Join(" ", reference.SetLandmarks.Skip(8 * i).Take(8))}");
        var refAreas = reference.AreaLandmarks.ToDictionary(x => x.Area, x => x.Landmarks);
        int areaOrder = mine.AreaLandmarks.Zip(reference.AreaLandmarks).Count(p => p.First.Area == p.Second.Area), areaLm = 0, areaShown = 0;
        foreach (var (area, lm) in mine.AreaLandmarks)
            if (refAreas.TryGetValue(area, out var r))
            {
                if (lm.SequenceEqual(r)) areaLm++;
                else if (areaShown++ < examples) sb.AppendLine($"    area {area}: mine {string.Join(" ", lm)} ref {string.Join(" ", r)}");
            }
        sb.AppendLine($"  areas mine {mine.AreaLandmarks.Count} ref {reference.AreaLandmarks.Count}; same order {areaOrder}; same landmarks {areaLm}");
        long cells = 0, set = 0, areaSame = 0, g = 0, ar = 0, gTotal = 0, arTotal = 0;
        var shown = 0;
        for (var y = reference.Y0; y <= reference.Y1; y++)
        for (var x = reference.X0; x <= reference.X1; x++)
        {
            var rc = (y - reference.Y0) * reference.Width + (x - reference.X0);
            cells++;
            if (x < mine.X0 || x > mine.X1 || y < mine.Y0 || y > mine.Y1) continue;
            var mc = (y - mine.Y0) * mine.Width + (x - mine.X0);
            if (mine.Sets[mc] == reference.Sets[rc]) set++;
            else if (shown++ < examples) sb.AppendLine($"    ({x},{y}) set mine {mine.Sets[mc]} ref {reference.Sets[rc]}");
            if (mine.Areas[mc] == reference.Areas[rc]) areaSame++;
            else if (shown++ < examples) sb.AppendLine($"    ({x},{y}) area mine {mine.Areas[mc]} ref {reference.Areas[rc]}");
            for (var j = 0; j < SpdData.Stride; j++)
            {
                var mv = mine.Values[(long)mc * SpdData.Stride + j];
                var rv = reference.Values[(long)rc * SpdData.Stride + j];
                if (j < 8) { gTotal++; if (mv == rv) g++; } else { arTotal++; if (mv == rv) ar++; }
                if (mv != rv && shown++ < examples) sb.AppendLine($"    ({x},{y}) value {j}: mine {mv} ref {rv}");
            }
        }
        sb.AppendLine($"  cells {cells}: set {set}, area {areaSame}, set costs {g}/{gTotal}, area costs {ar}/{arTotal}");
        return sb.ToString();
    }
}

/// <summary>Field-level comparison of two hlp_data.esf files.</summary>
public static class HlpCompare
{
    /// <summary>Research: why each non-identical area differs (first failing check).</summary>
    public static string AreaCategories(HlpData mine, HlpData reference, int examples)
    {
        var refAreas = reference.Nodes.SelectMany(n => n.Areas).ToDictionary(a => a.Area);
        var cats = new Dictionary<string, List<string>>();
        foreach (var a in mine.Nodes.SelectMany(n => n.Areas))
        {
            var r = refAreas[a.Area];
            string? cat = null;
            var mk = a.Transitions.Select(t => (t.X, t.Y, t.OtherX, t.OtherY, t.Target.Region, t.Target.Area)).ToList();
            var rk = r.Transitions.Select(t => (t.X, t.Y, t.OtherX, t.OtherY, t.Target.Region, t.Target.Area)).ToList();
            if (mk.Count != rk.Count) cat = $"transition count ({mk.Count} vs {rk.Count})";
            else if (!mk.Order().SequenceEqual(rk.Order())) cat = "transition hexes";
            else if (!a.Transitions.Select(t => (t.X, t.Y, t.OtherX, t.OtherY, t.Target.Region, t.Target.Area, t.Cost)).Order().SequenceEqual(r.Transitions.Select(t => (t.X, t.Y, t.OtherX, t.OtherY, t.Target.Region, t.Target.Area, t.Cost)).Order()))
                cat = a.Transitions.Zip(r.Transitions).Any(p => p.First.Flag1 && p.First.Cost != p.Second.Cost) ? "cost (land-sea)" : "cost (same medium)";
            else if (!a.Transitions.Select(t => (t.X, t.Y, t.Flag1, t.Flag2)).Order().SequenceEqual(r.Transitions.Select(t => (t.X, t.Y, t.Flag1, t.Flag2)).Order())) cat = "flags";
            else if (!mk.SequenceEqual(rk)) cat = "order only";
            else if (!a.Transitions.Select(t => t.Index).SequenceEqual(r.Transitions.Select(t => t.Index))) cat = "index";
            else if (!a.Costs.SequenceEqual(r.Costs)) cat = "matrix";
            else if (a.B != r.B) cat = "b";
            if (cat is null) continue;
            if (!cats.TryGetValue(cat, out var l)) cats[cat] = l = [];
            l.Add(a.Area.ToString());
        }
        return string.Join(Environment.NewLine, cats.OrderByDescending(c => c.Value.Count).Select(c => $"  {c.Value.Count,5} {c.Key}: {string.Join(" ", c.Value.Take(examples))}"));
    }

    public static string RegionTables(HlpData mine, HlpData reference)
    {
        int cost = 0, costAll = 0, hops = 0, hopsAll = 0;
        for (var i = 0; i < mine.RegionCosts.Length; i++)
        {
            if (reference.RegionCosts[i] != HlpData.NoRegionCost || mine.RegionCosts[i] != HlpData.NoRegionCost)
            {
                costAll++;
                if (mine.RegionCosts[i] == reference.RegionCosts[i]) cost++;
            }
            if (reference.RegionHops[i] != 0 || mine.RegionHops[i] != 0)
            {
                hopsAll++;
                if (mine.RegionHops[i] == reference.RegionHops[i]) hops++;
            }
        }
        return $"region table: costs {cost}/{costAll}, hops {hops}/{hopsAll}, max cost mine {mine.MaxRegionCost} ref {reference.MaxRegionCost}";
    }

    public static string Report(HlpData mine, HlpData reference, int examples)
    {
        var regionLine = RegionTables(mine, reference);

        var sb = new System.Text.StringBuilder();
        int nodes = 0, areas = 0, areaSame = 0, centre = 0, a = 0, b = 0, trTotal = 0, trPq = 0, trCost = 0, trAll = 0, order = 0, mat = 0, matTotal = 0;
        var refAreas = reference.Nodes.SelectMany(n => n.Areas).ToDictionary(x => x.Area);
        var shown = 0;
        var costDiffs = new List<string>();
        var flagDiffs = new List<string>();
        if (mine.Nodes.Count == reference.Nodes.Count) nodes = 1;
        foreach (var ma in mine.Nodes.SelectMany(n => n.Areas))
        {
            if (!refAreas.TryGetValue(ma.Area, out var ra)) continue;
            areas++;
            centre += ma.CentreX == ra.CentreX && ma.CentreY == ra.CentreY ? 1 : 0;
            a += ma.A == ra.A ? 1 : 0;
            b += ma.B == ra.B ? 1 : 0;
            trTotal += ra.Transitions.Count;
            var mineByPq = ma.Transitions.GroupBy(t => (t.X, t.Y, t.OtherX, t.OtherY, t.Target.Region, t.Target.Area)).ToDictionary(g => g.Key, g => g.First());
            foreach (var t in ra.Transitions)
                if (mineByPq.TryGetValue((t.X, t.Y, t.OtherX, t.OtherY, t.Target.Region, t.Target.Area), out var m))
                {
                    trPq++;
                    if (m.Cost == t.Cost) trCost++;
                    else if (costDiffs.Count < examples * 3) costDiffs.Add($"  cost area {ma.Area} ({t.X},{t.Y})->({t.OtherX},{t.OtherY}) to {t.Target}: mine {m.Cost} ref {t.Cost} f{(t.Flag1 ? 1 : 0)}{(t.Flag2 ? 1 : 0)}");
                    if (m.Flag2 != t.Flag2 && flagDiffs.Count < examples * 3) flagDiffs.Add($"  f2 area {ma.Area} ({t.X},{t.Y})->({t.OtherX},{t.OtherY}) to {t.Target}: mine {m.Flag2} ref {t.Flag2} cost {t.Cost}");
                    if (m == t) trAll++;
                }
            var sameList = ma.Transitions.SequenceEqual(ra.Transitions);
            order += sameList ? 1 : 0;
            if (sameList)
            {
                matTotal += ra.Costs.Count;
                mat += ma.Costs.Zip(ra.Costs).Count(z => z.First == z.Second);
            }
            var whole = sameList && ma.Costs.SequenceEqual(ra.Costs) && ma.B == ra.B && ma.A == ra.A && ma.CentreX == ra.CentreX && ma.CentreY == ra.CentreY;
            areaSame += whole ? 1 : 0;
            if (!whole && shown < examples)
            {
                shown++;
                sb.AppendLine($"  area {ma.Area}: centre ({ma.CentreX},{ma.CentreY}) ref ({ra.CentreX},{ra.CentreY}) a {ma.A}/{ra.A} b {ma.B}/{ra.B}");
                foreach (var t in ra.Transitions) sb.AppendLine($"    ref  {Fmt(t)}{(ma.Transitions.Contains(t) ? "" : "  <<")}");
                foreach (var t in ma.Transitions) sb.AppendLine($"    mine {Fmt(t)}{(ra.Transitions.Contains(t) ? "" : "  <<")}");
                if (sameList && !ma.Costs.SequenceEqual(ra.Costs))
                    sb.AppendLine($"    matrix mine [{string.Join(",", ma.Costs)}] ref [{string.Join(",", ra.Costs)}]");
            }
        }
        foreach (var l in costDiffs.Concat(flagDiffs)) sb.AppendLine(l);
        sb.Insert(0, $"hlp fields: nodes {(nodes == 1 ? "same count" : "DIFFERENT count")}, areas {areas}/{refAreas.Count}, identical areas {areaSame}, centre {centre}, a {a}, b {b}, " +
                     $"transitions {trTotal}: same hexes+target {trPq}, +cost {trCost}, all fields {trAll}; same transition list {order}; matrix values {mat}/{matTotal}\n");
        sb.AppendLine(regionLine);
        return sb.ToString();
    }

    private static string Fmt(HlpData.HlpTransition t) =>
        $"({t.X},{t.Y})->({t.OtherX},{t.OtherY}) cost {t.Cost} to {t.Target} idx {t.Index} f {(t.Flag1 ? 1 : 0)}{(t.Flag2 ? 1 : 0)}";
}
