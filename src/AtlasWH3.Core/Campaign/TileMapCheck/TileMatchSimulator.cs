using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>Counts from a simulated Tilemap run. PlacedPerPass uses BOB's log names (large, transition, junction,
/// link_target, linked, all). BobFailedMessages (BOB's "Failed to find tile for point" lines) is approximate.</summary>
public sealed record TileMatchSummary(int Placed, int BobFailedMessages, IReadOnlyDictionary<string, int> PlacedPerPass, int NoTilePoints);

/// <summary>One placed tile: the variation's model folder, its scan origin (tile-map pixel, y = 0 south), rotation
/// (0x10/0x20/0x40/0x80), climate index and layer (1 = the tile, 2 = an also-place tile under it).</summary>
/// <param name="AnchorX">First covered point (sub-tiles in row order, north row first), where BOB stores the instance.</param>
public sealed record SimulatedTile(string Location, int X, int Y, int Rotation, int Climate, int Layer, int AnchorX, int AnchorY);

public sealed record TileMatchResult(TileMatchSummary Summary, IReadOnlyList<(int X, int Y)> NoTile, IReadOnlyList<SimulatedTile> Tiles)
{
    /// <summary>The hexes holding the no-tile points (each hex once), [col, row].</summary>
    public IReadOnlyList<int[]> NoTileHexes(HexTileMap map)
    {
        var seen = new HashSet<int>();
        var result = new List<int[]>();
        foreach (var (x, y) in NoTile)
        {
            // internal y is from the south; image row = H-1-y
            var imageRow = map.PixelHeight - 1 - y;
            var col = x / 2;
            var row = (2 * map.Height - imageRow - (col & 1)) / 2;
            if (col < 0 || col >= map.Width || row < 0 || row >= map.Height) continue;
            if (seen.Add(row * map.Width + col)) result.Add([col, row]);
        }
        return result;
    }
}

/// <summary>
/// Port of BOB's campaign tile matching, WARSCAPE::EDITOR_TILE_MAP::build_tile_map (warscape.modder.x64.dll; notes
/// in docs/bob_tile_matching.md, decompiles in research/bob_re/editor_tile_map, tilematch*). Tile-map pixels are the
/// points; each pixel's exact RGB picks a placement group (tile set, tile or variation colour). Passes large,
/// transition, junction, link target, linked and all place tiles whose sub-tiles all fall in one group, whose
/// links (TLT_EQUALS / TLT_NOT_EQUALS) agree with the neighbours and with the link map earlier tiles left.
/// Same database order, introsort, xoroshiro128+ seed and quirks as BOB. Checked against BOB runs: vanilla per-pass
/// counts large/transition/junction/link-target are exact, linked/all within 0.05%, and the uncovered points are
/// BOB's (vanilla 175 of 176, main190 all 54 plus 4). Which of several equally ranked edge tiles BOB tries first is
/// not reproduced yet (~12% of records differ in variant or rotation), and with it BobFailedMessages, which depends on
/// the last candidate of the final pass, is approximate.
/// </summary>
public sealed class TileMatchSimulator
{
    private const int Invalid = -1;

    private sealed class Tile
    {
        public required CampaignTile Source;
        public int Index;                 // position after TILE_DATABASE::sort
        public int Set;                   // tile-set index
        public int W, H;
        public bool[] Mask = [];          // row-major, row 0 north; empty = all valid
        public bool Masked => Mask.Length != 0;
        public bool MaskOk;               // mask length == W*H (else every masked sub-tile is invalid)
        public int ValidCount;
        public Link[] Links = [];
        public (int Set, int X, int Y)[] Targets = [];
        public bool RandomRotatable;
        public int VariationCount;
        public string Variation0 = "";
        public (int Set, int Lx, int Ly, int Tx, int Ty)[] LinkEntries = [];   // EDITOR_TILE_MAP this+0x68
        public bool[] MatchesGroup = [];  // TILE_PLACEMENT_GROUPS::matches(group, variation(0))

        public bool SubtileValid(int col, int row)
        {
            if (col < 0 || row < 0 || col >= W || row >= H) return false;
            if (Mask.Length == 0) return true;
            return MaskOk && Mask[row * W + col];
        }
    }

    private sealed record Link(int Set, int X, int Y, int BaseX, int BaseY, bool IsEntry, int BlendSize, bool NoOfflineBlend, bool EqualsTest);

    private sealed class Group
    {
        public uint Rgb;
        public int[] Sets = [];           // tile-set list
        public int[] Tiles = [];          // tiles of the variation list (one entry per variation)
        public string[] VariationKeys = [];
    }

    private readonly CampaignTileDatabase _db;
    private readonly Tile[] _tiles;
    private readonly Group[] _groups;
    private readonly Dictionary<uint, int> _groupByRgb = new();
    private readonly string[] _setLinkAs;
    private readonly Dictionary<string, int> _setByName;
    private readonly int[] _alsoPlace;    // set → also_place set index, -1 none
    private readonly int _setCount;

