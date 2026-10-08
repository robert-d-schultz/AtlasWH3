using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Maps;

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
/// One height-brush stroke on a 16-bit raster (the kit's LowFrequencyHeight or LowFrequencyHeightSea TIF): Begin → Dab* →
/// End, with the same cosine falloff as the Terrain painter's <see cref="HeightBrush"/>, plus Set-to-value. Radius in
/// pixels of the raster it paints.
/// </summary>
public sealed class KitHeightBrush
{
    public KitHeightMode Mode { get; set; } = KitHeightMode.Raise;
    public double Radius { get; set; } = 20;
    /// <summary>0..1.</summary>
    public double Strength { get; set; } = 0.5;
    /// <summary>0 = hard edge, 1 = fully smooth falloff.</summary>
    public double Softness { get; set; } = 0.7;
    /// <summary>Target of Set-to-value (raw u16).</summary>
    public ushort Value { get; set; } = KitTerrainEditSession.SeaLevel;
    /// <summary>Largest change per dab at full strength (Raise / Lower / Noise), in raw u16 steps (1000 ≈ 0.22 world units).</summary>
    public double MaxStepPerDab { get; set; } = 600;

    private Raster<ushort>? _raster;
    private RasterStroke<ushort>? _stroke;
    private double _flattenTarget;
    private Random _random = new(1234);

    public string Name => Mode == KitHeightMode.SetValue ? $"Set height {Value}" : $"Height {Mode}";

