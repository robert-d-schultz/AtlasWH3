using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign;

/// <summary>One tile placed by BOB's tile matching (pre-flow), at its anchor (the scan point), y = 0 the south row.</summary>
public sealed record PlacedTile(CampaignTile Tile, int X, int Y, int Rotation, int Climate, int Layer);

/// <summary>A float height field like BOB's SCALAR_FIELD: row 0 is the north row.</summary>
public sealed record HeightField(float[] Data, int Width, int Height)
{
    /// <summary>A composited Terry height map (TerrainComposite.Heights), as is.</summary>
    public static HeightField FromRaster(Raster<float> r) => new(r.Data, r.Width, r.Height);

    public static HeightField Constant(float value) => new([value], 1, 1);
}

/// <summary>
/// Turns BOB's placed tiles into terrain\campaigns\&lt;map&gt;\tile_list.bin, the way WH3's Tilemap action finishes
/// (warscape.modder.x64.dll; Atlas3K's 3K notes in docs/bob_re_tile_placement.md):
///  - WARSCAPE::TILE_MAP::calculate_flow: breadth-first from every river_mouth tile along river links. Each newly
///    reached river tile gets flow = is_entry of its link pointing back into the previous tile; river junctions
///    (3+ river links) whose back link is an entry are swapped for the first same-layout tile whose back link is an
///    exit, or removed if there is none. Neither fixture map has river tiles, so this is 3K's rule unchecked.
///  - TILE_MAP::calculate_lf_min_maxs (0x1805b62b0): low/high = min/max of the composited Height map (HeightSea for
///    use_alt_lf tiles: the sea set) over [anchor − 2, anchor + max(w, h) + 2) tile-map units. The box is mapped by
///    multiplying with 1/W and 1/H, not dividing (BATTLE_TILE_MAP's copy divides): on a map height that is not a
///    power of two that sometimes takes one more texel row. A NaN texel counts as 0.
///  - TILE_MAP::build_battle_field + BATTLE_TILE_MAP::add_tile and the serializer: records cell by cell (rows from the
///    south, then columns) at each instance's first covered cell, layer 1 before layer 2; orientation = rotation |
///    0x04 when flowing; flag 7; the path table sorted (ordinal), climates in first-use order.
/// </summary>
public static class TileListWriter
{
    private sealed class Instance(PlacedTile p)
    {
        public CampaignTile Tile = p.Tile;
        public readonly int X = p.X, Y = p.Y, Rotation = p.Rotation, Climate = p.Climate, Layer = p.Layer;
        public bool Flow, Removed;
        public float Low, High;
        public int FirstCell = -1;
    }

    /// <summary>TILE_MAP::rotate_in_tile_space(width, height, rotation, x, y).</summary>
    public static (int X, int Y) Rotate(int w, int h, int rotation, int x, int y) => rotation switch
    {
        0x20 => (y, w - x - 1),
        0x40 => (w - x - 1, h - y - 1),
        0x80 => (h - y - 1, x),
        _ => (x, y),
    };

    /// <summary>Cells a tile covers, in place_tile's order (rows north first, then columns).</summary>
    public static IEnumerable<(int X, int Y)> Footprint(CampaignTile t, int x, int y, int rotation)
    {
        for (var j = 0; j < t.Height; j++)
            for (var i = 0; i < t.Width; i++)
                if (t.SubtileValid(i, j))
                {
                    var (dx, dy) = Rotate(t.Width, t.Height, rotation, i, t.Height - j - 1);
                    yield return (x + dx, y + dy);
                }
    }

