using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// The game's campaign movement grid (EMPIRECAMPAIGN::CAMPAIGN_PATHFINDER) as the campaign AI's offline analysis sees
/// it, rebuilt from pathfinding.ppd, map_data.esf and three DB values. Reverse-engineered from
/// empirecampaign.modder.x64.dll (CAMPAIGN_PATHFINDER constructor FUN_1811f7fa0, road application FUN_1812028c0, the
/// AI landmark search FUN_18059f480); see docs/hlp_spd.md.
///
///  - Edge byte per hex and direction (ppd): bits 0-5 index the 256-entry cost table, bit 6 is set to bit 7 on load;
///    the table holds the ppd move costs at i, i+64, i+128, i+192, the beach costs (campaign variables
///    pathfinding_land_to_sea / sea_to_land_beach_transition_action_point_cost) at 1 and 2, and the road cost
///    (campaign_map_roads, lowest threshold of the campaign) at 62.
///  - Roads: every road hex's masked edges get index 62; a road hex on a river (type 6) gets it on all six edges and
///    so do its neighbours' edges back to it.
///  - Settlements: edges between a slot hex (primary or port slot area) and a neighbour that is land (0), sea (1) or
///    another slot hex cost 0 both ways (observed: identical costs across a settlement and its ring in CA's files).
///  - Moves are allowed by hex type pairs (FUN_1805fa120 + FUN_1805d3a70); type 2 is impassable.
///  - Bridges (ppd): every hex of one side links to every hex of the other side at cost 500.
/// </summary>
public sealed class CampaignPathGrid
{
    public const uint NoEdge = uint.MaxValue;
    public const uint BridgeCost = 500;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Hex type (byte 7 high nibble) per hex.</summary>
    public byte[] Types { get; }
    /// <summary>The ppd's high-level connectivity index (HLCI) per hex, from its tile group (0 without one).</summary>
    public ushort[] Hlci { get; }
    /// <summary>Cost of leaving hex h in direction d: Forward[h*6+d] (NoEdge when the move is not allowed).</summary>
    public uint[] Forward { get; }
    /// <summary>The game's HEX edge bytes after roads / slots (cost index in the low 7 bits).</summary>
    public byte[] EdgeBytes { get; }
    /// <summary>Reverse search: cost of the neighbour's edge back into h, gated like the game by the type of h first:
    /// Reverse[h*6+d] = allowed(type h → type n) ? cost(n, d+3) : NoEdge.</summary>
    public uint[] Reverse { get; }
    /// <summary>Neighbour index of hex h in direction d, or −1.</summary>
    public int[] Neighbour { get; }
    /// <summary>Bridge links (CSR): LinkStart[h]..LinkStart[h+1] index into Links.</summary>
    public int[] LinkStart { get; }
    public int[] Links { get; }
    /// <summary>Largest finite edge cost (for bucket queues).</summary>
    public uint MaxEdgeCost { get; }

    /// <summary>Allowed moves by hex type: bit u of TypeMask[t] = may move from type t to type u.</summary>
    public static readonly ushort[] TypeMask = BuildTypeMask();

    private static ushort[] BuildTypeMask()
    {
        var m = new ushort[16];
        (int, int)[] pairs =
        [
            (0, 0), (1, 1), (4, 4), (8, 8), (9, 9), (0, 6), (6, 0), (1, 3), (3, 1), (0, 4), (4, 0), (1, 8), (8, 1), (1, 7), (7, 1),
            (0, 5), (5, 0), (4, 5), (5, 4), (8, 7), (7, 8), (4, 6), (6, 4), (8, 3), (3, 8), (9, 0), (0, 9), (9, 3), (3, 9), (9, 4),
            (4, 9), (9, 5), (5, 9),
            // FUN_1805d3a70 (the table the AI searches use)
            (0, 3), (3, 0), (0, 7), (7, 0), (4, 7), (7, 4), (4, 3), (3, 4), (9, 7), (7, 9),
        ];
        foreach (var (t, u) in pairs) m[t] |= (ushort)(1 << u);
        return m;
    }

