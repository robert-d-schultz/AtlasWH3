using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>
/// Post-Tilemap check (port of research/main190/tile_holes.py): tile-map cells that no tile_list.bin record covers are
/// see-through holes in game. A record covers w × h cells from (x, y) (w/h swapped for orientation 0x20/0x80) where its
/// orientation-rotated sub-tile is set in the tile's mask (as <see cref="GlobalMesh.TileCoverage"/>). Holes are grouped
/// into 4-connected clusters; clusters touching the map border are "edge", the rest "suspect". Overlapping base tiles
/// are normal at tile edges and not reported. Settlement classification (map.hex) is left to tile_holes.py.
/// </summary>
public static class TileHoles
{
    public sealed record Cluster(int Cells, string Kind, int Col, int Row, IReadOnlyList<int[]> Hexes, bool Edited);
    public sealed record Report(int Width, int Height, int Records, IReadOnlyList<string> UnknownPaths, int UncoveredCells,
                                IReadOnlyList<Cluster> Clusters);

    public static Report Check(TileList list, CampaignTileDatabase db, ISet<(int Col, int Row)>? edited = null)
    {
        var byLocation = new Dictionary<string, CampaignTile>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in db.Tiles)
            foreach (var v in t.Variations)
                byLocation.TryAdd(TileDatabase.NormalisePath(v.Location), t);
        int w = list.Ints[1], h = list.Ints[2];
        var tileOf = list.Paths.Select(p => byLocation.GetValueOrDefault(TileDatabase.NormalisePath(p))).ToArray();
        var used = list.Records.Select(r => (int)r.Path).ToHashSet();
        var unknown = used.Where(i => tileOf[i] is null).Select(i => list.Paths[i]).Order(StringComparer.Ordinal).ToList();

        var covered = new bool[w * h];
        foreach (var rec in list.Records)
        {
            var tile = tileOf[rec.Path];
            if (tile is null) continue;
            int tw = tile.Width, th = tile.Height;
            var o = rec.Orientation & 0xF0;
            var (rw, rh) = o is 0x20 or 0x80 ? (th, tw) : (tw, th);
            for (var j = 0; j < rh && rec.Y + j < h; j++)
                for (var i = 0; i < rw && rec.X + i < w; i++)
                {
                    var (row, col) = o switch
                    {
                        0x20 => (i, tw - j - 1),
                        0x40 => (th - j - 1, tw - i - 1),
                        0x80 => (th - i - 1, j),
                        _ => (j, i),
                    };
                    if (!tile.SubtileValid(col, th - row - 1)) continue;   // mask rows are stored north first
                    var x = rec.X + i; var y = rec.Y + j;
                    if (x >= 0 && y >= 0) covered[y * w + x] = true;
                }
        }

        var clusters = new List<Cluster>();
        var seen = new bool[w * h];
        var holes = 0;
        for (var start = 0; start < covered.Length; start++)
        {
            if (covered[start] || seen[start]) continue;
            var cells = new List<int>();
            var stack = new Stack<int>([start]);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                cells.Add(k);
                int x = k % w, y = k / w;
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var n = ny * w + nx;
                    if (!covered[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                }
            }
            holes += cells.Count;
            var edge = cells.Any(k => k % w < 2 || k / w < 2 || k % w > w - 3 || k / w > h - 3);
            var hexes = cells.Select(k => CellToHex(k % w, k / w)).Distinct().ToList();
            var (mc, mr) = CellToHex((int)cells.Average(k => k % w), (int)cells.Average(k => k / w));
            clusters.Add(new Cluster(cells.Count, edge ? "edge" : "suspect", mc, mr,
                hexes.Take(50).Select(x => new[] { x.Col, x.Row }).ToList(), edited is not null && hexes.Any(edited.Contains)));
        }
        return new Report(w, h, list.Records.Count, unknown, holes,
            clusters.OrderByDescending(c => c.Edited).ThenBy(c => c.Kind == "edge").ThenByDescending(c => c.Cells).ToList());
    }

    /// <summary>Tile-map cell (x, y = 0 south) → hex.</summary>
    public static (int Col, int Row) CellToHex(int x, int y)
    {
        var c = x / 2;
        return (c, Math.Max(0, (y - (c & 1)) / 2));
    }
}
