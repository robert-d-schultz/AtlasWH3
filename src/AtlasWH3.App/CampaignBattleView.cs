using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtlasWH3.Core.Battle;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.App;

/// <summary>
/// The campaign-battle catchment map: the battle grid's landmass, the areas of the visible lists, settlement markers
/// and the coverage gaps of the active list. Right/middle drag pans, the wheel zooms around the cursor; the left button
/// selects, moves and resizes areas (Select tool) or drags out a new one (Draw tool). Coordinates are cells, row 0 =
/// north, as stored in battle_locations_map.bin.
/// </summary>
public sealed class CampaignBattleView : FrameworkElement
{
    public enum ToolMode { Select, Draw }

    /// <summary>A settlement marker: cell, colour, and the tag returned on click.</summary>
    public sealed record Marker(int X, int Y, Color Colour, object Tag);

    public static readonly IReadOnlyDictionary<string, Color> ListColours = new Dictionary<string, Color>
    {
        [BattleLocations.Ambush] = Color.FromRgb(0xE0, 0xA0, 0x40),
        [BattleLocations.Standard] = Color.FromRgb(0xF0, 0x50, 0x50),
        [BattleLocations.Encampments] = Color.FromRgb(0x90, 0x90, 0x90),
        [BattleLocations.Unfortified] = Color.FromRgb(0x60, 0xC0, 0xF0),
        [BattleLocations.Gate] = Color.FromRgb(0xC0, 0x70, 0xF0),
    };

    public static Color ColourOf(string list) => ListColours.TryGetValue(list, out var c) ? c : Colors.White;

    private BattleLocations? _map;
    private WriteableBitmap? _base;
    private double _scale = 1;        // screen DIPs per cell
    private Point _origin;            // screen position of cell (0, 0)
    private Point? _panStart;
    private bool _userView;           // the user zoomed or panned: stop refitting on resize
    private Point _panOrigin;

    // drag state
    private enum Drag { None, Move, Resize, Create }
    private Drag _drag;
    private int _handle;               // resize handle 0..7 (clockwise from top-left)
    private (int X, int Y) _dragStartCell;
    private CellBox _dragStartBox;
    private (int X, int Y) _dragStartCentre;
    private CellBox? _creating;
    private bool _dragChanged;

    public BattleLocations? Map => _map;
    public HashSet<string> VisibleLists { get; } = [];
    public string ActiveList { get; set; } = BattleLocations.Standard;
    public bool ShowGaps { get; set; }
    public bool ShowMarkers { get; set; } = true;
    public IReadOnlyList<Marker> Markers { get; set; } = [];
    public ToolMode Tool { get; set; }
    public (string List, CatchmentArea Area)? Selected { get; private set; }

    /// <summary>The cell under the cursor (null outside the map).</summary>
    public event Action<(int X, int Y)?>? HoverChanged;
    public event Action? SelectionChanged;
    /// <summary>An edit is about to start (take the undo snapshot now).</summary>
    public event Action? EditStarting;
    /// <summary>A drag edit ended; <c>true</c> when it changed something.</summary>
    public event Action<bool>? EditFinished;
    /// <summary>A new area box was dragged out in the active list.</summary>
    public event Action<CellBox>? AreaDrawn;
    public event Action<object>? MarkerClicked;

    public CampaignBattleView()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public void Load(BattleLocations map)
    {
        _map = map;
        Selected = null;
        RebuildBase();
        FitToWindow();
    }

    /// <summary>Redraws the land and gap layer (after a coverage change).</summary>
    public void RebuildBase()
    {
        if (_map is null) return;
        var (w, h) = (_map.Width, _map.Height);
        var px = new uint[w * h];
        var gaps = ShowGaps ? CatchmentOps.CoverageMask(_map, ActiveList) : null;
        var land = _map.LandIndex;
        for (var i = 0; i < px.Length; i++)
        {
            var isLand = _map.Cells[i] == land;
            px[i] = !isLand ? 0xFF1C2430u : gaps is not null && !gaps[i] ? 0xFF3A6EA8u : 0xFF55604Au;
        }
        _base = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        _base.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        InvalidateVisual();
    }