    /// <summary>Defaults: WH3's values (every vanilla campaign's lowest-threshold road costs 80; both beach costs 2100). The old
    /// combi and chaos maps' playable-areas rows now name *_old campaigns with no road rows, so they get the default.</summary>
    public sealed record Settings(uint RoadCost = 80, uint LandToSeaCost = 2100, uint SeaToLandCost = 2100);

    public int Index(int x, int y) => y * Width + x;

    public CampaignPathGrid(PathfindingPpd ppd, MapDataRegions? regions, Settings? settings = null)
    {
        settings ??= new Settings();
        Width = ppd.Width;
        Height = ppd.Height;
        var n = Width * Height;
        var edges = new byte[n * 6];
        Types = new byte[n];
        Hlci = new ushort[n];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var tg = ppd.TileGroup(x, y);
            Hlci[y * Width + x] = tg < ppd.TileGroups.Count ? ppd.TileGroups[tg].Hlci : (ushort)0;
        }
        for (var h = 0; h < n; h++)
        {
            for (var d = 0; d < 6; d++)
            {
                var e = ppd.Cells[h * 8 + d];
                edges[h * 6 + d] = (byte)((e & 0xBF) | (e >> 1 & 0x40));
            }
            Types[h] = (byte)(ppd.Cells[h * 8 + 7] >> 4);
        }
        var table = new uint[256];
        for (var i = 0; i < ppd.MoveCosts.Length && i < 64; i++)
            for (var b = 0; b < 256; b += 64) table[i + b] = ppd.MoveCosts[i];
        const int roadSlot = 0x3E;
        for (var b = 0; b < 256; b += 64)
        {
            table[1 + b] = settings.LandToSeaCost;
            table[2 + b] = settings.SeaToLandCost;
            table[roadSlot + b] = settings.RoadCost;
        }

        Neighbour = new int[n * 6];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        for (var d = 0; d < 6; d++)
            Neighbour[(y * Width + x) * 6 + d] = ppd.Neighbour(x, y, d, out var nx, out var ny) ? ny * Width + nx : -1;

        void SetMask(int h, int mask, byte index)
        {
            for (var d = 0; d < 6; d++)
                if ((mask >> d & 1) != 0) edges[h * 6 + d] = (byte)(edges[h * 6 + d] & 0xC0 | index);
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

        // edges a settlement slot opens (slot hex to land, sea or slot, both ways): WH3's ppd types slot hexes as plain
        // land, so these bypass the type-pair gate (combi map 1: Arnheim's port slot reaches the sea at cost 0)
        var slotEdge = new bool[n * 6];
        if (regions is not null)
        {
            var slot = new bool[n];
            // research: SPD_SKIP_SLOT=<region key> leaves one settlement's slot area out
            foreach (var r in regions.Regions.Where(r => r.Key != Environment.GetEnvironmentVariable("SPD_SKIP_SLOT")))
                foreach (var (x, y) in r.PrimarySlot.Concat(r.PortSlot))
                    if ((uint)x < (uint)Width && (uint)y < (uint)Height) slot[Index(x, y)] = true;
            for (var h = 0; h < n; h++)
            {
                if (!slot[h]) continue;
                // the settlement slot area is walkable even where the ppd marks a hex impassable (190E: 823,261 / 912,773
                // get the slot's 0 cost in CA's spd)
                if (Types[h] == 2) Types[h] = 0;
                for (var d = 0; d < 6; d++)
                {
                    var nb = Neighbour[h * 6 + d];
                    if (nb < 0) continue;
                    // land, sea and other slot hexes: free, whatever the hex types; bridge decks (type 5): free where the
                    // types allow the move (combi map 1: Isle of Wight's and Fu Chow's land slots step onto their bridges
                    // at their own cost, Lothern's type-3 slot does not)
                    var deck = Types[nb] == 5 && !slot[nb];
                    if (!(Types[nb] <= 1 || deck || slot[nb])) continue;
                    edges[h * 6 + d] &= 0xC0;
                    edges[nb * 6 + (d + 3) % 6] &= 0xC0;
                    if (!deck) slotEdge[h * 6 + d] = slotEdge[nb * 6 + (d + 3) % 6] = true;
                }
            }
        }

        Forward = new uint[n * 6];
        Reverse = new uint[n * 6];
        uint max = BridgeCost;
        for (var h = 0; h < n; h++)
        {
            var mask = TypeMask[Types[h]];
            for (var d = 0; d < 6; d++)
            {
                var nb = Neighbour[h * 6 + d];
                if (nb < 0 || (mask >> Types[nb] & 1) == 0 && !slotEdge[h * 6 + d])
                {
                    Forward[h * 6 + d] = Reverse[h * 6 + d] = NoEdge;
                    continue;
                }
                var f = table[edges[h * 6 + d] & 0x7F];
                var r = table[edges[nb * 6 + (d + 3) % 6] & 0x7F];
                Forward[h * 6 + d] = f;
                Reverse[h * 6 + d] = r;
                if (f != NoEdge && f > max) max = f;
                if (r != NoEdge && r > max) max = r;
            }
        }
        MaxEdgeCost = max;
        EdgeBytes = edges;

        var lists = new Dictionary<int, List<int>>();
        foreach (var (a, b) in ppd.Bridges)
        {
            // CAMPAIGN_PATHFINDER load (FUN_1811f7fa0) assigns, not appends: a hex listed by several bridges keeps the
            // last bridge's other bank
            foreach (var (x, y) in a) lists[Index(x, y)] = b.Select(p => Index(p.X, p.Y)).ToList();
            foreach (var (x, y) in b) lists[Index(x, y)] = a.Select(p => Index(p.X, p.Y)).ToList();
        }
        LinkStart = new int[n + 1];
        var all = new List<int>();
        for (var h = 0; h < n; h++)
        {
            LinkStart[h] = all.Count;
            if (lists.TryGetValue(h, out var l)) all.AddRange(l);
        }
        LinkStart[n] = all.Count;
        Links = all.ToArray();
    }

