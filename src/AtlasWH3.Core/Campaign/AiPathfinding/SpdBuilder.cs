using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// Builds WH3's spd_data.esf (CAI_SIMPLE_PATH_DIRECTORY v1), the campaign AI's landmark tables. 3K's single landmark
/// search (empirecampaign FUN_180593f60, reprocess_spd_data) is applied at two levels:
///  - landmark sets: one per connected piece of the movement grid; per set 8 landmarks and 8 searches, giving every
///    hex of the set its costs 0..7;
///  - areas: per map_data region area 8 landmarks (targets from the box of all its hexes, landmarks among its passable
///    ones; combi map 1: 3,440 of 3,440 against 3,432 with the passable hexes' box) and 8 searches, giving the area's hexes
///    costs 8..15. The searches are not kept inside the area: a path may leave it and come back (prologue map: 1,036
///    of 611,240 area costs are lower than inside-only paths, and CA's file has those).
/// Landmarks (3K's rule): the box of the hexes; eight targets, its corners (min x,min y), (min x,max y), (max x,min y),
/// (max x,max y), then the edge midpoints (mid x,min y), (mid x,max y), (min x,mid y), (max x,mid y) with
/// mid = ((max − min + 1) &gt;&gt; 1) + min; landmark i = the hex nearest (hex distance) target i, scanning x outer,
/// y inner, first wins ties.
/// </summary>
public static class SpdBuilder
{
    public static int HexDistance(int x1, int y1, int x2, int y2)
    {
        var s3 = (short)(x1 - x2);
        var s2 = (short)(((x1 + 1) >> 1) - ((x2 + 1) >> 1) - y2 + y1);
        var s1 = (short)(s3 - s2);
        return Math.Max(Math.Abs((int)s3), Math.Max(Math.Abs((int)s2), Math.Abs((int)s1)));
    }

    /// <summary>3K's eight landmarks of a set of hexes (hex indices, any order); the box is that of
    /// <paramref name="boxHexes"/> (default: the hexes themselves).</summary>
    public static int[] Landmarks(CampaignPathGrid grid, IReadOnlyList<int> hexes, IReadOnlyList<int>? boxHexes = null)
    {
        var w = grid.Width;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var h in boxHexes ?? hexes)
        {
            int x = h % w, y = h / w;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        var mx = ((x1 - x0 + 1) >> 1) + x0;
        var my = ((y1 - y0 + 1) >> 1) + y0;
        (int X, int Y)[] targets = [(x0, y0), (x0, y1), (x1, y0), (x1, y1), (mx, y0), (mx, y1), (x0, my), (x1, my)];
        var best = new int[8];
        var bestD = new int[8];
        Array.Fill(bestD, int.MaxValue);
        // x outer, y inner
        foreach (var h in hexes.OrderBy(h => h % w).ThenBy(h => h / w))
        {
            int x = h % w, y = h / w;
            for (var i = 0; i < 8; i++)
            {
                var d = HexDistance(x, y, targets[i].X, targets[i].Y);
                if (d < bestD[i]) { bestD[i] = d; best[i] = h; }
            }
        }
        return best;
    }

    /// <summary>Connected pieces of the movement grid (forward edges and bridge links, either way): piece per hex,
    /// −1 for impassable hexes. Pieces are numbered in the order their first hex comes scanning x outer, y inner
    /// (combi map 1: its 13 sets in CA's order).</summary>
    public static int[] Pieces(CampaignPathGrid g, out int count)
    {
        var n = g.Width * g.Height;
        var piece = new int[n];
        Array.Fill(piece, -1);
        count = 0;
        var stack = new Stack<int>();
        for (var x = 0; x < g.Width; x++)
        for (var y = 0; y < g.Height; y++)
        {
            var s = y * g.Width + x;
            if (piece[s] >= 0 || g.Types[s] == 2) continue;
            var id = count++;
            piece[s] = id;
            stack.Push(s);
            while (stack.Count > 0)
            {
                var h = stack.Pop();
                for (var d = 0; d < 6; d++)
                {
                    var nb = g.Neighbour[h * 6 + d];
                    if (nb < 0 || piece[nb] >= 0) continue;
                    if (g.Forward[h * 6 + d] == CampaignPathGrid.NoEdge && g.Reverse[h * 6 + d] == CampaignPathGrid.NoEdge) continue;
                    piece[nb] = id;
                    stack.Push(nb);
                }
                for (var l = g.LinkStart[h]; l < g.LinkStart[h + 1]; l++)
                {
                    var nb = g.Links[l];
                    if (piece[nb] >= 0) continue;
                    piece[nb] = id;
                    stack.Push(nb);
                }
            }
        }
        return piece;
    }

