namespace AtlasWH3.Core.Campaign.GlobalMesh;

/// <summary>
/// BOB's TRIANGLE_MERGER (tooldatabuilder rigidmodel_optimise.cpp), a vertex-removal decimator. See
/// docs/bob_re_global_mesh.md. Per pass k (edge limit L = k · span / N, N = min(span/6, 50) passes, 4 stagnant passes
/// end it) every vertex v in index order with flag ≥ 2 whose neighbours' normals are all within the factor collapses
/// into the nearest neighbour w (x/z distance) that keeps every rewired triangle non-sliver, within L and correctly
/// wound. Flags: 0 pinned, 2 free, 3 slides only onto 3/0 (map edges).
/// </summary>
public sealed class TriangleMerger
{
    private readonly float[] _x, _y, _z;
    private readonly float[] _n; // 3 per vertex
    private readonly byte[] _flags;

    /// <summary>Research: per-pass / per-vertex trace lines in the format of research/bob_re/frida_gmerge_trace.js.</summary>
    public Action<string>? Trace { get; init; }

    public TriangleMerger(float[] x, float[] y, float[] z, float[] normals, byte[] flags)
    {
        _x = x; _y = y; _z = z; _n = normals; _flags = flags;
    }

    public List<int[]> Run(List<int[]> triangles, float factor, float span = 64f, float heightTolerance = float.MaxValue)
    {
        // FUN_18009b4d0 is max: 50 passes of 64/50 = 1.28 (BOB trace, main190)
        var passes = Math.Max((int)(span * (1f / 6f)), 50);
        var step = span / passes;
        var current = triangles.Select(t => (int[])t.Clone()).ToList();
        int stall = 0, k = 0, multiplier = 1;
        var nv = _x.Length;
        while (k < passes)
        {
            var limit = multiplier * step;
            var limit2 = limit * limit;
            var tri = current;
            Trace?.Invoke($"pass limit {limit.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} tris {tri.Count * 3}");
            var adj = new List<int>[nv];
            for (var t = 0; t < tri.Count; t++)
                foreach (var c in tri[t]) (adj[c] ??= []).Add(t);

            for (var v = 0; v < nv; v++)
            {
                if (_flags[v] < 2 || adj[v] is null) continue;
                var removable = Removable(v, tri, adj[v], factor, heightTolerance);
                Trace?.Invoke($"rem v {v} {(removable ? 1 : 0)}");
                if (!removable) continue;
                var target = Target(v, tri, adj, limit2);
                if (target < 0) continue;
                (adj[target] ??= []).AddRange(adj[v]);
                adj[v] = [];
                foreach (var t in adj[target])
                {
                    var tr = tri[t];
                    for (var q = 0; q < 3; q++) if (tr[q] == v) tr[q] = target;
                    if (Degenerate(tr)) { tr[0] = tr[1] = tr[2] = 0; }
                }
            }
            var next = tri.Where(t => !Degenerate(t)).ToList();
            if (next.Count == current.Count && ++stall > 3)
            {
                k += 10; multiplier += 10; stall = 0;
            }
            current = next;
            k++; multiplier++;
        }
        return current;
    }

    private static bool Degenerate(int[] t) => t[0] == t[1] || t[0] == t[2] || t[1] == t[2];

    private float Dot(int a, int b) =>
        _n[a * 3 + 1] * _n[b * 3 + 1] + _n[a * 3] * _n[b * 3] + _n[a * 3 + 2] * _n[b * 3 + 2];

    private bool Removable(int v, List<int[]> tri, List<int> adj, float factor, float tolerance)
    {
        foreach (var ti in adj)
        {
            var t = tri[ti];
            if (Degenerate(t)) continue;
            int a, b;
            if (t[0] == v) { a = t[1]; b = t[2]; }
            else if (t[1] == v) { a = t[0]; b = t[2]; }
            else if (t[2] == v) { a = t[0]; b = t[1]; }
            else continue;
            foreach (var w in (ReadOnlySpan<int>)[a, b])
            {
                if (MathF.Abs(Dot(v, w)) < factor) return false;
                if (_flags[v] != 4 && _flags[w] is not 1 and not 4 && MathF.Abs(_y[w] - _y[v]) > tolerance) return false;
            }
        }
        return true;
    }