    public void Begin(Raster<ushort> raster, double mx, double my)
    {
        _raster = raster;
        _stroke = new RasterStroke<ushort>(raster, Name);
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

    public PixelRect Dab(double mx, double my)
    {
        var h = _raster ?? throw new InvalidOperationException("Begin the stroke first.");
        var r = (int)Math.Ceiling(Radius);
        var rect = new PixelRect(Math.Max(0, (int)Math.Floor(mx) - r), Math.Max(0, (int)Math.Floor(my) - r),
                                 Math.Min(h.Width - 1, (int)Math.Ceiling(mx) + r), Math.Min(h.Height - 1, (int)Math.Ceiling(my) + r));
        if (rect.IsEmpty) return rect;
        _stroke!.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);

        // Smooth reads a snapshot of the brush area (+ blur margin), so the result doesn't cascade across the dab.
        var blur = Math.Clamp((int)Math.Round(Radius / 6), 1, 6);
        ushort[]? source = null;
        int sx0 = 0, sy0 = 0, sw = 0, sh = 0;
        if (Mode == KitHeightMode.Smooth)
        {
            sx0 = Math.Max(0, rect.X0 - blur); sy0 = Math.Max(0, rect.Y0 - blur);
            var sx1 = Math.Min(h.Width - 1, rect.X1 + blur);
            var sy1 = Math.Min(h.Height - 1, rect.Y1 + blur);
            sw = sx1 - sx0 + 1;
            sh = sy1 - sy0 + 1;
            source = new ushort[sw * sh];
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
            var nv = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
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
/// Editing session on a campaign map's kit sources, as the scene editor sees them: the .terry's LowFrequencyHeight (land)
/// and LowFrequencyHeightSea (sea surface, usually half resolution) 16-bit TIFs, and the CampaignTree palette TIF (one tree
/// colour per hex, 2×2 px per hex, palette = the sorted campaign_tree_ids colours, <see cref="NoTreeIndex"/> = no tree).
/// The rasters are edited in place (the scene's own copies, so 2D/3D draw them live); every stroke is one undo step.
/// Save writes only the targets that changed, in each file's own format (<see cref="TiffMap.SaveGray16Like"/>), through a
/// <see cref="FileJournal"/> that backs up the originals first, and refuses to overwrite a file changed on disk since load.
/// </summary>
public sealed class KitTerrainEditSession
{
    /// <summary>Raw u16 of the vanilla sea surface (LowFrequencyHeightSea is flat 14219 over the open sea).</summary>
    public const ushort SeaLevel = 14219;
    public const byte NoTreeIndex = 19;

    public sealed record Edit(int Id, KitTarget Target, IUndoable Change, PixelRect Rect, int[] Hexes);

    /// <summary>A change to show: the raster rectangle (in that target's pixels) and the tree hexes (row · cols + col) touched.</summary>
    public sealed record ChangeInfo(KitTarget Target, PixelRect Rect, IReadOnlyCollection<int> Hexes);

    public Raster<ushort> Land { get; }
    public Raster<ushort>? Sea { get; }
    public Raster<byte>? TreeMap { get; }
    public TiffMap.Palette? TreePalette { get; }
    public HexGrid? Grid { get; }
    public double WorldW { get; }
    public double WorldH { get; }
    public FileJournal Journal { get; }

    private readonly Dictionary<KitTarget, (string Path, List<string> Mirrors)> _files = [];
    private readonly Dictionary<string, (long Length, DateTime Time)> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];
    private readonly Dictionary<KitTarget, int> _savedId = new() { [KitTarget.Land] = 0, [KitTarget.Sea] = 0, [KitTarget.Trees] = 0 };
    private int _nextId = 1;

    // current stroke
    private KitTarget _strokeTarget;
    private KitHeightBrush? _heightStroke;
    private RasterStroke<byte>? _treeStroke;
    private PixelRect _strokeRect = PixelRect.Empty;
    private HashSet<int> _strokeHexes = [];
    private (double X, double Z)? _last;

    // brush settings (world units)
    public KitHeightBrush HeightBrush { get; } = new();
    public double RadiusWorld { get; set; } = 2;
    /// <summary>Palette index painted by the tree brush and fills.</summary>
    public byte TreeIndex { get; set; }
    /// <summary>Tree brush and fills only change hexes whose current palette index passes this (null: any hex).</summary>
    public Func<byte, bool>? TreeFilter { get; set; }

    public event Action<ChangeInfo>? Changed;

    public KitTerrainEditSession(FileJournal journal, double worldW, double worldH,
                                 string landPath, Raster<ushort> land, IEnumerable<string>? landMirrors,
                                 string? seaPath, Raster<ushort>? sea, IEnumerable<string>? seaMirrors,
                                 string? treePath, Raster<byte>? treeMap, TiffMap.Palette? treePalette)
    {
        Journal = journal;
        WorldW = worldW;
        WorldH = worldH;
        Land = land;
        _files[KitTarget.Land] = (landPath, MirrorsOf(land, landMirrors));
        if (seaPath is not null && sea is not null)
        {
            Sea = sea;
            _files[KitTarget.Sea] = (seaPath, MirrorsOf(sea, seaMirrors));
        }
        if (treePath is not null && treeMap is not null && treePalette is not null)
        {
            TreeMap = treeMap;
            TreePalette = treePalette;
            Grid = HexGrid.ForTreeMap(treeMap.Width, treeMap.Height, (float)worldW);
            _files[KitTarget.Trees] = (treePath, []);
        }
        foreach (var (path, mirrors) in _files.Values)
            foreach (var p in mirrors.Prepend(path)) Stamp(p);
    }

    /// <summary>Extra copies kept in step with a source (lf_heights.tif / lf_sea_heights.tif), when they exist at the same size.</summary>
    private static List<string> MirrorsOf<T>(Raster<T> raster, IEnumerable<string>? mirrors) where T : unmanaged =>
        (mirrors ?? []).Where(File.Exists).Where(m =>
        {
            try { return TiffMap.ReadSize(m) == (raster.Width, raster.Height); }
            catch (Exception) { return false; }
        }).ToList();

    private void Stamp(string path)
    {
        var info = new FileInfo(path);
        _stamps[path] = info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
    }

    public bool Has(KitTarget target) => _files.ContainsKey(target);
    public string? PathOf(KitTarget target) => _files.TryGetValue(target, out var f) ? f.Path : null;
    public IReadOnlyList<string> MirrorsOf(KitTarget target) => _files.TryGetValue(target, out var f) ? f.Mirrors : [];

    public Raster<ushort>? HeightRaster(KitTarget target) => target switch { KitTarget.Land => Land, KitTarget.Sea => Sea, _ => null };
    public WorldRaster Frame(KitTarget target) => target switch
    {
        KitTarget.Sea when Sea is not null => new(Sea.Width, Sea.Height, WorldW, WorldH),
        KitTarget.Trees when TreeMap is not null => new(TreeMap.Width, TreeMap.Height, WorldW, WorldH),
        _ => new(Land.Width, Land.Height, WorldW, WorldH),
    };

    /// <summary>Raw value of a height target at a world point (nearest pixel), null outside.</summary>
    public ushort? RawAt(KitTarget target, double x, double z)
    {
        if (HeightRaster(target) is not { } r) return null;
        var (px, py) = Frame(target).ToPixel(x, z);
        int ix = (int)Math.Round(px), iy = (int)Math.Round(py);
        return r.Contains(ix, iy) ? r[ix, iy] : null;
    }

    // ---------------------------------------------------------------- dirty / undo

    public bool CanUndo => _undo.Count > 0 && _heightStroke is null && _treeStroke is null;
    public bool CanRedo => _redo.Count > 0 && _heightStroke is null && _treeStroke is null;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Change.Description : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Change.Description : null;

    private int TopId(KitTarget target)
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
            if (_undo[i].Target == target) return _undo[i].Id;
        return 0;
    }

    /// <summary>The in-memory raster differs from what was loaded or last saved.</summary>
    public bool IsDirty(KitTarget target) => TopId(target) != _savedId[target];
    public bool IsDirty() => IsDirty(KitTarget.Land) || IsDirty(KitTarget.Sea) || IsDirty(KitTarget.Trees);

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
        var e = new Edit(_nextId++, target, change, rect, hexes.ToArray());
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
        if (!Has(target)) throw new InvalidOperationException($"no {target} source in this project");
        _strokeTarget = target;
        _strokeRect = PixelRect.Empty;
        _strokeHexes = [];
        _last = null;
        _treeErase = erase;
        if (target == KitTarget.Trees)
            _treeStroke = new RasterStroke<byte>(TreeMap!, erase ? "Erase trees" : "Paint trees");
        else
        {
            var raster = HeightRaster(target)!;
            var (px, py) = Frame(target).ToPixel(x, z);
            _heightStroke = HeightBrush;
            HeightBrush.Radius = Math.Max(0.5, RadiusWorld * Frame(target).PixelsPerWorld);
            HeightBrush.Begin(raster, px, py);
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
                rect = rect.Union(_heightStroke!.Dab(px, py));
            }
        }
        _last = (x, z);
        if (_heightStroke is not null && !rect.IsEmpty) hexes.UnionWith(HexesInRect(_strokeTarget, rect));
        if (rect.IsEmpty && hexes.Count == 0) return null;
        _strokeRect = _strokeRect.Union(rect);
        _strokeHexes.UnionWith(hexes);
        var info = new ChangeInfo(_strokeTarget, rect, hexes);
        Changed?.Invoke(info);
        return info;
    }

    /// <summary>Ends the stroke; returns its undo step (null when nothing changed).</summary>
    public Edit? EndStroke()
    {
        IUndoable? change = null;
        if (_heightStroke is not null)
        {
            change = _heightStroke.End();
            _heightStroke = null;
        }
        else if (_treeStroke is not null)
        {
            var s = _treeStroke;
            _treeStroke = null;
            if (!s.IsEmpty) { s.Finish(); change = s; }
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

    /// <summary>Palette index of a hex: the tree-map pixel BOB samples for it.</summary>
    public byte HexIndex(int col, int row)
    {
        var map = TreeMap!;
        var (x, y) = HexGrid.SamplePixel(col, row, map.Height);
        return map.Contains(x, y) ? map[x, y] : NoTreeIndex;
    }

    /// <summary>Sets a hex's 2×2 footprint (as <see cref="CampaignTreeGenerator.WriteTreeMap"/> paints it); returns its pixel rect,
    /// empty when nothing changed.</summary>
    private PixelRect SetHex(int col, int row, byte index, RasterStroke<byte>? stroke)
    {
        var map = TreeMap!;
        var (x, y) = HexGrid.SamplePixel(col, row, map.Height);
        var rect = new PixelRect(Math.Max(0, x), Math.Max(0, y - 1), Math.Min(map.Width - 1, x + 1), Math.Min(map.Height - 1, y));
        if (rect.IsEmpty) return PixelRect.Empty;
        var same = true;
        for (var py = rect.Y0; py <= rect.Y1 && same; py++)
            for (var px = rect.X0; px <= rect.X1; px++)
                if (map[px, py] != index) { same = false; break; }
        if (same) return PixelRect.Empty;
        stroke?.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);
        for (var py = rect.Y0; py <= rect.Y1; py++)
            for (var px = rect.X0; px <= rect.X1; px++)
                map[px, py] = index;
        return rect;
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
            var changed = SetHex(c, r, index, _treeStroke);
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
    /// Flood fill from the hex under a world point with <see cref="TreeIndex"/> (or no tree when erasing). Connected: every
    /// hex reachable through hexes of the start hex's current index. Region: every hex of the same region key
    /// (<paramref name="regionAt"/>) reachable through that region. <paramref name="maxRadius"/> (world, 0 = none) bounds it.
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
        var stroke = new RasterStroke<byte>(TreeMap, erase ? "Erase fill" : "Fill trees");
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
                var changed = SetHex(c, r, index, stroke);
                if (!changed.IsEmpty) { rect = rect.Union(changed); hexes.Add(HexId(c, r)); }
            }
            foreach (var (nc, nr) in Neighbours(c, r))
                if (seen.Add(HexId(nc, nr)) && Inside(nc, nr)) queue.Enqueue((nc, nr));
        }
        if (stroke.IsEmpty) return null;
        stroke.Finish();
        var e = Push(KitTarget.Trees, stroke, rect, hexes);
        Changed?.Invoke(new ChangeInfo(KitTarget.Trees, rect, hexes));
        return e;
    }

    /// <summary>Hex count per palette index (sampled pixels), 256 entries.</summary>
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

    /// <summary>Files of the dirty targets that changed on disk since they were loaded or saved (another tool).</summary>
    public List<string> ChangedOnDisk() =>
        _files.Where(kv => IsDirty(kv.Key)).SelectMany(kv => kv.Value.Mirrors.Prepend(kv.Value.Path))
            .Where(p =>
            {
                var info = new FileInfo(p);
                return _stamps.TryGetValue(p, out var s) && (info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default)) != s;
            }).ToList();

    /// <summary>
    /// Writes every dirty target (and its mirrors) through the journal, in the files' own format. Returns the files
    /// written and the journal seq (0 when nothing was dirty).
    /// </summary>
    public (IReadOnlyList<string> Files, int Seq) Save(string label, bool force = false)
    {
        if (Stroking) EndStroke();
        var dirty = _files.Keys.Where(IsDirty).ToList();
        if (dirty.Count == 0) return ([], 0);
        if (!force && ChangedOnDisk() is { Count: > 0 } changed)
            throw new InvalidOperationException($"changed on disk since loaded: {string.Join(", ", changed.Select(Path.GetFileName))}; reload, or save anyway");
        var writes = new List<(string, Action<string>)>();
        foreach (var t in dirty)
        {
            var (path, mirrors) = _files[t];
            foreach (var p in mirrors.Prepend(path))
                writes.Add((p, t == KitTarget.Trees
                    ? f => TiffMap.SavePalette8Like(f, TreeMap!, TreePalette!)
                    : f => TiffMap.SaveGray16Like(f, HeightRaster(t)!)));
        }
        var seq = Journal.Commit(writes, label);
        foreach (var (p, _) in writes) Stamp(p);
        foreach (var t in dirty) _savedId[t] = TopId(t);
        return (writes.Select(w => w.Item1).ToList(), seq);
    }
}
