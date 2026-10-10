using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// The campaign grid as seen by the game's A*-family searches (UTILITYLIB::A_STAR_GRID_SYSTEM over
/// EMPIRECAMPAIGN::CAMPAIGN_PATHFINDER), which the AI's region border analysis (hlp_data.esf) uses. Unlike the landmark
/// search (<see cref="CampaignPathGrid"/>) it moves along edges whose navigability bit is set (FUN_180538610: bit 7),
/// costs index the same 256-entry table, and port hexes (type 5) link to the port hexes listed with them at cost 500.
/// Beach edges (ppd beaches: embark edges, disembark edges per HLCI pair) are switched by the search's setup
/// (FUN_181209d80 / FUN_181207070): embark edges on when the search's HLCI is the pair's land HLCI, disembark edges on when
/// it is the pair's sea HLCI, and both off for setups without an HLCI.
/// </summary>
public sealed class AiPathGrid
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Types { get; }
    public ushort[] Hlci { get; }
    public bool[] Slot { get; }
    public uint[] CostTable { get; } = new uint[256];
    /// <summary>Edge bytes after load (bit 6 = bit 7, roads applied); settlement slot edges at cost index 0 (as the
    /// landmark search sees them) in <see cref="EdgesZero"/>.</summary>
    public byte[] EdgesPlain { get; }
    public byte[] EdgesZero { get; }
    public int[] Neighbour { get; }
    public int[] PortLinkStart { get; }
    public int[] PortLinks { get; }
    /// <summary>Hex radius and half hex height in world units (the pathfinder's +0x50 / +0x54 copies).</summary>
    /// <summary>Edges navigable in the ppd: a beach only ever switches these (FUN_1812013e0 masks its entry with them).</summary>
    private readonly bool[] _open;
    public float HexSize { get; }
    public float HalfRow { get; }
    public float[] WorldX { get; }
    public float[] WorldY { get; }
    private readonly List<(ushort Enter, ushort Leave, List<(int Hex, byte Mask)> EnterEdges, List<(int Hex, byte Mask)> LeaveEdges)> _beaches = [];
    private readonly Dictionary<(bool Zero, int Cc, int Ce), byte[]> _gated = [];

    public int Index(int x, int y) => y * Width + x;

    public AiPathGrid(PathfindingPpd ppd, MapDataRegions regions, CampaignPathGrid.Settings settings)
    {
        Width = ppd.Width;
        Height = ppd.Height;
        var n = Width * Height;
        Types = new byte[n];
        Hlci = new ushort[n];
        Slot = new bool[n];
        Neighbour = new int[n * 6];
        EdgesPlain = new byte[n * 6];
        _open = new bool[n * 6];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var h = y * Width + x;
            Types[h] = (byte)(ppd.Cells[h * 8 + 7] >> 4);
            var tg = ppd.TileGroup(x, y);
            Hlci[h] = tg < ppd.TileGroups.Count ? ppd.TileGroups[tg].Hlci : (ushort)0;
            for (var d = 0; d < 6; d++)
            {
                var e = ppd.Cells[h * 8 + d];
                EdgesPlain[h * 6 + d] = (byte)((e & 0xBF) | (e >> 1 & 0x40));
                if ((e & 0x80) != 0) _open[h * 6 + d] = true;
                Neighbour[h * 6 + d] = ppd.Neighbour(x, y, d, out var nx, out var ny) ? ny * Width + nx : -1;
            }
        }
        for (var i = 0; i < ppd.MoveCosts.Length && i < 64; i++)
            for (var b = 0; b < 256; b += 64) CostTable[i + b] = ppd.MoveCosts[i];
        const int roadSlot = 0x3E;
        for (var b = 0; b < 256; b += 64)
        {
            CostTable[1 + b] = settings.LandToSeaCost;
            CostTable[2 + b] = settings.SeaToLandCost;
            CostTable[roadSlot + b] = settings.RoadCost;
        }
        void SetMask(int h, int mask, byte index)
        {
            for (var d = 0; d < 6; d++)
                if ((mask >> d & 1) != 0) EdgesPlain[h * 6 + d] = (byte)(EdgesPlain[h * 6 + d] & 0xC0 | index);
        }
        foreach (var (_, hexes) in ppd.Roads)
            foreach (var (x, y, mask) in hexes)
            {
                var h = Index(x, y);
                if (Types[h] != 6) { SetMask(h, mask, roadSlot); continue; }
                for (var d = 0; d < 6; d++)
                {
                    var nb = Neighbour[h * 6 + d];
                    if (nb >= 0) SetMask(nb, 1 << (d + 3) % 6, roadSlot);
                    SetMask(h, 1 << d, roadSlot);
                }
            }
        // impassable hexes (type 2): no navigable edge in or out (FUN_181203a20 does this for the setup's blocked hexes;
        // the landmark search's type masks give the same)
        for (var h = 0; h < n; h++)
        {
            if (Types[h] != 2) continue;
            for (var d = 0; d < 6; d++)
            {
                EdgesPlain[h * 6 + d] &= 0x3F;
                var nb = Neighbour[h * 6 + d];
                if (nb >= 0) EdgesPlain[nb * 6 + (d + 3) % 6] &= 0x3F;
            }
        }
        foreach (var r in regions.Regions.Where(r => r.Key != Environment.GetEnvironmentVariable("SPD_SKIP_SLOT")))
            foreach (var (x, y) in r.PrimarySlot.Concat(r.PortSlot))
                if ((uint)x < (uint)Width && (uint)y < (uint)Height) Slot[Index(x, y)] = true;
        EdgesZero = (byte[])EdgesPlain.Clone();
        for (var h = 0; h < n; h++)
        {
            if (!Slot[h]) continue;
            for (var d = 0; d < 6; d++)
            {
                var nb = Neighbour[h * 6 + d];
                if (nb < 0 || !(Types[nb] <= 1 || Slot[nb])) continue;
                EdgesZero[h * 6 + d] &= 0xC0;
                EdgesZero[nb * 6 + (d + 3) % 6] &= 0xC0;
            }
        }
        foreach (var (a, l, ent, lev) in ppd.Beaches)
            _beaches.Add((a, l, ent.Select(t => (Index(t.X, t.Y), t.Mask)).ToList(), lev.Select(t => (Index(t.X, t.Y), t.Mask)).ToList()));

        var lists = new Dictionary<int, List<int>>();
        foreach (var (sa, sb) in ppd.Bridges)
        {
            // the game assigns (not appends): a hex listed by several bridges keeps the last bridge's other bank
            foreach (var (x, y) in sa) lists[Index(x, y)] = sb.Select(p => Index(p.X, p.Y)).ToList();
            foreach (var (x, y) in sb) lists[Index(x, y)] = sa.Select(p => Index(p.X, p.Y)).ToList();
        }
        PortLinkStart = new int[n + 1];
        var all = new List<int>();
        for (var h = 0; h < n; h++)
        {
            PortLinkStart[h] = all.Count;
            if (lists.TryGetValue(h, out var l)) all.AddRange(l);
        }
        PortLinkStart[n] = all.Count;
        PortLinks = all.ToArray();

        // CAMPAIGN_MAP_DATA::real_world_position_for_logical_position (FUN_18130a860)
        var hexSize = 0.6666667f / (Width - 1f) * (regions.WorldMax.X - regions.WorldMin.X);
        var halfRow = hexSize * 0.8660254f;
        HexSize = hexSize;
        HalfRow = halfRow;
        var rowStep = halfRow + halfRow;
        var colStep = hexSize * 1.5f;
        WorldX = new float[n];
        WorldY = new float[n];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var wy = y * rowStep;
            if ((x & 1) != 0) wy += halfRow;
            WorldX[y * Width + x] = x * colStep + regions.WorldMin.X;
            WorldY[y * Width + x] = wy + regions.WorldMin.Y;
        }
    }

    /// <summary>Edge bytes for a search setup: <paramref name="cc"/> = the setup's HLCI (0 = none), <paramref name="ce"/> = its
    /// second HLCI; beach edges are on as described on the class.</summary>
    public byte[] Gated(bool zero, int cc, int ce)
    {
        lock (_gated)
        {
            if (_gated.TryGetValue((zero, cc, ce), out var e)) return e;
            e = (byte[])(zero ? EdgesZero : EdgesPlain).Clone();
            // Beach objects (ppd beaches, loaded by FUN_181208f40, switched by FUN_181209d80 in CAMPAIGN_PATHFINDER::setup)
            // keep a counter per edge: an edge is open only while every beach side listing it is on, and a beach only
            // ever switches edges that are navigable in the ppd (FUN_1812013e0 masks its entry with them). There is no
            // move-type table in these searches (FUN_180538bd0 checks the navigation bit only).
            var state = new Dictionary<int, bool>();
            foreach (var (a, l, ent, lev) in _beaches)
            {
                var onE = cc != ce && a == cc;
                var onL = cc != ce && l == cc;
                foreach (var (h, m) in ent)
                    for (var d = 0; d < 6; d++)
                        if ((m >> d & 1) != 0 && _open[h * 6 + d]) state[h * 6 + d] = (!state.TryGetValue(h * 6 + d, out var o) || o) && onE;
                foreach (var (h, m) in lev)
                    for (var d = 0; d < 6; d++)
                        if ((m >> d & 1) != 0 && _open[h * 6 + d]) state[h * 6 + d] = (!state.TryGetValue(h * 6 + d, out var o) || o) && onL;
            }
            foreach (var (k, on) in state)
                e[k] = on ? (byte)(e[k] | 0x80) : (byte)(e[k] & 0x7F);
            // impassable hexes stay closed whatever the beach state (their edges were cut when the grid was built)
            for (var h = 0; h < Types.Length; h++)
            {
                if (Types[h] != 2) continue;
                for (var d = 0; d < 6; d++)
                {
                    e[h * 6 + d] &= 0x7F;
                    var nb = Neighbour[h * 6 + d];
                    if (nb >= 0) e[nb * 6 + (d + 3) % 6] &= 0x7F;
                }
            }
            _gated[(zero, cc, ce)] = e;
            return e;
        }
    }
}

