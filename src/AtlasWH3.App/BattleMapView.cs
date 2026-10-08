using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Rendering;

namespace AtlasWH3.App;

/// <summary>Mouse-driven object editing on the battle map (explicit tiles, catchments). Coordinates are fractional cells.</summary>
public interface IMapInteraction
{
    /// <summary>Left button pressed; return true to capture the mouse for a drag.</summary>
    bool Down(double cx, double cy);
    /// <summary>Mouse moved (dragging = left button held after a captured Down); return true to repaint.</summary>
    bool Move(double cx, double cy, bool dragging);
    void Up(double cx, double cy);
    /// <summary>Draws selection/preview overlays; <paramref name="toScreen"/> maps cell coordinates to DIPs.</summary>
    void Draw(DrawingContext dc, Func<double, double, Point> toScreen);
}

/// <summary>
/// Pan/zoom canvas for a battle project (same controls as <see cref="MapView"/>): right/middle drag pans, wheel zooms
/// around the cursor, left drag applies the active tool. View pixels are land-height pixels.
/// </summary>
public sealed class BattleMapView : FrameworkElement
{
    private WriteableBitmap? _bitmap;
    private uint[] _buffer = [];
    private bool _dirty = true;
    private Point? _panStart;
    private Viewport _panOrigin;
    private bool _painting;
    private Point _lastMouse;
    private double _dpiScale = 1;

    public BattleProject? Project { get; private set; }
    public BattleRenderer? Renderer { get; private set; }
    public IBattleTool? ActiveTool { get; set; }
    /// <summary>Single-click action (eyedropper, fill) used instead of a stroke when set; receives the cell.</summary>
    public Action<int, int>? ClickAction { get; set; }
    /// <summary>Object editing mode; takes the left button instead of <see cref="ActiveTool"/> / <see cref="ClickAction"/>.</summary>
    public IMapInteraction? Interaction { get; set; }
    private bool _interactionDrag;
    public UndoStack Undo { get; } = new();
    public Viewport View { get; private set; } = new(0, 0, 8);

    /// <summary>View pixel under the cursor, or null outside the map.</summary>
    public event Action<(double X, double Y)?>? HoverChanged;
    public event Action? Edited;

    public BattleMapView()
    {
        ClipToBounds = true;
        Focusable = true;
        CompositionTarget.Rendering += (_, _) =>
        {
            if (_dirty && IsVisible) RenderNow();
        };
        Undo.Changed += Invalidate;
    }

    public void Load(BattleProject project, BattleRenderer renderer)
    {
        Project = project;
        Renderer = renderer;
        FitToWindow();
    }

    public void Invalidate() => _dirty = true;

    public void FitToWindow()
    {
        if (Project == null || ActualWidth < 1) return;
        var (w, h) = PixelSize();
        var mw = Project.Land.Width;
        var mh = Project.Land.Height;
        var scale = Math.Max((double)mw / w, (double)mh / h);
        View = new Viewport((mw - w * scale) / 2, (mh - h * scale) / 2, scale);
        Invalidate();
    }

    /// <summary>Centres the view on a cell at a zoom where one cell is <paramref name="screenPxPerCell"/> screen pixels.</summary>
    public void CentreOn(int cx, int cy, double screenPxPerCell = 12)
    {
        if (Project == null) return;
        var (w, h) = PixelSize();
        var scale = Project.LandPerCell / screenPxPerCell;
        View = new Viewport((cx + 0.5) * Project.LandPerCell - w * scale / 2, (cy + 0.5) * Project.LandPerCell - h * scale / 2, scale);
        Invalidate();
    }

    public (double X, double Y) ToView(Point p) => View.ScreenToMap(p.X * _dpiScale, p.Y * _dpiScale);

    /// <summary>Screen point → fractional cell.</summary>
    public (double X, double Y) ToCell(Point p)
    {
        var (vx, vy) = ToView(p);
        var per = Project?.LandPerCell ?? 8;
        return (vx / per, vy / per);
    }

    /// <summary>Cell coordinate → screen point (DIPs).</summary>
    public Point CellToScreen(double cx, double cy)
    {
        var per = Project?.LandPerCell ?? 8;
        var (sx, sy) = View.MapToScreen(cx * per, cy * per);
        return new Point(sx / _dpiScale, sy / _dpiScale);
    }

