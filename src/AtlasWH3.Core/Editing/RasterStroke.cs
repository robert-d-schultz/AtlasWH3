using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Records a brush stroke on a raster as snapshots of the 256x256 tiles it touched, so undo/redo only
/// copies the affected area of the (up to 80 MB) rasters.
/// </summary>
public sealed class RasterStroke<T> : IUndoable where T : unmanaged
{
    public const int TileSize = 256;

    private readonly Raster<T> _raster;
    private readonly Dictionary<(int, int), T[]> _before = new();
    private Dictionary<(int, int), T[]>? _after;

    public string Description { get; }

    public RasterStroke(Raster<T> raster, string description)
    {
        _raster = raster;
        Description = description;
    }

    public bool IsEmpty => _before.Count == 0;

    /// <summary>Snapshot every tile overlapping the rectangle that hasn't been captured yet. Call before modifying it.</summary>
    public void Capture(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(_raster.Width - 1, x1); y1 = Math.Min(_raster.Height - 1, y1);
        if (x0 > x1 || y0 > y1) return;
        for (var ty = y0 / TileSize; ty <= y1 / TileSize; ty++)
        for (var tx = x0 / TileSize; tx <= x1 / TileSize; tx++)
            if (!_before.ContainsKey((tx, ty)))
                _before[(tx, ty)] = CopyTile(tx, ty);
    }

    /// <summary>Called when the stroke ends: snapshots the final state for redo.</summary>
    public void Finish()
    {
        _after = _before.Keys.ToDictionary(k => k, k => CopyTile(k.Item1, k.Item2));
    }

    public void Undo() => Restore(_before);
    public void Redo() => Restore(_after ?? throw new InvalidOperationException("Stroke not finished."));

    private T[] CopyTile(int tx, int ty)
    {
        var (x, y, w, h) = TileRect(tx, ty);
        var copy = new T[w * h];
        for (var r = 0; r < h; r++)
            Array.Copy(_raster.Data, (y + r) * _raster.Width + x, copy, r * w, w);
        return copy;
    }

    private void Restore(Dictionary<(int, int), T[]> tiles)
    {
        foreach (var ((tx, ty), data) in tiles)
        {
            var (x, y, w, h) = TileRect(tx, ty);
            for (var r = 0; r < h; r++)
                Array.Copy(data, r * w, _raster.Data, (y + r) * _raster.Width + x, w);
        }
    }

    private (int X, int Y, int W, int H) TileRect(int tx, int ty)
    {
        var x = tx * TileSize;
        var y = ty * TileSize;
        return (x, y, Math.Min(TileSize, _raster.Width - x), Math.Min(TileSize, _raster.Height - y));
    }
}

/// <summary>Several undoables applied/reverted as one step (e.g. height + blend in one stroke).</summary>
public sealed class CompositeUndo(string description, IReadOnlyList<IUndoable> parts) : IUndoable
{
    public string Description { get; } = description;

    public void Undo()
    {
        for (var i = parts.Count - 1; i >= 0; i--) parts[i].Undo();
    }

    public void Redo()
    {
        foreach (var p in parts) p.Redo();
    }
}
