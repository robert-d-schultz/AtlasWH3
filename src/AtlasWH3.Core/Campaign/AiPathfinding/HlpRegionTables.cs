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
///  - Costs: upper triangle only. Pairs of regions that both have areas but no path: 0xFFFFFFFF; pairs with a region
///    without areas, the diagonal and the lower triangle: <see cref="HlpData.NoRegionCost"/>.
///  - Hops: the upper triangle [r, s] holds the hops above; the lower triangle [s, r] the hops of the cheapest path
///    without land-sea transitions (flag 1); 255 without a path, 0 for a region without areas and on the diagonal
///    (combi map 1, from CA's own transitions: 99.3 % of the lower triangle). Regions from
///    <see cref="HlpData.RegionTableSize"/> on have no row.
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
            var best = Search(r, stateArea, stateIndex, landing, states, false);
            var sameMedium = Search(r, stateArea, stateIndex, landing, states, true);
            foreach (var s in regions)
            {
                if (s <= r) continue;
                var (c, h) = best.TryGetValue(s, out var b) ? b : (Unreachable, UnreachableHops);
                costs[r * size + s] = (uint)c;
                hops[r * size + s] = (byte)Math.Min(h, UnreachableHops);
                hops[s * size + r] = (byte)Math.Min(sameMedium.TryGetValue(s, out var m) ? m.Hops : UnreachableHops, UnreachableHops);
            }
        });
        hlp.RegionCosts = costs;
        hlp.RegionHops = hops;
        hlp.MaxRegionCost = costs.Where(c => c is not (HlpData.NoRegionCost or Unreachable)).DefaultIfEmpty(0u).Max();
    }

    /// <summary>Cheapest (cost, region changes) from <paramref name="region"/> to every region; with
    /// <paramref name="sameMedium"/> the land-sea transitions (flag 1) are left out.</summary>
    private static Dictionary<int, (long Cost, int Hops)> Search(int region, List<HlpData.HlpArea> stateArea, List<int> stateIndex,
                                                                  int[][] landing, Dictionary<(RegionArea, int), int> states, bool sameMedium)
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
            if (sameMedium && t.Flag1) continue;
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
