using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Rendering;

namespace AtlasWH3.App;

/// <summary>
/// Pan/zoom map canvas. Renders the terrain (and overlays) for the visible area into a WriteableBitmap
/// sized to the control in device pixels. Right/middle drag pans, wheel zooms around the cursor, and
/// left drag applies the active brush.
/// </summary>
public sealed class MapView : FrameworkElement
{
    private WriteableBitmap? _bitmap;
    private uint[] _buffer = [];
    private bool _dirty = true;
    private Point? _panStart;
    private Viewport _panOrigin;
    private bool _painting;
    private Point _lastMouse;
    private double _dpiScale = 1;

    public TerrainData? Terrain { get; private set; }
    public TerrainRenderer? Renderer { get; private set; }
    public TreeOverlay? Trees { get; set; }
    public TerrainBrush? ActiveBrush { get; set; }
    public UndoStack Undo { get; } = new();

    public Viewport View { get; private set; } = new(0, 0, 4);

    /// <summary>Raised with the map pixel under the cursor (or null when outside the map).</summary>
    public event Action<(double X, double Y)?>? HoverChanged;
    public event Action? Edited;

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        CompositionTarget.Rendering += (_, _) =>
        {
            if (_dirty && IsVisible) RenderNow();
        };
        Undo.Changed += Invalidate;
    }

    public void Load(TerrainData terrain, TerrainRenderer renderer)
    {
        Terrain = terrain;
        Renderer = renderer;
        FitToWindow();
    }

    public void Invalidate() => _dirty = true;

    public void FitToWindow()
    {
        if (Terrain == null || ActualWidth < 1) return;
        var (w, h) = PixelSize();
        var scale = Math.Max((double)Terrain.Width / w, (double)Terrain.HeightPx / h);
        View = new Viewport((Terrain.Width - w * scale) / 2, (Terrain.HeightPx - h * scale) / 2, scale);
        Invalidate();
    }

    /// <summary>Screen (DIP) point → lf map pixel.</summary>
    public (double X, double Y) ToMap(Point p) => View.ScreenToMap(p.X * _dpiScale, p.Y * _dpiScale);

    /// <summary>lf map pixel → screen (DIP) point.</summary>
    public Point ToScreen(double mx, double my)
    {
        var (sx, sy) = View.MapToScreen(mx, my);
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
        if (Renderer == null || Terrain == null || ActualWidth < 1 || ActualHeight < 1) return;
        var (w, h) = PixelSize();
        if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96 * _dpiScale, 96 * _dpiScale, PixelFormats.Bgra32, null);
            _buffer = new uint[w * h];
        }
        Renderer.Render(View, w, h, _buffer);
        Trees?.Draw(View, w, h, _buffer, Terrain.Width, Terrain.HeightPx, Terrain.Coords);
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _buffer, w * 4, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(RenderSize));
        if (_bitmap != null)
            dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));

        // Brush outline.
        if (ActiveBrush != null && Terrain != null && IsMouseOver)
        {
            var radius = ActiveBrush.Radius / View.Scale / _dpiScale;
            var pen = new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash };
            dc.DrawEllipse(null, new Pen(Brushes.Black, 2.5), _lastMouse, radius, radius);
            dc.DrawEllipse(null, pen, _lastMouse, radius, radius);
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
        var (mx, my) = ToMap(p);
        var factor = e.Delta > 0 ? 1 / 1.25 : 1.25;
        var scale = Math.Clamp(View.Scale * factor, 0.05, 16);
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
        }
        else if (e.ChangedButton == MouseButton.Left && ActiveBrush != null && Terrain != null)
        {
            var (mx, my) = ToMap(p);
            _painting = true;
            ActiveBrush.Begin(Terrain, mx, my);
            ActiveBrush.Dab(mx, my);
            CaptureMouse();
            Invalidate();
        }
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
        else if (_painting && ActiveBrush != null)
        {
            // Dab along the segment so fast strokes stay continuous.
            var (x0, y0) = ToMap(_lastMouse);
            var (x1, y1) = ToMap(p);
            var dist = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            var spacing = Math.Max(1, ActiveBrush.Radius * 0.25);
            var steps = Math.Max(1, (int)(dist / spacing));
            for (var i = 1; i <= steps; i++)
                ActiveBrush.Dab(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            Invalidate();
        }

        _lastMouse = p;
        if (Terrain != null)
        {
            var (mx, my) = ToMap(p);
            HoverChanged?.Invoke(mx >= 0 && my >= 0 && mx < Terrain.Width && my < Terrain.HeightPx ? (mx, my) : null);
        }
        if (ActiveBrush != null) InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panStart != null && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = null;
            ReleaseMouseCapture();
        }
        else if (_painting && e.ChangedButton == MouseButton.Left)
        {
            _painting = false;
            ReleaseMouseCapture();
            if (ActiveBrush?.End() is { } undo)
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