    public IReadOnlyList<string> Messages => _messages;
    public Action<string>? Log { get; init; }
    /// <summary>Run only the first n passes (large, transition, junction, link target, linked, all); diagnostics.</summary>
    public int MaxPasses { get; init; } = 6;
    /// <summary>Checked once per scanned row.</summary>
    public CancellationToken Cancel { get; init; }
    private readonly List<string> _messages = [];

    public TileMatchSimulator(CampaignTileDatabase db)
    {
        _db = db;
        _setCount = db.TileSets.Count;
        _setByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _setCount; i++) _setByName.TryAdd(db.TileSets[i].Name, i);
        _setLinkAs = db.TileSets.Select(s => s.LinkAs).ToArray();
        _alsoPlace = db.TileSets.Select(s => s.AlsoPlaceTileSet.Length > 0 && _setByName.TryGetValue(s.AlsoPlaceTileSet, out var a) ? a : -1).ToArray();

        // TILE_DATABASE: tiles in VFS listing order (lower-case file name, ordinal: '_' before letters), then
        // TILE_DATABASE::sort
        var tiles = db.Tiles.OrderBy(t => t.File.ToLowerInvariant(), StringComparer.Ordinal).Select(Build).ToArray();
        MsvcSort.Sort(tiles, DatabaseLess);
        for (var i = 0; i < tiles.Length; i++) tiles[i].Index = i;
        _tiles = tiles;