    private int Target(int v, List<int[]> tri, List<int>?[] adj, float limit2)
    {
        var candidates = new List<int>();
        foreach (var ti in adj[v]!)
        {
            var t = tri[ti];
            if (Degenerate(t)) continue;
            foreach (var w in t)
                if (w != v && CanCollapse(v, w, tri, adj, limit2)) candidates.Add(w);
        }
        float vx = _x[v], vz = _z[v];
        var keys = candidates.ToArray();
        MsvcSort.Sort(keys, (a, b) => Dist2(a, vx, vz) < Dist2(b, vx, vz));
        Trace?.Invoke($"cand v {v} ok {(keys.Length > 0 ? 1 : 0)} target {(keys.Length > 0 ? keys[0] : 0)} n {keys.Length} [{string.Join(",", keys)}]");
        return keys.Length == 0 ? -1 : keys[0];
    }

    private float Dist2(int w, float vx, float vz)
    {
        var dz = _z[w] - vz;
        var dx = _x[w] - vx;
        return dz * dz + dx * dx;
    }

    private bool CanCollapse(int v, int w, List<int[]> tri, List<int>?[] adj, float limit2)
    {
        var fv = _flags[v];
        var fw = _flags[w];
        if (fv == 0) return false;
        if (fv == 3 && fw is not 3 and not 0) return false;
        if (fv == 4 && fw is not 4 and not 1) return false;
        foreach (var u in (ReadOnlySpan<int>)[v, w])
        {
            if (adj[u] is null) continue;
            foreach (var ti in adj[u]!)
            {
                var t = tri[ti];
                if (Degenerate(t)) continue;
                var a = t[0] == v ? w : t[0];
                var b = t[1] == v ? w : t[1];
                var c = t[2] == v ? w : t[2];
                var degenerate = a == b || a == c || b == c;
                if (fw > 1 && degenerate && Math.Min(_flags[a], Math.Min(_flags[b], _flags[c])) < 2) return false;
                if (degenerate) continue;
                if (Sliver(a, b, c) || BadShape(a, b, c, limit2)) return false;
            }
        }
        return true;
    }

    /// <summary>FUN_1800cf140: nearly collinear in x/z (|cross of the normalised edges| &lt; 0.1).</summary>
    private bool Sliver(int a, int b, int c)
    {
        if ((_z[a] == _z[b] && _z[b] == _z[c]) || (_x[a] == _x[b] && _x[b] == _x[c])) return true;
        var cx = _x[c] - _x[a]; var cz = _z[c] - _z[a];
        var bx = _x[b] - _x[a]; var bz = _z[b] - _z[a];
        var lac = cz * cz + cx * cx;
        var lab = bz * bz + bx * bx;
        if (lac == 0 || lab == 0) return true;
        var iab = 1f / MathF.Sqrt(lab);
        var iac = 1f / MathF.Sqrt(lac);
        return MathF.Abs(iac * cx * bz * iab - iac * cz * bx * iab) < 0.1f;
    }

    /// <summary>FUN_1800cf290: an edge longer than the pass limit, or the wrong winding in x/z.</summary>
    private bool BadShape(int a, int b, int c, float limit2)
    {
        float abx = _x[b] - _x[a], abz = _z[b] - _z[a];
        float acx = _x[c] - _x[a], acz = _z[c] - _z[a];
        float bcx = _x[c] - _x[b], bcz = _z[c] - _z[b];
        if (abz * abz + abx * abx <= limit2 && acz * acz + acx * acx <= limit2 && bcx * bcx + bcz * bcz <= limit2)
            return (_z[c] - _z[a]) * (_x[b] - _x[a]) - (_z[b] - _z[a]) * (_x[c] - _x[a]) > 0;
        return true;
    }
}

/// <summary>MSVC std::sort (_Sort_unchecked: insertion sort up to 32 elements, median-of-3/9 partition, heap sort when
/// the depth budget runs out), as tooldatabuilder FUN_180133020 sorts TRIANGLE_MERGER's collapse candidates; the
/// candidate order on ties decides the collapse target.</summary>
internal static class MsvcSort
{
    public static void Sort(int[] a, Func<int, int, bool> less) => SortRange(a, 0, a.Length, a.Length, less);

    private static void SortRange(int[] a, int first, int last, int ideal, Func<int, int, bool> less)
    {
        for (;;)
        {
            if (last - first <= 32) { Insertion(a, first, last, less); return; }
            if (ideal <= 0) { HeapSort(a, first, last, less); return; }
            var (pf, pl) = Partition(a, first, last, less);
            ideal = (ideal >> 1) + (ideal >> 2);
            if (pf - first < last - pl) { SortRange(a, first, pf, ideal, less); first = pl; }
            else { SortRange(a, pl, last, ideal, less); last = pf; }
        }
    }

