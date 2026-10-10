using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// Builds hlp_data.esf (CAI_TRANSITION_DATA) the way EMPIRECAMPAIGNAI::CAI_REGION_BORDER_ANALYSER does
/// (empirecampaign FUN_18059fbb0, determine_transition_points FUN_1805a03b0, compute_distances FUN_180597b80):
///  1. One node per region slot; per region the areas of type 0 (land), 3 and 4 (sea); the centre is the settlement
///     when it lies in the area, else the area centre from map_data.esf.
///  2. Per area a cost search from the centre (FIND_REGION_BORDER, setup with the centre's HLCI): b = the largest cost
///     of a land/sea hex of the area; land/sea hexes of other areas are collected per neighbouring area (a border
///     segment, in order of discovery) and not expanded.
///  3. Each pair of segments (A→B, B→A), in area order: a path between the two centres (line-tiebroken search refined
///     by CAMPAIGN_PATHFINDER::refine_path); its last hex in B's segment and first hex in A's segment are the first
///     transition; then clusters of the remaining border hexes (sets ordered by descending x, y) give more transitions,
///     each erasing border hexes closer than 10 hexes. A transition's cost is the path cost between its two hexes.
///     WH3: the path is the start region's faction path (its own settlement open at the slots' 0 cost, foreign ones
///     closed); a land-sea transition takes the cheaper of that and the landmark search's grid
///     (<see cref="CampaignPathGrid"/>), and its flag 2 says whether the path taken goes through a settlement (3K: flag 1
///     and cost 0). A bridge crossing (onto, along and off a run of deck hexes) counts 500 on either path.
///  5. WH3's region tables from the transitions (<see cref="HlpRegionTables"/>).
///  4. Per area the transitions sit in an MSVC unordered_multimap keyed by hex (hash y·1016 + x), whose iteration order
///     is the file order; the matrix holds the path costs between the transitions' inside hexes, searched inside the
///     area itself (other areas of the same region are closed).
/// </summary>
public static class HlpBuilder
{
    // research traces: HLP_DEBUG_PAIR=<area id>,<area id> (centre path, refine steps, border sets), HLP_DEBUG_COST=x,y,x,y
    private static readonly string? DebugPair = Environment.GetEnvironmentVariable("HLP_DEBUG_PAIR");
    private static readonly string? DebugCost = Environment.GetEnvironmentVariable("HLP_DEBUG_COST");

    public sealed record Options
    {
        /// <summary>refine_path tolerance (0 = the game value: the half hex height, compared with squared distances). A segment is kept when no hex of its path is further than this (squared
        /// world units) from the segment's line.</summary>
        public float RefineThreshold { get; init; }
        /// <summary>Settlement handling for the centre-to-centre paths (true = slot edges cost 0, false = slots blocked).</summary>
        public bool CentrePathZero { get; init; } = true;
        /// <summary>Pre-2020 STL (VS2017) unordered_map: insert first, then rehash (dlc04 / 8p files); else VS2019 (rehash first).</summary>
        public bool LegacyStlOrder { get; init; }
        public int MaxThreads { get; init; }
        /// <summary>Transition ends next to the same settlement: path without a faction (settlements passable at cost 0).</summary>
        public bool CostSameSettlementZero { get; init; } = Environment.GetEnvironmentVariable("HLP_SAME_ZERO") != "0";
    }

    private sealed class Seg
    {
        public int Nb;
        public HashSet<int> Border = [];
        public bool Matched;
    }

    private sealed class Tr
    {
        public int P, Q, Target, Idx;
        public uint Cost;
        public bool F1, F2;
    }

    private sealed class Entry
    {
        public int Region, Aid, Centre, Type;
        public uint A, B;
        public List<Seg> Segs = [];
        public List<Tr> Created = [];
        public List<Tr> Ordered = [];
        public List<uint> Matrix = [];
    }

