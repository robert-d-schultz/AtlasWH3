using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

public enum KitTarget { Land, Sea, Trees }

public enum KitHeightMode { Raise, Lower, Smooth, Flatten, SetValue, Noise }

public enum TreeFillScope { Connected, Region }

/// <summary>A raster laid over the campaign world: pixel centres at (i + 0.5), row 0 the north edge (world z = WorldH).</summary>
public readonly record struct WorldRaster(int Width, int Height, double WorldW, double WorldH)
{
    public (double Px, double Py) ToPixel(double x, double z) => (x / WorldW * Width - 0.5, (1 - z / WorldH) * Height - 0.5);
    public (double X, double Z) ToWorld(double px, double py) => ((px + 0.5) / Width * WorldW, (1 - (py + 0.5) / Height) * WorldH);
    public double PixelsPerWorld => Width / WorldW;
}

/// <summary>
/// One height-brush stroke on a float raster in world units (a composited Height or HeightSea map): Begin → Dab* → End,
/// with the same cosine falloff as <see cref="HeightBrush"/>, plus Set-to-value. Radius in pixels
/// of the raster it paints.
/// </summary>
public sealed class KitHeightBrush
{
    public KitHeightMode Mode { get; set; } = KitHeightMode.Raise;
    public double Radius { get; set; } = 20;
    /// <summary>0..1.</summary>
    public double Strength { get; set; } = 0.5;
    /// <summary>0 = hard edge, 1 = fully smooth falloff.</summary>
    public double Softness { get; set; } = 0.7;
    /// <summary>Target of Set-to-value (world y).</summary>
    public float Value { get; set; } = CampaignTerrain.SeaLevel;
    /// <summary>Largest change per dab at full strength (Raise / Lower / Noise), in world units.</summary>
    public double MaxStepPerDab { get; set; } = 0.13;

    private Raster<float>? _raster;
    private RasterStroke<float>? _stroke;
    private double _flattenTarget;
    private Random _random = new(1234);

    public string Name => Mode == KitHeightMode.SetValue ? $"Set height {Value:0.###}" : $"Height {Mode}";

    public void Begin(Raster<float> raster, double mx, double my)
    {
        _raster = raster;
        _stroke = new RasterStroke<float>(raster, Name);
        _flattenTarget = raster.GetClamped((int)Math.Round(mx), (int)Math.Round(my));
        _random = new Random(1234);
    }

    /// <summary>Weight 0..1 of a pixel at distance d from the centre.</summary>
    public double Falloff(double d)
    {
        if (d >= Radius) return 0;
        var t = d / Radius;
        var hardCore = 1 - Softness;
        if (t <= hardCore) return 1;
        var s = (t - hardCore) / Math.Max(1e-6, Softness);
        return 0.5 * (1 + Math.Cos(Math.PI * s));
    }

    /// <summary>The rectangle a dab at (mx, my) may touch, clipped to the raster.</summary>
    public PixelRect Bounds(double mx, double my)
    {
        var h = _raster ?? throw new InvalidOperationException("Begin the stroke first.");
        var r = (int)Math.Ceiling(Radius);
        return new PixelRect(Math.Max(0, (int)Math.Floor(mx) - r), Math.Max(0, (int)Math.Floor(my) - r),
                             Math.Min(h.Width - 1, (int)Math.Ceiling(mx) + r), Math.Min(h.Height - 1, (int)Math.Ceiling(my) + r));
    }

    public PixelRect Dab(double mx, double my)
    {
        var h = _raster ?? throw new InvalidOperationException("Begin the stroke first.");
        var rect = Bounds(mx, my);
        if (rect.IsEmpty) return rect;
        _stroke!.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);

        // Smooth reads a snapshot of the brush area (+ blur margin), so the result doesn't cascade across the dab.
        var blur = Math.Clamp((int)Math.Round(Radius / 6), 1, 6);
        float[]? source = null;
        int sx0 = 0, sy0 = 0, sw = 0, sh = 0;
        if (Mode == KitHeightMode.Smooth)
        {
            sx0 = Math.Max(0, rect.X0 - blur); sy0 = Math.Max(0, rect.Y0 - blur);
            var sx1 = Math.Min(h.Width - 1, rect.X1 + blur);
            var sy1 = Math.Min(h.Height - 1, rect.Y1 + blur);
            sw = sx1 - sx0 + 1;
            sh = sy1 - sy0 + 1;
            source = new float[sw * sh];
            for (var y = sy0; y <= sy1; y++)
                Array.Copy(h.Data, y * h.Width + sx0, source, (y - sy0) * sw, sw);
        }

