using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// WH3 hlp_data.esf's region tables, the way Warhammer3.exe fills them (0x142a12e80, a parallel loop over region pairs
/// 0x142a03ef4): for every pair of regions r &lt; s (a region is left out when all its map_data areas are of type 6/7 or
/// it has none) the cost 0x1429ed2bc, the hops 0x1429ede54 with land-sea transitions (upper triangle [r, s]) and without
/// (lower triangle [s, r]). Each runs the campaign's runtime high-level search (0x142a24698 -> A* 0x1429ec824) from every
/// area of r to every area of s:
///  - Points (0x14284f9d8): a region's areas of type 0 (land) and 3/4 (sea), in map_data order. Without land-sea
///    transitions s offers only areas of a medium r has, and an area pair needs equal <c>a</c>.
///  - Nodes are (area, transition to take) plus (area, none) for the start and the goal. From a node the search moves
///    into the area its transition leads to (the start: its own area); if that is the goal area the only successor is
///    the goal, else every transition of the area in file order (land-sea ones only when allowed).
///  - Step (0x142a0869c): 0 from the start; else the transition's cost plus the area's matrix value from the landing
///    transition (the exact reverse one: inside = this one's outside and outside = its inside, 0x142a28e70) to the next
///    transition, 0 when there is no landing transition or it is the next one. Summed in u32 (an unreachable 0xFFFFFFFF
///    matrix value wraps to a step of −1; <c>wrapLikeGame</c> = false leaves those steps out).
///  - A* with float g (g + (float)step) and h = max(0, spd landmark estimate (0x142a18174) from the node's hex (its
///    transition's inside hex, or the area centre) to the goal area's centre − the goal's b (and the start's b)).
///    An MSVC binary heap ordered by f = h + g only; a better g reopens a closed node; a successor with g above the
///    bound (when the bound is above 0) is dropped. The first goal popped ends the search; its path cost is the u32
///    sum of its steps.
///  - Cost: the bound is the best cost so far (the last path found), 0xFFFFFFFF when none. Hops: the region changes
///    along the path; the bound passed is the hop count found so far (the game's own oddity), so after the first
///    path later area pairs rarely replace it. A path of cost 0 ends either loop at once.
/// Costs: upper triangle only; pairs never computed (a region left out, the diagonal, the lower triangle):
/// <see cref="HlpData.NoRegionCost"/>; no path: 0xFFFFFFFF / 255 hops. Regions from <see cref="HlpData.RegionTableSize"/>
/// on have no row but count for OTHER_CONSTANTS, the largest cost found.
/// </summary>
public static class HlpRegionTables
{
    public const uint Unreachable = 0xFFFFFFFF;
    public const byte UnreachableHops = 255;

    private sealed class Graph
    {
        public required HlpData.HlpArea[] Areas;
        public required int[] Base;          // first transition node of each area
        public required int[] TransArea;     // transition node -> area
        public required HlpData.HlpTransition[] Trans;
        public required int[] TransTarget;   // transition node -> target area (-1 unknown)
        public required int[] Landing;       // transition node -> the target area's reverse transition node (-1 none)
        public required int[] Region;        // area -> region
        public required bool[] Sea;          // area -> type 3/4
        public required SpdData Spd;
        public required uint[] Cost;         // transition node -> cost
        public required bool[] Flag1;        // transition node -> land-sea
        public required int[] MatRow;        // transition node -> offset in Mat of its area's matrix row for it
        public required int[] MatCol;        // transition node -> its creation index (a column before the diagonal skip)
        public required uint[] Mat;          // every area's matrix, flat
        public int NodeCount => Trans.Length + Areas.Length;   // transition nodes, then one (area, none) node per area
        private float[]?[] _h = [];
        /// <summary>Cheapest exact (64-bit) cost from area a (start node) into area b, [a * areas + b], with and
        /// without land-sea transitions (<see cref="NoPath"/>: none). No path the A* can find costs less, so a search
        /// whose bound is below it fails and is not run. With the bound 0xFFFFFFFF (as a float 2^32, which a wrapped
        /// step of 0xFFFFFFFF reaches without being pruned) only searches without any path are left out.</summary>
        public ulong[] OptAll = [], OptSame = [];
        public const ulong NoPath = ulong.MaxValue;