    public static HlpData Build(PathfindingPpd ppd, MapDataRegions regions, CampaignPathGrid.Settings settings, uint timestamp,
                                Action<string>? log = null, Options? options = null)
    {
        options ??= new Options();
        var g = new AiPathGrid(ppd, regions, settings);
        var W = g.Width;
        var entries = new List<Entry>();
        var byRegion = new List<List<Entry>>();
        for (var r = 0; r < regions.Regions.Count; r++)
        {
            var R = regions.Regions[r];
            var list = new List<Entry>();
            for (var i = 0; i < R.Areas.Count; i++)
            {
                var a = R.Areas[i];
                if (a.Type is not (0 or 3 or 4)) continue;
                var aid = MapDataRegions.AreaKey(r, i);
                var centre = a.Centre;
                if (R.Settlement is { } s && regions.AreaMap[s.Y * W + s.X] == aid) centre = s;
                var e = new Entry { Region = r, Aid = aid, Centre = centre.Y * W + centre.X, Type = a.Type, A = (uint)a.Id };
                list.Add(e);
                entries.Add(e);
            }
            byRegion.Add(list);
        }
        var po = new ParallelOptions { MaxDegreeOfParallelism = options.MaxThreads > 0 ? options.MaxThreads : Environment.ProcessorCount };
        var searches = new ThreadLocal<AiSearch>(() => new AiSearch(g), true);

        // phase 1: FIND_REGION_BORDER from each centre
        Parallel.ForEach(entries, po, e =>
        {
            var search = searches.Value!;
            var segIndex = new Dictionary<int, Seg>();
            uint b = 0;
            var edges = g.Gated(true, g.Hlci[e.Centre], 0);
            search.Run(e.Centre, edges, visit: (h, c) =>
            {
                var t = g.Types[h];
                // settlement slot hexes are slottlement types (4, 7-9) in the game's grid: neither land nor sea here
                int area = regions.AreaMap[h];
                if (t > 1 || g.Slot[h]) return AiSearch.Visit.Continue;
                if (area == e.Aid) { if (c > b) b = c; return AiSearch.Visit.Continue; }
                if (!segIndex.TryGetValue(area, out var seg))
                {
                    seg = new Seg { Nb = area };
                    segIndex[area] = seg;
                    e.Segs.Add(seg);
                }
                seg.Border.Add(h);
                return AiSearch.Visit.NoExpand;
            });
            e.B = b;
        });
        log?.Invoke($"hlp: {entries.Count} areas, {entries.Sum(e => e.Segs.Count)} border segments");

        var byAid = entries.ToDictionary(e => e.Aid);
        // WH3: a land-sea transition is costed on the landmark search's grid (CampaignPathGrid: every settlement open
        // at the slots' 0 cost, the type gate, bridges at 500), and its flag 2 says whether that path goes through a
        // settlement; combi map 1: every such cost CA has where the A* path paid the 2,100 beach (1,000, 785, 1,191…)
        var landmarkGrid = new CampaignPathGrid.PairSearch(new CampaignPathGrid(ppd, regions, settings));
        var search0 = new AiSearch(g);
        var blocked = g.Slot;

        // settlement owning each slot hex (FUN_1812040c0 via the slot's tile group)
        var slotOwner = new int[g.Width * g.Height];
        Array.Fill(slotOwner, -1);
        for (var r = 0; r < regions.Regions.Count; r++)
            foreach (var (x, y) in regions.Regions[r].PrimarySlot.Concat(regions.Regions[r].PortSlot))
                if ((uint)x < (uint)g.Width && (uint)y < (uint)g.Height) slotOwner[y * W + x] = r;
        int AdjacentSettlement(int h)
        {
            for (var d = 0; d < 6; d++)
            {
                var nb = g.Neighbour[h * 6 + d];
                if (nb >= 0 && slotOwner[nb] >= 0) return slotOwner[nb];
            }
            return -1;
        }

        // the faction paths run one at a time: one blocked array, the start region's own slots opened for the call
        var ownSlots = new Dictionary<int, List<int>>();
        for (var h = 0; h < slotOwner.Length; h++)
            if (slotOwner[h] >= 0)
            {
                if (!ownSlots.TryGetValue(slotOwner[h], out var l)) ownSlots[slotOwner[h]] = l = [];
                l.Add(h);
            }
        var foreign = (bool[])blocked.Clone();

        // FUN_1805f8910: both ends next to the same settlement -> path without a faction (settlements passable),
        // else the faction path (the start's region), for which foreign settlements are closed
        var viaSlot = false; // the last PathCost's path crossed a settlement slot hex
        uint PathCost(int a, int b)
        {
            viaSlot = false;
            if (a == b) return 0;
            var sa = AdjacentSettlement(a);
            var same = options.CostSameSettlementZero && sa >= 0 && sa == AdjacentSettlement(b);
            // WH3: the faction path has its own settlement open at the slots' 0 cost (combi map 1: Altdorf's bridge
            // transitions cost 500 through its slots), foreign ones closed
            var own = !same;
            var edges = g.Gated(same || own, g.Hlci[a], g.Hlci[b]);
            var mine = own ? ownSlots.GetValueOrDefault(regions.AreaMap[a] & MapDataRegions.RegionMask) : null;
            if (mine is not null) foreach (var h in mine) foreign[h] = false;
            var cost = search0.Run(a, edges, target: b, blocked: same ? null : own ? foreign : blocked);
            if (mine is not null) foreach (var h in mine) foreign[h] = true;
            if (cost == uint.MaxValue) return cost;
            var path = search0.PathTo(b);
            viaSlot = path.Any(h => slotOwner[h] >= 0);
            if (DebugCost is not null && DebugCost == $"{a % W},{a / W},{b % W},{b / W}")
                Console.Error.WriteLine($"cost path same={same} hlci {g.Hlci[a]}->{g.Hlci[b]}: {string.Join(" ", path.Select(h => $"({h % W},{h / W})t{g.Types[h]}c{search0.Cost(h)}"))}");
            return BridgeCost(path, search0.Cost, cost);
        }

        // the reported cost is the sum of the path's waypoint costs (FUN_1805cc840): a bridge crossing counts 500 instead
        // of its steps: onto the first type-5 hex, along the deck (bridge links or neighbouring deck hexes) and off the
        // last one (3K: prev -> deck -> linked deck -> next; Old World: land -> deck -> deck -> sea is 500 as well).
        // A step onto the bridge from a river hex still counts; from land or sea it does not.
        uint BridgeCost(IReadOnlyList<int> path, Func<int, uint> at, uint cost)
        {
            for (var i = 0; i + 1 < path.Count; i++)
            {
                if (g.Types[path[i + 1]] != 5) continue;
                var last = i + 1;
                while (last + 1 < path.Count && g.Types[path[last + 1]] == 5) last++;
                var end = Math.Min(last + 1, path.Count - 1);
                var from = i + (g.Types[path[i]] == 6 ? 1 : 0);
                var steps = at(path[end]) - at(path[from]);
                cost = cost - steps + 500;
                i = end - 1;
            }
            return cost;
        }

        uint GridCost(int a, int b, out bool slot)
        {
            var cost = landmarkGrid.Cost(a, b);
            slot = false;
            if (cost == uint.MaxValue) return cost;
            var path = landmarkGrid.LastPath(b).Reverse().ToList();
            slot = path.Any(h => slotOwner[h] >= 0);
            if (DebugCost is not null && DebugCost == $"{a % W},{a / W},{b % W},{b / W}")
                Console.Error.WriteLine($"grid path: {string.Join(" ", path.Select(h => $"({h % W},{h / W})t{g.Types[h]}c{landmarkGrid.CostTo(h)}"))} -> {BridgeCost(path, landmarkGrid.CostTo, cost)}");
            return BridgeCost(path, landmarkGrid.CostTo, cost);
        }

        var DebugRefine = false;
        List<int>? CentrePath(int a, int b)
        {
            var edges = g.Gated(options.CentrePathZero, g.Hlci[a], g.Hlci[b]);
            var block = options.CentrePathZero ? null : blocked;
            var stack = new List<(int A, int B)> { (a, b) };
            var result = new List<int>();
            while (stack.Count > 0)
            {
                var (sa, sb) = stack[^1];
                var cost = search0.Run(sa, edges, target: sb, blocked: block,
                                       lineFrom: (g.WorldX[sa], g.WorldY[sa]), lineTo: (g.WorldX[sb], g.WorldY[sb]));
                if (cost == uint.MaxValue) return null;
                var path = search0.PathTo(sb);
                float ax = g.WorldX[sa], ay = g.WorldY[sa], fx = g.WorldX[sb] - ax, fy = g.WorldY[sb] - ay;
                var worst = 0f;
                var far = -1;
                foreach (var h in path)
                {
                    var d = LineDistance2(ax, ay, fx, fy, g.WorldX[h], g.WorldY[h]);
                    if (worst < d) { worst = d; far = h; }
                }
                if (DebugRefine) Console.Error.WriteLine($"  seg ({sa % W},{sa / W})->({sb % W},{sb / W}) cost {cost} worst {worst} at ({far % W},{far / W}) path {string.Join(" ", path.Select(h => $"({h % W},{h / W})"))}");
                if (worst <= (options.RefineThreshold > 0 ? options.RefineThreshold : g.HalfRow) || far < 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                    for (var i = 0; i < path.Count - 1; i++) result.Add(path[i]);
                }
                else
                {
                    stack[^1] = (far, sb);
                    stack.Add((sa, far));
                }
            }
            result.Add(b);
            return result;
        }

        int Dist(int a, int b) => SpdBuilder.HexDistance(a % W, a / W, b % W, b / W);

        // sets are ordered by descending (x, y)
        int Desc(int a, int b)
        {
            int ax = a % W, bx = b % W;
            if (ax != bx) return bx.CompareTo(ax);
            return (b / W).CompareTo(a / W);
        }

        int Nearest(IEnumerable<int> set, int pt)
        {
            var best = -1;
            var bd = int.MaxValue;
            foreach (var h in set.OrderBy(h => h, Comparer<int>.Create(Desc)))
            {
                var d = Dist(h, pt);
                if (d < bd) { bd = d; best = h; }
            }
            return best;
        }

        void Emit(Entry e, Entry f, int pa, int qb, bool f1)
        {
            var cab = PathCost(pa, qb);
            var sab = viaSlot;
            var cba = PathCost(qb, pa);
            var sba = viaSlot;
            if (f1)
            {
                // the cheaper of the A* path and the landmark grid's (Old World: land -> bridge deck -> deck -> sea is
                // only on the A* grid, whose navigation bits allow deck-to-deck and deck-to-sea steps)
                var gab = GridCost(pa, qb, out var gsab);
                var gba = GridCost(qb, pa, out var gsba);
                if (gab <= cab) { cab = gab; sab = gsab; }
                if (gba <= cba) { cba = gba; sba = gsba; }
            }
            // flag 2 (WH3): a land-sea transition whose path goes through a settlement (a port); 3K's "cost 0" is the
            // same on the prologue map and 61 areas fewer on combi map 1
            bool F2(uint c, bool slot) => f1 && slot;
            e.Created.Add(new Tr { P = pa, Q = qb, Cost = cab, Target = f.Aid, Idx = e.Created.Count, F1 = f1, F2 = F2(cab, sab) });
            f.Created.Add(new Tr { P = qb, Q = pa, Cost = cba, Target = e.Aid, Idx = f.Created.Count, F1 = f1, F2 = F2(cba, sba) });
        }

        void Determine(Entry e, Seg s, Seg t, Entry f)
        {
            var f1 = (e.Type == 0) != (f.Type == 0);
            DebugRefine = DebugPair is not null && DebugPair == $"{e.Aid},{f.Aid}";
            var path = CentrePath(e.Centre, f.Centre);
            DebugRefine = false;
            int q = -1, p = -1;
            if (path is not null)
                foreach (var h in path)
                {
                    if (t.Border.Contains(h)) p = h;
                    if (q < 0 && s.Border.Contains(h)) q = h;
                }
            var dbg = DebugPair is not null && DebugPair == $"{e.Aid},{f.Aid}";
            string H(int h) => $"({h % W},{h / W})";
            if (dbg) Console.Error.WriteLine($"centre path {string.Join(" ", path?.Select(H) ?? [])} halfrow {g.HalfRow} world {regions.WorldMin} {regions.WorldMax} grid {W}x{g.Height}");
            if (dbg) Console.Error.WriteLine($"pair {e.Aid}->{f.Aid}: path p {(p >= 0 ? H(p) : "-")} q {(q >= 0 ? H(q) : "-")}\n  S(in f, found by e) {string.Join(" ", s.Border.Select(H))}\n  T(in e, found by f) {string.Join(" ", t.Border.Select(H))}");
            var copyA = new HashSet<int>(s.Border);
            var copyB = new HashSet<int>(t.Border);
            if (p >= 0 && q >= 0)
            {
                Emit(e, f, p, q, f1);
                copyA.RemoveWhere(h => Dist(h, q) < 10);
                copyB.RemoveWhere(h => Dist(h, p) < 10);
            }
            var cmp = Comparer<int>.Create(Desc);
            while (copyA.Count + copyB.Count > 0)
            {
                var vec = copyA.OrderBy(h => h, cmp).Concat(copyB.OrderBy(h => h, cmp)).ToList();
                var last = vec[^1];
                var cluster = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(last);
                while (stack.Count > 0)
                {
                    var u = stack.Pop();
                    if (!cluster.Add(u)) continue;
                    foreach (var v in vec)
                        if (!cluster.Contains(v) && Dist(u, v) == 1) stack.Push(v);
                }
                long sx = 0, sy = 0;
                foreach (var h in cluster) { sx += h % W; sy += h / W; }
                var cx = (int)(sx / cluster.Count);
                var cy = (int)(sy / cluster.Count);
                var c = cy * W + cx;
                var bc = Nearest(copyA, c);
                if (dbg) Console.Error.WriteLine($"  cluster last {H(last)} n {cluster.Count} centroid {H(c)}");
                var ac = Nearest(copyB, c);
                var ok = false;
                if (bc >= 0 && ac >= 0)
                {
                    var n150 = Nearest(s.Border, ac);
                    var n14c = Nearest(t.Border, bc);
                    var dba = Dist(bc, ac);
                    ok = dba < 2 * Dist(n150, ac) && dba < 2 * Dist(bc, n14c);
                }
                if (ok)
                {
                    Emit(e, f, ac, bc, f1);
                    copyA.RemoveWhere(h => Dist(h, bc) < 10);
                    copyB.RemoveWhere(h => Dist(h, ac) < 10);
                }
                else
                {
                    copyA.Remove(last);
                    copyB.Remove(last);
                }
            }
        }

        foreach (var e in entries)
            foreach (var s in e.Segs)
            {
                if (s.Matched) continue;
                if (!byAid.TryGetValue(s.Nb, out var f)) continue;
                var t = f.Segs.FirstOrDefault(x => x.Nb == e.Aid);
                if (t is null) continue;
                Determine(e, s, t, f);
                s.Matched = t.Matched = true;
            }
        log?.Invoke($"hlp: {entries.Sum(e => e.Created.Count)} transitions");

        // hash-map order, then the distance matrix between the transitions' inside hexes
        Parallel.ForEach(entries, po, e =>
        {
            e.Ordered = MsvcMultimapOrder(e.Created, tr => (uint)(tr.P / W * 1016 + tr.P % W), tr => tr.P, options.LegacyStlOrder);
            var search = searches.Value!;
            var edges = g.Gated(false, 0, 0);
            // rows and columns in creation (idx) order (CAI_REGION_AREA_TRANSITION_DATA::distance_between_transition_point)
            var byIdx = e.Created;
            var n = byIdx.Count;
            for (var i = 0; i < n; i++)
            {
                var others = new HashSet<int>();
                for (var j = 0; j < n; j++) if (j != i) others.Add(byIdx[j].P);
                var remaining = new HashSet<int>(others);
                if (remaining.Count > 0)
                    search.Run(byIdx[i].P, edges, visit: (h, c) =>
                    {
                        remaining.Remove(h);
                        return remaining.Count == 0 ? AiSearch.Visit.Stop : AiSearch.Visit.Continue;
                    }, blocked: BlockedExcept(blocked, others), regionOf: regions.AreaMap, region: e.Aid, regionMask: -1);
                for (var j = 0; j < n; j++)
                    if (j != i) e.Matrix.Add(byIdx[i].P == byIdx[j].P ? 0 : search.Cost(byIdx[j].P));
            }
        });

        var hlp = new HlpData { Timestamp = timestamp };
        for (var r = 0; r < regions.Regions.Count; r++)
        {
            var node = new HlpData.HlpNode { Id = (ushort)r };
            foreach (var e in byRegion[r])
            {
                var area = new HlpData.HlpArea
                {
                    Area = MapDataRegions.ToRegionArea(e.Aid), CentreX = (ushort)(e.Centre % W), CentreY = (ushort)(e.Centre / W), A = e.A, B = e.B,
                };
                foreach (var t in e.Ordered)
                    area.Transitions.Add(new HlpData.HlpTransition((ushort)(t.P % W), (ushort)(t.P / W), (ushort)(t.Q % W), (ushort)(t.Q / W),
                                                                    t.Cost, MapDataRegions.ToRegionArea(t.Target), (byte)t.Idx, t.F1, t.F2));
                area.Costs.AddRange(e.Matrix);
                node.Areas.Add(area);
            }
            hlp.Nodes.Add(node);
        }
        HlpRegionTables.Fill(hlp, options.MaxThreads);
        return hlp;
    }