    /// <summary>Dial-bucket search from <paramref name="source"/> over the whole grid; <paramref name="settle"/> sees each
    /// settled hex and its cost and returns false to stop. Per-thread scratch arrays with generation stamps.</summary>
    private sealed class Search(CampaignPathGrid g)
    {
        private readonly uint[] _dist = new uint[g.Width * g.Height];
        private readonly int[] _seen = new int[g.Width * g.Height];
        private readonly int[] _done = new int[g.Width * g.Height];
        private readonly List<int>[] _buckets = Enumerable.Range(0, (int)g.MaxEdgeCost + 1).Select(_ => new List<int>()).ToArray();
        private int _gen;

        public void Run(int source, bool reverse, Func<int, uint, bool> settle)
        {
            _gen++;
            var ring = _buckets.Length;
            var costs = reverse ? g.Reverse : g.Forward;
            _dist[source] = 0;
            _seen[source] = _gen;
            _buckets[0].Add(source);
            var pending = 1;
            for (uint cur = 0; pending > 0; cur++)
            {
                var bucket = _buckets[cur % ring];
                for (var i = 0; i < bucket.Count; i++)
                {
                    var h = bucket[i];
                    pending--;
                    if (_done[h] == _gen || _dist[h] != cur) continue;
                    _done[h] = _gen;
                    if (!settle(h, cur))
                    {
                        foreach (var b in _buckets) b.Clear();
                        return;
                    }
                    var o = h * 6;
                    for (var d = 0; d < 6; d++)
                    {
                        var c = costs[o + d];
                        if (c == CampaignPathGrid.NoEdge) continue;
                        Relax(g.Neighbour[o + d], cur + c, ring, ref pending);
                    }
                    for (var k = g.LinkStart[h]; k < g.LinkStart[h + 1]; k++)
                        Relax(g.Links[k], cur + CampaignPathGrid.BridgeCost, ring, ref pending);
                }
                bucket.Clear();
            }
        }

        private void Relax(int nb, uint nd, int ring, ref int pending)
        {
            if (_done[nb] == _gen) return;
            if (_seen[nb] == _gen && nd >= _dist[nb]) return;
            _seen[nb] = _gen;
            _dist[nb] = nd;
            _buckets[nd % ring].Add(nb);
            pending++;
        }
    }

    /// <summary>The file keeps a path cost rounded down to a multiple of 4 (combi map 1: steps of 75 store 72, 148, 224,
    /// 300); the search itself runs on the exact sums.</summary>
    public static uint Stored(uint cost) => cost & ~3u;

    public sealed record Options
    {
        /// <summary>Search direction for the costs: false = from the landmark outwards, true = towards it.</summary>
        public bool Inwards { get; init; }
        public int MaxThreads { get; init; }
    }

