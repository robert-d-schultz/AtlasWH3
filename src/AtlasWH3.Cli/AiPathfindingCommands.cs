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
/// </summary>
static class AiPathfindingCommands
{
    public static readonly HashSet<string> Names = ["hlp-spd"];

    static string? Option(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        var inDir = Option(a, "--in") ?? paths.AkWorkingCampaignMapDir;
        var outDir = Option(a, "--out");
        var compare = Option(a, "--compare");
        var only = Option(a, "--only");
        var threads = int.Parse(Option(a, "--threads") ?? "0");
        var sw = Stopwatch.StartNew();
        var ppd = PathfindingPpd.Read(Path.Combine(inDir, "pathfinding.ppd"));
        var regions = MapDataRegions.Read(Path.Combine(inDir, "map_data.esf"));
        var settings = AiPathfindingStep.DbSettings(paths);
        Console.WriteLine($"read {ppd.Width}x{ppd.Height}, {regions.Regions.Count} regions in {sw.Elapsed.TotalSeconds:F2} s; road {settings.RoadCost} beach {settings.LandToSeaCost}/{settings.SeaToLandCost}");
        var ts = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exit = 0;
        if (only is null or "spd")
        {
            var t = Stopwatch.StartNew();
            var grid = new CampaignPathGrid(ppd, regions, settings);
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
            var spd = SpdBuilder.Build(grid, refSpd?.Timestamp ?? ts, maxThreads: threads);
            var bytes = spd.ToBytes();
            Console.WriteLine($"spd: {spd.Width}x{spd.Height} cells, {bytes.Length:N0} bytes in {t.Elapsed.TotalSeconds:F2} s");
            if (outDir is not null) { Directory.CreateDirectory(outDir); File.WriteAllBytes(Path.Combine(outDir, "spd_data.esf"), bytes); }
            if (refPath is not null && File.Exists(refPath))
            {
                var same = File.ReadAllBytes(refPath).AsSpan().SequenceEqual(bytes);
                Console.WriteLine($"spd vs reference: {(same ? "byte-identical" : "DIFFERENT")}");
                if (!same) exit = 1;
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
            };
            var hlp = HlpBuilder.Build(ppd, regions, settings, refHlp?.Timestamp ?? ts, Console.WriteLine, opt);
            var bytes = hlp.ToBytes();
            Console.WriteLine($"hlp: {hlp.Nodes.Count} nodes, {bytes.Length:N0} bytes in {t.Elapsed.TotalSeconds:F2} s");
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

/// <summary>Field-level comparison of two hlp_data.esf files.</summary>
public static class HlpCompare
{
    public static string Report(HlpData mine, HlpData reference, int examples)
    {
        var sb = new System.Text.StringBuilder();
        int nodes = 0, areas = 0, areaSame = 0, centre = 0, a = 0, b = 0, trTotal = 0, trPq = 0, trCost = 0, trAll = 0, order = 0, mat = 0, matTotal = 0;
        var refAreas = reference.Nodes.SelectMany(n => n.Areas).ToDictionary(x => x.AreaId);
        var shown = 0;
        var costDiffs = new List<string>();
        var flagDiffs = new List<string>();
        if (mine.Nodes.Count == reference.Nodes.Count) nodes = 1;
        foreach (var ma in mine.Nodes.SelectMany(n => n.Areas))
        {
            if (!refAreas.TryGetValue(ma.AreaId, out var ra)) continue;
            areas++;
            centre += ma.CentreX == ra.CentreX && ma.CentreY == ra.CentreY ? 1 : 0;
            a += ma.A == ra.A ? 1 : 0;
            b += ma.B == ra.B ? 1 : 0;
            trTotal += ra.Transitions.Count;
            var mineByPq = ma.Transitions.GroupBy(t => (t.X, t.Y, t.OtherX, t.OtherY, t.TargetArea)).ToDictionary(g => g.Key, g => g.First());
            foreach (var t in ra.Transitions)
                if (mineByPq.TryGetValue((t.X, t.Y, t.OtherX, t.OtherY, t.TargetArea), out var m))
                {
                    trPq++;
                    if (m.Cost == t.Cost) trCost++;
                    else if (costDiffs.Count < examples * 3) costDiffs.Add($"  cost area {ma.AreaId} ({t.X},{t.Y})->({t.OtherX},{t.OtherY}) to {t.TargetArea}: mine {m.Cost} ref {t.Cost} f{(t.Flag1 ? 1 : 0)}{(t.Flag2 ? 1 : 0)}");
                    if (m.Flag2 != t.Flag2 && flagDiffs.Count < examples * 3) flagDiffs.Add($"  f2 area {ma.AreaId} ({t.X},{t.Y})->({t.OtherX},{t.OtherY}) to {t.TargetArea}: mine {m.Flag2} ref {t.Flag2} cost {t.Cost}");
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
                sb.AppendLine($"  area {ma.AreaId}: centre ({ma.CentreX},{ma.CentreY}) ref ({ra.CentreX},{ra.CentreY}) a {ma.A}/{ra.A} b {ma.B}/{ra.B}");
                foreach (var t in ra.Transitions) sb.AppendLine($"    ref  {Fmt(t)}{(ma.Transitions.Contains(t) ? "" : "  <<")}");
                foreach (var t in ma.Transitions) sb.AppendLine($"    mine {Fmt(t)}{(ra.Transitions.Contains(t) ? "" : "  <<")}");
                if (sameList && !ma.Costs.SequenceEqual(ra.Costs))
                    sb.AppendLine($"    matrix mine [{string.Join(",", ma.Costs)}] ref [{string.Join(",", ra.Costs)}]");
            }
        }
        foreach (var l in costDiffs.Concat(flagDiffs)) sb.AppendLine(l);
        sb.Insert(0, $"hlp fields: nodes {(nodes == 1 ? "same count" : "DIFFERENT count")}, areas {areas}/{refAreas.Count}, identical areas {areaSame}, centre {centre}, a {a}, b {b}, " +
                     $"transitions {trTotal}: same hexes+target {trPq}, +cost {trCost}, all fields {trAll}; same transition list {order}; matrix values {mat}/{matTotal}\n");
        return sb.ToString();
    }

    private static string Fmt(HlpData.HlpTransition t) =>
        $"({t.X},{t.Y})->({t.OtherX},{t.OtherY}) cost {t.Cost} to {t.TargetArea} idx {t.Index} f {(t.Flag1 ? 1 : 0)}{(t.Flag2 ? 1 : 0)}";
}