        public bool Worth(bool landSea, int a, int b, uint bound)
        {
            var opt = (landSea ? OptAll : OptSame)[a * Areas.Length + b];
            return bound == Unreachable ? opt != NoPath : opt <= bound;
        }

        /// <summary>h of every node towards goal area <paramref name="goal"/> (computed once, shared by the threads).</summary>
        public float[] HTo(int goal)
        {
            if (_h.Length == 0) Interlocked.CompareExchange(ref _h, new float[]?[Areas.Length], []);
            var t = Volatile.Read(ref _h[goal]);
            if (t is not null) return t;
            t = new float[NodeCount];
            var ga = Areas[goal];
            for (var node = 0; node < NodeCount; node++)
            {
                uint sub = ga.B;
                int hx, hy;
                if (node >= Trans.Length)
                {
                    var a = Areas[node - Trans.Length];
                    (hx, hy) = (a.CentreX, a.CentreY);
                    sub = unchecked(sub + a.B);
                }
                else (hx, hy) = (Trans[node].X, Trans[node].Y);
                var est = Estimate(Spd, hx, hy, ga.CentreX, ga.CentreY);
                t[node] = est - Math.Min(sub, est);
            }
            Interlocked.CompareExchange(ref _h[goal], t, null);
            return _h[goal]!;
        }
    }