        var changed = false;
        for (var y = rect.Y0; y <= rect.Y1; y++)
        for (var x = rect.X0; x <= rect.X1; x++)
        {
            var w = Falloff(Math.Sqrt((x - mx) * (x - mx) + (y - my) * (y - my))) * Strength;
            if (w <= 0) continue;
            ref var cell = ref h[x, y];
            double v = cell;
            switch (Mode)
            {
                case KitHeightMode.Raise: v += MaxStepPerDab * w; break;
                case KitHeightMode.Lower: v -= MaxStepPerDab * w; break;
                case KitHeightMode.Flatten: v += (_flattenTarget - v) * w * 0.5; break;
                case KitHeightMode.SetValue: v += (Value - v) * w; break;
                case KitHeightMode.Noise: v += (_random.NextDouble() * 2 - 1) * MaxStepPerDab * w; break;
                case KitHeightMode.Smooth:
                {
                    double sum = 0;
                    var n = 0;
                    for (var dy = -blur; dy <= blur; dy++)
                    for (var dx = -blur; dx <= blur; dx++)
                    {
                        var sx = Math.Clamp(x + dx - sx0, 0, sw - 1);
                        var sy = Math.Clamp(y + dy - sy0, 0, sh - 1);
                        sum += source![sy * sw + sx];
                        n++;
                    }
                    v += (sum / n - v) * w;
                    break;
                }
            }
            var nv = (float)v;
            if (nv != cell) { cell = nv; changed = true; }
        }
        return changed ? rect : PixelRect.Empty;
    }

    public IUndoable? End()
    {
        var stroke = _stroke;
        _stroke = null;
        _raster = null;
        if (stroke == null || stroke.IsEmpty) return null;
        stroke.Finish();
        return stroke;
    }
}

/// <summary>
/// Editing session on a WH3 campaign map's kit sources, as the scene editor sees them. Each target is a stack of Terry
/// layers: Land the Height map, Sea the HeightSea map (the ground of the sea tiles, not a water surface), Trees the
/// CampaignTree map (one tree colour per hex, 2×2 px per hex, <see cref="NoTreeIndex"/> = empty). The scene shows each
/// map composited (<see cref="TerrainComposite"/>); a stroke paints that composite and writes the same change into one
/// chosen layer (<see cref="SetLayer"/>), so what the user sees is what the build will composite:
///  - heights add, so a change d of the composite is d / opacity on the layer;
///  - tree layers replace where not 255, so the composite pixel is recomputed from the layers, top first.
/// Every stroke is one undo step (composite and layer together). Save writes only the layers that changed, in their
/// files' own format, through a <see cref="FileJournal"/> that backs up the originals first, and refuses to overwrite a
/// file changed on disk since load.
/// </summary>
public sealed class KitTerrainEditSession
{
    /// <summary>Palette index of an empty tree-map pixel: no tree, and in a layer, the layers below show.</summary>
    public const byte NoTreeIndex = TreesStep.EmptyIndex;

    /// <summary>One composited layer of a target's map, bottom first.</summary>
    public sealed record LayerFile(string Id, string Name, string Path, float Opacity);

    /// <summary>A terrain map's composited layers, with the map's size.</summary>
    public sealed record MapLayers(string Type, int Width, int Height, IReadOnlyList<LayerFile> Layers)
    {
        /// <summary>The map's composited layers in a project (null when it has no such map or no composited layer).</summary>
        public static MapLayers? Of(TerryProject project, string type)
        {
            if (project.Find(type) is not { } map) return null;
            var layers = map.Layers.Where(l => l.Composited).Select(l => new LayerFile(l.Id, l.Name, project.LayerTifPath(map, l), l.Opacity)).ToList();
            var (w, h) = map.Size;
            return layers.Count == 0 ? null : new MapLayers(type, w, h, layers);
        }
    }

    public sealed record Edit(int Id, KitTarget Target, string LayerId, IUndoable Change, PixelRect Rect, int[] Hexes);

    /// <summary>A change to show: the raster rectangle (in that target's pixels) and the tree hexes (row · cols + col) touched.</summary>
    public sealed record ChangeInfo(KitTarget Target, PixelRect Rect, IReadOnlyCollection<int> Hexes);

