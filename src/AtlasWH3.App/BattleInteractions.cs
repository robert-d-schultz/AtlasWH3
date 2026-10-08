using System.Windows;
using System.Windows.Media;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.App;

/// <summary>Select / move / place explicit tiles. Rotation and delete are driven by the window (R, Delete).</summary>
public sealed class ExplicitTileInteraction(BattleProject project, Action<IUndoable> push, Action selectionChanged) : IMapInteraction
{
    /// <summary>Index into <see cref="BattleProject.ExplicitTiles"/>, or -1.</summary>
    public int Selected { get; private set; } = -1;
    /// <summary>Tile folder to place on click; null = select/move mode.</summary>
    public string? PlaceLocation { get; set; }
    public int PlaceRotation { get; set; }
    public HashSet<int> Conflicts { get; set; } = new();

    private ExplicitTile[]? _before;
    private (int X, int Y) _dragStart;
    private ExplicitTile? _dragOriginal;
    private (double X, double Y) _cursor;

    private List<ExplicitTile> Tiles => project.ExplicitTiles;

    public void Select(int index)
    {
        Selected = index;
        selectionChanged();
    }

    /// <summary>Topmost tile whose bounds contain the cell.</summary>
    public int HitTest(int cx, int cy)
    {
        for (var i = Tiles.Count - 1; i >= 0; i--)
            if (project.TileDatabase.TileAt(Tiles[i].Location) is { } tile && ExplicitTileGeometry.BoundsContain(Tiles[i], tile, cx, cy))
                return i;
        return -1;
    }

    private ExplicitTile? Preview()
    {
        if (PlaceLocation == null || project.TileDatabase.TileAt(PlaceLocation) is not { } tile) return null;
        var t = new ExplicitTile(0, 0, PlaceLocation, PlaceRotation);
        var (w, h) = t.Size(tile);
        return t with { X = (int)Math.Floor(_cursor.X - w / 2.0 + 0.5), Y = (int)Math.Floor(_cursor.Y - h / 2.0 + 0.5) };
    }

    public bool Down(double cx, double cy)
    {
        _cursor = (cx, cy);
        if (Preview() is { } place)
        {
            if (ListEdit<ExplicitTile>.Apply(Tiles, $"Place {place.Location.Split('/').Last()}", l => l.Add(place)) is { } undo)
                push(undo);
            Select(Tiles.Count - 1);
            return false;
        }
        Select(HitTest((int)cx, (int)cy));
        if (Selected < 0) return false;
        _before = Tiles.ToArray();
        _dragStart = ((int)cx, (int)cy);
        _dragOriginal = Tiles[Selected];
        return true;
    }

    public bool Move(double cx, double cy, bool dragging)
    {
        _cursor = (cx, cy);
        if (!dragging || _dragOriginal == null || Selected < 0) return false;
        var moved = _dragOriginal with { X = _dragOriginal.X + (int)cx - _dragStart.X, Y = _dragOriginal.Y + (int)cy - _dragStart.Y };
        if (moved == Tiles[Selected]) return false;
        Tiles[Selected] = moved;
        return true;
    }

    public void Up(double cx, double cy)
    {
        if (_before != null && !_before.SequenceEqual(Tiles))
            push(new ListEdit<ExplicitTile>(Tiles, _before, "Move explicit tile"));
        _before = null;
        _dragOriginal = null;
        selectionChanged();
    }

    /// <summary>Applies a change to the selected tile (rotate, delete, ...) as one undo step.</summary>
    public void EditSelected(string description, Func<ExplicitTile, ExplicitTile?> change)
    {
        if (Selected < 0 || Selected >= Tiles.Count) return;
        var index = Selected;
        var undo = ListEdit<ExplicitTile>.Apply(Tiles, description, l =>
        {
            if (change(l[index]) is { } replacement) l[index] = replacement;
            else l.RemoveAt(index);
        });
        if (undo != null) push(undo);
        if (index >= Tiles.Count) Select(-1);
        else selectionChanged();
    }