    public static SpdData Build(CampaignPathGrid grid, MapDataRegions regions, uint timestamp, Action<string>? log = null, Options? options = null)
    {
        options ??= new Options();
        var w = grid.Width;
        var n = w * grid.Height;
        var piece = Pieces(grid, out var pieceCount);
        var pieceHexes = new List<int>[pieceCount];
        for (var i = 0; i < pieceCount; i++) pieceHexes[i] = [];
        for (var h = 0; h < n; h++) if (piece[h] >= 0) pieceHexes[piece[h]].Add(h);
        log?.Invoke($"spd: {pieceCount} landmark sets");

        // areas in region order, then area order; their passable hexes
        var areaKeys = new List<int>();
        var areaIndex = new Dictionary<int, int>();
        for (var r = 0; r < regions.Regions.Count; r++)
            for (var a = 0; a < regions.Regions[r].Areas.Count; a++)
            {
                var key = MapDataRegions.AreaKey(r, a);
                areaIndex[key] = areaKeys.Count;
                areaKeys.Add(key);
            }
        var areaHexes = areaKeys.Select(_ => new List<int>()).ToArray();
        var areaAll = areaKeys.Select(_ => new List<int>()).ToArray();
        for (var h = 0; h < n; h++)
            if (areaIndex.TryGetValue(regions.AreaMap[h], out var ai))
            {
                areaAll[ai].Add(h);
                if (grid.Types[h] != 2) areaHexes[ai].Add(h);
            }

        var values = new uint[(long)n * SpdData.Stride];
        Array.Fill(values, SpdData.NoPath);
        var written = new bool[n];
        var setLandmarks = pieceHexes.Select(p => Landmarks(grid, p)).ToArray();
        var areaLandmarks = areaHexes.Select((p, i) => p.Count > 0 ? Landmarks(grid, p, areaAll[i]) : null).ToArray();

        var po = new ParallelOptions { MaxDegreeOfParallelism = options.MaxThreads > 0 ? options.MaxThreads : Environment.ProcessorCount };
        var scratch = new ThreadLocal<Search>(() => new Search(grid));
        Parallel.For(0, pieceCount * 8, po, k =>
        {
            var p = k / 8;
            var slot = k % 8;
            scratch.Value!.Run(setLandmarks[p][slot], options.Inwards, (h, d) =>
            {
                values[(long)h * SpdData.Stride + slot] = Stored(d);
                written[h] = true; // benign race: every writer stores true
                return true;
            });
        });
        Parallel.For(0, areaKeys.Count * 8, po, k =>
        {
            var a = k / 8;
            if (areaLandmarks[a] is not { } lm) return;
            var slot = k % 8;
            var key = areaKeys[a];
            // the search crosses other areas freely; it is done once it has settled every hex of the area it can reach
            var left = areaHexes[a].Count(h => piece[h] == piece[lm[slot]]);
            scratch.Value!.Run(lm[slot], options.Inwards, (h, d) =>
            {
                if (regions.AreaMap[h] != key) return true;
                values[(long)h * SpdData.Stride + 8 + slot] = Stored(d);
                return --left > 0;
            });
        });

        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
        for (var h = 0; h < n; h++)
        {
            if (!written[h]) continue;
            int x = h % w, y = h / w;
            bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y);
        }
        var spd = new SpdData { Timestamp = timestamp, X0 = bx0, Y0 = by0, X1 = bx1, Y1 = by1 };
        var bw = spd.Width;
        var cells = bw * spd.Height;
        spd.Values = new uint[(long)cells * SpdData.Stride];
        spd.Sets = new uint[cells];
        spd.Areas = new RegionArea[cells];
        for (var y = by0; y <= by1; y++)
        {
            Array.Copy(values, (long)(y * w + bx0) * SpdData.Stride, spd.Values, (long)(y - by0) * bw * SpdData.Stride, (long)bw * SpdData.Stride);
            for (var x = bx0; x <= bx1; x++)
            {
                var h = y * w + x;
                var c = (y - by0) * bw + (x - bx0);
                spd.Sets[c] = piece[h] >= 0 ? (uint)piece[h] : SpdData.NoSet;
                spd.Areas[c] = grid.Types[h] != 2 ? MapDataRegions.ToRegionArea(regions.AreaMap[h]) : new RegionArea(SpdData.None, SpdData.None);
            }
        }
        foreach (var lm in setLandmarks)
            foreach (var h in lm) spd.SetLandmarks.Add(((ushort)(h % w), (ushort)(h / w)));
        for (var a = 0; a < areaKeys.Count; a++)
            spd.AreaLandmarks.Add((MapDataRegions.ToRegionArea(areaKeys[a]),
                areaLandmarks[a] is { } lm ? lm.Select(h => ((ushort)(h % w), (ushort)(h / w))).ToArray()
                                           : Enumerable.Repeat((SpdData.None, SpdData.None), 8).ToArray()));
        return spd;
    }
}