    /// <param name="spd">the spd the game holds when it fills the tables (its own, built just before).</param>
    /// <param name="regions">map_data's regions: which are left out, and each area's medium.</param>
    /// <param name="wrapLikeGame">true: the game's u32 sums that wrap (an unreachable matrix value is a step of −1), for
    /// parity with CA's files; false (default): such a step is not taken.</param>
    /// <param name="rows">only these regions' pairs with the regions after them (tests); null: all.</param>
    public static void Fill(HlpData hlp, SpdData spd, MapDataRegions regions, int maxThreads = 0, bool wrapLikeGame = false,
                            IReadOnlyCollection<int>? rows = null)
    {
        const int size = HlpData.RegionTableSize;
        var g = BuildGraph(hlp, spd, regions);
        var byId = new Dictionary<RegionArea, int>();
        for (var i = 0; i < g.Areas.Length; i++) byId[g.Areas[i].Area] = i;
        var n = regions.Regions.Count;
        var points = new int[n][];
        var counted = new bool[n];
        for (var r = 0; r < n; r++)
        {
            var R = regions.Regions[r];
            counted[r] = R.Areas.Any(a => a.Type is not (6 or 7));
            points[r] = R.Areas.Select((a, i) => (a, i)).Where(p => p.a.Type is 0 or 3 or 4)
                .Select(p => byId.TryGetValue(new RegionArea((ushort)r, (ushort)p.i), out var ai) ? ai : -1).Where(ai => ai >= 0).ToArray();
        }
        var costs = HlpData.NewRegionCosts();
        var hops = new byte[size * size];
        var max = 0u;
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : Environment.ProcessorCount };
        var searches = new ThreadLocal<Search>(() => new Search(g, wrapLikeGame), true);
        g.OptAll = new ulong[g.Areas.Length * g.Areas.Length];
        g.OptSame = new ulong[g.Areas.Length * g.Areas.Length];
        Parallel.For(0, g.Areas.Length, po, a =>
        {
            var search = searches.Value!;
            search.Cheapest(a, true, g.OptAll.AsSpan(a * g.Areas.Length, g.Areas.Length));
            search.Cheapest(a, false, g.OptSame.AsSpan(a * g.Areas.Length, g.Areas.Length));
        });
        var maxLock = new object();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(Enumerable.Range(0, n).ToArray(), true), po, r =>
        {
            if (!counted[r] || (rows is not null && !rows.Contains(r))) return;
            var search = searches.Value!;
            var localMax = 0u;
            var any = false;
            for (var s = r + 1; s < n; s++)
            {
                if (!counted[s]) continue;
                search.Kind = 0;
                var c = search.PairCost(points[r], points[s], out var first);
                // until its first path the upper hop search runs the cost search's searches (same bound, all
                // unreachable); without any path both give up the same way
                var hu = first is null ? (c == 0 ? 0 : Unreachable) : search.PairHops(points[r], points[s], true, first);
                search.Kind = 1;
                var hl = search.PairHops(points[r], points[s], false);
                if (c != Unreachable) { localMax = Math.Max(localMax, c); any = true; }
                if (r >= size || s >= size) continue;
                costs[r * size + s] = c;
                hops[r * size + s] = (byte)Math.Min(hu, UnreachableHops);
                hops[s * size + r] = (byte)Math.Min(hl, UnreachableHops);
            }
            if (any) lock (maxLock) max = Math.Max(max, localMax);
        });
        hlp.RegionCosts = costs;
        hlp.RegionHops = hops;
        hlp.MaxRegionCost = max;
        if (Environment.GetEnvironmentVariable("HLP_TABLE_STATS") is not null)
        {
            var st = new long[6];
            foreach (var se in searches.Values) for (var i = 0; i < 6; i++) st[i] += se._stats[i];
            Console.Error.WriteLine($"tables: cost {st[0]} searches {st[1]} pops {st[2]} found; hops/lower {st[3]} searches {st[4]} pops {st[5]} found");
        }
    }

    /// <summary>The spd landmark estimate (0x142a18174 with area landmarks allowed): the largest |cost difference| over
    /// the landmarks both cells have (the set's 8, plus the area's 8 when both cells are in the same area), from the
    /// game's u16 copies (cost / 4, times 4); 0xFFFFFFFF outside the box, across sets or without a landmark.</summary>
    private static uint Estimate(SpdData spd, int ax, int ay, int bx, int by)
    {
        if (ax < spd.X0 || ax > spd.X1 || ay < spd.Y0 || ay > spd.Y1 || bx < spd.X0 || bx > spd.X1 || by < spd.Y0 || by > spd.Y1) return Unreachable;
        var ca = (ay - spd.Y0) * spd.Width + (ax - spd.X0);
        var cb = (by - spd.Y0) * spd.Width + (bx - spd.X0);
        var n = spd.Areas[ca] == spd.Areas[cb] ? SpdData.Stride : 8;
        if (spd.Sets[ca] != spd.Sets[cb]) return Unreachable;
        var best = 0u;
        var any = false;
        long oa = (long)ca * SpdData.Stride, ob = (long)cb * SpdData.Stride;
        for (var i = 0; i < n; i++)
        {
            var va = spd.Values[oa + i];
            var vb = spd.Values[ob + i];
            if (va == SpdData.NoPath || vb == SpdData.NoPath) continue;
            uint ua = Math.Min(va >> 2, 0xFFFE), ub = Math.Min(vb >> 2, 0xFFFE);
            var d = (ua > ub ? ua - ub : ub - ua) * 4;
            any = true;
            if (d > best) best = d;
        }
        return any ? best : Unreachable;
    }

    private static Graph BuildGraph(HlpData hlp, SpdData spd, MapDataRegions regions)
    {
        var areas = hlp.Nodes.SelectMany(nd => nd.Areas).ToArray();
        var byId = new Dictionary<RegionArea, int>();
        for (var i = 0; i < areas.Length; i++) byId[areas[i].Area] = i;
        var bases = new int[areas.Length + 1];
        for (var i = 0; i < areas.Length; i++) bases[i + 1] = bases[i] + areas[i].Transitions.Count;
        var count = bases[^1];
        var trans = new HlpData.HlpTransition[count];
        var transArea = new int[count];
        var target = new int[count];
        for (var i = 0; i < areas.Length; i++)
            for (var j = 0; j < areas[i].Transitions.Count; j++)
            {
                var t = areas[i].Transitions[j];
                trans[bases[i] + j] = t;
                transArea[bases[i] + j] = i;
                target[bases[i] + j] = byId.TryGetValue(t.Target, out var ti) ? ti : -1;
            }
        var landing = new int[count];
        for (var k = 0; k < count; k++)
        {
            landing[k] = -1;
            var b = target[k];
            if (b < 0) continue;
            var t = trans[k];
            for (var j = bases[b]; j < bases[b + 1]; j++)
                if (trans[j].X == t.OtherX && trans[j].Y == t.OtherY && trans[j].OtherX == t.X && trans[j].OtherY == t.Y) { landing[k] = j; break; }
        }
        var matBase = new int[areas.Length];
        var mat = new List<uint>();
        for (var i = 0; i < areas.Length; i++) { matBase[i] = mat.Count; mat.AddRange(areas[i].Costs); }
        var matRow = new int[count];
        for (var k = 0; k < count; k++)
        {
            var ai = transArea[k];
            matRow[k] = matBase[ai] + (areas[ai].Transitions.Count - 1) * trans[k].Index;
        }
        return new Graph
        {
            Cost = trans.Select(t => t.Cost).ToArray(), Flag1 = trans.Select(t => t.Flag1).ToArray(), MatRow = matRow,
            MatCol = trans.Select(t => (int)t.Index).ToArray(), Mat = mat.ToArray(),
            Areas = areas, Base = bases, Trans = trans, TransArea = transArea, TransTarget = target, Landing = landing,
            Region = areas.Select(a => (int)a.Area.Region).ToArray(),
            Sea = areas.Select(a => regions.Regions[a.Area.Region].Areas[a.Area.Area].Type is 3 or 4).ToArray(),
            Spd = spd,
        };
    }

    /// <summary>One thread's A* state (records stamped per search).</summary>
    private sealed class Search(Graph g, bool wrap)
    {
        private readonly float[] _g = new float[g.NodeCount];
        private readonly float[] _h = new float[g.NodeCount];
        private readonly int[] _parent = new int[g.NodeCount];
        private readonly byte[] _state = new byte[g.NodeCount];   // 0 open, 1 closed (2 = new: stamp differs)
        private readonly int[] _stamp = new int[g.NodeCount];
        private readonly int[] _pos = new int[g.NodeCount];       // heap position of an open node
        private readonly List<int> _heap = new(1024);
        private int _gen;
        private readonly List<int> _path = [];

        private int AreaNode(int area) => g.Trans.Length + area;
        private bool IsAreaNode(int node) => node >= g.Trans.Length;
        private int AreaOf(int node) => IsAreaNode(node) ? node - g.Trans.Length : g.TransArea[node];

        /// <summary>The pair's cost; also the area pair (indexes into from, to) and hops of its first path found, where
        /// the hop search (same searches until its first path) takes over.</summary>
        public uint PairCost(int[] from, int[] to, out (int I, int J, uint Hops, uint Cost)? first)
        {
            first = null;
            var best = Unreachable;
            for (var i = 0; i < from.Length; i++)
                for (var j = 0; j < to.Length; j++)
                {
                    int a = from[i], b = to[j];
                    if (a == b) return 0;
                    // a bounded search finds a cost between the cheapest and the bound, so when the cheapest is the
                    // bound the best stays
                    if (g.Worth(true, a, b, best) && (best == Unreachable || g.OptAll[a * g.Areas.Length + b] < best) && Run(a, b, true, best))
                    {
                        best = PathCost();
                        first ??= (i, j, PathHops(), best);
                    }
                    if (best == 0) return 0;
                }
            return best;
        }

        /// <param name="resume">for the upper hops: the cost search's first path (identical searches until then), the
        /// loop goes on after it.</param>
        public uint PairHops(int[] from, int[] to, bool landSea, (int I, int J, uint Hops, uint Cost)? resume = null)
        {
            var hops = Unreachable;
            var cost = Unreachable;
            var land = landSea || from.Any(a => !g.Sea[a]);
            var sea = landSea || from.Any(a => g.Sea[a]);
            var i0 = 0;
            var j0 = 0;
            if (resume is { } rs)
            {
                (hops, cost) = (rs.Hops, rs.Cost);
                if (cost == 0) return 0;
                (i0, j0) = (rs.I, rs.J + 1);
            }
            for (var i = i0; i < from.Length; i++)
                for (var j = i == i0 ? j0 : 0; j < to.Length; j++)
                {
                    int a = from[i], b = to[j];
                    if (g.Sea[b] ? !sea : !land) continue;
                    if (a == b) return 0;
                    if (landSea || g.Areas[a].A == g.Areas[b].A)
                        if (g.Worth(landSea, a, b, hops) && Run(a, b, landSea, hops))
                        {
                            cost = PathCost();
                            hops = PathHops();
                        }
                    if (cost == 0) return 0;
                }
            return hops;
        }

        // the game's u32 step between two nodes (0x142a0869c); null: a step through an unreachable matrix value
        // when not wrapping like the game
        private const long NoStep = -1;

        private long Step(int from, int to)
        {
            if (from >= _transCount) return 0;
            var c = _cost[from];
            if (to >= _transCount) return c;
            var l = _landing[from];
            if (l < 0 || l == to) return c;
            var j = _col[to];
            var m = _mat[_row[l] + j - (j > _col[l] ? 1 : 0)];
            if (m == Unreachable && !wrap) return NoStep;
            return unchecked(c + m);
        }

        private readonly int _transCount = g.Trans.Length;
        private readonly uint[] _cost = g.Cost;
        private readonly int[] _landing = g.Landing;
        private readonly int[] _row = g.MatRow;
        private readonly int[] _col = g.MatCol;
        private readonly uint[] _mat = g.Mat;
        private float[] _hTo = [];

        private readonly float[] _f = new float[g.NodeCount];
        private float F(int node) => _f[node];

        private void SiftUp(int hole, int value)
        {
            var f = F(value);
            while (hole > 0)
            {
                var parent = (hole - 1) >> 1;
                if (!(f < F(_heap[parent]))) break;
                Put(hole, _heap[parent]);
                hole = parent;
            }
            Put(hole, value);
        }

        private void Put(int at, int node)
        {
            _heap[at] = node;
            _pos[node] = at;
        }

        private int Pop()
        {
            var count = _heap.Count;
            var top = _heap[0];
            if (count > 1)
            {
                var len = count - 1;
                var value = _heap[len];
                _heap[len] = top;
                var hole = 0;
                var maxNonLeaf = (len - 1) >> 1;
                while (hole < maxNonLeaf)
                {
                    var child = 2 * hole + 1;
                    if (F(_heap[child + 1]) <= F(_heap[child])) child++;
                    Put(hole, _heap[child]);
                    hole = child;
                }
                if (hole == maxNonLeaf && (len & 1) == 0)
                {
                    Put(hole, _heap[len - 1]);
                    hole = len - 1;
                }
                SiftUp(hole, value);
            }
            _heap.RemoveAt(count - 1);
            return top;
        }

        private int _goalNode;

        /// <summary>One search from area <paramref name="a"/> to area <paramref name="b"/>; true when the goal was popped
        /// (the path is then in <see cref="_path"/>).</summary>
        public readonly long[] _stats = new long[6]; // research: searches / pops / found, per kind (0 cost, 1 lower hops)
        public int Kind;

        private bool Run(int a, int b, bool landSea, uint bound)
        {
            _stats[Kind * 3]++;
            _gen++;
            _heap.Clear();
            var boundF = (float)bound;
            var start = AreaNode(a);
            _goalNode = AreaNode(b);
            Touch(start);
            _hTo = g.HTo(b);
            _g[start] = 0;
            _h[start] = _hTo[start];
            _f[start] = _h[start] + _g[start];
            _parent[start] = -1;
            _state[start] = 0;
            _heap.Add(start);
            _pos[start] = 0;
            while (_heap.Count > 0)
            {
                var cur = Pop();
                _stats[Kind * 3 + 1]++;
                if (cur == _goalNode)
                {
                    _stats[Kind * 3 + 2]++;
                    _path.Clear();
                    for (var x = cur; x >= 0; x = _parent[x]) _path.Add(x);
                    _path.Reverse();
                    return true;
                }
                var into = IsAreaNode(cur) ? AreaOf(cur) : g.TransTarget[cur];
                if (into >= 0)
                {
                    if (into == b) Relax(cur, _goalNode, b, boundF);
                    else
                        for (int k = g.Base[into], end = g.Base[into + 1]; k < end; k++)
                        {
                            if (!landSea && g.Flag1[k]) continue;
                            Relax(cur, k, b, boundF);
                        }
                }
                _state[cur] = 1;
            }
            return false;
        }

        private readonly PriorityQueue<int, ulong> _dq = new();
        private readonly ulong[] _dist = new ulong[g.NodeCount];

        /// <summary>Exact cheapest costs from area <paramref name="a"/>'s start node into every area (Dijkstra over the
        /// same nodes and steps as <see cref="Run"/>).</summary>
        public void Cheapest(int a, bool landSea, Span<ulong> into)
        {
            into.Fill(Graph.NoPath);
            _gen++;
            var start = AreaNode(a);
            Touch(start);
            _dist[start] = 0;
            _state[start] = 0;
            _dq.Clear();
            _dq.Enqueue(start, 0);
            while (_dq.TryDequeue(out var cur, out var d))
            {
                if (d != _dist[cur] || _state[cur] == 1) continue;
                _state[cur] = 1;
                var to = IsAreaNode(cur) ? AreaOf(cur) : g.TransTarget[cur];
                if (to < 0) continue;
                if (!IsAreaNode(cur))
                {
                    var c = d + _cost[cur];
                    if (c < into[to]) into[to] = c;
                }
                for (int k = g.Base[to], end = g.Base[to + 1]; k < end; k++)
                {
                    if (!landSea && g.Flag1[k]) continue;
                    var step = Step(cur, k);
                    if (step == NoStep) continue;
                    var nd = d + (uint)step;
                    var fresh = Touch(k);
                    if (!fresh && nd >= _dist[k]) continue;
                    _dist[k] = nd;
                    _state[k] = 0;
                    _dq.Enqueue(k, nd);
                }
            }
        }

        private bool Touch(int node)
        {
            if (_stamp[node] == _gen) return false;
            _stamp[node] = _gen;
            return true;
        }

        private void Relax(int cur, int next, int goalArea, float bound)
        {
            var step = Step(cur, next);
            if (step == NoStep) return;
            var ng = (float)(uint)step + _g[cur];
            if (ng > bound && bound > 0f) return;
            var fresh = Touch(next);
            if (!fresh && !(ng < _g[next])) return;
            _g[next] = ng;
            _h[next] = _hTo[next];
            _f[next] = _h[next] + ng;
            _parent[next] = cur;
            if (!fresh && _state[next] == 0)
            {
                SiftUp(_pos[next], next);
            }
            else
            {
                _state[next] = 0;
                _heap.Add(next);
                SiftUp(_heap.Count - 1, next);
            }
        }

        private uint PathCost()
        {
            var sum = 0u;
            for (var i = 1; i < _path.Count; i++) sum = unchecked(sum + (uint)Step(_path[i - 1], _path[i]));
            return sum;
        }

        // region changes along the path's nodes (0x142a31168)
        private uint PathHops()
        {
            var count = 0u;
            var prev = -1;
            foreach (var node in _path)
            {
                var r = g.Region[AreaOf(node)];
                if (r != prev) count++;
                prev = r;
            }
            return count > 1 ? count - 1 : 0;
        }
    }
}