    /// <param name="width">Tile map width (2 points per hex).</param>
    /// <param name="height">Tile map height (2 points per hex, plus 1).</param>
    /// <param name="tileMask">tile_mask.dds (<see cref="TileMask"/>).</param>
    public static TileList Build(CampaignTileDatabase db, int width, int height, IEnumerable<PlacedTile> placed,
                                 HeightField land, HeightField sea, out byte[] tileMask)
    {
        var instances = placed.Select(p => new Instance(p)).ToList();
        var grids = new Dictionary<int, int[]> { [1] = new int[width * height], [2] = new int[width * height] };
        for (var k = 0; k < instances.Count; k++)
        {
            var inst = instances[k];
            var grid = grids[inst.Layer];
            foreach (var (cx, cy) in Footprint(inst.Tile, inst.X, inst.Y, inst.Rotation))
            {
                if (cy >= height || cx < 0 || cy < 0 || cx >= width) continue;
                var cell = cy * width + cx;
                if (inst.FirstCell < 0) { inst.FirstCell = cell; grid[cell] = k + 1; }
                else grid[cell] = -(k + 1);
            }
        }

        CalculateFlow(db, instances, grids, width, height);

        Parallel.ForEach(instances, inst => (inst.Low, inst.High) = MinMax(inst.Tile.UseAltLf ? sea : land, inst, width, height));

        var order = new List<Instance>(instances.Count);
        for (var cell = 0; cell < width * height; cell++)
            foreach (var layer in new[] { 1, 2 })
            {
                var id = grids[layer][cell];
                if (id > 0 && !instances[id - 1].Removed) order.Add(instances[id - 1]);
            }

        var list = new TileList();
        list.Paths.AddRange(order.Select(i => i.Tile.Variations[0].Location).Distinct().Order(StringComparer.Ordinal));
        var pathIndex = list.Paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => (uint)x.i);
        var climateIndex = new Dictionary<int, byte>();
        int x0 = 0, y0 = 0;
        foreach (var inst in order)
        {
            if (!climateIndex.TryGetValue(inst.Climate, out var climate))
            {
                climateIndex[inst.Climate] = climate = (byte)list.Climates.Count;
                list.Climates.Add(db.Climates[inst.Climate].Name);
            }
            var size = Math.Max(inst.Tile.Width, inst.Tile.Height);
            x0 = Math.Min(x0, inst.X - size);
            y0 = Math.Min(y0, inst.Y - size);
            list.Records.Add(new TileList.Record
            {
                Version = 1, Path = pathIndex[inst.Tile.Variations[0].Location], Climate = climate,
                X = (ushort)inst.X, Y = (ushort)inst.Y, Orientation = (byte)(inst.Rotation | (inst.Flow ? 4 : 0)), Flag = 7,
                LowHeight = inst.Low, HighHeight = inst.High,
            });
        }
        list.Floats = [0, 0, 0, 0, 500, 1.333f];
        // the tile map, the hex grid, then the map area: from the lowest anchor − max(w, h) to the tile map + 2
        list.Ints = [0, width, height, width / 2, height / 2, 0, 0, x0, y0, width + 2, height + 2];
        list.Marker = 1;
        tileMask = TileMask(db, instances, grids, width, height);
        return list;
    }

    /// <summary>
    /// tile_mask.dds, written by the Tilemap action with the tile list: one byte per tile-map point, rows from the south,
    /// as an 8-bit luminance DDS. Each tile on the point (both layers) adds 16 for a land tile and 32 for a use_alt_lf
    /// (sea) tile, nothing when its set is exclude_from_global_mesh (roads, cliffs, coasts); a point with no tile is 64.
    /// Byte-identical to BOB's on IEE and Old World given BOB's placement.
    /// </summary>
    private static byte[] TileMask(CampaignTileDatabase db, List<Instance> instances, Dictionary<int, int[]> grids, int width, int height)
    {
        var header = AtlasWH3.Formats.Dds.DdsHeader.BuildL8(width, height);
        var mask = new byte[header.Length + width * height];
        header.CopyTo(mask, 0);
        for (var cell = 0; cell < width * height; cell++)
        {
            int value = 0, tiles = 0;
            foreach (var grid in grids.Values)
            {
                var id = Math.Abs(grid[cell]);
                if (id == 0 || instances[id - 1].Removed) continue;
                tiles++;
                var tile = instances[id - 1].Tile;
                if (db.TileSet(tile.TileSet) is { ExcludeFromGlobalMesh: true }) continue;
                value |= tile.UseAltLf ? 32 : 16;
            }
            mask[header.Length + cell] = (byte)(tiles == 0 ? 64 : value);
        }
        return mask;
    }

    /// <summary>TILE_MAP::calculate_lf_min_maxs for one instance.</summary>
    private static (float Low, float High) MinMax(HeightField f, Instance inst, int width, int height)
    {
        static float Clamp(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        float size = Math.Max(inst.Tile.Width, inst.Tile.Height);
        float rw = 1f / width, rh = 1f / height;
        var u0 = Clamp((inst.X - 2) * rw);
        var v0 = Clamp((inst.Y - 2) * rh);
        var u1 = Clamp((inst.X + size + 2f) * rw);
        var v1 = Clamp((inst.Y + size + 2f) * rh);
        var colEnd = u1 * f.Width;
        var rowEnd = v1 * f.Height;
        float low = float.MaxValue, high = -float.MaxValue;
        for (var row = (int)(v0 * f.Height); row < rowEnd; row++)
        {
            var baseIndex = (f.Height - row - 1) * f.Width;
            for (var col = (int)(u0 * f.Width); col < colEnd; col++)
            {
                var v = f.Data[baseIndex + col];
                if (float.IsNaN(v)) v = 0f;
                if (v <= low) low = v;
                if (high <= v) high = v;
            }
        }
        return (low, high);
    }

    // ------------------------------------------------------------------------------------------------ flow
    private static bool IsRiverSet(string? name) => name is not null && name.Contains("river", StringComparison.Ordinal);

    private static int InstanceAt(Dictionary<int, int[]> grids, int layer, int x, int y, int width, int height)
    {
        if (!grids.TryGetValue(layer, out var grid) || (uint)x >= width || (uint)y >= height) return -1;
        var id = grid[y * width + x];
        return id == 0 ? -1 : Math.Abs(id) - 1;
    }

    private static void CalculateFlow(CampaignTileDatabase db, List<Instance> instances, Dictionary<int, int[]> grids,
                                      int width, int height)
    {
        var queue = new List<int>();
        var visited = new HashSet<(int, int)>();
        for (var cell = 0; cell < width * height; cell++)
        {
            var added = false;
            foreach (var layer in new[] { 1, 2 })
            {
                var id = grids[layer][cell];
                if (id <= 0) continue;
                var inst = instances[id - 1];
                if (inst.Removed || !inst.Tile.TileSet.StartsWith("river_mouth", StringComparison.Ordinal)) continue;
                if (!visited.Add((inst.X, inst.Y))) continue;
                queue.Add(id - 1);
                added = true;
            }
            if (!added) continue;
            // BOB re-walks the whole queue for each new mouth. "Visited" holds tiles once processed (not when
            // discovered), so a tile reached from two sides is queued twice and the later discoverer's flow wins.

            for (var k = 0; k < queue.Count; k++)
            {
                var cur = instances[queue[k]];
                ProcessFlow(db, instances, grids, width, height, cur, queue, visited);
                visited.Add((cur.X, cur.Y));
            }
        }
    }

    private static void ProcessFlow(CampaignTileDatabase db, List<Instance> instances, Dictionary<int, int[]> grids,
                                    int width, int height, Instance cur, List<int> queue, HashSet<(int, int)> visited)
    {
        {
            {
                if (cur.Removed) return;
                var t = cur.Tile;
                foreach (var link in t.Links)
                {
                    if (!IsRiverSet(link.LinkSet)) continue;
                    var (dx, dy) = Rotate(t.Width, t.Height, cur.Rotation, link.X, t.Height - link.Y - 1);
                    int px = cur.X + dx, py = cur.Y + dy;
                    if (px < 0 || py < 0) continue;
                    var found = -1;
                    foreach (var layer in new[] { 1, 0, 2 })
                    {
                        var n = InstanceAt(grids, layer, px, py, width, height);
                        if (n >= 0 && !instances[n].Removed && IsRiverSet(instances[n].Tile.TileSet)) found = n;
                    }
                    if (found < 0) continue;
                    var next = instances[found];
                    if (visited.Contains((next.X, next.Y))) continue;
                    // calculate_flow counts the next tile's TLT_EQUALS links (any set) that are entries; with none, the
                    // tile gets no flow and is not queued, so the walk stops there (e.g. river_crossing cross_5 on
                    // vanilla: everything upstream of it keeps flow 0). Checked against BOB 2026-10-04.
                    if (!next.Tile.Links.Any(l => l.TestEquals && l.IsEntry)) continue;
                    var swap = cur.Rotation is 0x20 or 0x80;
                    var box = (X0: cur.X, Y0: cur.Y, X1: cur.X + (swap ? t.Height : t.Width), Y1: cur.Y + (swap ? t.Width : t.Height));
                    var riverLinks = next.Tile.Links.Count(l => l.TestEquals && IsRiverSet(l.LinkSet));
                    if (riverLinks < 3)
                    {
                        var back = BackLink(next, next.Tile, box, t, cur.Rotation);
                        if (back is not null) next.Flow = back.IsEntry;
                    }
                    else SelectJunctionTile(db, next, box, t, cur.Rotation);
                    queue.Add(found);
                }
            }
        }
    }

    /// <summary>FUN_1803e6d90: the river link of <paramref name="tile"/> (placed as <paramref name="inst"/>) that lands on a
    /// valid cell of the previous tile.</summary>
    private static TileLink? BackLink(Instance inst, CampaignTile tile, (int X0, int Y0, int X1, int Y1) box,
                                      CampaignTile prev, int prevRotation)
    {
        foreach (var link in tile.Links)
        {
            if (!IsRiverSet(link.LinkSet)) continue;
            var (dx, dy) = Rotate(tile.Width, tile.Height, inst.Rotation, link.X, tile.Height - link.Y - 1);
            int px = inst.X + dx, py = inst.Y + dy;
            if (px < 0 || py < 0 || px < box.X0 || px > box.X1 || py < box.Y0 || py > box.Y1) continue;
            for (var j = 0; j < prev.Height; j++)
                for (var i = 0; i < prev.Width; i++)
                {
                    var (cx, cy) = Rotate(prev.Width, prev.Height, prevRotation, i, prev.Height - j - 1);
                    if (box.X0 + cx == px && box.Y0 + cy == py && prev.SubtileValid(i, j)) return link;
                }
        }
        return null;
    }

    /// <summary>TILE_MAP::select_junction_tile.</summary>
    private static void SelectJunctionTile(CampaignTileDatabase db, Instance inst, (int, int, int, int) box,
                                           CampaignTile prev, int prevRotation)
    {
        var back = BackLink(inst, inst.Tile, box, prev, prevRotation);
        if (back is null || !back.IsEntry) return;
        foreach (var alt in JunctionAlternatives(db, inst.Tile))
        {
            var link = BackLink(inst, alt, box, prev, prevRotation);
            if (link is null || link.IsEntry) continue;
            inst.Tile = alt;
            return;
        }
        inst.Removed = true;
    }

    /// <summary>FUN_1803e9050: same set, size, mask and link layout (point, set, test), in database order.</summary>
    private static IEnumerable<CampaignTile> JunctionAlternatives(CampaignTileDatabase db, CampaignTile t) =>
        db.Tiles.Where(u => u.TileSet == t.TileSet && u.Width == t.Width && u.Height == t.Height
                            && Enumerable.Range(0, t.Width * t.Height).All(k => u.SubtileValid(k % t.Width, k / t.Width) == t.SubtileValid(k % t.Width, k / t.Width))
                            && u.Links.Count == t.Links.Count
                            && u.Links.All(a => t.Links.Any(b => a.X == b.X && a.Y == b.Y && a.LinkSet == b.LinkSet && a.Test == b.Test)))
            .OrderByDescending(u => u.LinkTargets.Count).ThenBy(u => u.Name, StringComparer.Ordinal);
}