/// <summary>
/// One heuristic-free search of the game's A* grid system (Dijkstra ordered by cost, then by the tiebreaker: the squared
/// distance of a hex from the segment between two world points, CAMPAIGN_PATHFINDER::tiebreaker). The open list is an
/// MSVC binary heap with the game's comparator (FUN_1805316e0); a cheaper cost for an open node is sifted up in place.
/// Not thread-safe: one instance per thread.
/// </summary>
public sealed class AiSearch
{
    public enum Visit { Continue, NoExpand, Stop }

    private readonly AiPathGrid _g;
    private readonly uint[] _cost;
    private readonly float[] _tb;
    private readonly int[] _parent;
    private readonly int[] _stamp;
    private readonly byte[] _state; // 1 open, 2 closed
    private readonly List<int> _heap = new(1 << 12);
    private int _gen;
    private bool _hasLine;
    private float _ax, _ay, _bx, _by;

    public AiSearch(AiPathGrid g)
    {
        _g = g;
        var n = g.Width * g.Height;
        _cost = new uint[n];
        _tb = new float[n];
        _parent = new int[n];
        _stamp = new int[n];
        _state = new byte[n];
    }

    public uint Cost(int h) => _stamp[h] == _gen ? _cost[h] : uint.MaxValue;
    public bool Closed(int h) => _stamp[h] == _gen && _state[h] == 2;

