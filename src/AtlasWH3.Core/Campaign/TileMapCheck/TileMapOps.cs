using System.Globalization;
using System.Text.Json.Nodes;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>
/// Hex-level edits of a campaign tile map in memory: paint, erase ("remove tiles"), line, fill and replace. Every op
/// is a JSON object (the tiles-edit / edit_tiles schema) and records the hexes it changed. Hexes are [col, row],
/// row 0 = south; tile sets are given by name (e.g. "mountain_rocky") or as "#rrggbb".
///
/// Hex distance and lines use cube coordinates: the grid is odd-q (odd columns half a hex north), so
/// x = col, z = row − (col − (col &amp; 1)) / 2, y = −x − z (matches the neighbour table in <see cref="HexTileMap"/>).
/// </summary>
public sealed class TileMapOps
{
    private readonly HexTileMap _map;
    private readonly Dictionary<uint, string> _setOfColour;
    private readonly Dictionary<string, uint> _colourOfSet;
    private readonly HashSet<(int Col, int Row)> _changed = [];
    private static readonly int AreaCategory = TileMapValidator.Category(null);
    private static readonly int SeaCategory = TileMapValidator.Category("generic_sea");

    public TileMapOps(HexTileMap map, CampaignTileDatabase db)
    {
        if (!map.HasHexLayout) throw new InvalidDataException($"tile map is {map.PixelWidth}x{map.PixelHeight}, not a 2W x (2H+1) hex layout");
        _map = map;
        _setOfColour = TileMapValidator.PlacementColours(db);
        _colourOfSet = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in db.TileSets) _colourOfSet.TryAdd(s.Name, s.Rgb);
    }

    public HexTileMap Map => _map;
    /// <summary>Every hex any op changed so far.</summary>
    public IReadOnlyCollection<(int Col, int Row)> Changed => _changed;

    public uint Colour(int col, int row) { var (x, y) = _map.HexPixel(col, row); return _map.Pixel(x, y); }
    public string? SetAt(int col, int row) => _setOfColour.GetValueOrDefault(Colour(col, row));
    public string? SetOf(uint rgb) => _setOfColour.GetValueOrDefault(rgb);
    public bool InMap(int col, int row) => col >= 0 && row >= 0 && col < _map.Width && row < _map.Height;

    /// <summary>A tile-set name or "#rrggbb" → colour. Unknown names throw; unknown colours are allowed (the validator
    /// reports them).</summary>
    public uint ResolveSet(string set)
    {
        var s = set.Trim();
        if (s.StartsWith('#') && uint.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return rgb & 0xffffff;
        return _colourOfSet.TryGetValue(s, out var c) ? c
            : throw new KeyNotFoundException($"no tile set '{set}' (see tiles-info for the names)");
    }

    /// <summary>Sets one hex; returns whether it changed.</summary>
    public bool Set(int col, int row, uint rgb)
    {
        if (!InMap(col, row) || Colour(col, row) == rgb && _map.HexUniform(col, row)) return false;
        _map.SetHex(col, row, rgb);
        _changed.Add((col, row));
        return true;
    }

    // ---------------------------------------------------------------- geometry

    public static (int X, int Y, int Z) Cube(int col, int row)
    {
        var z = row - (col - (col & 1)) / 2;
        return (col, -col - z, z);
    }

    public static (int Col, int Row) Offset(int x, int z) => (x, z + (x - (x & 1)) / 2);

    public static int Distance((int Col, int Row) a, (int Col, int Row) b)
    {
        var (ax, ay, az) = Cube(a.Col, a.Row);
        var (bx, by, bz) = Cube(b.Col, b.Row);
        return Math.Max(Math.Abs(ax - bx), Math.Max(Math.Abs(ay - by), Math.Abs(az - bz)));
    }

    public IEnumerable<(int Col, int Row)> Neighbours(int col, int row)
    {
        for (var d = 0; d < 6; d++)
            if (_map.Neighbour(col, row, d, out var nc, out var nr)) yield return (nc, nr);
    }

    /// <summary>Hexes within <paramref name="radius"/> steps of a centre (radius 0 = the centre).</summary>
    public IEnumerable<(int Col, int Row)> Circle(int col, int row, int radius)
    {
        var (cx, _, cz) = Cube(col, row);
        for (var dx = -radius; dx <= radius; dx++)
            for (var dz = Math.Max(-radius, -dx - radius); dz <= Math.Min(radius, -dx + radius); dz++)
            {
                var h = Offset(cx + dx, cz + dz);
                if (InMap(h.Col, h.Row)) yield return h;
            }
    }

    public IEnumerable<(int Col, int Row)> Rect(int c0, int r0, int c1, int r1)
    {
        for (var r = Math.Max(0, Math.Min(r0, r1)); r <= Math.Min(_map.Height - 1, Math.Max(r0, r1)); r++)
            for (var c = Math.Max(0, Math.Min(c0, c1)); c <= Math.Min(_map.Width - 1, Math.Max(c0, c1)); c++)
                yield return (c, r);
    }

    /// <summary>Hexes whose centre lies inside a polygon of hex positions (even-odd rule, hex centres at
    /// (col, row + (col &amp; 1) / 2)).</summary>
    public IEnumerable<(int Col, int Row)> Polygon(IReadOnlyList<(int Col, int Row)> poly)
    {
        if (poly.Count < 3) yield break;
        static (double X, double Y) P((int Col, int Row) h) => (h.Col, h.Row + (h.Col & 1) * 0.5);
        var pts = poly.Select(P).ToArray();
        foreach (var h in Rect(poly.Min(p => p.Col), poly.Min(p => p.Row) - 1, poly.Max(p => p.Col), poly.Max(p => p.Row) + 1))
        {
            var (x, y) = P(h);
            var inside = false;
            for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
                if ((pts[i].Y > y) != (pts[j].Y > y) && x < (pts[j].X - pts[i].X) * (y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                    inside = !inside;
            if (inside) yield return h;
        }
    }

    /// <summary>A connected one-hex-wide path through the waypoints (cube line interpolation; each step is to a
    /// neighbour, so the line has no diagonal gaps).</summary>
    public static List<(int Col, int Row)> Line(IReadOnlyList<(int Col, int Row)> waypoints)
    {
        var path = new List<(int Col, int Row)>();
        for (var i = 0; i < waypoints.Count; i++)
        {
            if (i == 0) { path.Add(waypoints[0]); continue; }
            var a = Cube(waypoints[i - 1].Col, waypoints[i - 1].Row);
            var b = Cube(waypoints[i].Col, waypoints[i].Row);
            var n = Distance(waypoints[i - 1], waypoints[i]);
            for (var k = 1; k <= n; k++)
            {
                var t = (double)k / n;
                // nudge so ties between two hexes always break the same way
                var (x, _, z) = CubeRound(a.X + (b.X - a.X) * t + 1e-6, a.Y + (b.Y - a.Y) * t + 2e-6, a.Z + (b.Z - a.Z) * t - 3e-6);
                var h = Offset(x, z);
                if (path[^1] != h) path.Add(h);
            }
        }
        return path;
    }

    private static (int X, int Y, int Z) CubeRound(double x, double y, double z)
    {
        double rx = Math.Round(x), ry = Math.Round(y), rz = Math.Round(z);
        double dx = Math.Abs(rx - x), dy = Math.Abs(ry - y), dz = Math.Abs(rz - z);
        if (dx > dy && dx > dz) rx = -ry - rz;
        else if (dy > dz) ry = -rx - rz;
        else rz = -rx - ry;
        return ((int)rx, (int)ry, (int)rz);
    }

    // ---------------------------------------------------------------- operations

    public int Paint(IEnumerable<(int Col, int Row)> hexes, uint rgb) => hexes.Count(h => Set(h.Col, h.Row, rgb));

    /// <summary>Flood fill from a hex over its connected same-colour area (capped so a click on the sea can't
    /// repaint half the map by accident).</summary>
    public int Fill(int col, int row, uint rgb, int maxHexes = 20000)
    {
        if (!InMap(col, row)) throw new ArgumentException($"hex [{col},{row}] is outside the {_map.Width}x{_map.Height} map");
        var from = Colour(col, row);
        if (from == rgb) return 0;
        var area = new List<(int, int)>();
        var seen = new HashSet<(int, int)> { (col, row) };
        var queue = new Queue<(int Col, int Row)>([(col, row)]);
        while (queue.Count > 0)
        {
            var h = queue.Dequeue();
            area.Add(h);
            if (area.Count > maxHexes)
                throw new InvalidOperationException($"fill area is larger than {maxHexes} hexes; pass a larger \"max\" if you mean it");
            foreach (var n in Neighbours(h.Col, h.Row))
                if (seen.Add(n) && Colour(n.Col, n.Row) == from) queue.Enqueue(n);
        }
        return Paint(area, rgb);
    }

    /// <summary>Replace colour A with colour B inside an area (or the whole map).</summary>
    public int Replace(IEnumerable<(int Col, int Row)> hexes, uint from, uint to) =>
        hexes.Where(h => Colour(h.Col, h.Row) == from).ToList().Count(h => Set(h.Col, h.Row, to));

    /// <summary>
    /// "Remove tiles": every hex goes back to the area set around it. Hexes are resolved from the edge of the area
    /// inwards; each takes the most common area colour (generic / mountain / forest... - not sea, coast, lines) among
    /// its neighbours outside the area or already resolved, else sea when it only touches sea, else
    /// <paramref name="fallback"/>.
    /// </summary>
    public int Erase(IEnumerable<(int Col, int Row)> hexes, uint? fallback = null)
    {
        var todo = hexes.Where(h => InMap(h.Col, h.Row)).ToHashSet();
        var sea = _colourOfSet.GetValueOrDefault("generic_sea", 0x3971b7u);
        var generic = fallback ?? _colourOfSet.GetValueOrDefault("generic", 0x96aa64u);
        var changed = 0;
        while (todo.Count > 0)
        {
            var round = new List<((int Col, int Row) Hex, uint Rgb)>();
            foreach (var h in todo)
            {
                var areas = new List<uint>();
                var seaOnly = true;
                var known = 0;
                foreach (var n in Neighbours(h.Col, h.Row))
                {
                    if (todo.Contains(n)) continue;
                    known++;
                    var c = Colour(n.Col, n.Row);
                    var cat = TileMapValidator.Category(SetOf(c));
                    if (cat == AreaCategory && SetOf(c) is not null) areas.Add(c);
                    if (cat != SeaCategory) seaOnly = false;
                }
                if (known == 0) continue;
                var rgb = areas.Count > 0 ? areas.GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key
                    : seaOnly ? sea : generic;
                round.Add((h, rgb));
            }
            if (round.Count == 0) // an area with no outside neighbours at all (the whole map)
            {
                foreach (var h in todo) changed += Set(h.Col, h.Row, generic) ? 1 : 0;
                break;
            }
            foreach (var (h, rgb) in round)
            {
                changed += Set(h.Col, h.Row, rgb) ? 1 : 0;
                todo.Remove(h);
            }
        }
        return changed;
    }

    // ---------------------------------------------------------------- JSON ops

    /// <summary>
    /// Applies one op and returns a summary. Shapes (any op that takes an area):
    ///   "hexes": [[c,r],...]  |  "circle": [c, r, radius]  |  "rect": [c0, r0, c1, r1]  |  "polygon": [[c,r],...]
    ///   |  "all": true (replace only)
    /// Ops:
    ///   {"op":"paint", "set":..., area}
    ///   {"op":"erase", area, "to"?: set}                     remove tiles: back to the surrounding land / sea
    ///   {"op":"line", "set":..., "points": [[c,r],...], "width"?: 1}   one-hex-wide path (river, road, cliff...)
    ///   {"op":"fill", "set":..., "at": [c,r], "max"?: 20000}  flood fill of the connected same-colour area
    ///   {"op":"replace", "from": set, "to": set, area | "all": true}
    /// </summary>
    public JsonObject Apply(JsonObject op)
    {
        var name = Str(op, "op") ?? throw new ArgumentException("missing \"op\"");
        var before = _changed.Count;
        int n;
        switch (name)
        {
            case "paint":
                n = Paint(Area(op), ResolveSet(Need(op, "set")));
                break;
            case "erase":
                n = Erase(Area(op), Str(op, "to") is { } to ? ResolveSet(to) : null);
                break;
            case "line":
            {
                var points = Hexes(op["points"]) ?? throw new ArgumentException("line needs \"points\": [[c,r],...]");
                if (points.Count < 2) throw new ArgumentException("line needs at least 2 points");
                var width = op["width"] is JsonValue w ? (int)w : 1;
                var path = Line(points);
                n = Paint(width <= 1 ? path : path.SelectMany(h => Circle(h.Col, h.Row, width - 1)).Distinct().ToList(), ResolveSet(Need(op, "set")));
                break;
            }
            case "fill":
            {
                var at = Hexes(new JsonArray(op["at"]?.DeepClone())) is [var h] ? h : throw new ArgumentException("fill needs \"at\": [c,r]");
                n = Fill(at.Col, at.Row, ResolveSet(Need(op, "set")), op["max"] is JsonValue m ? (int)m : 20000);
                break;
            }
            case "replace":
                n = Replace(op["all"] is JsonValue all && (bool)all ? Rect(0, 0, _map.Width - 1, _map.Height - 1) : Area(op),
                            ResolveSet(Need(op, "from")), ResolveSet(Need(op, "to")));
                break;
            default:
                throw new ArgumentException($"unknown op '{name}' (paint, erase, line, fill, replace)");
        }
        return new JsonObject { ["op"] = name, ["changed"] = n, ["new_hexes"] = _changed.Count - before };
    }

    private List<(int Col, int Row)> Area(JsonObject op)
    {
        if (Hexes(op["hexes"]) is { } list) return list;
        if (Ints(op["circle"]) is [var c, var r, var radius]) return Circle(c, r, radius).ToList();
        if (Ints(op["rect"]) is [var c0, var r0, var c1, var r1]) return Rect(c0, r0, c1, r1).ToList();
        if (Hexes(op["polygon"]) is { } poly) return Polygon(poly).ToList();
        throw new ArgumentException("needs an area: \"hexes\", \"circle\": [c,r,radius], \"rect\": [c0,r0,c1,r1] or \"polygon\"");
    }

    private static List<(int Col, int Row)>? Hexes(JsonNode? node) =>
        node is JsonArray a ? a.Select(p => Ints(p) is [var c, var r] ? (c, r) : throw new ArgumentException($"bad hex {p?.ToJsonString()}: use [col, row]")).ToList() : null;

    private static int[]? Ints(JsonNode? node) => node is JsonArray a ? a.Select(v => (int)v!).ToArray() : null;

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;

    private static string Need(JsonObject o, string name) => Str(o, name) ?? throw new ArgumentException($"{o["op"]} needs \"{name}\"");
}