    public void Draw(DrawingContext dc, Func<double, double, Point> toScreen)
    {
        void Outline(ExplicitTile t, Brush stroke, double thickness, Brush? fill)
        {
            if (project.TileDatabase.TileAt(t.Location) is not { } tile) return;
            if (fill != null)
                foreach (var (x, y) in ExplicitTileGeometry.Footprint(t, tile))
                    dc.DrawRectangle(fill, null, new Rect(toScreen(x, y), toScreen(x + 1, y + 1)));
            var (w, h) = t.Size(tile);
            dc.DrawRectangle(null, new Pen(stroke, thickness), new Rect(toScreen(t.X, t.Y), toScreen(t.X + w, t.Y + h)));
        }
        foreach (var i in Conflicts.Where(i => i < Tiles.Count))
            Outline(Tiles[i], Brushes.Red, 2.5, new SolidColorBrush(Color.FromArgb(70, 255, 0, 0)));
        if (Selected >= 0 && Selected < Tiles.Count)
            Outline(Tiles[Selected], Brushes.Yellow, 2.5, new SolidColorBrush(Color.FromArgb(60, 255, 255, 0)));
        if (Preview() is { } p)
            Outline(p, Brushes.Cyan, 2, new SolidColorBrush(Color.FromArgb(80, 0, 255, 255)));
    }
}

/// <summary>Select / move / resize / draw catchment areas. Tab cycles through overlapping areas under the last click.</summary>
public sealed class CatchmentInteraction(BattleProject project, Action<IUndoable> push, Action selectionChanged) : IMapInteraction
{
    public int Selected { get; private set; } = -1;
    /// <summary>Drag out new areas instead of selecting.</summary>
    public bool DrawNew { get; set; }
    /// <summary>Types given to newly drawn areas.</summary>
    public IReadOnlyList<string> NewTypes { get; set; } = ["land_ambush"];

    private enum Drag { None, Move, Resize, Centre, New }
    private Drag _drag;
    private bool _left, _right, _top, _bottom;
    private BattleCatchment[]? _before;
    private BattleCatchment? _original;
    private (int X, int Y) _start;
    private (int X, int Y) _end;
    private List<int> _hits = new();

    private List<BattleCatchment> Areas => project.Catchments;

    public void Select(int index)
    {
        Selected = index;
        selectionChanged();
    }

    /// <summary>Next area under the last click (overlapping areas).</summary>
    public void Cycle()
    {
        if (_hits.Count < 2) return;
        var i = _hits.IndexOf(Selected);
        Select(_hits[(i + 1) % _hits.Count]);
    }

    public bool Down(double cx, double cy)
    {
        var (x, y) = ((int)Math.Floor(cx), (int)Math.Floor(cy));
        _before = Areas.ToArray();
        _start = (x, y);
        _end = (x, y);
        if (DrawNew)
        {
            _drag = Drag.New;
            return true;
        }
        if (Selected >= 0 && Selected < Areas.Count)
        {
            var c = Areas[Selected];
            const double grip = 0.75;
            if (Math.Abs(cx - (c.Centre.X + 0.5)) < grip && Math.Abs(cy - (c.Centre.Y + 0.5)) < grip)
            {
                _drag = Drag.Centre;
                _original = c;
                return true;
            }
            _left = Math.Abs(cx - c.Box.MinX) < grip;
            _right = Math.Abs(cx - (c.Box.MaxX + 1)) < grip;
            _top = Math.Abs(cy - c.Box.MinY) < grip;
            _bottom = Math.Abs(cy - (c.Box.MaxY + 1)) < grip;
            var inside = cx > c.Box.MinX - grip && cx < c.Box.MaxX + 1 + grip && cy > c.Box.MinY - grip && cy < c.Box.MaxY + 1 + grip;
            if (inside && (_left || _right || _top || _bottom))
            {
                _drag = Drag.Resize;
                _original = c;
                return true;
            }
        }
        _hits = Enumerable.Range(0, Areas.Count).Where(i => Areas[i].Box.Contains(x, y)).OrderBy(i => Areas[i].Box.Area).ToList();
        Select(_hits.Count > 0 ? _hits[0] : -1);
        if (Selected < 0) return false;
        _drag = Drag.Move;
        _original = Areas[Selected];
        return true;
    }