    private (int W, int H) PixelSize()
    {
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (Math.Max(1, (int)(ActualWidth * _dpiScale)), Math.Max(1, (int)(ActualHeight * _dpiScale)));
    }

    private void RenderNow()
    {
        _dirty = false;
        if (Renderer == null || ActualWidth < 1 || ActualHeight < 1) return;
        var (w, h) = PixelSize();
        if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96 * _dpiScale, 96 * _dpiScale, PixelFormats.Bgra32, null);
            _buffer = new uint[w * h];
        }
        Renderer.Render(View, w, h, _buffer);
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _buffer, w * 4, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(RenderSize));
        if (_bitmap != null)
            dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Interaction != null && Project != null)
        {
            Interaction.Draw(dc, CellToScreen);
            return;
        }
        if (ActiveTool != null && ClickAction == null && Project != null && IsMouseOver)
        {
            var radius = ActiveTool.ViewRadius / View.Scale / _dpiScale;
            dc.DrawEllipse(null, new Pen(Brushes.Black, 2.5), _lastMouse, radius, radius);
            dc.DrawEllipse(null, new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash }, _lastMouse, radius, radius);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (info.PreviousSize.Width < 1) FitToWindow();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var (mx, my) = ToView(p);
        var factor = e.Delta > 0 ? 1 / 1.25 : 1.25;
        var scale = Math.Clamp(View.Scale * factor, 0.02, 40);
        View = new Viewport(mx - p.X * _dpiScale * scale, my - p.Y * _dpiScale * scale, scale);
        Invalidate();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = p;
            _panOrigin = View;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || Project == null) return;
        if (Interaction != null)
        {
            var (ix, iy) = ToCell(p);
            _interactionDrag = Interaction.Down(ix, iy);
            if (_interactionDrag) CaptureMouse();
            InvalidateVisual();
            return;
        }
        var (vx, vy) = ToView(p);
        if (ClickAction != null)
        {
            var cx = (int)(vx / Project.LandPerCell);
            var cy = (int)(vy / Project.LandPerCell);
            if (cx >= 0 && cy >= 0 && cx < Project.Width && cy < Project.Height) ClickAction(cx, cy);
            Invalidate();
            return;
        }
        if (ActiveTool == null) return;
        _painting = true;
        ActiveTool.Begin(Project, vx, vy);
        ActiveTool.Dab(vx, vy);
        CaptureMouse();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panStart is { } start)
        {
            var d = p - start;
            View = _panOrigin with
            {
                OriginX = _panOrigin.OriginX - d.X * _dpiScale * View.Scale,
                OriginY = _panOrigin.OriginY - d.Y * _dpiScale * View.Scale,
            };
            Invalidate();
        }
        else if (Interaction != null)
        {
            var (ix, iy) = ToCell(p);
            if (Interaction.Move(ix, iy, _interactionDrag)) Invalidate();
            InvalidateVisual();
        }
        else if (_painting && ActiveTool != null)
        {
            var (x0, y0) = ToView(_lastMouse);
            var (x1, y1) = ToView(p);
            var dist = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            var spacing = Math.Max(1, ActiveTool.ViewRadius * 0.25);
            var steps = Math.Max(1, (int)(dist / spacing));
            for (var i = 1; i <= steps; i++)
                ActiveTool.Dab(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            Invalidate();
        }

        _lastMouse = p;
        if (Project != null)
        {
            var (vx, vy) = ToView(p);
            HoverChanged?.Invoke(vx >= 0 && vy >= 0 && vx < Project.Land.Width && vy < Project.Land.Height ? (vx, vy) : null);
        }
        if (ActiveTool != null) InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panStart != null && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = null;
            ReleaseMouseCapture();
        }
        else if (_interactionDrag && e.ChangedButton == MouseButton.Left)
        {
            _interactionDrag = false;
            ReleaseMouseCapture();
            var (ix, iy) = ToCell(e.GetPosition(this));
            Interaction?.Up(ix, iy);
            Invalidate();
            Edited?.Invoke();
        }
        else if (_painting && e.ChangedButton == MouseButton.Left)
        {
            _painting = false;
            ReleaseMouseCapture();
            if (ActiveTool?.End() is { } undo)
            {
                Undo.Push(undo);
                Edited?.Invoke();
            }
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HoverChanged?.Invoke(null);
        InvalidateVisual();
    }
}