        foreach (var t in _tiles) BuildLinkEntries(t);
        _groups = BuildGroups();
        foreach (var t in _tiles)
        {
            t.MatchesGroup = new bool[_groups.Length];
            for (var g = 0; g < _groups.Length; g++)
                t.MatchesGroup[g] = _groups[g].Sets.Contains(t.Set) || _groups[g].VariationKeys.Contains(VariationKey(t.Source, 0));
        }
    }

    private int SetIndex(string name) => _setByName.TryGetValue(name, out var i) ? i : Invalid;

    private Tile Build(CampaignTile t)
    {
        var tile = new Tile
        {
            Source = t, Set = SetIndex(t.TileSet), W = t.Width, H = t.Height,
            Mask = t.Mask.Select(c => c == '1').ToArray(),
            RandomRotatable = t.RandomRotatable, VariationCount = t.Variations.Count,
            Variation0 = t.Variations.Count > 0 ? t.Variations[0].Location : "",
            Links = t.Links.Select(l => new Link(SetIndex(l.LinkSet), l.X, l.Y, l.BaseX, l.BaseY, l.IsEntry, l.BlendSize, l.NoOfflineBlend, l.TestEquals)).ToArray(),
            Targets = t.LinkTargets.Select(x => (SetIndex(x.TargetSet), x.X, x.Y)).ToArray(),
        };
        tile.MaskOk = tile.Mask.Length == tile.W * tile.H;
        for (var r = 0; r < tile.H; r++)
            for (var c = 0; c < tile.W; c++)
                if (tile.SubtileValid(c, r)) tile.ValidCount++;
        return tile;
    }

    private static string VariationKey(CampaignTile t, int v) => t.File + "#" + v;

    /// <summary>TILE_DATABASE::sort comparator: larger area first, then more link targets, then name (ordinal).
    /// BOB runs the sort before link targets are filled in: link_target_count() is 0 for every tile at that point
    /// (Frida dump of a vanilla Tilemap run, 2026-10-04, research/bob_re/frida_out), so the targets term never
    /// separates two tiles and the order is area, then name.</summary>
    private static bool DatabaseLess(Tile a, Tile b)
    {
        int areaA = a.W * a.H, areaB = b.W * b.H;
        if (areaA != areaB) return areaA > areaB;
        return string.CompareOrdinal(a.Source.Name, b.Source.Name) < 0;
    }

    /// <summary>EDITOR_TILE_MAP constructor: for each TLT_EQUALS link whose clamped point is one of the tile's
    /// targets of the same link_as set, remember (target set, link point, target point), both y-inverted.</summary>
    private void BuildLinkEntries(Tile t)
    {
        var entries = new List<(int, int, int, int, int)>();
        foreach (var l in t.Links)
        {
            if (!l.EqualsTest) continue;
            int lx = Math.Clamp(l.X, 0, Math.Max(0, t.W - 1)), ly = Math.Clamp(l.Y, 0, Math.Max(0, t.H - 1));
            foreach (var target in t.Targets)
            {
                if (target.X != lx || target.Y != ly) continue;
                if (l.Set < 0 || target.Set < 0 || _setLinkAs[l.Set] != _setLinkAs[target.Set]) continue;
                var s = SetIndex(_setLinkAs[target.Set]);
                if (s < 0) { _messages.Add($"Failed to find tile set {_setLinkAs[target.Set]} to link to {t.Source.Name}"); continue; }
                entries.Add((s, l.X, t.H - l.Y - 1, target.X, t.H - target.Y - 1));
            }
        }
        t.LinkEntries = [.. entries];
    }

    /// <summary>TILE_PLACEMENT_GROUPS::init_from_tile_database: a group per tile set, per coloured tile and per
    /// coloured variation (find_group: first exact RGB match wins).</summary>
    private Group[] BuildGroups()
    {
        var groups = new List<Group>();
        for (var s = 0; s < _setCount; s++)
            groups.Add(new Group { Rgb = _db.TileSets[s].Rgb, Sets = [s] });
        foreach (var t in _tiles)
            if (t.Source.Rgb != 0)
                groups.Add(new Group
                {
                    Rgb = t.Source.Rgb,
                    Tiles = Enumerable.Repeat(t.Index, t.VariationCount).ToArray(),
                    VariationKeys = Enumerable.Range(0, t.VariationCount).Select(v => VariationKey(t.Source, v)).ToArray(),
                });
        foreach (var t in _tiles)
            for (var v = 0; v < t.VariationCount; v++)
                if (t.Source.Variations[v].Rgb != 0)
                    groups.Add(new Group { Rgb = t.Source.Variations[v].Rgb, Tiles = [t.Index], VariationKeys = [VariationKey(t.Source, v)] });
        for (var g = 0; g < groups.Count; g++) _groupByRgb.TryAdd(groups[g].Rgb, g);
        return [.. groups];
    }

    /// <summary>The tile sets a group stands for (add_tile_sets_from_group).</summary>
    private IEnumerable<int> GroupSets(int g) => _groups[g].Sets.Concat(_groups[g].Tiles.Select(t => _tiles[t].Set)).Distinct();

    /// <summary>contains_link_as: a set (or a variation's tile's set) in the group links as the set's link_as.</summary>
    private bool MatchesLinkAs(int g, int set)
    {
        var name = _setLinkAs[set];
        foreach (var t in _groups[g].Tiles) if (_setLinkAs[_tiles[t].Set] == name) return true;
        foreach (var s in _groups[g].Sets) if (_setLinkAs[s] == name) return true;
        return false;
    }

    /// <summary>add_tiles_from_group: the variation list, then every variation of every tile in each set
    /// (database order). Entries are tiles, one per variation.</summary>
    private List<int> GroupTiles(int g)
    {
        var list = new List<int>(_groups[g].Tiles);
        foreach (var s in _groups[g].Sets)
            foreach (var t in _tiles)
                if (t.Set == s)
                    for (var v = 0; v < t.VariationCount; v++) list.Add(t.Index);
        return list;
    }

    // ------------------------------------------------------------------ state

    private int _w, _h;
    private int[] _group = [];            // per point, Invalid = no group
    private byte[] _climate = [];
    private int[] _layer1 = [];           // per point: placed tile index + 1 (0 = empty)
    private bool[] _layer2 = [];
    private (int MinX, int MinY, int MaxX, int MaxY)[] _box = [];
    private readonly Dictionary<int, LinkState> _links = new();
    private Xoroshiro _rng;
    private readonly List<SimulatedTile> _placed = [];
    private readonly Dictionary<(int Group, int Tile), List<int>> _matching = new();

    private sealed class LinkState
    {
        public ulong Flags;
        public Dictionary<int, (int X, int Y)[]>? Points;
        public bool Empty => Flags == 0;
        public bool Has(int id) => (Flags >> id & 1) != 0;
        public void Set(int id) => Flags |= 1UL << id;
        public void Add(int id, (int X, int Y) p)
        {
            Set(id);
            Points ??= new();
            if (!Points.TryGetValue(id, out var pts)) Points[id] = pts = new (int, int)[4];
            for (var i = 0; i < 4; i++)
                if (pts[i] == (0, 0)) { pts[i] = p; return; }
        }
    }

    private struct Xoroshiro(ulong s0, ulong s1)
    {
        private ulong _s0 = s0, _s1 = s1;

        /// <summary>FUN_1803e2470: the seeding mix.</summary>
        public static Xoroshiro Seed(ulong a, ulong b)
        {
            if (a == 0 && b == 0) { a = 0x33001294d9708f82; b = 0xa524c8b000000004; }
            var t = a ^ b;
            return new Xoroshiro(ulong.RotateRight(a, 9) ^ (t << 14) ^ t, ulong.RotateRight(t, 28));
        }

        public ushort Next16()
        {
            var result = _s0 + _s1;
            var t = _s1 ^ _s0;
            _s0 = ulong.RotateLeft(_s0, 55) ^ t ^ (t << 14);
            _s1 = ulong.RotateLeft(t, 36);
            return (ushort)(result >> 48);
        }

        /// <summary>BOB's bounded draw: reject values ≤ 0xffff % n, then take the value mod n.</summary>
        public int Next(int n)
        {
            if (n <= 0) return 0;
            var threshold = 0xffff % n;
            int r;
            do r = Next16(); while (r <= threshold);
            return r % n;
        }
    }

    private int P(int x, int y) => y * _w + x;
    private int AlsoPlace(Tile t) => t.Set >= 0 ? _alsoPlace[t.Set] : -1;
    private bool Occupied(Tile t, int p) => AlsoPlace(t) >= 0 ? _layer2[p] : _layer1[p] != 0;

    /// <summary>TILE_MAP::rotate_in_tile_space (W, H = tile size; y counted from the tile's south row).</summary>
    private static (int X, int Y) Rotate(int w, int h, int rot, int x, int y) => rot switch
    {
        1 => (y, w - 1 - x),
        2 => (w - 1 - x, h - 1 - y),
        3 => (h - 1 - y, x),
        _ => (x, y),
    };

    private static int RotationCode(int rot) => rot switch { 1 => 0x20, 2 => 0x40, 3 => 0x80, _ => 0x10 };

    // ------------------------------------------------------------------ run

    /// <summary>Runs every pass on a tile map (climate: per image pixel, row-major from the top, as
    /// TileMapValidator.ClimateIndices returns).</summary>
    public TileMatchResult Run(HexTileMap map, byte[] climate)
    {
        _w = map.PixelWidth;
        _h = map.PixelHeight;
        var n = _w * _h;
        _group = new int[n];
        _climate = new byte[n];
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                var image = (_h - 1 - y) * _w + x;
                _group[P(x, y)] = _groupByRgb.TryGetValue(map.Pixels[image], out var g) ? g : Invalid;
                _climate[P(x, y)] = image < climate.Length ? climate[image] : (byte)0;
            }
        _layer1 = new int[n];
        _layer2 = new bool[n];
        _links.Clear();
        _placed.Clear();
        _rng = Xoroshiro.Seed(0x12344332, 0x12344332);
        ScanTileAreas();

        var perPass = new Dictionary<string, int>();
        var failed = 0;
        foreach (var (pass, name) in new[] { (1, "large"), (2, "transition"), (3, "junction"), (4, "link_target"), (5, "linked"), (0, "all") }.Take(MaxPasses))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            perPass[name] = Scan(pass, ref failed);
            Log?.Invoke($"{name}: {perPass[name]:N0} tiles, {sw.Elapsed.TotalSeconds:F1} s");
        }

        var noTile = new List<(int, int)>();
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                var p = P(x, y);
                if (_group[p] != Invalid && _layer1[p] == 0 && !_layer2[p]) noTile.Add((x, y));
            }
        var summary = new TileMatchSummary(perPass.Values.Sum(), failed, perPass, noTile.Count);
        return new TileMatchResult(summary, noTile, [.. _placed]);
    }

    /// <summary>scan_tile_areas: per tile set, the bounds (±128 points) of the points whose group stands for it.</summary>
    private void ScanTileAreas()
    {
        _box = Enumerable.Repeat((int.MaxValue, int.MaxValue, -int.MaxValue, -int.MaxValue), _setCount).ToArray();
        var setsOf = Enumerable.Range(0, _groups.Length).Select(g => GroupSets(g).ToArray()).ToArray();
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                var g = _group[P(x, y)];
                if (g == Invalid) continue;
                foreach (var s in setsOf[g])
                {
                    var b = _box[s];
                    _box[s] = (Math.Min(b.MinX, x - 128), Math.Min(b.MinY, y - 128), Math.Max(b.MaxX, x + 128), Math.Max(b.MaxY, y + 128));
                }
            }
    }

    private bool InBox(Tile t, int x, int y)
    {
        if (t.Set < 0) return false;
        var b = _box[t.Set];
        return x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY;
    }

    /// <summary>Tile files in a pass's candidate order (diagnostics).</summary>
    public IReadOnlyList<string> PassOrder(int pass) => SelectTiles(pass).Select(t => $"{t.Source.File} v{t.ValidCount} t{t.Targets.Length} l{t.Links.Length} r{(t.RandomRotatable ? 1 : 0)}").ToList();

    /// <summary>Tile files in database order (diagnostics).</summary>
    public IReadOnlyList<string> DatabaseOrder => _tiles.Select(t => t.Source.File).ToList();

    /// <summary>select_tiles_for_pass, then the pass sort (std::sort).</summary>
    private Tile[] SelectTiles(int pass)
    {
        var list = new List<Tile>();
        foreach (var t in _tiles)
        {
            if (pass == 4)
            {
                foreach (var l in t.Links)
                    foreach (var u in _tiles)
                        if (u.Links.Length == 0 && u.Set == l.Set && !list.Contains(u)) list.Add(u);
                continue;
            }
            if (pass == 5 && t.Links.Length == 0) continue;
            if (pass == 3)
            {
                var own = t.Set >= 0 ? _setLinkAs[t.Set] : "";
                // FUN_1803e6680 is "not equal": a target of another link_as set makes a junction
                var otherLinkAs = t.Targets.Any(x => x.Set >= 0 && _setLinkAs[x.Set] != own);
                var linkAsSet = t.Set >= 0 && _db.TileSets[t.Set].LinkAsSet.Length > 0 && t.Targets.Length > 0;
                if (!otherLinkAs && t.Targets.Length < 3 && !linkAsSet) continue;
            }
            if (pass == 2 && !t.Targets.Any(x => x.Set != t.Targets[0].Set)) continue;
            if (pass == 1 && !(t.Targets.Length == 0 && !t.Masked && t.W > 7 && t.H > 7)) continue;
            list.Add(t);
        }
        var array = list.ToArray();
        if (pass == 3) MsvcSort.Sort(array, (a, b) => a.Targets.Length < b.Targets.Length);
        else MsvcSort.Sort(array, (a, b) => a.ValidCount != b.ValidCount ? a.ValidCount > b.ValidCount : a.Targets.Length > b.Targets.Length);
        return array;
    }

    private int Scan(int pass, ref int failed)
    {
        var list = SelectTiles(pass);
        if (list.Length == 0) return 0;
        var placed = 0;
        if (pass == 0)
        {
            for (var i = 0; i < list.Length; i++)
            {
                var t = list[i];
                var last = i == list.Length - 1;
                Cancel.ThrowIfCancellationRequested();
                for (var y = 0; y < _h; y++)
                    for (var x = 0; x < _w; x++)
                    {
                        var p = P(x, y);
                        if (!t.Masked && _layer1[p] != 0 && AlsoPlace(t) < 0) continue;
                        if (!InBox(t, x, y)) continue;
                        int climate = _climate[p];
                        if (TestFinalPosition(t, x, y, out var rot, ref climate, out var group))
                        {
                            PlaceChosen(t, x, y, rot, climate, group);
                            placed++;
                        }
                        if (last && _group[p] != Invalid && _layer1[p] == 0 && !_layer2[p]) failed++;
                    }
            }
            return placed;
        }
        for (var y = 0; y < _h; y++)
        {
            Cancel.ThrowIfCancellationRequested();
            for (var x = 0; x < _w; x++)
            {
                var p = P(x, y);
                int climate = _climate[p];
                foreach (var t in list)
                {
                    if (!(pass == 3 || t.Masked || !Occupied(t, p))) continue;
                    if (!InBox(t, x, y)) continue;
                    if (!TestFinalPosition(t, x, y, out var rot, ref climate, out var group)) continue;
                    if (pass == 3 && t.W == 2 && t.H == 2 && TwoHighStripBeside(x, y)) continue;
                    PlaceChosen(t, x, y, rot, climate, group);
                    placed++;
                    if (pass == 5) break;
                }
            }
        }
        return placed;
    }

    /// <summary>The campaign junction-pass rule for 2×2 tiles: skip when the column left (x-1) or right (x+2)
    /// holds a run of the same group exactly 2 points tall (y, y+1, not y-1 or y+2).</summary>
    private bool TwoHighStripBeside(int x, int y)
    {
        int G(int px, int py)
        {
            var i = py * _w + px;                // BOB indexes linearly (rows wrap)
            return i >= 0 && i < _group.Length ? _group[i] : int.MinValue;
        }
        var g = G(x, y);
        foreach (var cx in new[] { x - 1, x + 2 })
            if (G(cx, y) == g && G(cx, y + 1) == g && G(cx, y - 1) != g && G(cx, y + 2) != g) return true;
        return false;
    }

    /// <summary>collect_matching_tiles + random pick + place_tile + add_tile_to_link_map.</summary>
    private void PlaceChosen(Tile candidate, int x, int y, int rot, int climate, int group)
    {
        if (!_matching.TryGetValue((group, candidate.Index), out var matching))
        {
            matching = [];
            foreach (var ti in GroupTiles(group))
            {
                var u = _tiles[ti];
                if (u.W == candidate.W && u.H == candidate.H && u.Links.Length == candidate.Links.Length && AllLinksMatch(u, candidate))
                    matching.Add(ti);
            }
            _matching[(group, candidate.Index)] = matching;
        }
        var chosen = matching.Count > 0 ? _tiles[matching[_rng.Next(matching.Count)]] : candidate;
        if (PlaceTile(chosen, x, y, rot, climate, 1)) PlaceAlsoPlace(chosen, x, y, rot, climate);
        AddToLinkMap(candidate, x, y, rot, group);
    }

    /// <summary>TILE_DATABASE_TILE::all_links_match: same mask and every link of a has an identical one in b.</summary>
    private static bool AllLinksMatch(Tile a, Tile b)
    {
        if (a.Links.Length != b.Links.Length || a.Mask.Length != b.Mask.Length) return false;
        for (var i = 0; i < a.Mask.Length; i++) if (a.Mask[i] != b.Mask[i]) return false;
        foreach (var l in a.Links)
            if (!b.Links.Contains(l)) return false;
        return true;
    }

    /// <summary>TILE_MAP::place_tile: writes each valid sub-tile; stops (keeping what it wrote) at an occupied point.</summary>
    private bool PlaceTile(Tile t, int x, int y, int rot, int climate, int layer)
    {
        var anchor = -1;
        for (var row = 0; row < t.H; row++)
            for (var col = 0; col < t.W; col++)
            {
                if (t.Masked && !t.SubtileValid(col, row)) continue;
                var (rx, ry) = Rotate(t.W, t.H, rot, col, t.H - row - 1);
                int px = x + rx, py = y + ry;
                if (py >= _h || py < 0) continue;
                if (anchor >= 0 && (px >= _w || px < 0)) continue;
                if (px < 0 || px >= _w) continue;
                var p = P(px, py);
                if (layer == 1 ? _layer1[p] != 0 : _layer2[p]) return false;
                if (anchor < 0)
                {
                    anchor = p;
                    _placed.Add(new SimulatedTile(t.Variation0, x, y, RotationCode(rot), climate, layer, px, py));
                }
                if (layer == 1) _layer1[p] = t.Index + 1;
                else _layer2[p] = true;
            }
        return true;
    }

    /// <summary>place_also_place_tiles: under a tile whose set has also_place_tile_set, the first tile of that set
    /// (database order) with the same mask goes in layer 2; else a 1×1 tile of it on each sub-tile.</summary>
    private void PlaceAlsoPlace(Tile t, int x, int y, int rot, int climate)
    {
        var also = AlsoPlace(t);
        if (also < 0) return;
        foreach (var u in _tiles)
        {
            if (u.Set != also) continue;
            if (u.W == t.W && u.H == t.H && u.Mask.SequenceEqual(t.Mask))
            {
                PlaceTile(u, x, y, rot, climate, 2);
                return;
            }
            if (u.W == 1 && u.H == 1)
            {
                for (var row = 0; row < t.H; row++)
                    for (var col = 0; col < t.W; col++)
                    {
                        if (!t.SubtileValid(col, row)) continue;
                        var (rx, ry) = Rotate(t.W, t.H, rot, col, t.H - row - 1);
                        if (!PlaceTile(u, x + rx, y + ry, 0, climate, 2)) return;
                    }
                return;
            }
        }
    }

    /// <summary>test_final_tile_position: try the 4 rotations (space free and links match); draw the unused
    /// variation number; a random-rotatable tile takes the first good rotation from a random start, any other
    /// tile only rotation 0.</summary>
    private bool TestFinalPosition(Tile t, int x, int y, out int rotation, ref int climate, out int group)
    {
        group = Invalid;
        rotation = 0;
        if (!t.Masked)
        {
            // every rotation of an unmasked tile covers its origin point: a wrong group there fails all four
            var o = P(x, y);
            var g0 = _group[o];
            if (g0 == Invalid || !t.MatchesGroup[g0] || (AlsoPlace(t) >= 0 ? _layer2[o] : _layer1[o] != 0)) return false;
        }
        Span<bool> ok = stackalloc bool[4];
        Span<int> anchors = stackalloc int[4];
        var any = false;
        for (var r = 0; r < 4; r++)
        {
            anchors[r] = -1;
            ok[r] = SpaceFree(t, x, y, r, ref anchors[r], ref group) && LinksMatch(t, x, y, r);
            any |= ok[r];
        }
        if (!any) return false;
        _rng.Next(t.VariationCount);
        if (!t.RandomRotatable) return ok[0];
        var start = _rng.Next(4);
        for (var k = 0; k < 4; k++)
        {
            var r = (start + k) % 4;
            if (!ok[r]) continue;
            rotation = r;
            if (t.Masked && anchors[r] >= 0) climate = _climate[anchors[r]];
            return true;
        }
        return false;
    }

    /// <summary>space_free_for_tile: every valid sub-tile on the map, in a group that matches the tile, all in one
    /// group, and free (layer 2 when the set also-places, else layer 1).</summary>
    private bool SpaceFree(Tile t, int x, int y, int rot, ref int anchor, ref int groupOut)
    {
        var seen = Invalid;
        var also = AlsoPlace(t) >= 0;
        for (var row = 0; row < t.H; row++)
            for (var col = 0; col < t.W; col++)
            {
                if (t.Masked && !t.SubtileValid(col, row)) continue;
                var (rx, ry) = Rotate(t.W, t.H, rot, col, t.H - row - 1);
                int px = x + rx, py = y + ry;
                if ((uint)px >= (uint)_w || (uint)py >= (uint)_h) return false;
                var p = P(px, py);
                if (anchor < 0) anchor = p;
                var g = _group[p];
                if (g == Invalid || !t.MatchesGroup[g]) return false;
                if (seen != Invalid && seen != g) return false;
                if (also ? _layer2[p] : _layer1[p] != 0) return false;
                groupOut = g;
                seen = g;
            }
        return true;
    }

    /// <summary>links_match: each link's point (outside the tile) must hold the linked set (TLT_EQUALS, also
    /// checked against the link map) or must not (TLT_NOT_EQUALS). Off-map links are ignored.</summary>
    private bool LinksMatch(Tile t, int x, int y, int rot)
    {
        foreach (var l in t.Links)
        {
            var (rx, ry) = Rotate(t.W, t.H, rot, l.X, t.H - l.Y - 1);
            int px = x + rx, py = y + ry;
            if (px < 0 || py < 0 || px >= _w || py >= _h) continue;     // no transition tile sets on campaign
            var p = P(px, py);
            var g = _group[p];
            var s = l.Set >= 0 ? SetIndex(_setLinkAs[l.Set]) : Invalid;
            if (g == Invalid || s == Invalid) return false;
            bool eq;
            var placed = _layer1[p];
            if (placed == 0) eq = MatchesLinkAs(g, s);
            else eq = _setLinkAs[s] == _setLinkAs[_tiles[placed - 1].Set];
            _links.TryGetValue(p, out var state);
            if (l.EqualsTest)
            {
                if (eq && (state == null || state.Empty)) continue;
                if (state == null || !state.Has(s)) return false;
                var pts = state.Points?.GetValueOrDefault(s);
                if (pts == null || pts.All(q => q == (0, 0))) continue;
                var found = false;
                foreach (var q in pts)
                {
                    if (q == (0, 0)) continue;
                    foreach (var target in t.Targets)
                    {
                        var (tx, ty) = Rotate(t.W, t.H, rot, target.X, t.H - target.Y - 1);
                        if (x + tx == q.X && y + ty == q.Y) { found = true; break; }
                    }
                }
                if (!found) return false;
            }
            else if (eq) return false;
        }
        return true;
    }

    private LinkState State(int p)
    {
        if (!_links.TryGetValue(p, out var s)) _links[p] = s = new LinkState();
        return s;
    }

    /// <summary>add_tile_to_link_map: a tile without targets marks its sub-tiles with its link_as set; a tile with
    /// targets marks them with the "linked" sentinel and records, at each target, the point its link reaches.</summary>
    private void AddToLinkMap(Tile t, int x, int y, int rot, int group)
    {
        if (t.Targets.Length == 0)
        {
            var name = t.Set >= 0 ? _setLinkAs[t.Set] : "";
            var s = SetIndex(name);
            if (s < 0) { _messages.Add($"Failed to find tile set {name} to link to {t.Source.Name}"); return; }
            for (var row = 0; row < t.H; row++)
                for (var col = 0; col < t.W; col++)
                {
                    if (!t.SubtileValid(col, row)) continue;
                    var (rx, ry) = Rotate(t.W, t.H, rot, col, t.H - row - 1);
                    var p = P(x + rx, y + ry);
                    var g = _group[p];
                    if (g != Invalid && t.MatchesGroup[g]) State(p).Set(s);
                }
            return;
        }
        for (var row = 0; row < t.H; row++)
            for (var col = 0; col < t.W; col++)
            {
                if (!t.SubtileValid(col, row)) continue;
                var (rx, ry) = Rotate(t.W, t.H, rot, col, t.H - row - 1);
                State(P(x + rx, y + ry)).Set(_setCount);
            }
        foreach (var e in t.LinkEntries)
        {
            var (tx, ty) = Rotate(t.W, t.H, rot, e.Tx, e.Ty);
            var (lx, ly) = Rotate(t.W, t.H, rot, e.Lx, e.Ly);
            int cx = x + tx, cy = y + ty;
            if ((uint)cx >= (uint)_w || (uint)cy >= (uint)_h) continue;
            State(P(cx, cy)).Add(e.Set, (x + lx, y + ly));
        }
    }
}