    public bool Move(double cx, double cy, bool dragging)
    {
        if (!dragging || _drag == Drag.None) return false;
        var (x, y) = ((int)Math.Floor(cx), (int)Math.Floor(cy));
        _end = (x, y);
        if (_drag == Drag.New || _original == null || Selected < 0) return false;
        var (dx, dy) = (x - _start.X, y - _start.Y);
        var o = _original;
        var updated = _drag switch
        {
            Drag.Move => o with { Box = o.Box.Offset(dx, dy), Centre = (o.Centre.X + dx, o.Centre.Y + dy) },
            Drag.Centre => o with { Centre = (o.Centre.X + dx, o.Centre.Y + dy) },
            _ => o with { Box = Resized(o.Box, dx, dy) },
        };
        if (updated == Areas[Selected]) return false;
        Areas[Selected] = updated;
        return true;
    }

    private CellBox Resized(CellBox b, int dx, int dy)
    {
        int x0 = b.MinX + (_left ? dx : 0), x1 = b.MaxX + (_right ? dx : 0);
        int y0 = b.MinY + (_top ? dy : 0), y1 = b.MaxY + (_bottom ? dy : 0);
        return new CellBox(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    public void Up(double cx, double cy)
    {
        if (_drag == Drag.New)
        {
            var box = new CellBox(Math.Min(_start.X, _end.X), Math.Min(_start.Y, _end.Y), Math.Max(_start.X, _end.X), Math.Max(_start.Y, _end.Y));
            if (box.MaxX > box.MinX || box.MaxY > box.MinY)
            {
                var area = new BattleCatchment(project.NewCatchmentId(), NewTypes.ToList(), "", "",
                    ((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2), box, null);
                if (ListEdit<BattleCatchment>.Apply(Areas, "Add catchment area", l => l.Add(area)) is { } undo) push(undo);
                Select(Areas.Count - 1);
            }
        }
        else if (_before != null && !_before.SequenceEqual(Areas))
        {
            push(new ListEdit<BattleCatchment>(Areas, _before, _drag == Drag.Resize ? "Resize catchment area" : "Move catchment area"));
            selectionChanged();
        }
        _drag = Drag.None;
        _before = null;
        _original = null;
    }

    /// <summary>Replaces (or with null deletes) the selected area as one undo step.</summary>
    public void EditSelected(string description, Func<BattleCatchment, BattleCatchment?> change)
    {
        if (Selected < 0 || Selected >= Areas.Count) return;
        var index = Selected;
        var undo = ListEdit<BattleCatchment>.Apply(Areas, description, l =>
        {
            if (change(l[index]) is { } replacement) l[index] = replacement;
            else l.RemoveAt(index);
        });
        if (undo != null) push(undo);
        if (index >= Areas.Count) Select(-1);
        else selectionChanged();
    }

    public void Draw(DrawingContext dc, Func<double, double, Point> toScreen)
    {
        if (_drag == Drag.New)
        {
            var r = new Rect(toScreen(Math.Min(_start.X, _end.X), Math.Min(_start.Y, _end.Y)),
                             toScreen(Math.Max(_start.X, _end.X) + 1, Math.Max(_start.Y, _end.Y) + 1));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, 0, 255, 255)), new Pen(Brushes.Cyan, 2), r);
        }
        if (Selected < 0 || Selected >= Areas.Count) return;
        var c = Areas[Selected];
        var box = new Rect(toScreen(c.Box.MinX, c.Box.MinY), toScreen(c.Box.MaxX + 1, c.Box.MaxY + 1));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 0)), new Pen(Brushes.Yellow, 2.5), box);
        foreach (var corner in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight })
            dc.DrawRectangle(Brushes.Yellow, new Pen(Brushes.Black, 1), new Rect(corner.X - 4, corner.Y - 4, 8, 8));
        var centre = new Rect(toScreen(c.Centre.X, c.Centre.Y), toScreen(c.Centre.X + 1, c.Centre.Y + 1));
        var mid = new Point((centre.Left + centre.Right) / 2, (centre.Top + centre.Bottom) / 2);
        var pen = new Pen(Brushes.Black, 3);
        var pen2 = new Pen(Brushes.Yellow, 1.5);
        foreach (var p in new[] { pen, pen2 })
        {
            dc.DrawLine(p, new Point(mid.X - 8, mid.Y), new Point(mid.X + 8, mid.Y));
            dc.DrawLine(p, new Point(mid.X, mid.Y - 8), new Point(mid.X, mid.Y + 8));
        }
    }
}