    private static bool[] BlockedExcept(bool[] blocked, HashSet<int> allowed)
    {
        // the matrix searches may end on a slot hex (a transition inside a settlement); copy only when needed
        var hit = false;
        foreach (var h in allowed) if (blocked[h]) { hit = true; break; }
        if (!hit) return blocked;
        var b = (bool[])blocked.Clone();
        foreach (var h in allowed) b[h] = false;
        return b;
    }

    /// <summary>Squared distance of (px, py) from the segment (ax, ay) + t·(fx, fy), t clamped to [0, 1]
    /// (CAMPAIGN_PATHFINDER::tiebreaker / refine_path, float32 in the game's order).</summary>
    public static float LineDistance2(float ax, float ay, float fx, float fy, float px, float py)
    {
        var len = MathF.Sqrt(fy * fy + fx * fx);
        float t;
        if (len <= 0f) t = 0f;
        else
        {
            var inv = 1f / len;
            var u = (py - ay) * inv * inv * fy + (px - ax) * inv * inv * fx;
            t = u < 0f ? 0f : u >= 1f ? 1f : u;
        }
        var dy = fy * t + ay - py;
        var dx = fx * t + ax - px;
        return dy * dy + dx * dx;
    }

    /// <summary>Iteration order of an MSVC std::unordered_multimap after inserting <paramref name="items"/> in order
    /// (buckets: 8, ×8 while below 512, then ×2; max load 1). VS2019 STL (the current game): rehash before an insert that
    /// would exceed the load; legacy (VS2017): insert, then rehash. Insert: after the last equal key, else at the front
    /// of a non-empty bucket, else at the list end. Rehash: walk the list; a node whose new bucket is empty stays, else it
    /// moves after the last equal key of the bucket or to the bucket's front.</summary>
    public static List<T> MsvcMultimapOrder<T>(IReadOnlyList<T> items, Func<T, uint> hash, Func<T, int> key, bool legacy)
    {
        var list = new List<T>();
        ulong buckets = 8;
        void Rehash()
        {
            buckets = buckets < 512 ? buckets * 8 : buckets * 2;
            var mask = buckets - 1;
            var fresh = new List<T>();
            foreach (var x in list)
            {
                var b = hash(x) & mask;
                int lo = -1, lastEq = -1;
                for (var i = 0; i < fresh.Count; i++)
                    if ((hash(fresh[i]) & mask) == b)
                    {
                        if (lo < 0) lo = i;
                        if (key(fresh[i]) == key(x)) lastEq = i;
                    }
                if (lo < 0) fresh.Add(x);
                else if (lastEq >= 0) fresh.Insert(lastEq + 1, x);
                else fresh.Insert(lo, x);
            }
            list = fresh;
        }
        foreach (var item in items)
        {
            if (!legacy && (ulong)list.Count + 1 > buckets) Rehash();
            var mask = buckets - 1;
            var b = hash(item) & mask;
            int lo = -1, lastEq = -1;
            for (var i = 0; i < list.Count; i++)
                if ((hash(list[i]) & mask) == b)
                {
                    if (lo < 0) lo = i;
                    if (key(list[i]) == key(item)) lastEq = i;
                }
            if (lastEq >= 0) list.Insert(lastEq + 1, item);
            else if (lo >= 0) list.Insert(lo, item);
            else list.Add(item);
            if (legacy && (ulong)list.Count > buckets) Rehash();
        }
        return list;
    }
}