    /// <summary>Shortest path costs from <paramref name="source"/> over the whole grid (uint.MaxValue = unreachable).
    /// reverse = costs of paths from every hex to the source. Dial's bucket queue: costs are small integers.
    /// <paramref name="visit"/> gets each hex as it is settled, in increasing cost order.</summary>
    /// <summary>
    /// The landmark search exactly as the game runs it (FUN_18059f480): a binary heap of (hex, cost) entries ordered by
    /// cost only, with duplicates instead of decrease-key, a hex skipped when already visited. With
    /// <paramref name="aliasVisited"/> the visited set behaves like the game's 1024×1024 sparse map: coordinates ≥ 1024
    /// share the visited flag of 960 + (c &amp; 63) (64-hex leaves), so on maps wider or taller than 1024 hexes some hexes are never
    /// settled. <paramref name="visit"/> gets every settled hex in pop order.
    /// </summary>
    /// <summary>Research: landmark hexes for SPD_TRACE.</summary>
    public int[] TraceSources = [];

    public void SearchGame(int source, bool reverse, bool aliasVisited, Action<int, uint> visit)
    {
        var n = Width * Height;
        var visited = aliasVisited ? new bool[1024 * 1024] : new bool[n];
        int Key(int h)
        {
            if (!aliasVisited) return h;
            int x = h % Width, y = h / Width;
            if (x >= 1024) x = 960 + (x & 63); // CAI_SPARSE_MAP<1024,64,bool>: 64-hex leaves (checked in the DLL)
            if (y >= 1024) y = 960 + (y & 63);
            return y * 1024 + x;
        }
        var heapH = new List<int>(1 << 16);
        var heapC = new List<uint>(1 << 16);
        void Push(int h, uint c)
        {
            heapH.Add(h); heapC.Add(c);
            var i = heapH.Count - 1;
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (heapC[parent] <= c) break;
                heapH[i] = heapH[parent]; heapC[i] = heapC[parent];
                i = parent;
            }
            heapH[i] = h; heapC[i] = c;
        }
        (int H, uint C) Pop()
        {
            var cnt = heapH.Count;
            var topH = heapH[0]; var topC = heapC[0];
            if (cnt > 1)
            {
                var vh = heapH[cnt - 1]; var vc = heapC[cnt - 1];
                var len = cnt - 1;
                var hole = 0;
                var maxNonLeaf = (len - 1) >> 1;
                while (hole < maxNonLeaf)
                {
                    var child = 2 * hole + 2;
                    if (heapC[child - 1] < heapC[child]) child--;
                    heapH[hole] = heapH[child]; heapC[hole] = heapC[child];
                    hole = child;
                }
                if (hole == maxNonLeaf && (len & 1) == 0)
                {
                    heapH[hole] = heapH[len - 1]; heapC[hole] = heapC[len - 1];
                    hole = len - 1;
                }
                while (hole > 0)
                {
                    var parent = (hole - 1) >> 1;
                    if (!(vc < heapC[parent])) break;
                    heapH[hole] = heapH[parent]; heapC[hole] = heapC[parent];
                    hole = parent;
                }
                heapH[hole] = vh; heapC[hole] = vc;
            }
            heapH.RemoveAt(cnt - 1); heapC.RemoveAt(cnt - 1);
            return (topH, topC);
        }
        var costs = reverse ? Reverse : Forward;
        var trace = Environment.GetEnvironmentVariable("SPD_TRACE") is { } tr && tr.Split(',') is [var tx, var ty, var tk] &&
                    int.Parse(tk) == (reverse ? 1 : 0) + 2 * Array.IndexOf(TraceSources, source)
            ? Key(int.Parse(ty) * Width + int.Parse(tx)) : -1;
        Push(source, 0);
        while (heapH.Count > 0)
        {
            var (h, c) = Pop();
            var k = Key(h);
            if (trace >= 0)
            {
                int hx = h % Width, hy = h / Width;
                int V(int q) => q < 1024 ? q : 992 + (q & 31);
                if (k == trace || V(hx) == trace % 1024 && V(hy) == trace / 1024)
                    Console.Error.WriteLine($"pop ({hx},{hy}) cost {c} key ({k % 1024},{k / 1024}) {(visited[k] ? "skip" : "SETTLE")}");
            }
            if (visited[k]) continue;
            visited[k] = true;
            visit(h, c);
            var o = h * 6;
            for (var d = 0; d < 6; d++)
            {
                var ec = costs[o + d];
                if (ec == NoEdge) continue;
                var nb = Neighbour[o + d];
                if (visited[Key(nb)]) continue;
                Push(nb, c + ec);
            }
            for (var l = LinkStart[h]; l < LinkStart[h + 1]; l++)
            {
                var nb = Links[l];
                if (visited[Key(nb)]) continue;
                Push(nb, c + BridgeCost);
            }
        }
    }

    public uint[] Search(int source, bool reverse, Action<int, uint>? visit = null)
    {
        var n = Width * Height;
        var dist = new uint[n];
        Array.Fill(dist, uint.MaxValue);
        var done = new bool[n];
        var ring = (int)MaxEdgeCost + 1;
        var buckets = new List<int>[ring];
        for (var i = 0; i < ring; i++) buckets[i] = [];
        var costs = reverse ? Reverse : Forward;
        dist[source] = 0;
        buckets[0].Add(source);
        var pending = 1;
        for (uint cur = 0; pending > 0; cur++)
        {
            var bucket = buckets[cur % ring];
            for (var i = 0; i < bucket.Count; i++)
            {
                var h = bucket[i];
                pending--;
                if (done[h] || dist[h] != cur) continue;
                done[h] = true;
                visit?.Invoke(h, cur);
                var o = h * 6;
                for (var d = 0; d < 6; d++)
                {
                    var c = costs[o + d];
                    if (c == NoEdge) continue;
                    var nb = Neighbour[o + d];
                    if (done[nb]) continue;
                    var nd = cur + c;
                    if (nd < dist[nb])
                    {
                        dist[nb] = nd;
                        buckets[nd % ring].Add(nb);
                        pending++;
                    }
                }
                for (var k = LinkStart[h]; k < LinkStart[h + 1]; k++)
                {
                    var nb = Links[k];
                    if (done[nb]) continue;
                    var nd = cur + BridgeCost;
                    if (nd < dist[nb])
                    {
                        dist[nb] = nd;
                        buckets[nd % ring].Add(nb);
                        pending++;
                    }
                }
            }
            bucket.Clear();
        }
        return dist;
    }
}
