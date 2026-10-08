using System.Text.Json.Nodes;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>
/// A tile-map editing session with unsaved strokes (what the web editor holds on the server; the WPF window does the
/// same in-process). Each stroke is a <see cref="TileMapOps"/> op applied to a working copy, recorded with the old
/// pixels of the hexes it changed so it can be undone, and the pending change is checked against the file on disk
/// (<see cref="TileMapEditor.NewIssues"/>). Save writes through <see cref="TileMapEditor.Save"/> (same journal as the
/// CLI / MCP tools); if the file changed on disk meanwhile, the strokes are replayed onto the new file first.
/// Thread-safe (one lock).
/// </summary>
public sealed class TileEditSession
{
    public sealed record HexChange(int Col, int Row, uint Rgb);
    public sealed record StrokeResult(IReadOnlyList<HexChange> Changed, IReadOnlyList<TileMapFinding> Issues, int Pending, int Strokes);

    private sealed record Stroke(JsonObject Op, Dictionary<(int Col, int Row), uint[]> Old);

    private readonly object _lock = new();
    private readonly List<Stroke> _strokes = [];
    private readonly HashSet<(int Col, int Row)> _pending = [];
    private HexTileMap _disk;
    private HexTileMap _map;
    private string _diskHash;

    public TileMapEditor Editor { get; }
    public IReadOnlyList<TileMapFinding> Issues { get; private set; } = [];
    public int Version { get; private set; }
    public int Pending { get { lock (_lock) return _pending.Count; } }
    public int Strokes { get { lock (_lock) return _strokes.Count; } }
    public HexTileMap Map => _map;

    public TileEditSession(TileMapEditor editor)
    {
        Editor = editor;
        _disk = editor.Load();
        _map = Clone(_disk);
        _diskHash = FileJournal.Hash(File.ReadAllBytes(editor.TileMapPath));
    }

    private static HexTileMap Clone(HexTileMap m) => new(m.PixelWidth, m.PixelHeight, (uint[])m.Pixels.Clone());

    public uint Colour(int col, int row) { var (x, y) = _map.HexPixel(col, row); return _map.Pixel(x, y); }

    /// <summary>Hexes that differ from the file on disk.</summary>
    public List<(int Col, int Row)> PendingHexes() { lock (_lock) return _pending.ToList(); }

    /// <summary>The working map (unsaved strokes included) as PNG bytes.</summary>
    public byte[] Png()
    {
        HexTileMap copy;
        lock (_lock) copy = Clone(_map);
        var temp = Path.Combine(Path.GetTempPath(), $"tile_session_{Guid.NewGuid():N}.png");
        try { copy.Write(temp); return File.ReadAllBytes(temp); }
        finally { File.Delete(temp); }
    }

    /// <summary>Applies one op as a stroke; returns the changed hexes and the pending change's new issues.</summary>
    public StrokeResult Apply(JsonObject op)
    {
        lock (_lock)
        {
            var snapshot = (uint[])_map.Pixels.Clone();
            var ops = new TileMapOps(_map, Editor.Database);
            try { ops.Apply(op); }
            catch
            {
                snapshot.CopyTo(_map.Pixels, 0);
                throw;
            }
            var old = ops.Changed.ToDictionary(h => h, h => Pixels(snapshot, h));
            if (old.Count > 0)
            {
                _strokes.Add(new Stroke((JsonObject)op.DeepClone(), old));
                foreach (var h in old.Keys) UpdatePending(h);
                Revalidate();
            }
            return Result(old.Keys);
        }
    }

    public StrokeResult Undo()
    {
        lock (_lock)
        {
            if (_strokes.Count == 0) return Result([]);
            var s = _strokes[^1];
            _strokes.RemoveAt(_strokes.Count - 1);
            foreach (var (h, px) in s.Old) { Restore(h, px); UpdatePending(h); }
            Revalidate();
            return Result(s.Old.Keys);
        }
    }