    public void Select(string list, CatchmentArea? area)
    {
        Selected = area is null ? null : (list, area);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    /// <summary>DIPs per cell (the zoom).</summary>
    public double Zoom => _scale;

    public void FitToWindow()
    {
        _userView = false;
        if (_map is null || ActualWidth < 1 || ActualHeight < 1) return;
        _scale = Math.Min(ActualWidth / _map.Width, ActualHeight / _map.Height);
        _origin = new Point((ActualWidth - _map.Width * _scale) / 2, (ActualHeight - _map.Height * _scale) / 2);
        InvalidateVisual();
    }

    /// <summary>Centres on a cell at a zoom of <paramref name="pxPerCell"/> DIPs per cell.</summary>
    public void CentreOn(int x, int y, double pxPerCell = 10)
    {
        _userView = true;
        _scale = pxPerCell;
        _origin = new Point(ActualWidth / 2 - (x + 0.5) * _scale, ActualHeight / 2 - (y + 0.5) * _scale);
        InvalidateVisual();
    }

    private Point ToScreen(double cx, double cy) => new(_origin.X + cx * _scale, _origin.Y + cy * _scale);
    private (double X, double Y) ToCellF(Point p) => ((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);
    private (int X, int Y) ToCell(Point p) { var (x, y) = ToCellF(p); return ((int)Math.Floor(x), (int)Math.Floor(y)); }
    private Rect BoxRect(CellBox b) => new(ToScreen(b.MinX, b.MinY), ToScreen(b.MaxX + 1, b.MaxY + 1));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 20, 24)), null, new Rect(RenderSize));
        if (_map is null || _base is null) return;
        dc.DrawImage(_base, new Rect(_origin, new Size(_map.Width * _scale, _map.Height * _scale)));
        var view = new Rect(RenderSize);
        var thin = Math.Clamp(_scale * 0.12, 0.6, 1.5);
        foreach (var list in _map.Lists)
        {
            if (!VisibleLists.Contains(list.Key)) continue;
            var c = ColourOf(list.Key);
            var pen = new Pen(new SolidColorBrush(c), thin);
            pen.Freeze();
            var fill = list.Key == ActiveList ? new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B)) : null;
            fill?.Freeze();
            foreach (var a in list.Areas)
            {
                var r = BoxRect(a.Box);
                if (!r.IntersectsWith(view)) continue;
                dc.DrawRectangle(fill, pen, r);
            }
        }
        if (ShowMarkers)
        {
            var radius = Math.Clamp(_scale * 0.9, 2.5, 7);
            foreach (var m in Markers)
            {
                var p = ToScreen(m.X + 0.5, m.Y + 0.5);
                if (!view.Contains(p)) continue;
                dc.DrawEllipse(new SolidColorBrush(m.Colour), new Pen(Brushes.Black, 1), p, radius, radius);
            }
        }
        if (Selected is { } sel)
        {
            var r = BoxRect(sel.Area.Box);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 3.5), r);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(Brushes.White, 1.5), r);
            foreach (var h in Handles(r)) dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(h.X - 4, h.Y - 4, 8, 8));
            var c = ToScreen(sel.Area.Centre.X + 0.5, sel.Area.Centre.Y + 0.5);
            var cross = new Pen(Brushes.Yellow, 1.5);
            dc.DrawLine(cross, new Point(c.X - 6, c.Y), new Point(c.X + 6, c.Y));
            dc.DrawLine(cross, new Point(c.X, c.Y - 6), new Point(c.X, c.Y + 6));
        }
        if (_creating is { } box)
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(ColourOf(ActiveList)), 2) { DashStyle = DashStyles.Dash }, BoxRect(box));
    }

    private static Point[] Handles(Rect r) =>
    [
        r.TopLeft, new(r.Left + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Top + r.Height / 2),
        r.BottomRight, new(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Top + r.Height / 2),
    ];

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (!_userView) FitToWindow();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var (cx, cy) = ToCellF(p);
        _userView = true;
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 0.8), 0.2, 80);
        _origin = new Point(p.X - cx * _scale, p.Y - cy * _scale);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = p;
            _panOrigin = _origin;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _map is null) return;
        var cell = ToCell(p);
        if (Tool == ToolMode.Draw)
        {
            _drag = Drag.Create;
            _dragStartCell = cell;
            _creating = new CellBox(cell.X, cell.Y, cell.X, cell.Y);
            CaptureMouse();
            InvalidateVisual();
            return;
        }
        // a resize handle or the body of the selected area
        if (Selected is { } sel)
        {
            var handles = Handles(BoxRect(sel.Area.Box));
            for (var i = 0; i < handles.Length; i++)
                if ((handles[i] - p).Length <= 6) { BeginDrag(Drag.Resize, cell, sel.Area, i); return; }
        }
        // a settlement marker
        if (ShowMarkers)
            foreach (var m in Markers)
                if ((ToScreen(m.X + 0.5, m.Y + 0.5) - p).Length <= Math.Max(5, _scale * 0.9))
                {
                    MarkerClicked?.Invoke(m.Tag);
                    return;
                }
        if (Selected is { } s2 && s2.Area.Box.Contains(cell.X, cell.Y) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            BeginDrag(Drag.Move, cell, s2.Area, 0);
            return;
        }
        // pick the smallest visible area under the cursor (Shift: the next one)
        var hits = _map.Lists.Where(l => VisibleLists.Contains(l.Key))
            .SelectMany(l => l.Areas.Where(a => a.Box.Contains(cell.X, cell.Y)).Select(a => (l.Key, a)))
            .OrderBy(h => h.a.Box.Area).ToList();
        if (hits.Count == 0) { Select("", null); return; }
        var next = Selected is { } cur ? hits.FindIndex(h => ReferenceEquals(h.a, cur.Area)) + 1 : 0;
        var pick = hits[next % hits.Count];
        Select(pick.Key, pick.a);
    }

    private void BeginDrag(Drag kind, (int X, int Y) cell, CatchmentArea area, int handle)
    {
        EditStarting?.Invoke();
        _drag = kind;
        _handle = handle;
        _dragStartCell = cell;
        _dragStartBox = area.Box;
        _dragStartCentre = area.Centre;
        _dragChanged = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panStart is { } start)
        {
            _origin = _panOrigin + (p - start);
            _userView = true;
            InvalidateVisual();
        }
        var cell = ToCell(p);
        if (_map is not null)
            HoverChanged?.Invoke(cell.X >= 0 && cell.Y >= 0 && cell.X < _map.Width && cell.Y < _map.Height ? cell : null);
        if (_drag == Drag.Create)
        {
            _creating = new CellBox(Math.Min(cell.X, _dragStartCell.X), Math.Min(cell.Y, _dragStartCell.Y),
                Math.Max(cell.X, _dragStartCell.X), Math.Max(cell.Y, _dragStartCell.Y));
            InvalidateVisual();
        }
        else if (_drag is Drag.Move or Drag.Resize && Selected is { } sel && _map is not null)
        {
            var (dx, dy) = (cell.X - _dragStartCell.X, cell.Y - _dragStartCell.Y);
            var b = _dragStartBox;
            if (_drag == Drag.Move)
            {
                sel.Area.Box = b.Offset(dx, dy);
                sel.Area.Centre = (_dragStartCentre.X + dx, _dragStartCentre.Y + dy);
            }
            else
            {
                int x0 = b.MinX, y0 = b.MinY, x1 = b.MaxX, y1 = b.MaxY;
                if (_handle is 0 or 6 or 7) x0 = Math.Min(b.MinX + dx, x1);
                if (_handle is 2 or 3 or 4) x1 = Math.Max(b.MaxX + dx, x0);
                if (_handle is 0 or 1 or 2) y0 = Math.Min(b.MinY + dy, y1);
                if (_handle is 4 or 5 or 6) y1 = Math.Max(b.MaxY + dy, y0);
                sel.Area.Box = new CellBox(x0, y0, x1, y1);
            }
            _dragChanged = sel.Area.Box != _dragStartBox;
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panStart is not null && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = null;
            ReleaseMouseCapture();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (drag == Drag.Create)
        {
            var box = _creating;
            _creating = null;
            InvalidateVisual();
            if (box is { } b && _map is not null)
                AreaDrawn?.Invoke(new CellBox(Math.Max(0, b.MinX), Math.Max(0, b.MinY), Math.Min(_map.Width - 1, b.MaxX), Math.Min(_map.Height - 1, b.MaxY)));
            return;
        }
        EditFinished?.Invoke(_dragChanged);
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeave(MouseEventArgs e) => HoverChanged?.Invoke(null);
}
