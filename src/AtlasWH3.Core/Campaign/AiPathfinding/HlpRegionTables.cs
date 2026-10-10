using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// WH3 hlp_data.esf's region tables, computed from its transition data: per pair of regions (from &lt; to) the cheapest
/// way from one to the other over the transitions, and the number of region changes on it.
///  - States are (area, transition). A search from region r starts at every transition of r's areas at cost 0.
///  - Crossing transition t costs t.Cost and lands on the target area's transitions whose inside hex is t's outside hex;
///    moving on to another transition of that area costs the area's matrix value between the two.
///  - The cost of (r, s) is the cheapest crossing into any area of s; its hop count is the number of crossings into
///    another region on that path (crossings between areas of one region do not count); among equal costs the fewer hops.
///  - Upper triangle only. Pairs of regions that both have areas but no path: 0xFFFFFFFF and 255 hops; pairs with a
///    region without areas, the diagonal and the lower triangle of the costs: <see cref="HlpData.NoRegionCost"/> and 0.
///    The hop table is symmetric. Regions from <see cref="HlpData.RegionTableSize"/> on have no row.
///  - OTHER_CONSTANTS: the largest cost in the table.
/// Prologue map: all 231 pairs; combi map 1: 99.95 % of costs, 97 % of hop counts (the rest: equal-cost paths the game
/// settles in another order, and paths into region 545).
/// </summary>
public static class HlpRegionTables
{
    public const uint Unreachable = 0xFFFFFFFF;
    public const byte UnreachableHops = 255;

    public static void Fill(HlpData hlp, int maxThreads = 0)
    {
        const int size = HlpData.RegionTableSize;
        var areas = new Dictionary<RegionArea, HlpData.HlpArea>();
        foreach (var node in hlp.Nodes)
            foreach (var a in node.Areas) areas[a.Area] = a;
        var states = new Dictionary<(RegionArea, int), int>();
        var stateArea = new List<HlpData.HlpArea>();
        var stateIndex = new List<int>();
        foreach (var a in areas.Values)
            for (var i = 0; i < a.Transitions.Count; i++)
            {
                states[(a.Area, i)] = stateArea.Count;
                stateArea.Add(a);
                stateIndex.Add(i);
            }
        // the target area's transitions at each transition's outside hex
        var landing = new int[stateArea.Count][];
        for (var s = 0; s < stateArea.Count; s++)
        {
            var t = stateArea[s].Transitions[stateIndex[s]];
            landing[s] = areas.TryGetValue(t.Target, out var b)
                ? Enumerable.Range(0, b.Transitions.Count).Where(j => b.Transitions[j].X == t.OtherX && b.Transitions[j].Y == t.OtherY)
                    .Select(j => states[(b.Area, j)]).ToArray()
                : [];
        }
        var regions = areas.Keys.Select(a => (int)a.Region).Where(r => r < size).Distinct().Order().ToArray();
        var costs = HlpData.NewRegionCosts();
        var hops = new byte[size * size];
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : Environment.ProcessorCount };
        Parallel.ForEach(regions, po, r =>
        {
            var best = Search(r, stateArea, stateIndex, landing, states);
            foreach (var s in regions)
            {
                if (s == r) continue;
                var (c, h) = best.TryGetValue(s, out var b) ? b : (Unreachable, UnreachableHops);
                if (r < s) costs[r * size + s] = (uint)c;
                hops[r * size + s] = (byte)Math.Min(h, UnreachableHops);
            }
        });
        // the hop table is symmetric: the upper triangle's value (from the lower region's search) wins
        for (var r = 0; r < size; r++)
            for (var s = r + 1; s < size; s++) hops[s * size + r] = hops[r * size + s];
        hlp.RegionCosts = costs;
        hlp.RegionHops = hops;
        hlp.MaxRegionCost = costs.Where(c => c is not (HlpData.NoRegionCost or Unreachable)).DefaultIfEmpty(0u).Max();
    }

    private static Dictionary<int, (long Cost, int Hops)> Search(int region, List<HlpData.HlpArea> stateArea, List<int> stateIndex,
                                                                  int[][] landing, Dictionary<(RegionArea, int), int> states)
    {
        var best = new Dictionary<int, (long, int)>();
        var done = new bool[stateArea.Count];
        var queue = new PriorityQueue<int, (long, int)>();
        for (var s = 0; s < stateArea.Count; s++)
            if (stateArea[s].Area.Region == region) queue.Enqueue(s, (0, 0));
        while (queue.TryDequeue(out var s, out var key))
        {
            if (done[s]) continue;
            done[s] = true;
            var (c, h) = key;
            var area = stateArea[s];
            var t = area.Transitions[stateIndex[s]];
            var nc = c + t.Cost;
            var nh = h + (t.Target.Region != area.Area.Region ? 1 : 0);
            if (t.Target.Region != region && (!best.TryGetValue(t.Target.Region, out var b) || (nc, nh).CompareTo(b) < 0))
                best[t.Target.Region] = (nc, nh);
            foreach (var j0 in landing[s])
            {
                var target = stateArea[j0];
                var i0 = stateIndex[j0];
                for (var j = 0; j < target.Transitions.Count; j++)
                {
                    var next = states[(target.Area, j)];
                    if (done[next]) continue;
                    queue.Enqueue(next, (j == i0 ? nc : nc + Matrix(target, i0, j), nh));
                }
            }
        }
        return best;
    }

    /// <summary>The area's cost between transitions i and j (file order): rows and columns in creation (index) order,
    /// row-major without the diagonal.</summary>
    private static uint Matrix(HlpData.HlpArea a, int i, int j)
    {
        var m = a.Transitions.Count;
        int ii = a.Transitions[i].Index, jj = a.Transitions[j].Index;
        return a.Costs[ii * (m - 1) + (jj < ii ? jj : jj - 1)];
    }
}