    private static void Insertion(int[] a, int first, int last, Func<int, int, bool> less)
    {
        if (first == last) return;
        for (var mid = first + 1; mid != last; mid++)
        {
            var val = a[mid];
            if (less(val, a[first]))
            {
                Array.Copy(a, first, a, first + 1, mid - first);
                a[first] = val;
            }
            else
            {
                var hole = mid;
                for (var prev = hole - 1; less(val, a[prev]); hole = prev, prev--) a[hole] = a[prev];
                a[hole] = val;
            }
        }
    }

    private static void Swap(int[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static void Med3(int[] a, int first, int mid, int last, Func<int, int, bool> less)
    {
        if (less(a[mid], a[first])) Swap(a, mid, first);
        if (less(a[last], a[mid]))
        {
            Swap(a, last, mid);
            if (less(a[mid], a[first])) Swap(a, mid, first);
        }
    }

    private static void GuessMedian(int[] a, int first, int mid, int last, Func<int, int, bool> less)
    {
        var count = last - first;
        if (40 < count)
        {
            var step = (count + 1) >> 3;
            var two = step << 1;
            Med3(a, first, first + step, first + two, less);
            Med3(a, mid - step, mid, mid + step, less);
            Med3(a, last - two, last - step, last, less);
            Med3(a, first + step, mid, last - step, less);
        }
        else Med3(a, first, mid, last, less);
    }

    private static (int, int) Partition(int[] a, int first, int last, Func<int, int, bool> less)
    {
        var mid = first + ((last - first) >> 1);
        GuessMedian(a, first, mid, last - 1, less);
        var pfirst = mid;
        var plast = pfirst + 1;
        while (first < pfirst && !less(a[pfirst - 1], a[pfirst]) && !less(a[pfirst], a[pfirst - 1])) --pfirst;
        while (plast < last && !less(a[plast], a[pfirst]) && !less(a[pfirst], a[plast])) ++plast;
        var gfirst = plast;
        var glast = pfirst;
        for (;;)
        {
            for (; gfirst < last; ++gfirst)
            {
                if (less(a[pfirst], a[gfirst])) { }
                else if (less(a[gfirst], a[pfirst])) break;
                else if (plast != gfirst) { Swap(a, plast, gfirst); ++plast; }
                else ++plast;
            }
            for (; first < glast; --glast)
            {
                if (less(a[glast - 1], a[pfirst])) { }
                else if (less(a[pfirst], a[glast - 1])) break;
                else if (--pfirst != glast - 1) Swap(a, pfirst, glast - 1);
            }
            if (glast == first && gfirst == last) return (pfirst, plast);
            if (glast == first)
            {
                if (plast != gfirst) Swap(a, pfirst, plast);
                ++plast;
                Swap(a, pfirst, gfirst);
                ++pfirst;
                ++gfirst;
            }
            else if (gfirst == last)
            {
                if (--glast != --pfirst) Swap(a, glast, pfirst);
                Swap(a, pfirst, --plast);
            }
            else
            {
                Swap(a, gfirst, --glast);
                ++gfirst;
            }
        }
    }

    private static void HeapSort(int[] a, int first, int last, Func<int, int, bool> less)
    {
        // std::make_heap + std::sort_heap
        var n = last - first;
        for (var hole = n >> 1; hole > 0;)
        {
            --hole;
            PopHoleDown(a, first, hole, n, a[first + hole], less);
        }
        for (; n >= 2; n--)
        {
            var val = a[first + n - 1];
            a[first + n - 1] = a[first];
            PopHoleDown(a, first, 0, n - 1, val, less);
        }
    }

    private static void PopHoleDown(int[] a, int first, int hole, int bottom, int val, Func<int, int, bool> less)
    {
        var top = hole;
        var idx = hole;
        var maxNonLeaf = (bottom - 1) >> 1;
        while (idx < maxNonLeaf)
        {
            idx = 2 * idx + 2;
            if (less(a[first + idx], a[first + idx - 1])) --idx;
            a[first + hole] = a[first + idx];
            hole = idx;
        }
        if (idx == maxNonLeaf && bottom % 2 == 0)
        {
            a[first + hole] = a[first + bottom - 1];
            hole = bottom - 1;
        }
        for (var parent = (hole - 1) >> 1; top < hole && less(a[first + parent], val); parent = (hole - 1) >> 1)
        {
            a[first + hole] = a[first + parent];
            hole = parent;
        }
        a[first + hole] = val;
    }
}
