using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.Core.Battle;

/// <summary>Explicit-tile footprints and overlap checks, as BOB's EDITOR_TILE_MAP::space_free_for_tile sees them.</summary>
public static class ExplicitTileGeometry
{
    /// <summary>
    /// Cells (image orientation, row 0 = north) occupied by an explicit tile: only mask-valid sub-tiles, rotated the way
    /// TILE_MAP::rotate_in_tile_space does (fitted against every complete masked instance in vanilla 3k_main_map).
    /// </summary>
    public static IEnumerable<(int X, int Y)> Footprint(ExplicitTile t, BattleTile tile)
    {
        int w = tile.Width, h = tile.Height;
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
        {
            if (!tile.SubtileValid(c, r)) continue;
            yield return t.Rotation switch
            {
                90 => (t.X + h - 1 - r, t.Y + c),
                180 => (t.X + w - 1 - c, t.Y + h - 1 - r),
                270 => (t.X + r, t.Y + w - 1 - c),
                _ => (t.X + c, t.Y + r),
            };
        }
    }

    /// <summary>Whether an image cell is inside the tile's rotated bounding rectangle.</summary>
    public static bool BoundsContain(ExplicitTile t, BattleTile tile, int x, int y)
    {
        var (w, h) = t.Size(tile);
        return x >= t.X && y >= t.Y && x < t.X + w && y < t.Y + h;
    }

    /// <summary>
    /// Indices of tiles that BOB would reject, processing in file order (a tile is rejected when it overlaps an earlier
    /// accepted one, leaves the map, or isn't in the tile database).
    /// </summary>
    public static HashSet<int> Conflicts(IReadOnlyList<ExplicitTile> tiles, BattleTileDatabase db, int mapWidth, int mapHeight)
    {
        var occupied = new HashSet<(int, int)>();
        var bad = new HashSet<int>();
        for (var i = 0; i < tiles.Count; i++)
        {
            if (db.TileAt(tiles[i].Location) is not { } tile)
            {
                bad.Add(i);
                continue;
            }
            var cells = Footprint(tiles[i], tile).ToList();
            if (cells.Any(c => c.X < 0 || c.Y < 0 || c.X >= mapWidth || c.Y >= mapHeight || occupied.Contains(c)))
            {
                bad.Add(i);
                continue;
            }
            occupied.UnionWith(cells);
        }
        return bad;
    }
}

/// <summary>Undo record for an edit of a small object list (explicit tiles, catchments): whole-list snapshots.</summary>
public sealed class ListEdit<T>(List<T> list, IReadOnlyList<T> before, string description) : IUndoable
{
    private readonly T[] _before = before.ToArray();
    private readonly T[] _after = list.ToArray();

    public string Description { get; } = description;
    public void Undo() => Restore(_before);
    public void Redo() => Restore(_after);

    private void Restore(T[] items)
    {
        list.Clear();
        list.AddRange(items);
    }

    /// <summary>Applies <paramref name="change"/> to the list and returns its undo record (null when nothing changed).</summary>
    public static ListEdit<T>? Apply(List<T> list, string description, Action<List<T>> change)
    {
        var before = list.ToArray();
        change(list);
        return before.SequenceEqual(list) ? null : new ListEdit<T>(list, before, description);
    }
}