    private float Tiebreak(int h)
    {
        if (!_hasLine) return 0f;
        float px = _g.WorldX[h], py = _g.WorldY[h];
        var fx = _bx - _ax;
        var fy = _by - _ay;
        var len = MathF.Sqrt(fy * fy + fx * fx);
        float t;
        if (len <= 0f) t = 0f;
        else
        {
            var inv = 1f / len;
            var u = (py - _ay) * inv * fy * inv + (px - _ax) * inv * fx * inv;
            t = u < 0f ? 0f : u >= 1f ? 1f : u;
        }
        var dy = fy * t + _ay - py;
        var dx = fx * t + _ax - px;
        return dy * dy + dx * dx;
    }

    // comparator "a is worse than b" (FUN_1805316e0): cost, then tiebreaker
    private bool Greater(int a, int b) => _cost[a] != _cost[b] ? _cost[a] > _cost[b] : _tb[a] > _tb[b];

    private void SiftUp(int hole, int value)
    {
        while (hole > 0)
        {
            var parent = (hole - 1) >> 1;
            if (!Greater(_heap[parent], value)) break;
            _heap[hole] = _heap[parent];
            hole = parent;
        }
        _heap[hole] = value;
    }

    private int Pop()
    {
        var count = _heap.Count;
        var top = _heap[0];
        if (count > 1)
        {
            var bottom = count - 1;
            var value = _heap[bottom];
            _heap[bottom] = top;
            var len = bottom;
            var hole = 0;
            var maxNonLeaf = (len - 1) >> 1;
            while (hole < maxNonLeaf)
            {
                var child = 2 * hole + 2;
                if (Greater(_heap[child], _heap[child - 1])) child--;
                _heap[hole] = _heap[child];
                hole = child;
            }
            if (hole == maxNonLeaf && (len & 1) == 0)
            {
                _heap[hole] = _heap[len - 1];
                hole = len - 1;
            }
            SiftUp(hole, value);
        }
        _heap.RemoveAt(count - 1);
        return top;
    }