    /// <summary>Drops every unsaved stroke and reloads the file.</summary>
    public void Reload()
    {
        lock (_lock)
        {
            _disk = Editor.Load();
            _map = Clone(_disk);
            _diskHash = FileJournal.Hash(File.ReadAllBytes(Editor.TileMapPath));
            _strokes.Clear();
            _pending.Clear();
            Issues = [];
            Version++;
        }
    }

    public TileMapEditor.EditResult Save(bool allowWarnings, bool force)
    {
        lock (_lock)
        {
            if (FileJournal.Hash(File.ReadAllBytes(Editor.TileMapPath)) != _diskHash) Rebase();
            var ops = new JsonArray(_strokes.Select(s => s.Op.DeepClone()).ToArray());
            var label = $"web: {string.Join(", ", _strokes.GroupBy(s => s.Op["op"]?.ToString()).Select(g => $"{g.Key} x{g.Count()}"))}";
            var result = Editor.Save(Clone(_map), _pending.ToList(), ops, label, force, allowWarnings);
            if (result.Written)
            {
                _disk = Clone(_map);
                _diskHash = FileJournal.Hash(File.ReadAllBytes(Editor.TileMapPath));
                _strokes.Clear();
                _pending.Clear();
                Issues = [];
                Version++;
            }
            else Issues = result.NewIssues;
            return result;
        }
    }

    /// <summary>BOB tile-matching simulation of the working map (1-3 min); unsaved strokes count as edited.</summary>
    public TileMapEditor.SimulationResult Simulate()
    {
        HexTileMap copy;
        List<(int, int)> pending;
        lock (_lock) { copy = Clone(_map); pending = _pending.ToList(); }
        return Editor.Simulate(copy, pending);
    }

    private void Rebase()
    {
        var ops = _strokes.Select(s => s.Op).ToList();
        _disk = Editor.Load();
        _map = Clone(_disk);
        _diskHash = FileJournal.Hash(File.ReadAllBytes(Editor.TileMapPath));
        _strokes.Clear();
        _pending.Clear();
        foreach (var op in ops)
        {
            var snapshot = (uint[])_map.Pixels.Clone();
            var t = new TileMapOps(_map, Editor.Database);
            t.Apply(op);
            var old = t.Changed.ToDictionary(h => h, h => Pixels(snapshot, h));
            if (old.Count == 0) continue;
            _strokes.Add(new Stroke(op, old));
            foreach (var h in old.Keys) UpdatePending(h);
        }
        Revalidate();
    }

    private void Revalidate()
    {
        Version++;
        Issues = _pending.Count == 0 ? [] : Editor.NewIssues(_disk, _map, _pending.ToList()).New;
    }

    private StrokeResult Result(IEnumerable<(int Col, int Row)> hexes) =>
        new(hexes.Select(h => new HexChange(h.Col, h.Row, Colour(h.Col, h.Row))).ToList(), Issues, _pending.Count, _strokes.Count);

    private uint[] Pixels(uint[] pixels, (int Col, int Row) h)
    {
        var r = new uint[4];
        for (var d = 0; d < 4; d++)
        {
            var (x, y) = _map.HexPixel(h.Col, h.Row, d & 1, d >> 1);
            r[d] = pixels[y * _map.PixelWidth + x];
        }
        return r;
    }

    private void Restore((int Col, int Row) h, uint[] px)
    {
        for (var d = 0; d < 4; d++)
        {
            var (x, y) = _map.HexPixel(h.Col, h.Row, d & 1, d >> 1);
            _map.Pixels[y * _map.PixelWidth + x] = px[d];
        }
    }

    private void UpdatePending((int Col, int Row) h)
    {
        if (Pixels(_disk.Pixels, h).SequenceEqual(Pixels(_map.Pixels, h))) _pending.Remove(h);
        else _pending.Add(h);
    }
}