/// <summary>MSVC's std::sort (introsort: insertion sort up to 32, median-of-3 / Tukey ninther partition, heap sort
/// when too deep). Unstable, so ties come out exactly as BOB's.</summary>
internal static class MsvcSort
{
    private const int InsertionMax = 32;

    public static void Sort<T>(T[] a, Func<T, T, bool> less) => SortRange(a, 0, a.Length, a.Length, less);

    private static void SortRange<T>(T[] a, int first, int last, int ideal, Func<T, T, bool> less)
    {
        for (;;)
        {
            if (last - first <= InsertionMax)
            {
                InsertionSort(a, first, last, less);
                return;
            }
            if (ideal <= 0)
            {
                HeapSort(a, first, last, less);
                return;
            }
            var (mf, ms) = Partition(a, first, last, less);
            ideal = (ideal >> 1) + (ideal >> 2);
            if (mf - first < last - ms)
            {
                SortRange(a, first, mf, ideal, less);
                first = ms;
            }
            else
            {
                SortRange(a, ms, last, ideal, less);
                last = mf;
            }
        }
    }

    private static void InsertionSort<T>(T[] a, int first, int last, Func<T, T, bool> less)
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
                for (var prev = hole - 1; less(val, a[prev]); hole = prev, prev--)
                    a[hole] = a[prev];
                a[hole] = val;
            }
        }
    }

    private static void Swap<T>(T[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static void Med3<T>(T[] a, int first, int mid, int last, Func<T, T, bool> less)
    {
        if (less(a[mid], a[first])) Swap(a, mid, first);
        if (less(a[last], a[mid]))
        {
            Swap(a, last, mid);
            if (less(a[mid], a[first])) Swap(a, mid, first);
        }
    }

    private static void GuessMedian<T>(T[] a, int first, int mid, int last, Func<T, T, bool> less)
    {
        var count = last - first;
        if (count > 40)
        {
            var step = (count + 1) >> 3;
            var twoStep = step << 1;
            Med3(a, first, first + step, first + twoStep, less);
            Med3(a, mid - step, mid, mid + step, less);
            Med3(a, last - twoStep, last - step, last, less);
            Med3(a, first + step, mid, last - step, less);
        }
        else Med3(a, first, mid, last, less);
    }

    private static (int, int) Partition<T>(T[] a, int first, int last, Func<T, T, bool> less)
    {
        var mid = first + ((last - first) >> 1);
        GuessMedian(a, first, mid, last - 1, less);
        int pfirst = mid, plast = pfirst + 1;
        while (first < pfirst && !less(a[pfirst - 1], a[pfirst]) && !less(a[pfirst], a[pfirst - 1])) pfirst--;
        while (plast < last && !less(a[plast], a[pfirst]) && !less(a[pfirst], a[plast])) plast++;
        int gfirst = plast, glast = pfirst;
        for (;;)
        {
            for (; gfirst < last; gfirst++)
            {
                if (less(a[pfirst], a[gfirst])) continue;
                if (less(a[gfirst], a[pfirst])) break;
                if (plast != gfirst) Swap(a, plast, gfirst);
                plast++;
            }
            for (; first < glast; glast--)
            {
                if (less(a[glast - 1], a[pfirst])) continue;
                if (less(a[pfirst], a[glast - 1])) break;
                if (--pfirst != glast - 1) Swap(a, pfirst, glast - 1);
            }
            if (glast == first && gfirst == last) return (pfirst, plast);
            if (glast == first)
            {
                if (plast != gfirst) Swap(a, pfirst, plast);
                plast++;
                Swap(a, pfirst, gfirst);
                pfirst++;
                gfirst++;
            }
            else if (gfirst == last)
            {
                if (--glast != --pfirst) Swap(a, glast, pfirst);
                Swap(a, pfirst, --plast);
            }
            else Swap(a, gfirst++, --glast);
        }
    }

    // MSVC _Make_heap_unchecked / _Pop_heap_hole_by_index
    private static void HeapSort<T>(T[] a, int first, int last, Func<T, T, bool> less)
    {
        var bottom = last - first;
        for (var hole = bottom >> 1; hole > 0;)
        {
            --hole;
            var val = a[first + hole];
            PopHoleByIndex(a, first, hole, bottom, val, less);
        }
        for (; last - first >= 2; last--)
        {
            var val = a[last - 1];
            a[last - 1] = a[first];
            PopHoleByIndex(a, first, 0, last - 1 - first, val, less);
        }
    }

    private static void PopHoleByIndex<T>(T[] a, int first, int hole, int bottom, T val, Func<T, T, bool> less)
    {
        var top = hole;
        var idx = hole;
        var maxSequenceNonLeaf = (bottom - 1) >> 1;
        while (idx < maxSequenceNonLeaf)
        {
            idx = 2 * idx + 2;
            if (less(a[first + idx], a[first + idx - 1])) idx--;
            a[first + hole] = a[first + idx];
            hole = idx;
        }
        if (idx == maxSequenceNonLeaf && bottom % 2 == 0)
        {
            a[first + hole] = a[first + bottom - 1];
            hole = bottom - 1;
        }
        // push val up from hole toward top
        for (var i = (hole - 1) >> 1; top < hole && less(a[first + i], val); i = (hole - 1) >> 1)
        {
            a[first + hole] = a[first + i];
            hole = i;
        }
        a[first + hole] = val;
    }
}
