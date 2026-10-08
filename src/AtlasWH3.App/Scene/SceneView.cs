using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Top-down view of a Terry scene (world x to the right, z up): the campaign terrain as a shaded height map, every
/// visible entity as a marker coloured by type, and polylines/splines/shapes as outlines. Left click picks (Ctrl adds,
/// Shift toggles), left drag on empty ground box-selects, left drag on a selected entity moves the selection,
/// right/middle drag pans, wheel zooms. Frozen entities cannot be picked.
/// </summary>
public sealed class SceneView : FrameworkElement
{
    public enum SelectMode { Replace, Add, Toggle }

    /// <summary>A painting tool (terrain / tree brushes) that takes over the left button while set.</summary>
    public interface ITool
    {
        void Begin(double x, double z);
        void Drag(double x, double z);
        void End();
        /// <summary>Brush circle radius in world units (0: none).</summary>
        double CursorRadius { get; }
        /// <summary>Draws over the terrain into the view's BGRA buffer; world x at pixel column c is ox + (c + 0.5)·scale,
        /// world z at row r is oz − (r + 0.5)·scale.</summary>
        void DrawOverlay(uint[] buffer, int w, int h, double ox, double oz, double scale);
    }

    /// <summary>Active painting tool, or null for select / move.</summary>
    public ITool? Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            Cursor = value is null ? null : Cursors.Cross;
            Invalidate();
            InvalidateVisual();
        }
    }
    private ITool? _tool;
    private bool _painting;

    private sealed record Marker(string Id, double X, double Z, uint Colour, bool Frozen, (double X, double Z)[][] Outlines)
    {
        /// <summary>Prefab instances: the positions of the entities inside, drawn as faint dots.</summary>
        public (double X, double Z)[] Content { get; init; } = [];
        /// <summary>The model's x/z bounds placed by the entity transform (closed polyline), when known.</summary>
        public (double X, double Z)[]? Footprint { get; init; }
    }

    private SceneModel? _model;
    private int _builtVersion = -1;
    private Marker[] _markers = [];
    private WriteableBitmap? _bitmap;
    private uint[] _buffer = [];
    private bool _dirty = true;
    private double _dpi = 1;

    // view: world x at the left edge, world z at the top edge, world units per device pixel
    private double _ox, _oz, _scale = 1;

    private Point? _panStart;
    private (double Ox, double Oz) _panOrigin;
    private Point? _boxStart;
    private Point? _moveStart;
    private Point _mouse;

    public IReadOnlySet<string> Selection { get; set; } = new HashSet<string>();

    public event Action<IReadOnlyList<string>, SelectMode>? SelectionRequested;
    /// <summary>Raised when a drag moved the selection, with the world offset (dx, dz).</summary>
    public event Action<double, double>? MoveRequested;
    public event Action<(double X, double Z)?>? HoverChanged;

    public SceneView()
    {
        ClipToBounds = true;
        Focusable = true;
        CompositionTarget.Rendering += (_, _) =>
        {
            if (_dirty && IsVisible) RenderNow();
        };
    }

    /// <summary>Rebuilds the markers on the next frame (e.g. when model bounds arrived).</summary>
    public void Refresh()
    {
        _builtVersion = -1;
        Invalidate();
    }

    public void Attach(SceneModel model)
    {
        _model = model;
        _builtVersion = -1;
        FitToContent();
    }

    public void Invalidate() => _dirty = true;

    /// <summary>World point at the centre of the view.</summary>
    public (double X, double Z) Centre
    {
        get
        {
            var (w, h) = PixelSize();
            return (_ox + w / 2.0 * _scale, _oz - h / 2.0 * _scale);
        }
    }

    public void FitToContent()
    {
        if (_model is null || ActualWidth < 1) return;
        double x0, z0, x1, z1;
        if (_model.Terrain is { } t) (x0, z0, x1, z1) = (0, 0, t.WorldW, t.WorldH);
        else
        {
            var pts = _model.All.Select(i => i.Entity.Transform?.Position).Where(p => p is not null).ToList();
            if (pts.Count == 0) (x0, z0, x1, z1) = (-50, -50, 50, 50);
            else (x0, z0, x1, z1) = (pts.Min(p => p![0]) - 10, pts.Min(p => p![2]) - 10, pts.Max(p => p![0]) + 10, pts.Max(p => p![2]) + 10);
        }
        FitTo(x0, z0, x1, z1);
    }

    /// <summary>Frames a world rectangle.</summary>
    public void FitTo(double x0, double z0, double x1, double z1)
    {
        var (w, h) = PixelSize();
        var width = Math.Max(1e-3, x1 - x0);
        var height = Math.Max(1e-3, z1 - z0);
        _scale = Math.Max(width / w, height / h);
        _ox = (x0 + x1) / 2 - w / 2.0 * _scale;
        _oz = (z0 + z1) / 2 + h / 2.0 * _scale;
        Invalidate();
    }

    public void FrameSelection()
    {
        if (_model is null || Selection.Count == 0) return;
        var pts = Selection.Select(id => _model.Find(id)?.Entity.Transform?.Position).Where(p => p is not null).ToList();
        if (pts.Count == 0) return;
        // Prefab instances: include what is inside them.
        foreach (var m in _markers)
            if (Selection.Contains(m.Id))
                pts.AddRange(m.Content.Select(c => (double[]?)[c.X, 0, c.Z]));
        double pad = Math.Max(5, (pts.Max(p => p![0]) - pts.Min(p => p![0]) + pts.Max(p => p![2]) - pts.Min(p => p![2])) * 0.25);
        FitTo(pts.Min(p => p![0]) - pad, pts.Min(p => p![2]) - pad, pts.Max(p => p![0]) + pad, pts.Max(p => p![2]) + pad);
    }

    // ---------------------------------------------------------------- coordinates

    private (int W, int H) PixelSize()
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (Math.Max(1, (int)(ActualWidth * _dpi)), Math.Max(1, (int)(ActualHeight * _dpi)));
    }

    /// <summary>Screen (DIP) → world.</summary>
    public (double X, double Z) ToWorld(Point p) => (_ox + p.X * _dpi * _scale, _oz - p.Y * _dpi * _scale);

    /// <summary>World → screen (DIP).</summary>
    public Point ToScreen(double x, double z) => new((x - _ox) / _scale / _dpi, (_oz - z) / _scale / _dpi);

    // ---------------------------------------------------------------- rendering

    private void Rebuild()
    {
        if (_model is null) return;
        _builtVersion = _model.Version;
        var list = new List<Marker>();
        foreach (var item in _model.All)
        {
            var e = item.Entity;
            if (TerryEntityTypes.IsLayerType(e.Type) || e.Transform is not var (p, _, _) || !_model.IsDrawn(item)) continue;
            var outlines = e.Outlines.Select(o =>
            {
                var pts = o.Points.Select(pt => e.ToWorld(pt.X, pt.Z));
                return (o.Closed && o.Points.Count > 2 ? pts.Append(e.ToWorld(o.Points[0].X, o.Points[0].Z)) : pts).ToArray();
            }).ToList();
            (double X, double Z)[] content = [];
            if (e.Component("ECPrefab")?["key"] is { Length: > 0 } key && _model.ContentOf(key) is { } pc)
            {
                // The prefab's own outlines, plus its footprint (bounds of what is inside) as a box.
                outlines.AddRange(pc.Lines.Select(l => l.Select(pt => e.ToWorld(pt.X, pt.Z)).ToArray()));
                if (pc.Bounds is var (x0, z0, x1, z1))
                    outlines.Add([e.ToWorld(x0, z0), e.ToWorld(x1, z0), e.ToWorld(x1, z1), e.ToWorld(x0, z1), e.ToWorld(x0, z0)]);
                content = pc.Points.Select(pt => e.ToWorld(pt.X, pt.Z)).ToArray();
            }
            (double X, double Z)[]? footprint = null;
            if (SceneModel.ModelPathOf(e) is { } modelPath && _model.ModelBounds.TryGetValue(modelPath, out var b))
                footprint = [e.ToWorld(b[0], b[2]), e.ToWorld(b[3], b[2]), e.ToWorld(b[3], b[5]), e.ToWorld(b[0], b[5]), e.ToWorld(b[0], b[2])];
            list.Add(new Marker(e.Id, p[0], p[2], ColourOf(e.Type), _model.IsFrozen(item), [.. outlines]) { Content = content, Footprint = footprint });
        }
        _markers = [.. list];
    }

    private void RenderNow()
    {
        _dirty = false;
        if (_model is null || ActualWidth < 1 || ActualHeight < 1) return;
        if (_builtVersion != _model.Version) Rebuild();
        var (w, h) = PixelSize();
        if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96 * _dpi, 96 * _dpi, PixelFormats.Bgra32, null);
            _buffer = new uint[w * h];
        }
        DrawTerrain(w, h);
        DrawTiles(w, h);
        DrawRegions(w, h);
        _tool?.DrawOverlay(_buffer, w, h, _ox, _oz, _scale);
        var selected = Selection;
        foreach (var m in _markers)
        {
            var sel = selected.Contains(m.Id);
            var colour = sel ? 0xFFFFE040u : m.Frozen ? Dim(m.Colour) : m.Colour;
            foreach (var line in m.Outlines)
                for (var i = 1; i < line.Length; i++)
                    Line(w, h, line[i - 1], line[i], colour);
        }
        var size = _scale < 0.05 ? 3 : _scale < 0.5 ? 2 : 1;
        // Model footprints once zoomed in far enough for them to read (campaign props are ~0.1-2 units).
        if (_scale < 0.02)
            foreach (var m in _markers)
                if (m.Footprint is { } f)
                {
                    var c = selected.Contains(m.Id) ? 0xFFFFE040u : Blend(m.Colour);
                    for (var i = 1; i < f.Length; i++) Line(w, h, f[i - 1], f[i], c);
                }
        foreach (var m in _markers)
            foreach (var c in m.Content)
                Dot(w, h, c.X, c.Z, Math.Max(1, size - 1), Selection.Contains(m.Id) ? 0xFFC8B040u : Blend(m.Colour));
        foreach (var m in _markers)
        {
            if (selected.Contains(m.Id)) continue;
            Dot(w, h, m.X, m.Z, size, m.Frozen ? Dim(m.Colour) : m.Colour);
        }
        foreach (var m in _markers)
            if (selected.Contains(m.Id)) Dot(w, h, m.X, m.Z, size + 2, 0xFFFFE040u);
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _buffer, w * 4, 0);
        InvalidateVisual();
    }

    private void DrawTerrain(int w, int h)
    {
        if (_model?.Terrain is not var (raster, worldW, worldH))
        {
            Array.Fill(_buffer, 0xFF262626u);
            DrawGrid(w, h);
            return;
        }
        double kx = raster.Width / worldW, kz = raster.Height / worldH;
        Parallel.For(0, h, y =>
        {
            var wz = _oz - (y + 0.5) * _scale;
            var fr = (worldH - wz) * kz - 0.5;
            var row = (int)Math.Floor(fr);
            var line = y * w;
            if (row < 1 || row >= raster.Height - 2)
            {
                Array.Fill(_buffer, 0xFF202020u, line, w);
                return;
            }
            var ty = fr - row;
            for (var x = 0; x < w; x++)
            {
                var fc = (_ox + (x + 0.5) * _scale) * kx - 0.5;
                var col = (int)Math.Floor(fc);
                if (col < 1 || col >= raster.Width - 2) { _buffer[line + x] = 0xFF202020u; continue; }
                var tx = fc - col;
                // Bilinear, so close zoom is smooth rather than blocky.
                double v = (raster[col, row] * (1 - tx) + raster[col + 1, row] * tx) * (1 - ty)
                           + (raster[col, row + 1] * (1 - tx) + raster[col + 1, row + 1] * tx) * ty;
                var ground = v * CameraHeightmapStep.HeightStep + CameraHeightmapStep.HeightOffset;
                var shade = Math.Clamp(1 + (raster[col - 1, row] - raster[col + 1, row] + raster[col, row - 1] - raster[col, row + 1]) * 0.0006, 0.55, 1.4);
                byte r, g, b;
                if (ground <= 0.05) (r, g, b) = (28, 52, 78);
                else
                {
                    var t = Math.Clamp(ground / 10.0, 0, 1);
                    r = (byte)Math.Clamp((70 + 90 * t) * shade, 0, 255);
                    g = (byte)Math.Clamp((86 + 60 * t) * shade, 0, 255);
                    b = (byte)Math.Clamp((58 + 50 * t) * shade, 0, 255);
                }
                _buffer[line + x] = 0xFF000000u | (uint)r << 16 | (uint)g << 8 | b;
            }
        });
    }

    /// <summary>The region mask (when on and built): its RGBA image blended over the map, nearest pixel.</summary>
    private void DrawRegions(int w, int h)
    {
        if (_model is not { ShowRegions: true, RegionsIfBuilt: { } map } || _model.Terrain is not var (_, worldW, worldH)) return;
        Parallel.For(0, h, y =>
        {
            var wz = _oz - (y + 0.5) * _scale;
            var py = (int)((worldH - wz) / worldH * map.Height);
            if (py < 0 || py >= map.Height) return;
            var line = y * w;
            for (var x = 0; x < w; x++)
            {
                var px = (int)((_ox + (x + 0.5) * _scale) / worldW * map.Width);
                if (px < 0 || px >= map.Width) continue;
                var o = (py * map.Width + px) * 4;
                var a = map.Rgba[o + 3] / 255.0;
                if (a <= 0) continue;
                var p = _buffer[line + x];
                uint pr = p >> 16 & 0xFF, pg = p >> 8 & 0xFF, pb = p & 0xFF;
                _buffer[line + x] = 0xFF000000u | (uint)(pr * (1 - a) + map.Rgba[o] * a) << 16
                    | (uint)(pg * (1 - a) + map.Rgba[o + 1] * a) << 8 | (uint)(pb * (1 - a) + map.Rgba[o + 2] * a);
            }
        });
    }

    /// <summary>Tile footprints (the overlay selected in the model): 40% fills in the tile set's colour with a darker
    /// edge, for the tiles that touch the view.</summary>
    private void DrawTiles(int w, int h)
    {
        if (_model is null || _model.TilesShown == SceneModel.TileOverlay.Off || _model.Tiles.Count == 0) return;
        var (_, _, cx, cz) = _model.TileGrid;
        double vx0 = _ox, vx1 = _ox + w * _scale, vz1 = _oz, vz0 = _oz - h * _scale;
        foreach (var t in _model.Tiles)
        {
            if (!_model.ShowsTile(t)) continue;
            double x0 = t.X * cx, x1 = (t.X + t.W) * cx, z0 = t.Y * cz, z1 = (t.Y + t.H) * cz;
            if (x1 < vx0 || x0 > vx1 || z1 < vz0 || z0 > vz1) continue;
            int sx0 = Math.Max(0, (int)((x0 - _ox) / _scale)), sx1 = Math.Min(w - 1, (int)((x1 - _ox) / _scale));
            int sy0 = Math.Max(0, (int)((_oz - z1) / _scale)), sy1 = Math.Min(h - 1, (int)((_oz - z0) / _scale));
            var c = SceneModel.TileColour(t.TileSet);
            uint r = c >> 16 & 0xFF, g = c >> 8 & 0xFF, b = c & 0xFF;
            var edge = sx1 - sx0 > 3 && sy1 - sy0 > 3;
            for (var y = sy0; y <= sy1; y++)
            {
                var row = y * w;
                for (var x = sx0; x <= sx1; x++)
                {
                    var border = edge && (x == sx0 || x == sx1 || y == sy0 || y == sy1);
                    var a = border ? 0.75 : 0.4;
                    var p = _buffer[row + x];
                    uint pr = p >> 16 & 0xFF, pg = p >> 8 & 0xFF, pb = p & 0xFF;
                    var k = border ? 0.6 : 1.0;
                    _buffer[row + x] = 0xFF000000u
                        | (uint)(pr * (1 - a) + r * k * a) << 16 | (uint)(pg * (1 - a) + g * k * a) << 8 | (uint)(pb * (1 - a) + b * k * a);
                }
            }
        }
    }

    /// <summary>World grid for projects without terrain: a power-of-ten spacing ~40-400 px apart, axes brighter.</summary>
    private void DrawGrid(int w, int h)
    {
        var spacing = Math.Pow(10, Math.Ceiling(Math.Log10(40 * _scale)));
        void Lines(double step, uint colour)
        {
            for (var x = Math.Ceiling(_ox / step) * step; x < _ox + w * _scale; x += step)
                Line(w, h, (x, _oz), (x, _oz - h * _scale), Math.Abs(x) < step / 2 ? 0xFF5A5A5Au : colour);
            for (var z = Math.Floor(_oz / step) * step; z > _oz - h * _scale; z -= step)
                Line(w, h, (_ox, z), (_ox + w * _scale, z), Math.Abs(z) < step / 2 ? 0xFF5A5A5Au : colour);
        }
        Lines(spacing, 0xFF2E2E2Eu);
        Lines(spacing * 10, 0xFF3A3A3Au);
    }

    private void Dot(int w, int h, double wx, double wz, int r, uint colour)
    {
        var cx = (int)((wx - _ox) / _scale);
        var cy = (int)((_oz - wz) / _scale);
        for (var y = cy - r; y <= cy + r; y++)
        {
            if (y < 0 || y >= h) continue;
            for (var x = cx - r; x <= cx + r; x++)
                if (x >= 0 && x < w) _buffer[y * w + x] = colour;
        }
    }

    private void Line(int w, int h, (double X, double Z) a, (double X, double Z) b, uint colour)
    {
        double x0 = (a.X - _ox) / _scale, y0 = (_oz - a.Z) / _scale, x1 = (b.X - _ox) / _scale, y1 = (_oz - b.Z) / _scale;
        if ((x0 < 0 && x1 < 0) || (y0 < 0 && y1 < 0) || (x0 >= w && x1 >= w) || (y0 >= h && y1 >= h)) return;
        var steps = (int)Math.Min(4000, Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1);
        for (var i = 0; i <= steps; i++)
        {
            var x = (int)(x0 + (x1 - x0) * i / steps);
            var y = (int)(y0 + (y1 - y0) * i / steps);
            if (x >= 0 && x < w && y >= 0 && y < h) _buffer[y * w + x] = colour;
        }
    }

    private static uint Dim(uint c) => 0xFF000000u | ((c >> 1) & 0x7F7F7Fu);

    /// <summary>75% of the colour: visible on the dark background but quieter than real entities.</summary>
    private static uint Blend(uint c) =>
        0xFF000000u | (uint)((c >> 16 & 0xFF) * 3 / 4) << 16 | (uint)((c >> 8 & 0xFF) * 3 / 4) << 8 | (c & 0xFF) * 3 / 4;

    public static uint ColourOf(string type) => type switch
    {
        "Prop" => 0xFF7CC85Au,
        "Vegetation" => 0xFF3C963Cu,
        "Decal" => 0xFFC89650u,
        "VFX" => 0xFFFF5A3Cu,
        "PointLight" or "SpotLight" => 0xFFFFE65Au,
        "CompositeScene" => 0xFFBE6EFFu,
        "SoundMarker" => 0xFF50C8FFu,
        "River" => 0xFF3C8CFFu,
        "Building" => 0xFFDCDCDCu,
        "Prefab" or "LivePrefab" => 0xFFFF8CC8u,
        _ => 0xFF000000u | (uint)(type.GetHashCode() & 0x7F7F7F) | 0x404040u,
    };

    /// <summary>City dots, and their region names once few enough are in view to read.</summary>
    private void DrawCities(DrawingContext dc)
    {
        if (_model is not { ShowRegions: true, RegionsIfBuilt: { } map }) return;
        var view = new Rect(RenderSize);
        var visible = map.Cities.Select(c => (City: c, P: ToScreen(c.X, c.Z))).Where(c => view.Contains(c.P)).ToList();
        var dot = new SolidColorBrush(Color.FromRgb(255, 230, 80));
        var pen = new Pen(Brushes.Black, 1);
        foreach (var (_, p) in visible) dc.DrawEllipse(dot, pen, p, 3.5, 3.5);
        if (visible.Count > 120) return;
        var tf = new Typeface("Segoe UI");
        foreach (var (c, p) in visible)
        {
            var text = new FormattedText(CityLabel(c.Region), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 11, Brushes.White, 1.0);
            var at = new Point(p.X + 6, p.Y - text.Height / 2);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(at, new Size(text.Width + 4, text.Height)));
            dc.DrawText(text, new Point(at.X + 2, at.Y));
        }
    }

    private static string CityLabel(string region) => SceneModel.RegionLabel(region);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(RenderSize));
        if (_bitmap is not null) dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        DrawCities(dc);
        if (_tool is { CursorRadius: > 0 } tool && IsMouseOver)
        {
            var radius = tool.CursorRadius / _scale / _dpi;
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.2), _mouse, radius, radius);
            dc.DrawEllipse(null, new Pen(Brushes.Black, 0.6), _mouse, radius + 1.2, radius + 1.2);
        }
        if (_boxStart is { } b)
        {
            var r = new Rect(b, _mouse);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 224, 64)), new Pen(Brushes.Gold, 1), r);
        }
        if (_moveStart is { } m && _model is not null)
        {
            // Ghost of the selection at the drag offset.
            var d = _mouse - m;
            var pen = new Pen(Brushes.Gold, 1.5);
            foreach (var id in Selection.Take(3000))
                if (_model.Find(id)?.Entity.Transform is var (p, _, _))
                {
                    var s = ToScreen(p[0], p[2]) + d;
                    dc.DrawEllipse(null, pen, s, 4, 4);
                }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (info.PreviousSize.Width < 1) FitToContent();
        Invalidate();
    }

    // ---------------------------------------------------------------- input

    private Marker? Pick(Point p, double radiusDip = 7)
    {
        var (wx, wz) = ToWorld(p);
        var r = radiusDip * _dpi * _scale;
        Marker? best = null;
        var bestD = r * r;
        foreach (var m in _markers)
        {
            if (m.Frozen) continue;
            var d = (m.X - wx) * (m.X - wx) + (m.Z - wz) * (m.Z - wz);
            if (d <= bestD) { best = m; bestD = d; }
        }
        // Shapes are also picked by their outline.
        if (best is null)
            foreach (var m in _markers)
            {
                if (m.Frozen) continue;
                foreach (var line in m.Outlines)
                    for (var i = 1; i < line.Length; i++)
                        if (SegmentDistance2(wx, wz, line[i - 1], line[i]) <= r * r) return m;
            }
        return best;
    }

    private static double SegmentDistance2(double px, double pz, (double X, double Z) a, (double X, double Z) b)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, len = dx * dx + dz * dz;
        var t = len == 0 ? 0 : Math.Clamp(((px - a.X) * dx + (pz - a.Z) * dz) / len, 0, 1);
        double x = a.X + t * dx - px, z = a.Z + t * dz - pz;
        return x * x + z * z;
    }

    private static SelectMode ModeFromKeys() =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? SelectMode.Toggle
        : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectMode.Add
        : SelectMode.Replace;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var (wx, wz) = ToWorld(p);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1 / 1.25 : 1.25), 1e-4, 1e3);
        _ox = wx - p.X * _dpi * _scale;
        _oz = wz + p.Y * _dpi * _scale;
        Invalidate();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _mouse = p;
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = p;
            _panOrigin = (_ox, _oz);
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (_tool is { } tool)
        {
            _painting = true;
            CaptureMouse();
            var (tx, tz) = ToWorld(p);
            tool.Begin(tx, tz);
            return;
        }
        var hit = Pick(p);
        if (hit is not null && Selection.Contains(hit.Id) && ModeFromKeys() == SelectMode.Replace)
            _moveStart = p;
        else if (hit is not null)
        {
            SelectionRequested?.Invoke([hit.Id], ModeFromKeys());
            if (ModeFromKeys() == SelectMode.Replace) _moveStart = p;
        }
        else _boxStart = p;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _mouse = p;
        if (_panStart is { } start)
        {
            var d = p - start;
            _ox = _panOrigin.Ox - d.X * _dpi * _scale;
            _oz = _panOrigin.Oz + d.Y * _dpi * _scale;
            Invalidate();
        }
        if (_boxStart is not null || _moveStart is not null || _tool is not null) InvalidateVisual();
        if (_painting && _tool is { } tool)
        {
            var (tx, tz) = ToWorld(p);
            tool.Drag(tx, tz);
        }
        HoverChanged?.Invoke(ToWorld(p));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panStart is not null && e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            _panStart = null;
            ReleaseMouseCapture();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        ReleaseMouseCapture();
        if (_painting)
        {
            _painting = false;
            _tool?.End();
            return;
        }
        if (_moveStart is { } m)
        {
            _moveStart = null;
            var d = p - m;
            if (Math.Abs(d.X) + Math.Abs(d.Y) >= 3)
                MoveRequested?.Invoke(d.X * _dpi * _scale, -d.Y * _dpi * _scale);
            InvalidateVisual();
        }
        else if (_boxStart is { } b)
        {
            _boxStart = null;
            var mode = ModeFromKeys();
            if (Math.Abs(p.X - b.X) + Math.Abs(p.Y - b.Y) < 3)
            {
                if (mode == SelectMode.Replace) SelectionRequested?.Invoke([], SelectMode.Replace);
            }
            else
            {
                var (ax, az) = ToWorld(b);
                var (bx, bz) = ToWorld(p);
                double x0 = Math.Min(ax, bx), x1 = Math.Max(ax, bx), z0 = Math.Min(az, bz), z1 = Math.Max(az, bz);
                var ids = _markers.Where(k => !k.Frozen && k.X >= x0 && k.X <= x1 && k.Z >= z0 && k.Z <= z1).Select(k => k.Id).ToList();
                SelectionRequested?.Invoke(ids, mode == SelectMode.Replace ? SelectMode.Replace : SelectMode.Add);
            }
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        HoverChanged?.Invoke(null);
        if (_tool is not null) InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_painting) return;
        _painting = false;
        _tool?.End();
    }
}