    private void Relax(int nb, uint nc, int from)
    {
        if (_stamp[nb] == _gen)
        {
            if (nc >= _cost[nb]) return;
            _cost[nb] = nc;
            _parent[nb] = from;
            if (_state[nb] == 1)
            {
                var pos = _heap.IndexOf(nb);
                SiftUp(pos, nb);
            }
            else
            {
                _state[nb] = 1;
                _heap.Add(nb);
                SiftUp(_heap.Count - 1, nb);
            }
            return;
        }
        _stamp[nb] = _gen;
        _cost[nb] = nc;
        _tb[nb] = Tiebreak(nb);
        _parent[nb] = from;
        _state[nb] = 1;
        _heap.Add(nb);
        SiftUp(_heap.Count - 1, nb);
    }

    /// <summary>Runs a search from <paramref name="source"/>. <paramref name="visit"/> sees each settled hex and its
    /// cost (Continue = expand, NoExpand, Stop). <paramref name="blocked"/>: hexes that cannot be entered (except
    /// <paramref name="target"/>). Returns the target's cost (uint.MaxValue if not reached; search stops when the
    /// target is settled).</summary>
    public uint Run(int source, byte[] edges, int target = -1, Func<int, uint, Visit>? visit = null, bool[]? blocked = null,
                    (float X, float Y)? lineFrom = null, (float X, float Y)? lineTo = null, int[]? regionOf = null, int region = -1, int regionMask = MapDataRegions.RegionMask)
    {
        _gen++;
        _heap.Clear();
        if (lineFrom is { } a && lineTo is { } b && !(a.X == b.X && a.Y == b.Y))
        {
            _hasLine = true;
            (_ax, _ay, _bx, _by) = (a.X, a.Y, b.X, b.Y);
        }
        else _hasLine = false;
        _stamp[source] = _gen;
        _cost[source] = 0;
        _tb[source] = 0f;
        _parent[source] = -1;
        _state[source] = 1;
        _heap.Add(source);
        var types = _g.Types;
        var costs = _g.CostTable;
        while (_heap.Count > 0)
        {
            var h = Pop();
            _state[h] = 2;
            var c = _cost[h];
            if (h == target) return c;
            var v = visit?.Invoke(h, c) ?? Visit.Continue;
            if (v == Visit.Stop) break;
            if (v == Visit.NoExpand) continue;
            var o = h * 6;
            for (var d = 0; d < 6; d++)
            {
                var e = edges[o + d];
                if ((e & 0x80) == 0) continue;
                var nb = _g.Neighbour[o + d];
                if (nb < 0) continue;
                if (blocked is not null && blocked[nb] && nb != target) continue;
                if (regionOf is not null && (regionOf[nb] & regionMask) != region) continue;
                Relax(nb, c + costs[e], h);
            }
            if (types[h] == 5)
                for (var k = _g.PortLinkStart[h]; k < _g.PortLinkStart[h + 1]; k++)
                {
                    var nb = _g.PortLinks[k];
                    if (types[nb] != 5) continue;
                    if (blocked is not null && blocked[nb] && nb != target) continue;
                    if (regionOf is not null && (regionOf[nb] & regionMask) != region) continue;
                    Relax(nb, c + 500, h);
                }
        }
        return target >= 0 && Closed(target) ? _cost[target] : uint.MaxValue;
    }

    /// <summary>Hexes from the source to <paramref name="target"/> after a successful <see cref="Run"/>.</summary>
    public List<int> PathTo(int target)
    {
        var path = new List<int>();
        for (var h = target; h >= 0; h = _parent[h]) path.Add(h);
        path.Reverse();
        return path;
    }
}