    /// <summary>The composited Height map (the scene's own raster, edited in place).</summary>
    public Raster<float> Land { get; }
    /// <summary>The composited CampaignTree map (the scene's own raster, edited in place), or null.</summary>
    public Raster<byte>? TreeMap { get; }
    public TiffMap.Palette? TreePalette { get; }
    public HexGrid? Grid { get; }
    public double WorldW { get; }
    public double WorldH { get; }
    public FileJournal Journal { get; }

    private readonly Dictionary<KitTarget, MapLayers> _maps = [];
    private readonly Dictionary<KitTarget, string> _layer = [];
    private readonly Func<Raster<float>>? _loadSea;
    private Raster<float>? _sea;
    private readonly Dictionary<string, Raster<float>> _heightLayers = new(StringComparer.Ordinal);   // by layer id
    private readonly List<(LayerFile File, Raster<byte> Map, TiffMap.Palette Palette)> _treeLayers = [];
    private readonly Dictionary<string, (long Length, DateTime Time)> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];
    private readonly Dictionary<string, int> _savedId = new(StringComparer.Ordinal);   // by layer id
    private int _nextId = 1;

    // current stroke
    private KitTarget _strokeTarget;
    private KitHeightBrush? _heightStroke;
    private RasterStroke<float>? _layerStroke;
    private RasterStroke<byte>? _treeStroke, _treeLayerStroke;
    private PixelRect _strokeRect = PixelRect.Empty;
    private HashSet<int> _strokeHexes = [];
    private (double X, double Z)? _last;

    // brush settings (world units)
    public KitHeightBrush HeightBrush { get; } = new();
    public double RadiusWorld { get; set; } = 2;
    /// <summary>Palette index painted by the tree brush and fills.</summary>
    public byte TreeIndex { get; set; }
    /// <summary>Tree brush and fills only change hexes whose current (composited) palette index passes this (null: any hex).</summary>
    public Func<byte, bool>? TreeFilter { get; set; }

    public event Action<ChangeInfo>? Changed;

    /// <param name="land">The Height map's layers; <paramref name="landComposite"/> is their composite.</param>
    /// <param name="sea">The HeightSea map's layers; <paramref name="loadSea"/> composites them on first use.</param>
    /// <param name="trees">The CampaignTree map's layers; <paramref name="treeComposite"/> is their composite.</param>
    public KitTerrainEditSession(FileJournal journal, double worldW,
                                 MapLayers land, Raster<float> landComposite,
                                 MapLayers? sea, Func<Raster<float>>? loadSea,
                                 MapLayers? trees, Raster<byte>? treeComposite, TiffMap.Palette? treePalette)
    {
        Journal = journal;
        WorldW = worldW;
        WorldH = CampaignTerrain.DepthOf(landComposite.Width, landComposite.Height, worldW);
        Land = landComposite;
        Add(KitTarget.Land, land);
        if (sea is not null && loadSea is not null)
        {
            Add(KitTarget.Sea, sea);
            _loadSea = loadSea;
        }
        if (trees is not null && treeComposite is not null && treePalette is not null)
        {
            TreeMap = treeComposite;
            TreePalette = treePalette;
            Grid = HexGrid.ForTreeMap(treeComposite.Width, treeComposite.Height, (float)worldW);
            Add(KitTarget.Trees, trees);
            foreach (var layer in trees.Layers)
            {
                var (map, palette) = TiffMap.ReadPalette8(layer.Path);
                if ((map.Width, map.Height) != (treeComposite.Width, treeComposite.Height))
                    throw new InvalidDataException($"{Path.GetFileName(layer.Path)} is {map.Width}x{map.Height}, the tree map {treeComposite.Width}x{treeComposite.Height}");
                _treeLayers.Add((layer, map, palette));
            }
        }
    }

    private void Add(KitTarget target, MapLayers map)
    {
        _maps[target] = map;
        _layer[target] = map.Layers[^1].Id;   // the top layer
        foreach (var l in map.Layers)
        {
            _savedId[l.Id] = 0;
            Stamp(l.Path);
        }
    }

    private void Stamp(string path)
    {
        var info = new FileInfo(path);
        _stamps[path] = info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
    }

    public bool Has(KitTarget target) => _maps.ContainsKey(target);

    /// <summary>The composited layers of a target's map, bottom first (empty when the project has none).</summary>
    public IReadOnlyList<LayerFile> Layers(KitTarget target) => _maps.TryGetValue(target, out var m) ? m.Layers : [];

    /// <summary>The layer a target's strokes write to.</summary>
    public LayerFile? LayerOf(KitTarget target) =>
        _maps.TryGetValue(target, out var m) ? m.Layers.First(l => l.Id == _layer[target]) : null;

    /// <summary>Chooses the layer a target's strokes write to (one of <see cref="Layers"/>).</summary>
    public void SetLayer(KitTarget target, string layerId)
    {
        if (!_maps.TryGetValue(target, out var m) || m.Layers.All(l => l.Id != layerId))
            throw new ArgumentException($"{layerId} is not a composited layer of the {target} map");
        if (Stroking) EndStroke();
        _layer[target] = layerId;
    }

    public string? PathOf(KitTarget target) => LayerOf(target)?.Path;

    /// <summary>The composited HeightSea map, loaded on first use.</summary>
    public Raster<float>? Sea => _sea ??= _loadSea?.Invoke();

    /// <summary>The sea composite if it has been loaded (never loads it).</summary>
    public Raster<float>? SeaIfLoaded => _sea;

    public Raster<float>? HeightRaster(KitTarget target) => target switch { KitTarget.Land => Land, KitTarget.Sea => Sea, _ => null };

    public WorldRaster Frame(KitTarget target) => target switch
    {
        KitTarget.Sea when _maps.TryGetValue(KitTarget.Sea, out var s) => new(s.Width, s.Height, WorldW, WorldH),
        KitTarget.Trees when TreeMap is not null => new(TreeMap.Width, TreeMap.Height, WorldW, WorldH),
        _ => new(Land.Width, Land.Height, WorldW, WorldH),
    };

    /// <summary>Composited height of a target at a world point (nearest pixel), null outside; the sea only once loaded.</summary>
    public float? ValueAt(KitTarget target, double x, double z)
    {
        if ((target == KitTarget.Sea ? SeaIfLoaded : HeightRaster(target)) is not { } r) return null;
        var (px, py) = Frame(target).ToPixel(x, z);
        int ix = (int)Math.Round(px), iy = (int)Math.Round(py);
        return r.Contains(ix, iy) ? r[ix, iy] : null;
    }

    /// <summary>The edited layer's own raster (loaded on first use).</summary>
    private Raster<float> HeightLayer(KitTarget target)
    {
        var layer = LayerOf(target)!;
        if (!_heightLayers.TryGetValue(layer.Id, out var raster))
        {
            raster = TiffMap.ReadFloat32(layer.Path);
            var frame = Frame(target);
            if ((raster.Width, raster.Height) != (frame.Width, frame.Height))
                throw new InvalidDataException($"{Path.GetFileName(layer.Path)} is {raster.Width}x{raster.Height}, the {target} map {frame.Width}x{frame.Height}");
            _heightLayers[layer.Id] = raster;
        }
        return raster;
    }

    private (LayerFile File, Raster<byte> Map, TiffMap.Palette Palette) TreeLayer() =>
        _treeLayers.First(t => t.File.Id == _layer[KitTarget.Trees]);

    // ---------------------------------------------------------------- dirty / undo

    public bool CanUndo => _undo.Count > 0 && !Stroking;
    public bool CanRedo => _redo.Count > 0 && !Stroking;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Change.Description : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Change.Description : null;

    private int TopId(string layerId)
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
            if (_undo[i].LayerId == layerId) return _undo[i].Id;
        return 0;
    }

    /// <summary>A layer's in-memory raster differs from what was loaded or last saved.</summary>
    public bool IsDirtyLayer(string layerId) => TopId(layerId) != _savedId.GetValueOrDefault(layerId);
    public bool IsDirty(KitTarget target) => Layers(target).Any(l => IsDirtyLayer(l.Id));
    public bool IsDirty() => _maps.Keys.Any(IsDirty);

    /// <summary>The layer files with unsaved edits.</summary>
    public IEnumerable<LayerFile> DirtyLayers() => _maps.Values.SelectMany(m => m.Layers).Where(l => IsDirtyLayer(l.Id));

    public Edit? Undo()
    {
        if (!CanUndo) return null;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        e.Change.Undo();
        _redo.Add(e);
        Changed?.Invoke(new ChangeInfo(e.Target, e.Rect, e.Hexes));
        return e;
    }

    public Edit? Redo()
    {
        if (!CanRedo) return null;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        e.Change.Redo();
        _undo.Add(e);
        Changed?.Invoke(new ChangeInfo(e.Target, e.Rect, e.Hexes));
        return e;
    }

    private Edit Push(KitTarget target, IUndoable change, PixelRect rect, IEnumerable<int> hexes)
    {
        var e = new Edit(_nextId++, target, _layer[target], change, rect, hexes.ToArray());
        _undo.Add(e);
        _redo.Clear();
        return e;
    }

    // ---------------------------------------------------------------- strokes

    public bool Stroking => _heightStroke is not null || _treeStroke is not null;

    /// <summary>Starts a stroke on <paramref name="target"/> at a world point and dabs there once.</summary>
    public ChangeInfo? BeginStroke(KitTarget target, double x, double z, bool erase = false)
    {
        if (Stroking) EndStroke();
        if (!Has(target)) throw new InvalidOperationException($"no {target} map in this project");
        _strokeTarget = target;
        _strokeRect = PixelRect.Empty;
        _strokeHexes = [];
        _last = null;
        _treeErase = erase;
        if (target == KitTarget.Trees)
        {
            var name = (erase ? "Erase trees" : "Paint trees") + $" ({LayerOf(target)!.Name})";
            _treeStroke = new RasterStroke<byte>(TreeMap!, name);
            _treeLayerStroke = new RasterStroke<byte>(TreeLayer().Map, name);
        }
        else
        {
            var raster = HeightRaster(target)!;
            var layer = HeightLayer(target);
            var (px, py) = Frame(target).ToPixel(x, z);
            _heightStroke = HeightBrush;
            HeightBrush.Radius = Math.Max(0.5, RadiusWorld * Frame(target).PixelsPerWorld);
            HeightBrush.Begin(raster, px, py);
            _layerStroke = new RasterStroke<float>(layer, HeightBrush.Name);
        }
        return StrokeTo(x, z);
    }

    private bool _treeErase;

    /// <summary>Continues the stroke to a world point, dabbing every quarter radius on the way.</summary>
    public ChangeInfo? StrokeTo(double x, double z)
    {
        if (!Stroking) return null;
        var from = _last ?? (x, z);
        var dist = Math.Sqrt((x - from.X) * (x - from.X) + (z - from.Z) * (z - from.Z));
        var spacing = Math.Max(1e-3, RadiusWorld * 0.25);
        var steps = _last is null ? 0 : Math.Max(1, (int)Math.Ceiling(dist / spacing));
        var rect = PixelRect.Empty;
        var hexes = new HashSet<int>();
        for (var i = _last is null ? 0 : 1; i <= steps; i++)
        {
            var t = steps == 0 ? 1 : (double)i / steps;
            var (dx, dz) = (from.X + (x - from.X) * t, from.Z + (z - from.Z) * t);
            if (_treeStroke is not null) PaintHexes(dx, dz, ref rect, hexes);
            else
            {
                var (px, py) = Frame(_strokeTarget).ToPixel(dx, dz);
                rect = rect.Union(HeightDab(px, py));
            }
        }
        _last = (x, z);
        if (_heightStroke is not null && _strokeTarget == KitTarget.Land && !rect.IsEmpty) hexes.UnionWith(HexesInRect(_strokeTarget, rect));
        if (rect.IsEmpty && hexes.Count == 0) return null;
        _strokeRect = _strokeRect.Union(rect);
        _strokeHexes.UnionWith(hexes);
        var info = new ChangeInfo(_strokeTarget, rect, hexes);
        Changed?.Invoke(info);
        return info;
    }

    /// <summary>One dab on the composite, its change carried into the edited layer (d / opacity).</summary>
    private PixelRect HeightDab(double px, double py)
    {
        var composite = HeightRaster(_strokeTarget)!;
        var layer = HeightLayer(_strokeTarget);
        var opacity = LayerOf(_strokeTarget)!.Opacity;
        var bounds = _heightStroke!.Bounds(px, py);
        if (bounds.IsEmpty) return bounds;
        int bw = bounds.X1 - bounds.X0 + 1, bh = bounds.Y1 - bounds.Y0 + 1;
        var before = new float[bw * bh];
        for (var y = 0; y < bh; y++) Array.Copy(composite.Data, (bounds.Y0 + y) * composite.Width + bounds.X0, before, y * bw, bw);
        var rect = _heightStroke.Dab(px, py);
        if (rect.IsEmpty) return rect;
        _layerStroke!.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);
        for (var y = rect.Y0; y <= rect.Y1; y++)
            for (var x = rect.X0; x <= rect.X1; x++)
            {
                var d = composite[x, y] - before[(y - bounds.Y0) * bw + x - bounds.X0];
                if (d != 0) layer[x, y] += d / opacity;
            }
        return rect;
    }

    /// <summary>Ends the stroke; returns its undo step (null when nothing changed).</summary>
    public Edit? EndStroke()
    {
        IUndoable? change = null;
        if (_heightStroke is not null)
        {
            var composite = _heightStroke.End();
            var layer = _layerStroke!;
            _heightStroke = null;
            _layerStroke = null;
            if (composite is not null)
            {
                layer.Finish();
                change = new CompositeUndo(composite.Description, [composite, layer]);
            }
        }
        else if (_treeStroke is not null)
        {
            var (s, l) = (_treeStroke, _treeLayerStroke!);
            _treeStroke = null;
            _treeLayerStroke = null;
            if (!s.IsEmpty || !l.IsEmpty)
            {
                s.Finish();
                l.Finish();
                change = new CompositeUndo(s.Description, [s, l]);
            }
        }
        _last = null;
        return change is null ? null : Push(_strokeTarget, change, _strokeRect, _strokeHexes);
    }

    // ---------------------------------------------------------------- trees

    /// <summary>World centre of a hex (BOB's placement before the ±0.2 jitter).</summary>
    public (double X, double Z) HexCentre(int col, int row)
    {
        var g = Grid ?? throw new InvalidOperationException("no tree map");
        return (col * g.ColumnStep, row * g.RowStep + ((col & 1) != 0 ? g.HalfRow : 0));
    }

    /// <summary>The hex whose centre is nearest a world point (clamped to the grid).</summary>
    public (int Col, int Row) NearestHex(double x, double z)
    {
        var g = Grid ?? throw new InvalidOperationException("no tree map");
        var best = (Col: 0, Row: 0);
        var bestD = double.MaxValue;
        var c0 = (int)Math.Floor(x / g.ColumnStep);
        for (var col = c0 - 1; col <= c0 + 2; col++)
        {
            var c = Math.Clamp(col, 0, g.Columns - 1);
            var off = (c & 1) != 0 ? g.HalfRow : 0;
            var r0 = (int)Math.Floor((z - off) / g.RowStep);
            for (var row = r0; row <= r0 + 1; row++)
            {
                var r = Math.Clamp(row, 0, g.Rows - 1);
                var (hx, hz) = HexCentre(c, r);
                var d = (hx - x) * (hx - x) + (hz - z) * (hz - z);
                if (d < bestD) { bestD = d; best = (c, r); }
            }
        }
        return best;
    }

    /// <summary>Composited palette index of a hex: the tree-map pixel BOB samples for it.</summary>
    public byte HexIndex(int col, int row)
    {
        var map = TreeMap!;
        var (x, y) = HexGrid.SamplePixel(col, row, map.Height);
        return map.Contains(x, y) ? map[x, y] : NoTreeIndex;
    }

    /// <summary>Sets a hex's 2×2 footprint in the edited tree layer (as <see cref="CampaignTreeGenerator.WriteTreeMap"/>
    /// paints it) and recomposites it; returns its pixel rect, empty when nothing changed.</summary>
    private PixelRect SetHex(int col, int row, byte index, RasterStroke<byte>? stroke, RasterStroke<byte>? layerStroke)
    {
        var map = TreeMap!;
        var layer = TreeLayer().Map;
        var (x, y) = HexGrid.SamplePixel(col, row, map.Height);
        var rect = new PixelRect(Math.Max(0, x), Math.Max(0, y - 1), Math.Min(map.Width - 1, x + 1), Math.Min(map.Height - 1, y));
        if (rect.IsEmpty) return PixelRect.Empty;
        var same = true;
        for (var py = rect.Y0; py <= rect.Y1 && same; py++)
            for (var px = rect.X0; px <= rect.X1; px++)
                if (layer[px, py] != index) { same = false; break; }
        if (same) return PixelRect.Empty;
        stroke?.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);
        layerStroke?.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);
        for (var py = rect.Y0; py <= rect.Y1; py++)
            for (var px = rect.X0; px <= rect.X1; px++)
            {
                layer[px, py] = index;
                map[px, py] = Composite(px, py);
            }
        return rect;
    }

    /// <summary>The tree composite of one pixel: the topmost layer that is not empty there.</summary>
    private byte Composite(int x, int y)
    {
        for (var i = _treeLayers.Count - 1; i >= 0; i--)
            if (_treeLayers[i].Map[x, y] is var v && v != NoTreeIndex) return v;
        return NoTreeIndex;
    }

    public int HexId(int col, int row) => row * Grid!.Value.Columns + col;

    /// <summary>Hexes whose centres lie within <paramref name="radius"/> of a world point (always at least the nearest one).</summary>
    public List<(int Col, int Row)> HexesWithin(double x, double z, double radius)
    {
        var g = Grid ?? throw new InvalidOperationException("no tree map");
        var list = new List<(int, int)>();
        int c0 = Math.Max(0, (int)Math.Floor((x - radius) / g.ColumnStep)), c1 = Math.Min(g.Columns - 1, (int)Math.Ceiling((x + radius) / g.ColumnStep));
        int r0 = Math.Max(0, (int)Math.Floor((z - radius) / g.RowStep) - 1), r1 = Math.Min(g.Rows - 1, (int)Math.Ceiling((z + radius) / g.RowStep));
        for (var c = c0; c <= c1; c++)
            for (var r = r0; r <= r1; r++)
            {
                var (hx, hz) = HexCentre(c, r);
                if ((hx - x) * (hx - x) + (hz - z) * (hz - z) <= radius * radius) list.Add((c, r));
            }
        if (list.Count == 0) list.Add(NearestHex(x, z));
        return list;
    }

    private void PaintHexes(double x, double z, ref PixelRect rect, HashSet<int> hexes)
    {
        var index = _treeErase ? NoTreeIndex : TreeIndex;
        foreach (var (c, r) in HexesWithin(x, z, RadiusWorld))
        {
            if (TreeFilter is { } filter && !filter(HexIndex(c, r))) continue;
            var changed = SetHex(c, r, index, _treeStroke, _treeLayerStroke);
            if (changed.IsEmpty) continue;
            rect = rect.Union(changed);
            hexes.Add(HexId(c, r));
        }
    }

    /// <summary>The six hexes around one (flat-topped columns, odd columns half a row north).</summary>
    public IEnumerable<(int Col, int Row)> Neighbours(int col, int row)
    {
        var g = Grid!.Value;
        var odd = (col & 1) != 0;
        (int, int)[] around = odd
            ? [(col, row - 1), (col, row + 1), (col - 1, row), (col - 1, row + 1), (col + 1, row), (col + 1, row + 1)]
            : [(col, row - 1), (col, row + 1), (col - 1, row - 1), (col - 1, row), (col + 1, row - 1), (col + 1, row)];
        foreach (var (c, r) in around)
            if ((uint)c < (uint)g.Columns && (uint)r < (uint)g.Rows) yield return (c, r);
    }

    /// <summary>
    /// Flood fill from the hex under a world point with <see cref="TreeIndex"/> (or empty when erasing), in the edited
    /// layer. Connected: every hex reachable through hexes of the start hex's current (composited) index. Region: every
    /// hex of the same region key (<paramref name="regionAt"/>) reachable through that region.
    /// <paramref name="maxRadius"/> (world, 0 = none) bounds it.
    /// </summary>
    public Edit? FillTrees(double x, double z, TreeFillScope scope, bool erase = false, double maxRadius = 0,
                           Func<double, double, string?>? regionAt = null)
    {
        if (TreeMap is null) throw new InvalidOperationException("no tree map");
        if (Stroking) EndStroke();
        var (sc, sr) = NearestHex(x, z);
        var index = erase ? NoTreeIndex : TreeIndex;
        var from = HexIndex(sc, sr);
        string? region = null;
        if (scope == TreeFillScope.Region)
        {
            if (regionAt is null) throw new InvalidOperationException("no region map to fill by");
            var (hx, hz) = HexCentre(sc, sr);
            region = regionAt(hx, hz) ?? throw new InvalidOperationException("no region under the cursor");
        }
        else if (from == index) return null;
        var (ox, oz) = HexCentre(sc, sr);
        bool Inside(int c, int r)
        {
            var (hx, hz) = HexCentre(c, r);
            if (maxRadius > 0 && (hx - ox) * (hx - ox) + (hz - oz) * (hz - oz) > maxRadius * maxRadius) return false;
            return scope == TreeFillScope.Region ? regionAt!(hx, hz) == region : HexIndex(c, r) == from;
        }
        var name = (erase ? "Erase fill" : "Fill trees") + $" ({LayerOf(KitTarget.Trees)!.Name})";
        var stroke = new RasterStroke<byte>(TreeMap, name);
        var layerStroke = new RasterStroke<byte>(TreeLayer().Map, name);
        var seen = new HashSet<int> { HexId(sc, sr) };
        var queue = new Queue<(int, int)>();
        queue.Enqueue((sc, sr));
        var rect = PixelRect.Empty;
        var hexes = new List<int>();
        while (queue.Count > 0)
        {
            var (c, r) = queue.Dequeue();
            if (TreeFilter is not { } filter || filter(HexIndex(c, r)))
            {
                var changed = SetHex(c, r, index, stroke, layerStroke);
                if (!changed.IsEmpty) { rect = rect.Union(changed); hexes.Add(HexId(c, r)); }
            }
            foreach (var (nc, nr) in Neighbours(c, r))
                if (seen.Add(HexId(nc, nr)) && Inside(nc, nr)) queue.Enqueue((nc, nr));
        }
        if (layerStroke.IsEmpty) return null;
        stroke.Finish();
        layerStroke.Finish();
        var e = Push(KitTarget.Trees, new CompositeUndo(name, [stroke, layerStroke]), rect, hexes);
        Changed?.Invoke(new ChangeInfo(KitTarget.Trees, rect, hexes));
        return e;
    }

    /// <summary>Hex count per composited palette index (sampled pixels), 256 entries.</summary>
    public int[] TreeCounts()
    {
        var counts = new int[256];
        if (Grid is not { } g) return counts;
        for (var r = 0; r < g.Rows; r++)
            for (var c = 0; c < g.Columns; c++) counts[HexIndex(c, r)]++;
        return counts;
    }

    /// <summary>Colour (0xRRGGBB) of a palette index.</summary>
    public int PaletteRgb(int index) => TreePalette is not { } p || index >= p.R.Length ? 0
        : (p.R[index] >> 8) << 16 | (p.G[index] >> 8) << 8 | p.B[index] >> 8;

    /// <summary>Tree hexes whose centres fall in a pixel rectangle of a target raster (their trees follow the ground).</summary>
    public IEnumerable<int> HexesInRect(KitTarget target, PixelRect rect)
    {
        if (Grid is not { } g || rect.IsEmpty) yield break;
        var f = Frame(target);
        var (x0, z1) = f.ToWorld(rect.X0 - 1, rect.Y0 - 1);
        var (x1, z0) = f.ToWorld(rect.X1 + 1, rect.Y1 + 1);
        int c0 = Math.Max(0, (int)Math.Floor(x0 / g.ColumnStep)), c1 = Math.Min(g.Columns - 1, (int)Math.Ceiling(x1 / g.ColumnStep));
        int r0 = Math.Max(0, (int)Math.Floor(z0 / g.RowStep) - 1), r1 = Math.Min(g.Rows - 1, (int)Math.Ceiling(z1 / g.RowStep));
        for (var c = c0; c <= c1; c++)
            for (var r = r0; r <= r1; r++) yield return HexId(c, r);
    }

    // ---------------------------------------------------------------- save

    /// <summary>Files of the dirty layers that changed on disk since they were loaded or saved (another tool).</summary>
    public List<string> ChangedOnDisk() =>
        DirtyLayers().Select(l => l.Path).Where(p =>
        {
            var info = new FileInfo(p);
            return _stamps.TryGetValue(p, out var s) && (info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default)) != s;
        }).ToList();

    /// <summary>
    /// Writes every dirty layer through the journal, in the files' own format. Returns the files written and the journal
    /// seq (0 when nothing was dirty).
    /// </summary>
    public (IReadOnlyList<string> Files, int Seq) Save(string label, bool force = false)
    {
        if (Stroking) EndStroke();
        var dirty = DirtyLayers().ToList();
        if (dirty.Count == 0) return ([], 0);
        if (!force && ChangedOnDisk() is { Count: > 0 } changed)
            throw new InvalidOperationException($"changed on disk since loaded: {string.Join(", ", changed.Select(Path.GetFileName))}; reload, or save anyway");
        var writes = new List<(string, Action<string>)>();
        foreach (var l in dirty)
        {
            if (_heightLayers.TryGetValue(l.Id, out var heights))
                writes.Add((l.Path, f => TiffMap.SaveFloat32Like(f, heights)));
            else if (_treeLayers.FirstOrDefault(t => t.File.Id == l.Id) is { Map: not null } tree)
                writes.Add((l.Path, f => TiffMap.SavePalette8Like(f, tree.Map, tree.Palette)));
        }
        var seq = Journal.Commit(writes, label);
        foreach (var (p, _) in writes) Stamp(p);
        foreach (var l in dirty) _savedId[l.Id] = TopId(l.Id);
        return (writes.Select(w => w.Item1).ToList(), seq);
    }
}
