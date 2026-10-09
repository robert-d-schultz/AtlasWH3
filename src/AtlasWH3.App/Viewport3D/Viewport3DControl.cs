using System.Collections.Concurrent;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtlasWH3.App.Scene;
using AtlasWH3.Core.Assets;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Viewport3D;

/// <summary>
/// Terry-style 3D viewport of a <see cref="SceneModel"/>: campaign terrain (or a grid), every entity's model
/// (instanced, LOD by distance, frustum culled, textured), markers for entities without models, outlines for shapes,
/// selection tint and boxes, and W/E/R move / rotate / scale gizmos. Input: left click picks (Ctrl adds, Shift toggles),
/// left drag on empty ground box-selects, right drag orbits (+ WASD/QE flies), middle drag pans, wheel zooms, F frames
/// the selection, Home frames everything. Edits are raised as <see cref="TransformsCommitted"/>; the window turns
/// them into entity ops.
/// </summary>
public sealed class Viewport3DControl : Grid
{
    public enum GizmoMode { Move, Rotate, Scale }

    private sealed class GpuModel
    {
        /// <summary>This frame's instances per LOD (cleared lazily when <see cref="Frame"/> is stale).</summary>
        public List<InstanceData>[] Frame0 { get; set; } = [];
        public int Frame { get; set; } = -1;
        public float Lift => (Bounds[1] + Bounds[4]) / 2;
        public float Radius => new Vector3(Bounds[3] - Bounds[0], Bounds[4] - Bounds[1], Bounds[5] - Bounds[2]).Length() / 2;
        public List<List<GpuMesh>> Lods { get; } = [];
        public IReadOnlyList<float> Distances { get; init; } = [];
        public float[] Bounds { get; init; } = new float[6];
        public RenderModel? Cpu { get; init; }
        /// <summary>Largest per-mesh terrain offset (LF-offset mountains: drawn draped on the ground).</summary>
        public float TerrainOffset { get; init; }
    }

    private static bool IsWater(RenderMesh m) => m.Shader.Contains("campaign_water", StringComparison.OrdinalIgnoreCase);

    /// <summary>How far the GPU lifts an item's model at its origin (LF-offset mountains: offset × ground height).</summary>
    private float DrapeLift(GpuModel? gm, Vector3 at) =>
        gm is { TerrainOffset: > 0 } g && _model?.Terrain is not null ? g.TerrainOffset * (float)_model.GroundY(at.X, at.Z) : 0;

    private sealed record Item(string Id, string Type, string? ModelPath, TerryTransform Transform, uint Colour, bool Frozen,
                               List<(Vector3 A, Vector3 B)> Outline)
    {
        /// <summary>Prefab instances: the entities inside (prefab space), drawn through the instance transform.</summary>
        public IReadOnlyList<SceneModel.PrefabPart> Parts { get; init; } = [];
        /// <summary>Prefab instances: bounds of everything inside, in prefab space (min xyz, max xyz).</summary>
        public float[]? PartBounds { get; init; }

        // Per-item draw cache (valid while the item is not being dragged; items are rebuilt on every scene change).
        public GpuModel? Gpu;
        public InstanceData Instance;
        public Vector3 Centre;
        public float Radius;
        public float Scale;
    }

    private readonly D3DHost _host = new();
    private Renderer3D? _r;
    private readonly Camera3D _cam = new();
    private SceneModel? _model;
    private int _builtVersion = -1;
    private List<Item> _items = [];
    private readonly ConcurrentDictionary<string, GpuModel?> _gpuModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GpuTexture?> _gpuTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadSlots = new(6);
    private (Vortice.Direct3D11.ID3D11Buffer V, Vortice.Direct3D11.ID3D11Buffer I, int Count)? _terrain;
    private bool _terrainRequested;
    private TerrainMaterial? _ground;
    private (Vortice.Direct3D11.ID3D11Buffer V, Vortice.Direct3D11.ID3D11Buffer I, int Count)? _water;
    private readonly Dictionary<string, string?> _treeModels = new(StringComparer.OrdinalIgnoreCase);
    private (Vortice.Direct3D11.ID3D11Buffer V, Vortice.Direct3D11.ID3D11Buffer I, int Count)? _rivers;
    private (Vortice.Direct3D11.ID3D11Buffer V, Vortice.Direct3D11.ID3D11Buffer I, int Count)? _tileMesh;
    private SceneModel.TileOverlay _tileMeshMode = SceneModel.TileOverlay.Off;
    private bool _tilesBuilding;
    private int _riversVersion = -1;
    private bool _riversBuilding;

    public bool ShowTrees { get; set; } = true;
    /// <summary>Fly speed multiplier (mouse wheel while holding the right button).</summary>
    public float FlySpeed { get; set; } = 1;
    public bool ShowWater { get; set; } = true;
    public bool ShowTileMeshes { get; set; } = true;

    /// <summary>True once the terrain mesh (and its ground textures, when the project has a blend map) are on the GPU.</summary>
    public bool TerrainReady => _model?.Terrain is null || (_terrain is not null && (_model.TerrainBlend is null || _ground is not null)
                                                            && (_model.SeaHeight is null || _water is not null));
    private bool _dirty = true;

    // input
    private Point? _orbitFrom, _panFrom, _boxFrom;
    private Point _mouse;
    private readonly HashSet<Key> _keysDown = [];
    private DateTime _lastFrame = DateTime.Now;

    // gizmo
    private string? _gizmoPart;                       // "x", "y", "z", "xz", "ring", "scale"
    private Vector3 _gizmoPivot;
    private Vector3 _dragStartHit;
    private float _dragStartParam;
    private Point _dragStartMouse;
    private Dictionary<string, TerryTransform> _dragStart = [];
    private Dictionary<string, TerryTransform> _overrides = [];

    public GizmoMode Mode { get; set; } = GizmoMode.Move;
    public IReadOnlySet<string> Selection { get; set; } = new HashSet<string>();
    public Camera3D Camera => _cam;
    public string? AdapterName => _r?.AdapterName;

    public event Action<IReadOnlyList<string>, SceneView.SelectMode>? SelectionRequested;
    public event Action<IReadOnlyDictionary<string, TerryTransform>, string>? TransformsCommitted;
    public event Action<Key, ModifierKeys>? KeyCommand;
    public event Action<string>? HoverText;

    /// <summary>Placement mode (scene editor Props tab): a left click calls this with the ground point under the mouse
    /// instead of selecting; null = normal selection.</summary>
    public Action<Vector3>? PlaceAt { get; set; }

    public Viewport3DControl()
    {
        Children.Add(_host);
        _host.Created += (hwnd, w, h) =>
        {
            try
            {
                _r = new Renderer3D();
                _r.AttachWindow(hwnd, w, h);
                Invalidate();
            }
            catch (Exception ex) { HoverText?.Invoke("3D unavailable: " + ex.Message); }
        };
        _host.Resized += (w, h) => { _r?.Resize(w, h); Invalidate(); };
        _host.MouseDownAt += OnHostMouseDown;
        _host.MouseUpAt += OnHostMouseUp;
        _host.MouseMoveAt += OnHostMouseMove;
        _host.Wheel += (delta, x, y) =>
        {
            if (_orbitFrom is not null)
            {
                // While flying (right button held) the wheel sets the fly speed, as in Terry / Unreal.
                FlySpeed = Math.Clamp(FlySpeed * (delta > 0 ? 1.25f : 0.8f), 0.05f, 50f);
                HoverText?.Invoke($"fly speed ×{FlySpeed:0.##}");
                return;
            }
            ZoomAt(new Point(x, y), delta > 0);
            Invalidate();
        };
        _host.KeyChanged += KeyChanged;
        CompositionTarget.Rendering += (_, _) => Tick();
        Unloaded += (_, _) => { };
    }

    public void Attach(SceneModel model)
    {
        _model = model;
        _builtVersion = -1;
        _terrain = null;
        _ground = null;
        _water = null;
        _treeModels.Clear();
        _tileMeshCache = null;
        _tileRivers = null;
        _tileGround = null;
        _terrainRequested = false;
        FrameAll();
        Invalidate();
    }

    public void Invalidate() => _dirty = true;

    /// <summary>Rebuilds items on the next frame (scene or model bounds changed).</summary>
    public void Refresh()
    {
        _treeModels.Clear();
        _treeCache = null;
        // Rebuild now, not on the next frame: a gizmo drag right after an edit must start from the new transforms.
        if (_model is not null) Rebuild();
        else _builtVersion = -1;
        Invalidate();
    }

    // ---------------------------------------------------------------- scene

    private void Rebuild()
    {
        if (_model is null) return;
        _builtVersion = _model.Version;
        var items = new List<Item>();
        foreach (var it in _model.All)
        {
            var e = it.Entity;
            if (TerryEntityTypes.IsLayerType(e.Type) || e.Transform is not var (p, r, s) || !_model.IsDrawn(it)) continue;
            var t = new TerryTransform(p, r, s);
            var outline = new List<(Vector3, Vector3)>();
            foreach (var o in e.Outlines)
            {
                var pts = o.Points.Select(pt => V(t.Apply(pt.X, 0, pt.Z))).ToList();
                if (o.Closed && pts.Count > 2) pts.Add(pts[0]);
                for (var i = 1; i < pts.Count; i++) outline.Add((pts[i - 1], pts[i]));
            }
            IReadOnlyList<SceneModel.PrefabPart> parts = [];
            float[]? partBounds = null;
            if (e.Component("ECCompositeScene")?["path"] is { Length: > 0 } csc)
            {
                var empty = new TerryEntityData("", null, "CompositeScene", [], [], null);
                var sc = (double)_model.CompositeScale(csc);
                var local = new TerryTransform([0, 0, 0], [0, 0, 0], [sc, sc, sc]);
                parts = _model.CompositeModels(csc).Select(m => new SceneModel.PrefabPart("CompositeScene", m, local, empty)).ToList();
                partBounds = PartsBounds(parts);
            }
            else if (e.Component("ECPrefab")?["key"] is { Length: > 0 } key)
            {
                parts = _model.PartsOf(key);
                partBounds = PartsBounds(parts);
                // Shapes inside the prefab (no-go zones, capture circles, ...) as outlines too.
                foreach (var part in parts)
                    foreach (var o in part.Entity.Outlines)
                    {
                        var placed = t.Compose(part.Local);
                        var pts = o.Points.Select(pt => V(placed.Apply(pt.X, 0, pt.Z))).ToList();
                        if (o.Closed && pts.Count > 2) pts.Add(pts[0]);
                        for (var i = 1; i < pts.Count; i++) outline.Add((pts[i - 1], pts[i]));
                    }
            }
            items.Add(new Item(e.Id, e.Type, SceneModel.ModelPathOf(e), t, SceneView.ColourOf(e.Type), _model.IsFrozen(it), outline)
            {
                Parts = parts, PartBounds = partBounds,
            });
        }
        _items = items;
    }

    /// <summary>Prefab-space box around every part (its model box placed by the part transform, or a small box).</summary>
    private float[]? PartsBounds(IReadOnlyList<SceneModel.PrefabPart> parts)
    {
        if (parts.Count == 0) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in parts)
        {
            var b = part.ModelPath is not null && _model!.ModelBounds.TryGetValue(part.ModelPath, out var mb) ? mb : [-0.3f, -0.3f, -0.3f, 0.3f, 0.3f, 0.3f];
            for (var k = 0; k < 8; k++)
            {
                var c = V(part.Local.Apply((k & 1) == 0 ? b[0] : b[3], (k & 2) == 0 ? b[1] : b[4], (k & 4) == 0 ? b[2] : b[5]));
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }
        }
        return [min.X, min.Y, min.Z, max.X, max.Y, max.Z];
    }

    private static Vector3 V((double X, double Y, double Z) p) => new((float)p.X, (float)p.Y, (float)p.Z);
    private static Vector3 V(double[] p) => new((float)p[0], (float)p[1], (float)p[2]);

    private TerryTransform TransformOf(Item item) => _overrides.TryGetValue(item.Id, out var o) ? o : item.Transform;

    /// <summary>Row-vector world matrix for a transform (world = local · M), from the build's matrix convention.</summary>
    private static Matrix4x4 World(TerryTransform t)
    {
        var m = t.Matrix(); // column-vector R·S, row-major
        return new Matrix4x4(
            (float)m[0], (float)m[3], (float)m[6], 0,
            (float)m[1], (float)m[4], (float)m[7], 0,
            (float)m[2], (float)m[5], (float)m[8], 0,
            (float)t.Position[0], (float)t.Position[1], (float)t.Position[2], 1);
    }

    public void FrameAll()
    {
        if (_model is null) return;
        if (_model.Terrain is var (_, w, h))
        {
            _cam.Target = new Vector3((float)w / 2, 0, (float)h / 2);
            _cam.Distance = (float)Math.Max(w, h) * 0.9f;
            _cam.Pitch = 1.0f;
            _cam.Yaw = 0;
        }
        else
        {
            var pts = _model.All.Select(i => i.Entity.Transform?.Position).Where(p => p is not null).Select(p => V(p!)).ToList();
            if (pts.Count > 0) _cam.Frame(pts.Aggregate(Vector3.Min) - Vector3.One, pts.Aggregate(Vector3.Max) + Vector3.One);
        }
        Invalidate();
    }

    public void FrameSelection()
    {
        var sel = _items.Where(i => Selection.Contains(i.Id)).ToList();
        if (sel.Count == 0) return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var i in sel)
            foreach (var c in Corners(i))
            {
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }
        _cam.Frame(min, max);
        Invalidate();
    }

    /// <summary>The item's model box corners in world space (a small box around markers).</summary>
    private IEnumerable<Vector3> Corners(Item i)
    {
        var t = TransformOf(i);
        var b = ModelBounds(i) ?? [-0.3f, -0.3f, -0.3f, 0.3f, 0.3f, 0.3f];
        var lift = new Vector3(0, DrapeLift(i.Gpu, V(t.Position)), 0);
        for (var k = 0; k < 8; k++)
            yield return V(t.Apply((k & 1) == 0 ? b[0] : b[3], (k & 2) == 0 ? b[1] : b[4], (k & 4) == 0 ? b[2] : b[5])) + lift;
    }

    private float[]? ModelBounds(Item i) =>
        i.PartBounds ?? (i.ModelPath is not null && _model!.ModelBounds.TryGetValue(i.ModelPath, out var b) ? b : null);

    /// <summary>How many entities a prefab instance draws (0 for anything else).</summary>
    public int PartCount(string id) => _items.FirstOrDefault(i => i.Id == id)?.Parts.Count ?? 0;

    // ---------------------------------------------------------------- loading

    private GpuModel? GpuModelFor(string path)
    {
        if (_gpuModels.TryGetValue(path, out var m)) return m;
        if (!_gpuModels.TryAdd(path, null)) return null;
        var lib = _model!.Models;
        var r = _r!;
        _ = Task.Run(async () =>
        {
            await _loadSlots.WaitAsync();
            try
            {
                var cpu = lib.Load(path);
                if (cpu is null) return;
                var gm = new GpuModel
                {
                    Distances = cpu.LodDistances, Bounds = cpu.Bounds, Cpu = cpu,
                    TerrainOffset = cpu.Lods.SelectMany(l => l).Select(m => m.TerrainOffset).DefaultIfEmpty(0).Max(),
                };
                foreach (var lod in cpu.Lods)
                    gm.Lods.Add(lod.Select(mesh => r.CreateMesh(mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices,
                        mesh.BaseColour is { } tex && !IsWater(mesh) ? TextureFor(lib, r, tex) : null, mesh.AlphaTest, mesh.TerrainOffset, IsWater(mesh))).ToList());
                _gpuModels[path] = gm;
                _ = Dispatcher.BeginInvoke(Invalidate);
            }
            catch (Exception) { /* drawn as a box */ }
            finally { _loadSlots.Release(); }
        });
        return null;
    }

    private GpuTexture? TextureFor(ModelLibrary lib, Renderer3D r, string path) => _gpuTextures.GetOrAdd(path, p =>
        lib.Texture(p, 512) is { } img ? r.CreateTexture(img.Width, img.Height, img.Rgba) : null);

    /// <summary>Re-reads the model's trees (after the tree map or the ground under them was edited).</summary>
    public void ReloadTrees()
    {
        _treeCache = null;
        Invalidate();
    }

    /// <summary>Re-meshes the terrain and water from the (edited) height rasters; the ground textures are kept and the
    /// current mesh stays on screen until the new one is ready.</summary>
    public void ReloadTerrain()
    {
        if (_terrain is null) return;   // first build still running (it reads the current rasters anyway)
        if (_terrainRequested && _terrainReloading) { _terrainReloadAgain = true; return; }
        _terrainRequested = false;
        Invalidate();
    }
    private bool _terrainReloading, _terrainReloadAgain;

    private void EnsureTerrain()
    {
        if (_terrainRequested || _model?.Terrain is not var (raster, worldW, worldH) || _r is null) return;
        _terrainRequested = true;
        var r = _r;
        if (_terrain is { } oldTerrain)
        {
            // Reload after an edit: same ground, new mesh and water.
            _terrainReloading = true;
            var keptGround = _ground;
            var oldWater = _water;
            Task.Run(() =>
            {
                var t = CreateTerrainMesh(r, raster, worldW, worldH);
                var water = _model.SeaHeight is { } sea ? CreateWater(r, raster, sea, (float)worldW, (float)worldH) : null;
                Dispatcher.BeginInvoke(() =>
                {
                    _terrain = t;
                    _water = water;
                    _ground = keptGround;
                    oldTerrain.V.Dispose(); oldTerrain.I.Dispose();
                    if (oldWater is { } ow) { ow.V.Dispose(); ow.I.Dispose(); }
                    UploadTerrainHeight(r, raster, worldW, worldH);
                    _terrainReloading = false;
                    if (_terrainReloadAgain) { _terrainReloadAgain = false; _terrainRequested = false; }
                    Invalidate();
                });
            });
            return;
        }
        Task.Run(() =>
        {
            var t = CreateTerrainMesh(r, raster, worldW, worldH);
            UploadTerrainHeight(r, raster, worldW, worldH);
            var ground = _model.TerrainBlend is var (groups, arrays) ? CreateGround(r, groups, arrays, (float)worldW, (float)worldH) : null;
            var water = _model.SeaHeight is { } sea ? CreateWater(r, raster, sea, (float)worldW, (float)worldH) : null;
            Dispatcher.BeginInvoke(() => { _terrain = t; _ground = ground; _water = water; Invalidate(); });
        });
    }

    private static (Vortice.Direct3D11.ID3D11Buffer, Vortice.Direct3D11.ID3D11Buffer, int) CreateTerrainMesh(Renderer3D r,
        AtlasWH3.Formats.Maps.Raster<ushort> raster, double worldW, double worldH)
    {
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(raster.Width, raster.Height) / 2048.0));
        int cols = (raster.Width - 1) / step + 1, rows = (raster.Height - 1) / step + 1;
        double px = worldW / raster.Width, pz = worldH / raster.Height;
        float H(int c, int rr) => (float)(raster[Math.Clamp(c, 0, raster.Width - 1), Math.Clamp(rr, 0, raster.Height - 1)]
                                          * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset);
        var v = new float[cols * rows * 6];
        Parallel.For(0, rows, j =>
        {
            for (var i = 0; i < cols; i++)
            {
                int c = i * step, rr = j * step, o = (j * cols + i) * 6;
                v[o] = (float)((c + 0.5) * px);
                v[o + 1] = H(c, rr);
                v[o + 2] = (float)(worldH - (rr + 0.5) * pz);
                var dx = (H(c + step, rr) - H(c - step, rr)) / (float)(2 * step * px);
                var dz = (H(c, rr - step) - H(c, rr + step)) / (float)(2 * step * pz);
                var n = Vector3.Normalize(new Vector3(-dx, 1, -dz));
                (v[o + 3], v[o + 4], v[o + 5]) = (n.X, n.Y, n.Z);
            }
        });
        var idx = new uint[(cols - 1) * (rows - 1) * 6];
        Parallel.For(0, rows - 1, j =>
        {
            for (var i = 0; i < cols - 1; i++)
            {
                var o = (j * (cols - 1) + i) * 6;
                uint a = (uint)(j * cols + i), b = a + 1, c = a + (uint)cols, d = c + 1;
                (idx[o], idx[o + 1], idx[o + 2], idx[o + 3], idx[o + 4], idx[o + 5]) = (a, b, c, b, d, c);
            }
        });
        return r.CreateTerrain(v, idx);
    }

    /// <summary>Heights for the LF-offset mountain shader (at most 4096 px across).</summary>
    private static void UploadTerrainHeight(Renderer3D r, AtlasWH3.Formats.Maps.Raster<ushort> raster, double worldW, double worldH)
    {
        var hs = Math.Max(1, (int)Math.Ceiling(Math.Max(raster.Width, raster.Height) / 4096.0));
        int hw = raster.Width / hs, hh = raster.Height / hs;
        var raw = new ushort[hw * hh];
        Parallel.For(0, hh, y => { for (var x = 0; x < hw; x++) raw[y * hw + x] = raster[x * hs, y * hs]; });
        r.SetTerrainHeight(raw, hw, hh, (float)worldW, (float)worldH, (float)AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep, (float)AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset);
    }

    /// <summary>
    /// The campaign ground: every texture group's base colour at 512 px (resampled when the source is another size)
    /// and the group raster; groups whose texture is missing get the 2D renderer's flat colour. Textures repeat every
    /// 48 lf pixels, as in the 2D view.
    /// </summary>
    private TerrainMaterial? CreateGround(Renderer3D r, AtlasWH3.Formats.Maps.Raster<byte> groups, AtlasWH3.Formats.Maps.TextureArrays arrays,
                                          float worldW, float worldH)
    {
        const int size = 512;
        var lib = _model!.Models;
        var layers = new byte[]?[arrays.Groups.Count];
        Parallel.For(0, arrays.Groups.Count, i =>
        {
            var g = arrays.Groups[i];
            if (string.IsNullOrEmpty(g.BaseColour) || lib.Texture(g.BaseColour, size) is not { } img) return;
            if (img.Width == size && img.Height == size) { layers[i] = img.Rgba; return; }
            var data = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var sx = x * img.Width / size;
                    var sy = y * img.Height / size;
                    Array.Copy(img.Rgba, (sy * img.Width + sx) * 4, data, (y * size + x) * 4, 4);
                }
            layers[i] = data;
        });
        var fallback = Enumerable.Range(0, Math.Max(1, arrays.Groups.Count)).Select(i => AtlasWH3.Core.Rendering.TerrainRenderer.FallbackColour(i)).ToList();
        var repeat = 48 * worldW / groups.Width;
        return r.CreateTerrainMaterial(groups.Data, groups.Width, groups.Height, layers, fallback, new Vector2(worldW, worldH), repeat);
    }

    private GpuTexture? _regionTexture;
    private bool _regionBuilding;

    /// <summary>The region mask on the ground while <see cref="SceneModel.ShowRegions"/> is on (built in the
    /// background on first use).</summary>
    private void EnsureRegionOverlay()
    {
        if (_model is null || _r is null) return;
        if (!_model.ShowRegions) { _r.SetOverlay(null); return; }
        if (_regionTexture is not null) { _r.SetOverlay(_regionTexture); return; }
        if (_regionBuilding) return;
        _regionBuilding = true;
        var model = _model;
        var r = _r;
        _ = Task.Run(() =>
        {
            GpuTexture? tex = null;
            try { if (model.Regions is { } map) tex = r.CreateTexture(map.Width, map.Height, map.Rgba); } catch (Exception) { }
            Dispatcher.BeginInvoke(() => { _regionTexture = tex; _regionBuilding = false; RegionsChanged?.Invoke(); Invalidate(); });
        });
    }

    /// <summary>The region mask finished building (the 2D view redraws with it).</summary>
    public event Action? RegionsChanged;
    public bool RegionsReady => _model is null || !_model.ShowRegions || (!_regionBuilding && _regionTexture is not null);

    private (GpuTexture Texture, Dictionary<string, (float U0, float V0, float U1, float V1, int W, int H)> Rects)? _labelAtlas;
    private SceneModel.RegionMap? _labelAtlasFor;

    /// <summary>
    /// Every city's region name drawn once with WPF (white on a translucent dark box) into one texture: the 3D view
    /// is a native D3D window, so WPF can't draw text over it; the labels are screen-space sprites instead.
    /// </summary>
    private void EnsureLabelAtlas(SceneModel.RegionMap map)
    {
        if (_r is null || ReferenceEquals(_labelAtlasFor, map)) return;
        _labelAtlasFor = map;
        var typeface = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        const int atlasW = 1024, pad = 2;
        var placed = new List<(string Region, FormattedText Text, int X, int Y, int W, int H)>();
        int x = 0, y = 0, rowH = 0;
        foreach (var c in map.Cities)
        {
            var text = new FormattedText(SceneModel.RegionLabel(c.Region), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 13, System.Windows.Media.Brushes.White, 1.0);
            int w = (int)Math.Ceiling(text.Width) + 8, h = (int)Math.Ceiling(text.Height) + 4;
            if (x + w > atlasW) { x = 0; y += rowH + pad; rowH = 0; }
            placed.Add((c.Region, text, x, y, w, h));
            x += w + pad;
            rowH = Math.Max(rowH, h);
        }
        var atlasH = Math.Max(1, y + rowH);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var box = new SolidColorBrush(System.Windows.Media.Color.FromArgb(170, 10, 10, 12));
            foreach (var p in placed)
            {
                dc.DrawRectangle(box, null, new Rect(p.X, p.Y, p.W, p.H));
                dc.DrawText(p.Text, new Point(p.X + 4, p.Y + 2));
            }
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(atlasW, atlasH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new byte[atlasW * atlasH * 4];
        bitmap.CopyPixels(bgra, atlasW * 4, 0);
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            // Premultiplied BGRA → straight RGBA (the renderer blends non-premultiplied).
            rgba[i] = (byte)(a == 0 ? 0 : Math.Min(255, bgra[i + 2] * 255 / a));
            rgba[i + 1] = (byte)(a == 0 ? 0 : Math.Min(255, bgra[i + 1] * 255 / a));
            rgba[i + 2] = (byte)(a == 0 ? 0 : Math.Min(255, bgra[i] * 255 / a));
            rgba[i + 3] = (byte)a;
        }
        var rects = placed.ToDictionary(p => p.Region,
            p => ((float)p.X / atlasW, (float)p.Y / atlasH, (float)(p.X + p.W) / atlasW, (float)(p.Y + p.H) / atlasH, p.W, p.H));
        _labelAtlas = (_r.CreateTexture(atlasW, atlasH, rgba), rects);
    }

    /// <summary>
    /// Region names beside the city markers, nearest first (at most 150, skipping ones that would overlap a nearer
    /// label), in render-target pixels.
    /// </summary>
    private void DrawCityLabels(Renderer3D r, int w, int h)
    {
        if (_model is not { ShowRegions: true, RegionsIfBuilt: { } map }) return;
        EnsureLabelAtlas(map);
        if (_labelAtlas is not var (texture, rects)) return;
        var eye = _cam.Eye;
        var candidates = new List<(float D, float X, float Y, (float U0, float V0, float U1, float V1, int W, int H) R)>();
        foreach (var c in map.Cities)
        {
            if (!rects.TryGetValue(c.Region, out var rect)) continue;
            var g = new Vector3((float)c.X, (float)_model.GroundY(c.X, c.Z), (float)c.Z);
            var d = Vector3.Distance(eye, g);
            var top = g + Vector3.UnitY * Math.Clamp(d * 0.025f, 0.02f, 6f) * 2;
            if (_cam.ToScreen(top, w, h) is not { } s || s.X < -200 || s.Y < -50 || s.X > w + 50 || s.Y > h + 50) continue;
            candidates.Add((d, (float)s.X + 6, (float)s.Y - rect.H / 2f, rect));
        }
        var taken = new List<Rect>();
        var verts = new List<float>();
        foreach (var (_, x, y, rc) in candidates.OrderBy(c => c.D))
        {
            if (taken.Count >= 150) break;
            var box = new Rect(x, y, rc.W, rc.H);
            if (taken.Any(t => t.IntersectsWith(box))) continue;
            taken.Add(box);
            float X(double px) => (float)(px / w * 2 - 1);
            float Y(double py) => (float)(1 - py / h * 2);
            float x0 = X(x), x1 = X(x + rc.W), y0 = Y(y), y1 = Y(y + rc.H);
            verts.AddRange([x0, y0, rc.U0, rc.V0, x1, y0, rc.U1, rc.V0, x0, y1, rc.U0, rc.V1,
                            x1, y0, rc.U1, rc.V0, x1, y1, rc.U1, rc.V1, x0, y1, rc.U0, rc.V1]);
        }
        r.DrawSprites(texture, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts));
    }

    /// <summary>City markers: a pole from the ground with a cross on top, sized to stay readable.</summary>
    private void AddCityMarkers(List<LineVertex> lines, Frustum frustum, Vector3 eye)
    {
        if (_model is not { ShowRegions: true, RegionsIfBuilt: { } map }) return;
        var col = new Vector4(1f, 0.9f, 0.3f, 1);
        foreach (var c in map.Cities)
        {
            var g = new Vector3((float)c.X, (float)_model.GroundY(c.X, c.Z), (float)c.Z);
            var size = Math.Clamp(Vector3.Distance(eye, g) * 0.025f, 0.02f, 6f);
            if (!frustum.Sphere(g, size * 2)) continue;
            var top = g + Vector3.UnitY * size * 2;
            lines.Add(new(g, col)); lines.Add(new(top, col));
            lines.Add(new(top - Vector3.UnitX * size * 0.5f, col)); lines.Add(new(top + Vector3.UnitX * size * 0.5f, col));
            lines.Add(new(top - Vector3.UnitZ * size * 0.5f, col)); lines.Add(new(top + Vector3.UnitZ * size * 0.5f, col));
        }
    }


    /// <summary>
    /// The water surface from the LowFrequencyHeightSea map (half lf resolution, same height units): a grid over the
    /// sea raster (every 4th pixel) keeping the cells where the water is above the land at any corner, at the sea
    /// height — the sea, and lakes at their own levels.
    /// </summary>
    private static (Vortice.Direct3D11.ID3D11Buffer, Vortice.Direct3D11.ID3D11Buffer, int)? CreateWater(Renderer3D r,
        AtlasWH3.Formats.Maps.Raster<ushort> land, AtlasWH3.Formats.Maps.Raster<ushort> sea, float worldW, float worldH)
    {
        const int step = 4;
        int cols = (sea.Width - 1) / step + 1, rows = (sea.Height - 1) / step + 1;
        var colour = new Vector4(0.09f, 0.22f, 0.34f, 0.78f);
        var verts = new LineVertex[cols * rows];
        var wet = new bool[cols * rows];
        Parallel.For(0, rows, j =>
        {
            for (var i = 0; i < cols; i++)
            {
                int sx = Math.Min(i * step, sea.Width - 1), sy = Math.Min(j * step, sea.Height - 1);
                var s = sea[sx, sy];
                var l = land[Math.Min(sx * land.Width / sea.Width, land.Width - 1), Math.Min(sy * land.Height / sea.Height, land.Height - 1)];
                wet[j * cols + i] = s > l;
                var x = (sx + 0.5f) / sea.Width * worldW;
                var z = worldH - (sy + 0.5f) / sea.Height * worldH;
                verts[j * cols + i] = new LineVertex(new Vector3(x, (float)(s * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset), z), colour);
            }
        });
        var idx = new List<uint>();
        for (var j = 0; j < rows - 1; j++)
            for (var i = 0; i < cols - 1; i++)
            {
                uint a = (uint)(j * cols + i), b = a + 1, c = a + (uint)cols, d = c + 1;
                if (!(wet[a] || wet[b] || wet[c] || wet[d])) continue;
                idx.AddRange([a, b, c, b, d, c]);
            }
        return idx.Count == 0 ? null : r.CreateColoured(verts, [.. idx]);
    }

    /// <summary>Trees as ready instances: world rows (yaw only, no scale) per tree, grouped by tree id.</summary>
    private Dictionary<string, (InstanceData[] Instances, Vector3[] Positions)>? _treeCache;

    /// <summary>
    /// Forest trees are drawn at a tenth of model size: the campaign tree models are authored at real-world scale
    /// (like the fauna, whose .csc scenes carry 0.1), and the same castanopsis models placed by the mountain tiles'
    /// bmds come out at 10.7 × 4 × cell/128 ≈ 0.11. At 1 a canopy spans several tile-map cells and buries the hills.
    /// </summary>
    public const float TreeScale = 0.1f;

    private void AddTrees(Frustum frustum, Vector3 eye, List<GpuModel> batches)
    {
        _treeCache ??= _model!.Trees.GroupBy(t => t.TreeId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g =>
        {
            var inst = new InstanceData[g.Count()];
            var pos = new Vector3[inst.Length];
            var k = 0;
            foreach (var t in g)
            {
                var a = t.YawDegrees * MathF.PI / 180;
                float c = MathF.Cos(a), s = MathF.Sin(a);
                // Row-vector form of Blender's Ry (see World): rows (c, 0, -s), (0, 1, 0), (s, 0, c), translation.
                inst[k] = new InstanceData
                {
                    Row0 = new Vector4(c, 0, -s, 0) * TreeScale, Row1 = new Vector4(0, TreeScale, 0, 0), Row2 = new Vector4(s, 0, c, 0) * TreeScale,
                    Row3 = new Vector4(t.X, t.Y, t.Z, 1),
                };
                pos[k++] = new Vector3(t.X, t.Y, t.Z);
            }
            return (inst, pos);
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var (treeId, (instances, positions)) in _treeCache)
        {
            if (TreeModelFor(treeId) is not { } path || GpuModelFor(path) is not { } gm) continue;
            var lift = new Vector3(0, gm.Lift * TreeScale, 0);
            var radius = gm.Radius * TreeScale;
            for (var i = 0; i < instances.Length; i++)
            {
                var centre = positions[i] + lift;
                if (!frustum.Sphere(centre, radius)) continue;
                Queue(gm, Lod(gm, Vector3.Distance(eye, centre) / TreeScale, 1), instances[i], batches);
            }
        }
    }

    /// <summary>Placed tiles' meshes and props as ready instances, per model (built once in the background).</summary>
    private Dictionary<string, (InstanceData[] Instances, Vector3[] Centres, float[] Scales)>? _tileMeshCache;
    private bool _tileMeshCacheBuilding;
    private (Vortice.Direct3D11.ID3D11Buffer V, Vortice.Direct3D11.ID3D11Buffer I, int Count)? _tileRivers;
    private TileGround? _tileGround;

    /// <summary>The tiles' ground textures on the GPU: one vertex/index buffer of lf-draped grids, drawn per tile
    /// folder (its blend0 and channel groups).</summary>
    private sealed record TileGround(Vortice.Direct3D11.ID3D11Buffer V, Vortice.Direct3D11.ID3D11Buffer I,
        List<(int Start, int Count, GpuTexture Blend, GpuTexture? Normal, (int R, int G, int B, int A) Layers)> Ranges, int Tiles);

    public int TileGroundTiles => _tileGround?.Tiles ?? 0;

    /// <summary>
    /// Every placed tile whose texture set names a ground group of its own (roads, verges, river beds, banks...):
    /// a grid over the tile (two quads per cell side, through the tile transform, on the lf) whose pixels mix the
    /// groups by the tile's blend0.dds (R, G, B, A = texture-set slots 0–3; image row 0 = the tile's south edge:
    /// sampled at the tiles' river-mesh vertices the river-bed weight is 124/255 that way, 77–92 for the other seven).
    /// The "climate" slots are the global ground, so they are left to the terrain below via alpha.
    /// </summary>
    private static TileGround? BuildTileGround(SceneModel model, Renderer3D r)
    {
        if (model.TerrainBlend is not var (_, arrays)) return null;
        var groupIndex = arrays.Groups.ToDictionary(g => g.Name, g => g.Index, StringComparer.OrdinalIgnoreCase);
        var (_, _, cx, cz) = model.TileGrid;
        float sx = (float)(cx / 128), sz = (float)(cz / 128);
        var perFolder = new Dictionary<string, (byte[]? Rgba, int W, int H, AtlasWH3.Formats.Dds.DdsTexture.Image? Normal, (int, int, int, int) Layers, List<float> V, List<uint> I)>(StringComparer.OrdinalIgnoreCase);
        var tiles = 0;
        foreach (var t in model.Tiles)
        {
            if (t.Layers is not { Count: > 0 } names) continue;
            int L(int k) => k < names.Count && names[k].Length > 0 && !names[k].Equals("climate", StringComparison.OrdinalIgnoreCase)
                && groupIndex.TryGetValue(names[k], out var g) ? g : -1;
            var layers = (L(0), L(1), L(2), L(3)); // all climate still draws: the tile's normal map shapes the ground
            var folder = t.Path.Replace('\\', '/').Trim('/');
            if (!perFolder.TryGetValue(folder, out var f))
            {
                byte[]? rgba = null; int w = 0, h = 0;
                AtlasWH3.Formats.Dds.DdsTexture.Image? normal = null;
                try
                {
                    if (model.Models.Source.TryRead(folder + "/blend0.dds") is { } dds)
                    {
                        var img = AtlasWH3.Formats.Dds.DdsTexture.DecodeMip(dds, 256);
                        (rgba, w, h) = (img.Rgba, img.Width, img.Height);
                        if (model.Models.Source.TryRead(folder + "/normal.dds") is { } nd) normal = AtlasWH3.Formats.Dds.DdsTexture.DecodeMip(nd, 256);
                    }
                }
                catch (Exception) { }
                perFolder[folder] = f = (rgba, w, h, normal, layers, [], []);
            }
            if (f.Rgba is null) continue;
            tiles++;
            var m = TileMatrix(t, sx, sz, 0);
            int na = t.TileW * 2, nb = t.TileH * 2;
            float tw = t.TileW * 128f, th = t.TileH * 128f;
            var b0 = (uint)(f.V.Count / 12);
            var d = (float)cx * 0.5f;
            // World xz directions of the tile's a and b axes (the turn), for its normal map.
            var axisA = Vector2.Normalize(new Vector2(m.M11, m.M13));
            var axisB = Vector2.Normalize(new Vector2(m.M31, m.M33));
            for (var j = 0; j <= nb; j++)
                for (var i = 0; i <= na; i++)
                {
                    float a = i * 64f, b = j * 64f;
                    var p = Vector3.Transform(new Vector3(a, 0, b), m);
                    var y = (float)model.GroundY(p.X, p.Z) + 0.004f;
                    var dx = (float)(model.GroundY(p.X + d, p.Z) - model.GroundY(p.X - d, p.Z)) / (2 * d);
                    var dz = (float)(model.GroundY(p.X, p.Z + d) - model.GroundY(p.X, p.Z - d)) / (2 * d);
                    var n = Vector3.Normalize(new Vector3(-dx, 1, -dz));
                    f.V.AddRange([p.X, y, p.Z, n.X, n.Y, n.Z, a / tw, b / th, axisA.X, axisA.Y, axisB.X, axisB.Y]);
                }
            for (var j = 0; j < nb; j++)
                for (var i = 0; i < na; i++)
                {
                    uint q = b0 + (uint)(j * (na + 1) + i), q1 = q + 1, q2 = q + (uint)(na + 1), q3 = q2 + 1;
                    f.I.AddRange([q, q1, q2, q1, q3, q2]);
                }
        }
        var verts = new List<float>();
        var idx = new List<uint>();
        var ranges = new List<(int, int, GpuTexture, GpuTexture?, (int, int, int, int))>();
        foreach (var (_, f) in perFolder)
        {
            if (f.Rgba is null || f.I.Count == 0) continue;
            var baseVertex = (uint)(verts.Count / 12);
            ranges.Add((idx.Count, f.I.Count, r.CreateTexture(f.W, f.H, f.Rgba),
                f.Normal is { } nm ? r.CreateTexture(nm.Width, nm.Height, nm.Rgba) : null, f.Layers));
            verts.AddRange(f.V);
            foreach (var i in f.I) idx.Add(baseVertex + i);
        }
        if (idx.Count == 0) return null;
        var (vb, ib, _) = r.CreateTerrain([.. verts], [.. idx]);
        return new TileGround(vb, ib, ranges, tiles);
    }

    /// <summary>
    /// The tiles' own content, drawn through the engine's tile transform: each placed tile's custom_mesh.wsmodel
    /// (the coast cliffs) and the props of its bmd_data.bin (mountain models, their trees, road-side props).
    /// <para>
    /// From warscape's TERRAIN_RENDER_SETUP::get_tile_transform: tile space is 128 units per tile-map cell, the
    /// tile's south-west corner at (X·128, Y·128), x east and z north; for the orientations 0x10/0x20/0x40/0x80
    /// (0°/90°/180°/270°) the tile point (a, b) lands at (a, b), (b, W−a), (W−a, H−b), (H−b, a) from that corner,
    /// W×H the unturned size. World x/z then scale by the cell size / 128, y by the x scale.
    /// </para>
    /// <para>
    /// The frames were checked against the data: bmd props are in 32 units per cell with b = z (94% land on the
    /// tile's valid subtiles, against 85% for the mirror); custom meshes span z −H..0, so b = z + H (the cliff
    /// tiles' water-splash VFX then sit 4–10 units from a cliff-foot vertex, 20–50 for the 180° turn). Heights: a
    /// cliff's top is the record's high lf height (high − low = the mesh depth). Props: y = 0 is the tile's own
    /// ground (its hf_height_map spans −2..0.5 bmd units at most, flat on mountain tiles), so each prop goes on the
    /// lf at its position plus its y — bridges on the banks, trees on their rocks.
    /// </para>
    /// </summary>
    private void AddTileMeshes(Frustum frustum, Vector3 eye, List<GpuModel> batches)
    {
        if (_tileMeshCache is null)
        {
            if (!_tileMeshCacheBuilding && _model is { Tiles.Count: > 0 } model)
            {
                _tileMeshCacheBuilding = true;
                _ = Task.Run(() =>
                {
                    Dictionary<string, (InstanceData[], Vector3[], float[])>? cache = null;
                    (Vortice.Direct3D11.ID3D11Buffer, Vortice.Direct3D11.ID3D11Buffer, int)? rivers = null;
                    TileGround? ground = null;
                    try
                    {
                        var (built, rv, ri) = BuildTileMeshCache(model);
                        cache = built;
                        if (ri.Length > 0 && _r is { } r) rivers = r.CreateColoured(rv, ri);
                        if (_r is { } r2) ground = BuildTileGround(model, r2);
                    }
                    catch (Exception) { cache ??= []; }
                    Dispatcher.BeginInvoke(() => { _tileMeshCache = cache; _tileRivers = rivers; _tileGround = ground; _tileMeshCacheBuilding = false; Invalidate(); });
                });
            }
            return;
        }
        foreach (var (path, (instances, centres, scales)) in _tileMeshCache)
        {
            if (GpuModelFor(path) is not { } gm) continue;
            for (var i = 0; i < instances.Length; i++)
            {
                if (!frustum.Sphere(centres[i], gm.Radius * scales[i])) continue;
                Queue(gm, Lod(gm, Vector3.Distance(eye, centres[i]), scales[i]), instances[i], batches);
            }
        }
    }

    /// <summary>The tile content cache is built (or the project has no tiles).</summary>
    public bool TileMeshesReady => _model is null || _model.Tiles.Count == 0 || _tileMeshCache is not null;
    public int TileMeshInstances => _tileMeshCache?.Values.Sum(v => v.Instances.Length) ?? 0;
    public int TileMeshModels => _tileMeshCache?.Count ?? 0;

    /// <summary>Row-vector matrix from tile space (128 units per cell, origin at the tile's south-west corner before
    /// the turn) to world, with <paramref name="baseY"/> as the world height of tile y = 0.</summary>
    private static Matrix4x4 TileMatrix(SceneModel.TilePlacement t, float sx, float sz, float baseY)
    {
        float w = t.TileW * 128f, h = t.TileH * 128f, px = t.X * 128f, pz = t.Y * 128f, sy = sx;
        var (r0, r2, tx, tz) = (t.Orientation & 0xF0) switch
        {
            0x20 => (new Vector3(0, 0, -sz), new Vector3(sx, 0, 0), sx * px, sz * (w + pz)),
            0x40 => (new Vector3(-sx, 0, 0), new Vector3(0, 0, -sz), sx * (w + px), sz * (h + pz)),
            0x80 => (new Vector3(0, 0, sz), new Vector3(-sx, 0, 0), sx * (h + px), sz * pz),
            _ => (new Vector3(sx, 0, 0), new Vector3(0, 0, sz), sx * px, sz * pz),
        };
        return new Matrix4x4(r0.X, r0.Y, r0.Z, 0, 0, sy, 0, 0, r2.X, r2.Y, r2.Z, 0, tx, baseY, tz, 1);
    }

    private static (Dictionary<string, (InstanceData[] Instances, Vector3[] Centres, float[] Scales)> Models, LineVertex[] RiverVertices, uint[] RiverIndices)
        BuildTileMeshCache(SceneModel model)
    {
        var riverMeshes = new Dictionary<string, (Vector3[] P, uint[] I)?>(StringComparer.OrdinalIgnoreCase);
        var riverVerts = new List<LineVertex>();
        var riverIdx = new List<uint>();
        var (_, _, cx, cz) = model.TileGrid;
        float sx = (float)(cx / 128), sz = (float)(cz / 128);
        static float Lf(float v) => (float)(v * 65535 * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset);
        var content = new Dictionary<string, (string? Mesh, string? River, List<(string Path, Matrix4x4 Local, bool OnTerrain, float Scale)> Props)>(StringComparer.OrdinalIgnoreCase);
        var water = new Vector4(0.13f, 0.30f, 0.43f, 0.85f);
        var result = new Dictionary<string, (List<InstanceData> I, List<Vector3> C, List<float> S)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, Matrix4x4 world, float scale, Vector4 tint = default, Vector3? centre = null)
        {
            if (!result.TryGetValue(path, out var lists)) result[path] = lists = ([], [], []);
            lists.I.Add(Rows(world, tint));
            lists.C.Add(centre ?? world.Translation);
            lists.S.Add(scale);
        }
        var drapes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        float DrapeOf(string path)
        {
            if (drapes.TryGetValue(path, out var v)) return v;
            try { v = model.Models.Load(path)?.Lods.SelectMany(l => l).Select(m => m.TerrainOffset).DefaultIfEmpty(0).Max() ?? 0; } catch (Exception) { v = 0; }
            return drapes[path] = v;
        }
        foreach (var t in model.Tiles)
        {
            var folder = t.Path.Replace('\\', '/').Trim('/');
            if (!content.TryGetValue(folder, out var c))
            {
                var mesh = folder + "/custom_mesh.wsmodel";
                var props = new List<(string, Matrix4x4, bool, float)>();
                try
                {
                    if (model.Models.Source.TryRead(folder + "/bmd_data.bin") is { } bmd)
                        foreach (var p in AtlasWH3.Formats.Props.GlobalProps.ReadBody(bmd).Props)
                        {
                            var tr = p.Transform;
                            var local = World(new TerryTransform([tr.X, tr.Y, tr.Z], [tr.RotX, tr.RotY, tr.RotZ], [tr.ScaleX, tr.ScaleY, tr.ScaleZ]))
                                        * Matrix4x4.CreateScale(4); // bmd units: 32 per cell
                            props.Add((p.Path.Replace('\\', '/'), local, p.HeightMode == "BHM_TERRAIN",
                                (float)Math.Max(tr.ScaleX, Math.Max(tr.ScaleY, tr.ScaleZ)) * 4));
                        }
                }
                catch (Exception) { /* a bmd variant the reader does not know: no props for this tile */ }
                // The river water of river, crossing, canal and mouth tiles (an empty 140-byte stub elsewhere).
                var river = folder + "/river_mesh.wsmodel.rigid_model_v2";
                if (!riverMeshes.ContainsKey(river))
                {
                    riverMeshes[river] = null;
                    try
                    {
                        if (model.Models.Source.TryRead(river) is { Length: > 200 } rb)
                        {
                            var pos = new List<Vector3>();
                            var idx = new List<uint>();
                            foreach (var m in AtlasWH3.Formats.Models.RigidModel.Read(rb).Lods[0].Meshes)
                            {
                                var b0 = (uint)pos.Count;
                                for (var k = 0; k < m.VertexCount; k++) pos.Add(new Vector3(m.Positions[k * 3], m.Positions[k * 3 + 1], m.Positions[k * 3 + 2]));
                                foreach (var i in m.Indices) idx.Add(b0 + i);
                            }
                            riverMeshes[river] = ([.. pos], [.. idx]);
                        }
                    }
                    catch (Exception) { }
                }
                content[folder] = c = (model.Models.Source.Exists(mesh) ? mesh : null, riverMeshes[river] is null ? null : river, props);
            }
            if (c.Mesh is not null)
            {
                // Custom meshes: z from −H to 0 (b = z + H), y = 0 at the record's high height.
                var world = Matrix4x4.CreateTranslation(0, 0, t.TileH * 128f) * TileMatrix(t, sx, sz, Lf(t.High));
                Add(c.Mesh, world, sx);
            }
            // River meshes: tile space as the land mesh (z 0..H), baked into one coloured mesh with every vertex
            // just above the lf ground (the lf has no carved channels to hold a flat per-tile water level).
            if (c.River is not null && riverMeshes[c.River] is var (rp, ri))
            {
                var m = TileMatrix(t, sx, sz, 0);
                var b0 = (uint)riverVerts.Count;
                foreach (var v in rp)
                {
                    var w = Vector3.Transform(v with { Y = 0 }, m);
                    riverVerts.Add(new LineVertex(w with { Y = (float)model.GroundY(w.X, w.Z) + 0.012f }, water));
                }
                foreach (var i in ri) riverIdx.Add(b0 + i);
            }
            if (c.Props.Count == 0) continue;
            var tile = TileMatrix(t, sx, sz, 0);
            foreach (var (path, local, _, scale) in c.Props)
            {
                // Heights are relative to the tile's own ground, whatever the height mode: the tile hf maps are
                // flat (mountains) or within −2..0.5 bmd units (river beds, road verges), ≤ 0.02 world units.
                // LF-offset models (the mountain rocks, BHM_CUSTOM_VERTEX_OFFSETTING) get the ground per vertex on
                // the GPU instead.
                var world = local * tile;
                var at = world.Translation;
                var ground = (float)model.GroundY(at.X, at.Z);
                var drape = DrapeOf(path);
                world.M42 += ground * (1 - drape);
                Add(path, world, scale * sx, default, at with { Y = at.Y + ground });
            }
        }
        return (result.ToDictionary(kv => kv.Key, kv => (kv.Value.I.ToArray(), kv.Value.C.ToArray(), kv.Value.S.ToArray()), StringComparer.OrdinalIgnoreCase),
            [.. riverVerts], [.. riverIdx]);
    }

    /// <summary>Milliseconds the last frame took to build and submit (CPU side).</summary>
    public double LastFrameMs { get; private set; }
    public string? FrameBreakdown { get; private set; }

    /// <summary>
    /// River water: every River entity's spline turned into the same mesh the native build writes
    /// (RiverBuilder: sampled spline → 5-vertex cross-sections → triangles), on the lf terrain, rebuilt in the
    /// background whenever the scene changes.
    /// </summary>
    private void EnsureRivers()
    {
        if (_model is null || _r is null || _riversBuilding || _riversVersion == _model.Version || _model.Terrain is not var (_, worldW, worldH)) return;
        var layers = _model.All.Where(i => i.Entity.Type == "River").Select(i => i.Layer.FilePath).Distinct().Where(p => p is not null).ToList();
        _riversVersion = _model.Version;
        if (layers.Count == 0) { _rivers = null; return; }
        _riversBuilding = true;
        var r = _r;
        var model = _model;
        _ = Task.Run(() =>
        {
            try
            {
                var verts = new List<LineVertex>();
                var idx = new List<uint>();
                var colour = new Vector4(0.13f, 0.30f, 0.43f, 0.85f);
                foreach (var path in layers)
                    foreach (var river in AtlasWH3.Core.Campaign.Rivers.RiverBuilder.ReadLayer(path!))
                    {
                        var sections = AtlasWH3.Core.Campaign.Rivers.RiverBuilder.Sample(river, (x, z) => model.GroundY(x, z));
                        if (sections.Count < 2) continue;
                        var mesh = AtlasWH3.Formats.Models.RigidModel.Read(
                            AtlasWH3.Core.Campaign.Rivers.RiverBuilder.BuildModel(sections, (float)worldW, (float)worldH).ToBytes()).Lods[0].Meshes[0];
                        var baseVertex = (uint)verts.Count;
                        for (var k = 0; k < mesh.Positions.Length; k += 3)
                            verts.Add(new LineVertex(new Vector3(mesh.Positions[k], mesh.Positions[k + 1] + 0.01f, mesh.Positions[k + 2]), colour));
                        foreach (var i in mesh.Indices) idx.Add(baseVertex + i);
                    }
                var built = idx.Count == 0 ? ((Vortice.Direct3D11.ID3D11Buffer, Vortice.Direct3D11.ID3D11Buffer, int)?)null : r.CreateColoured([.. verts], [.. idx]);
                Dispatcher.BeginInvoke(() => { _rivers = built; _riversBuilding = false; Invalidate(); });
            }
            catch (Exception)
            {
                Dispatcher.BeginInvoke(() => _riversBuilding = false);
            }
        });
    }

    /// <summary>
    /// The tile overlay in 3D: every shown tile's cells as translucent quads draped on the lf terrain (corner heights
    /// sampled per tile-map cell), in the tile set's colour with alternating shades so neighbouring tiles read apart.
    /// Built in the background when the overlay mode changes.
    /// </summary>
    private void EnsureTileMesh()
    {
        if (_model is null || _r is null || _tilesBuilding || _model.TilesShown == _tileMeshMode) return;
        var mode = _model.TilesShown;
        _tileMeshMode = mode;
        if (mode == SceneModel.TileOverlay.Off || _model.Tiles.Count == 0) { _tileMesh = null; return; }
        _tilesBuilding = true;
        var r = _r;
        var model = _model;
        _ = Task.Run(() =>
        {
            var verts = new List<LineVertex>();
            var idx = new List<uint>();
            var (_, _, cx, cz) = model.TileGrid;
            var n = 0;
            foreach (var t in model.Tiles)
            {
                if (!model.ShowsTile(t)) continue;
                var c = SceneModel.TileColour(t.TileSet);
                var shade = (n++ & 1) == 0 ? 1f : 0.82f;
                var col = new Vector4((c >> 16 & 0xFF) / 255f * shade, (c >> 8 & 0xFF) / 255f * shade, (c & 0xFF) / 255f * shade, t.Base ? 0.35f : 0.55f);
                // One vertex per cell corner of the tile, heights from the lf terrain, lifted a little.
                int w = t.W, h = t.H;
                var start = (uint)verts.Count;
                for (var j = 0; j <= h; j++)
                    for (var i = 0; i <= w; i++)
                    {
                        double x = (t.X + i) * cx, z = (t.Y + j) * cz;
                        verts.Add(new LineVertex(new Vector3((float)x, (float)Math.Max(model.GroundY(x, z), 0) + 0.03f, (float)z), col));
                    }
                for (var j = 0; j < h; j++)
                    for (var i = 0; i < w; i++)
                    {
                        uint a = start + (uint)(j * (w + 1) + i), b = a + 1, d = a + (uint)(w + 1), e = d + 1;
                        idx.AddRange([a, b, d, b, e, d]);
                    }
            }
            var built = idx.Count == 0 ? ((Vortice.Direct3D11.ID3D11Buffer, Vortice.Direct3D11.ID3D11Buffer, int)?)null : r.CreateColoured([.. verts], [.. idx]);
            Dispatcher.BeginInvoke(() => { _tileMesh = built; _tilesBuilding = false; Invalidate(); });
        });
    }

    /// <summary>The tile overlay mesh matches the current mode.</summary>
    public bool TilesReady => _model is null || (!_tilesBuilding && _tileMeshMode == _model.TilesShown);

    /// <summary>River meshes are on the GPU (or the project has none).</summary>
    public bool RiversReady => _model is null || !_model.All.Any(i => i.Entity.Type == "River") || (_rivers is not null && !_riversBuilding);

    private string? TreeModelFor(string treeId)
    {
        if (!_treeModels.TryGetValue(treeId, out var path)) _treeModels[treeId] = path = _model!.TreeModel(treeId);
        return path;
    }

    // ---------------------------------------------------------------- frame

    private void Tick()
    {
        var now = DateTime.Now;
        // Real frame time (capped so a hitch doesn't jump the camera across the map).
        var dt = (float)Math.Min(0.25, (now - _lastFrame).TotalSeconds); // capped so a long hitch can't jump the camera; slow frames keep the speed
        _lastFrame = now;
        if (_keysDown.Count > 0)
        {
            var move = Vector3.Zero;
            var flying = _orbitFrom is not null; // WASD/QE while the right button is held; arrows/PgUp/PgDn any time
            if (flying && _keysDown.Contains(Key.W) || _keysDown.Contains(Key.Up)) move.Z += 1;
            if (flying && _keysDown.Contains(Key.S) || _keysDown.Contains(Key.Down)) move.Z -= 1;
            if (flying && _keysDown.Contains(Key.D) || _keysDown.Contains(Key.Right)) move.X += 1;
            if (flying && _keysDown.Contains(Key.A) || _keysDown.Contains(Key.Left)) move.X -= 1;
            if (flying && _keysDown.Contains(Key.E) || _keysDown.Contains(Key.PageUp)) move.Y += 1;
            if (flying && _keysDown.Contains(Key.Q) || _keysDown.Contains(Key.PageDown)) move.Y -= 1;
            if (move != Vector3.Zero)
            {
                var boost = _keysDown.Contains(Key.LeftShift) || _keysDown.Contains(Key.RightShift) ? 4f
                    : _keysDown.Contains(Key.LeftCtrl) || _keysDown.Contains(Key.RightCtrl) ? 0.25f : 1f;
                // The original speed (0.8 orbit distances per second), times the wheel-set multiplier.
                _cam.Fly(Vector3.Normalize(move), _cam.Distance * 0.8f * FlySpeed * boost * dt);
                _dirty = true;
            }
        }
        if (!_dirty || _r is null || _model is null || !IsVisible) return;
        _dirty = false;
        RenderFrame(null);
        _r.Present();
    }

    /// <summary>Draws the scene into the window, or (offscreen) into the given target.</summary>
    private void RenderFrame((Vortice.Direct3D11.ID3D11RenderTargetView Rtv, Vortice.Direct3D11.ID3D11DepthStencilView Dsv, int W, int H)? offscreen)
    {
        var r = _r!;
        var frameClock = System.Diagnostics.Stopwatch.StartNew();
        if (_builtVersion != _model!.Version) Rebuild();
        EnsureTerrain();
        EnsureRegionOverlay();
        int w = offscreen?.W ?? r.Width, h = offscreen?.H ?? r.Height;
        _cam.Aspect = w / (float)Math.Max(1, h);
        var vp = _cam.ViewProjection;
        r.Begin(vp, _cam.Eye, new Vector4(0.13f, 0.14f, 0.16f, 1), offscreen?.Rtv, offscreen?.Dsv, w, h);

        var lines = new List<LineVertex>();
        if (_terrain is { } t) r.DrawTerrain(t.V, t.I, t.Count, _ground);
        if (_terrain is not null && _ground is not null && ShowTileMeshes && _tileGround is { } tg)
            foreach (var (start, count, blend, normal, layers) in tg.Ranges) r.DrawTileGround(tg.V, tg.I, start, count, blend, normal, layers);
        else if (_model.Terrain is null) Grid(lines);

        // Models, grouped per (model, LOD), culled and LOD-picked per instance.
        var frustum = new Frustum(vp);
        _frame++;
        var batches = new List<GpuModel>();
        var eye = _cam.Eye;
        var placeholders = new List<Item>();
        var partMarkers = new List<LineVertex>();
        var tItems = frameClock.Elapsed.TotalMilliseconds;
        foreach (var item in _items)
        {
            var tr = TransformOf(item);
            var selected = Selection.Contains(item.Id);
            var tint = selected ? new Vector4(1, 0.85f, 0.2f, 0.45f) : item.Frozen ? new Vector4(0.1f, 0.1f, 0.1f, 0.5f) : Vector4.Zero;
            if (item.Parts.Count > 0)
            {
                // A prefab instance: cull the whole prefab once, then draw each part through the instance transform.
                if (item.PartBounds is { } pb && !frustum.Sphere(V(tr.Apply((pb[0] + pb[3]) / 2, (pb[1] + pb[4]) / 2, (pb[2] + pb[5]) / 2)),
                        new Vector3(pb[3] - pb[0], pb[4] - pb[1], pb[5] - pb[2]).Length() / 2 * MaxScale(tr))) continue;
                foreach (var part in item.Parts)
                {
                    var placed = tr.Compose(part.Local);
                    var pm = part.ModelPath is null ? null : GpuModelFor(part.ModelPath);
                    if (pm is not null)
                    {
                        AddInstance(pm, placed, tint, frustum, eye, batches);
                        continue;
                    }
                    var p = V(placed.Position);
                    var toEye = Vector3.Distance(eye, p);
                    if (!selected && toEye > 120) continue;
                    var size = toEye * 0.005f;
                    var col = Colour(selected ? 0xFFFFE040u : SceneView.ColourOf(part.Type));
                    partMarkers.Add(new(p - Vector3.UnitX * size, col)); partMarkers.Add(new(p + Vector3.UnitX * size, col));
                    partMarkers.Add(new(p, col)); partMarkers.Add(new(p + Vector3.UnitY * size * 2, col));
                    partMarkers.Add(new(p - Vector3.UnitZ * size, col)); partMarkers.Add(new(p + Vector3.UnitZ * size, col));
                }
                continue;
            }
            if (item.Gpu is null && item.ModelPath is not null && GpuModelFor(item.ModelPath) is { } loaded)
            {
                item.Gpu = loaded;
                var itemWorld = World(item.Transform);
                item.Instance = Rows(itemWorld, Vector4.Zero);
                var b = loaded.Bounds;
                item.Scale = MaxScale(item.Transform);
                item.Centre = Vector3.Transform(new Vector3((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2), itemWorld);
                item.Centre += new Vector3(0, DrapeLift(loaded, item.Centre), 0);
                item.Radius = loaded.Radius * item.Scale;
            }
            if (item.Gpu is not { } gm)
            {
                placeholders.Add(item);
                continue;
            }
            if (_overrides.ContainsKey(item.Id)) AddInstance(gm, tr, tint, frustum, eye, batches);
            else
            {
                if (!frustum.Sphere(item.Centre, item.Radius)) continue;
                var inst = item.Instance;
                inst.Tint = tint;
                Queue(gm, Lod(gm, Vector3.Distance(eye, item.Centre), item.Scale), inst, batches);
            }
        }
        lines.AddRange(partMarkers);
        AddCityMarkers(lines, frustum, eye);
        var tTrees = frameClock.Elapsed.TotalMilliseconds;

        // Forests (one tree per hex from the CampaignTree map); not selectable. Instances are precomputed per season.
        if (ShowTrees) AddTrees(frustum, eye, batches);
        if (ShowTileMeshes) AddTileMeshes(frustum, eye, batches);
        var tDraw = frameClock.Elapsed.TotalMilliseconds;
        // One upload for the whole frame, then a ranged draw per (model, LOD, mesh).
        var total = batches.Sum(g => g.Frame0.Sum(l => l.Count));
        var all = new InstanceData[total];
        var ranges = new List<(GpuModel Gm, int Lod, int Start, int Count)>();
        var at = 0;
        foreach (var g in batches)
            for (var lod = 0; lod < g.Frame0.Length; lod++)
            {
                var list = g.Frame0[lod];
                if (list.Count == 0) continue;
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).CopyTo(all.AsSpan(at));
                ranges.Add((g, lod, at, list.Count));
                at += list.Count;
            }
        r.UploadInstances(all);
        foreach (var (g, lod, start, count) in ranges)
            foreach (var mesh in g.Lods[lod]) r.DrawInstanceRange(mesh, start, count);

        var tAfterDraw = frameClock.Elapsed.TotalMilliseconds;
        FrameBreakdown = $"setup {tItems:F1}, entities {tTrees - tItems:F1}, trees {tDraw - tTrees:F1}, draws {tAfterDraw - tDraw:F1} ({ranges.Count} ranges, {total} instances)";

        // Water (campaign sea level), markers, shapes, selection.
        if (ShowWater && _water is { } wm) r.DrawColoured(wm.V, wm.I, wm.Count);
        EnsureRivers();
        EnsureTileMesh();
        if (_tileMesh is { } tm2 && _model.TilesShown != SceneModel.TileOverlay.Off) r.DrawColoured(tm2.V, tm2.I, tm2.Count);
        if (ShowWater && ShowTileMeshes && _tileRivers is { } trv) r.DrawColoured(trv.V, trv.I, trv.Count);
        if (ShowWater && _rivers is { } rv) r.DrawColoured(rv.V, rv.I, rv.Count);
        else if (ShowWater && _model.Terrain is var (_, ww, wh) && _model.SeaHeight is null)
        {
            var c = new Vector4(0.09f, 0.22f, 0.34f, 0.78f); // deep enough to read as sea over the textured seabed
            float sx = (float)ww, sz = (float)wh, y = 0.02f;
            r.DrawLines(stackalloc LineVertex[]
            {
                new(new(0, y, 0), c), new(new(sx, y, 0), c), new(new(sx, y, sz), c),
                new(new(0, y, 0), c), new(new(sx, y, sz), c), new(new(0, y, sz), c),
            }, triangles: true);
        }
        foreach (var item in placeholders)
        {
            var isSelected = Selection.Contains(item.Id);
            var col = Colour(isSelected ? 0xFFFFE040u : item.Colour);
            if (ModelBounds(item) is not null) Box(lines, Corners(item).ToArray(), col);
            else
            {
                // Markers keep a constant screen size and fade out of the overview (unless selected).
                var p = V(TransformOf(item).Position);
                var toEye = Vector3.Distance(eye, p);
                if (!isSelected && toEye > 120) continue;
                var markerSize = toEye * 0.006f;
                if (!frustum.Sphere(p, markerSize)) continue;
                lines.Add(new(p - Vector3.UnitX * markerSize, col)); lines.Add(new(p + Vector3.UnitX * markerSize, col));
                lines.Add(new(p - Vector3.UnitY * markerSize, col)); lines.Add(new(p + Vector3.UnitY * markerSize * 2, col));
                lines.Add(new(p - Vector3.UnitZ * markerSize, col)); lines.Add(new(p + Vector3.UnitZ * markerSize, col));
            }
        }
        foreach (var item in _items)
        {
            if (item.Outline.Count == 0) continue;
            var col = Colour(Selection.Contains(item.Id) ? 0xFFFFE040u : item.Colour);
            var o = _overrides.ContainsKey(item.Id) ? Offset(item) : null;
            foreach (var (a, b) in item.Outline)
            {
                lines.Add(new(o is null ? a : o(a), col));
                lines.Add(new(o is null ? b : o(b), col));
            }
        }
        foreach (var item in _items)
            if (Selection.Contains(item.Id) && (item.ModelPath is not null || item.Parts.Count > 0) && ModelBounds(item) is not null)
                Box(lines, Corners(item).ToArray(), new Vector4(1, 0.88f, 0.25f, 1));
        r.DrawLines(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines));

        LastFrameMs = frameClock.Elapsed.TotalMilliseconds;

        // Gizmo on top.
        var gizmo = new List<LineVertex>();
        DrawGizmo(gizmo);
        r.DrawLines(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(gizmo), overlay: true);
        DrawCityLabels(r, w, h);

        // Box selection rectangle, in pixels.
        if (_boxFrom is { } bf)
        {
            var ortho = Matrix4x4.CreateOrthographicOffCenterLeftHanded(0, w, h, 0, 0, 1);
            r.Begin(ortho, Vector3.Zero, default, offscreen?.Rtv, offscreen?.Dsv, w, h, clear: false);
            var c = new Vector4(1, 0.85f, 0.25f, 1);
            Vector3 P(double x, double y) => new((float)x, (float)y, 0.5f);
            r.DrawLines(stackalloc LineVertex[]
            {
                new(P(bf.X, bf.Y), c), new(P(_mouse.X, bf.Y), c), new(P(_mouse.X, bf.Y), c), new(P(_mouse.X, _mouse.Y), c),
                new(P(_mouse.X, _mouse.Y), c), new(P(bf.X, _mouse.Y), c), new(P(bf.X, _mouse.Y), c), new(P(bf.X, bf.Y), c),
            }, overlay: true);
        }
    }

    private static float MaxScale(TerryTransform t) =>
        (float)Math.Max(Math.Abs(t.Scale[0]), Math.Max(Math.Abs(t.Scale[1]), Math.Abs(t.Scale[2])));

    /// <summary>Queues one model instance: frustum culled, LOD picked by distance (RMV2 LOD distances, scaled).</summary>
    private int _frame;

    private void AddInstance(GpuModel gm, TerryTransform tr, Vector4 tint, Frustum frustum, Vector3 eye, List<GpuModel> batches)
    {
        var world = World(tr);
        var b = gm.Bounds;
        var centre = Vector3.Transform(new Vector3((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2), world);
        centre += new Vector3(0, DrapeLift(gm, centre), 0);
        var scale = MaxScale(tr);
        if (!frustum.Sphere(centre, gm.Radius * scale)) return;
        Queue(gm, Lod(gm, Vector3.Distance(eye, centre), scale), Rows(world, tint), batches);
    }

    private static int Lod(GpuModel gm, float dist, float scale)
    {
        var lod = 0;
        while (lod < gm.Lods.Count - 1 && lod < gm.Distances.Count && dist > gm.Distances[lod] * Math.Max(1, scale)) lod++;
        return lod;
    }

    /// <summary>Adds an instance to the model's list for this frame (registering the model on its first instance).</summary>
    private void Queue(GpuModel gm, int lod, in InstanceData inst, List<GpuModel> batches)
    {
        if (gm.Frame != _frame)
        {
            if (gm.Frame0.Length != gm.Lods.Count) gm.Frame0 = Enumerable.Range(0, gm.Lods.Count).Select(_ => new List<InstanceData>()).ToArray();
            foreach (var l in gm.Frame0) l.Clear();
            gm.Frame = _frame;
            batches.Add(gm);
        }
        gm.Frame0[lod].Add(inst);
    }

    private static InstanceData Rows(Matrix4x4 w, Vector4 tint) => new()
    {
        Row0 = new Vector4(w.M11, w.M12, w.M13, w.M14),
        Row1 = new Vector4(w.M21, w.M22, w.M23, w.M24),
        Row2 = new Vector4(w.M31, w.M32, w.M33, w.M34),
        Row3 = new Vector4(w.M41, w.M42, w.M43, w.M44),
        Tint = tint,
    };

    /// <summary>For an item being dragged: maps its saved outline points through the drag (old → new transform).</summary>
    private Func<Vector3, Vector3>? Offset(Item item)
    {
        if (!_overrides.TryGetValue(item.Id, out var now)) return null;
        Matrix4x4.Invert(World(item.Transform), out var inv);
        var m = inv * World(now);
        return p => Vector3.Transform(p, m);
    }

    private static Vector4 Colour(uint c) => new(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1);

    private static void Box(List<LineVertex> lines, Vector3[] c, Vector4 col)
    {
        int[] e = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        foreach (var i in e) lines.Add(new(c[i], col));
    }

    private void Grid(List<LineVertex> lines)
    {
        var step = MathF.Pow(10, MathF.Ceiling(MathF.Log10(Math.Max(0.1f, _cam.Distance / 10))));
        var c = new Vector4(0.3f, 0.3f, 0.32f, 1);
        var centre = new Vector3(MathF.Round(_cam.Target.X / step) * step, 0, MathF.Round(_cam.Target.Z / step) * step);
        for (var i = -20; i <= 20; i++)
        {
            var col = i == 0 ? new Vector4(0.45f, 0.45f, 0.48f, 1) : c;
            lines.Add(new(centre + new Vector3(i * step, 0, -20 * step), col));
            lines.Add(new(centre + new Vector3(i * step, 0, 20 * step), col));
            lines.Add(new(centre + new Vector3(-20 * step, 0, i * step), col));
            lines.Add(new(centre + new Vector3(20 * step, 0, i * step), col));
        }
    }

    // ---------------------------------------------------------------- gizmo

    private Vector3? SelectionPivot()
    {
        var pts = _items.Where(i => Selection.Contains(i.Id) && !i.Frozen).Select(i => V(TransformOf(i).Position)).ToList();
        return pts.Count == 0 ? null : pts.Aggregate(Vector3.Add) / pts.Count;
    }

    private float GizmoSize(Vector3 pivot) => Vector3.Distance(_cam.Eye, pivot) * 0.12f;

    private void DrawGizmo(List<LineVertex> lines)
    {
        if ((_gizmoPart is null ? SelectionPivot() : _gizmoPivot) is not { } p) return;
        var s = GizmoSize(p);
        Vector4 Hi(string part, Vector4 c) => _gizmoPart == part ? new Vector4(1, 1, 0.3f, 1) : c;
        switch (Mode)
        {
            case GizmoMode.Move:
                foreach (var (part, axis, col) in Axes())
                {
                    var c = Hi(part, col);
                    lines.Add(new(p, c)); lines.Add(new(p + axis * s, c));
                    // arrow head
                    var side = Vector3.Normalize(Vector3.Cross(axis, Math.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)) * s * 0.06f;
                    lines.Add(new(p + axis * s, c)); lines.Add(new(p + axis * s * 0.88f + side, c));
                    lines.Add(new(p + axis * s, c)); lines.Add(new(p + axis * s * 0.88f - side, c));
                }
                var q = Hi("xz", new Vector4(0.9f, 0.8f, 0.3f, 1));
                Vector3 a = p + new Vector3(s * 0.25f, 0, 0), b = p + new Vector3(s * 0.25f, 0, s * 0.25f), d = p + new Vector3(0, 0, s * 0.25f);
                lines.Add(new(a, q)); lines.Add(new(b, q)); lines.Add(new(b, q)); lines.Add(new(d, q));
                break;
            case GizmoMode.Rotate:
                var rc = Hi("ring", new Vector4(0.3f, 0.9f, 0.35f, 1));
                for (var i = 0; i < 64; i++)
                {
                    float a0 = i / 64f * MathF.Tau, a1 = (i + 1) / 64f * MathF.Tau;
                    lines.Add(new(p + new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * s, rc));
                    lines.Add(new(p + new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * s, rc));
                }
                break;
            case GizmoMode.Scale:
                var sc = Hi("scale", new Vector4(0.95f, 0.95f, 0.95f, 1));
                var k = s * 0.12f;
                var cube = Enumerable.Range(0, 8).Select(i => p + new Vector3(((i & 1) * 2 - 1) * k, ((i >> 1 & 1) * 2 - 1) * k + s * 0.5f, ((i >> 2 & 1) * 2 - 1) * k)).ToArray();
                Box(lines, cube, sc);
                lines.Add(new(p, sc)); lines.Add(new(p + Vector3.UnitY * (s * 0.5f - k), sc));
                break;
        }
    }

    private static IEnumerable<(string Part, Vector3 Axis, Vector4 Colour)> Axes() =>
    [
        ("x", Vector3.UnitX, new Vector4(0.95f, 0.3f, 0.3f, 1)),
        ("y", Vector3.UnitY, new Vector4(0.35f, 0.9f, 0.35f, 1)),
        ("z", Vector3.UnitZ, new Vector4(0.35f, 0.5f, 1, 1)),
    ];

    /// <summary>Which gizmo handle (if any) is under the mouse, tested in screen space.</summary>
    private string? GizmoHit(Point m)
    {
        if (SelectionPivot() is not { } p || _r is null) return null;
        var s = GizmoSize(p);
        float w = _r.Width, h = _r.Height;
        var mouse = new Vector2((float)m.X, (float)m.Y);
        float SegDist(Vector3 a, Vector3 b)
        {
            if (_cam.ToScreen(a, w, h) is not { } sa || _cam.ToScreen(b, w, h) is not { } sb) return float.MaxValue;
            var d = sb - sa;
            var t = Math.Clamp(Vector2.Dot(mouse - sa, d) / Math.Max(1e-6f, d.LengthSquared()), 0, 1);
            return Vector2.Distance(mouse, sa + d * t);
        }
        switch (Mode)
        {
            case GizmoMode.Move:
                var quad = new[] { p, p + new Vector3(s * 0.25f, 0, 0), p + new Vector3(s * 0.25f, 0, s * 0.25f), p + new Vector3(0, 0, s * 0.25f) };
                if (InsideScreenQuad(quad, mouse, w, h)) return "xz";
                var best = Axes().Select(a => (a.Part, D: SegDist(p, p + a.Axis * s))).MinBy(x => x.D);
                return best.D < 9 ? best.Part : null;
            case GizmoMode.Rotate:
                for (var i = 0; i < 64; i++)
                {
                    float a0 = i / 64f * MathF.Tau, a1 = (i + 1) / 64f * MathF.Tau;
                    if (SegDist(p + new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * s, p + new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * s) < 9) return "ring";
                }
                return null;
            case GizmoMode.Scale:
                return _cam.ToScreen(p + Vector3.UnitY * s * 0.5f, w, h) is { } c && Vector2.Distance(c, mouse) < 14 ? "scale" : null;
        }
        return null;
    }

    private bool InsideScreenQuad(Vector3[] quad, Vector2 m, float w, float h)
    {
        var pts = quad.Select(q => _cam.ToScreen(q, w, h)).ToList();
        if (pts.Any(p => p is null)) return false;
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var (a, b) = (pts[i]!.Value, pts[j]!.Value);
            if ((a.Y > m.Y) != (b.Y > m.Y) && m.X < (b.X - a.X) * (m.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private void BeginGizmoDrag(string part, Point m)
    {
        _gizmoPart = part;
        _gizmoPivot = SelectionPivot()!.Value;
        _dragStartMouse = m;
        _dragStart = _items.Where(i => Selection.Contains(i.Id) && !i.Frozen).ToDictionary(i => i.Id, i => TransformOf(i));
        var (o, d) = Ray(m);
        if (part is "x" or "y" or "z") _dragStartParam = AxisParam(o, d, AxisOf(part));
        else if (PlaneHit(o, d, _gizmoPivot.Y) is { } hit) _dragStartHit = hit;
    }

    private static Vector3 AxisOf(string part) => part switch { "x" => Vector3.UnitX, "y" => Vector3.UnitY, _ => Vector3.UnitZ };

    /// <summary>Parameter along the gizmo axis of the point closest to the mouse ray.</summary>
    private float AxisParam(Vector3 o, Vector3 d, Vector3 axis)
    {
        var w0 = _gizmoPivot - o;
        float a = Vector3.Dot(axis, axis), b = Vector3.Dot(axis, d), c = Vector3.Dot(d, d), dd = Vector3.Dot(axis, w0), e = Vector3.Dot(d, w0);
        var denom = a * c - b * b;
        return Math.Abs(denom) < 1e-8f ? 0 : (b * e - c * dd) / denom;
    }

    private static Vector3? PlaneHit(Vector3 o, Vector3 d, float y)
    {
        if (Math.Abs(d.Y) < 1e-6f) return null;
        var t = (y - o.Y) / d.Y;
        return t < 0 ? null : o + d * t;
    }

    private void UpdateGizmoDrag(Point m)
    {
        var (o, d) = Ray(m);
        var snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var result = new Dictionary<string, TerryTransform>();
        switch (_gizmoPart)
        {
            case "x" or "y" or "z":
                var delta = AxisParam(o, d, AxisOf(_gizmoPart)) - _dragStartParam;
                if (snap) delta = MathF.Round(delta / 0.5f) * 0.5f;
                var move = AxisOf(_gizmoPart) * delta;
                foreach (var (id, t) in _dragStart) result[id] = Moved(t, move);
                break;
            case "xz":
                if (PlaneHit(o, d, _gizmoPivot.Y) is not { } hit) return;
                var mv = hit - _dragStartHit;
                if (snap) mv = new Vector3(MathF.Round(mv.X / 0.5f) * 0.5f, 0, MathF.Round(mv.Z / 0.5f) * 0.5f);
                foreach (var (id, t) in _dragStart) result[id] = Moved(t, mv with { Y = 0 });
                break;
            case "ring":
                if (PlaneHit(o, d, _gizmoPivot.Y) is not { } h2) return;
                var a0 = MathF.Atan2(_dragStartHit.Z - _gizmoPivot.Z, _dragStartHit.X - _gizmoPivot.X);
                var a1 = MathF.Atan2(h2.Z - _gizmoPivot.Z, h2.X - _gizmoPivot.X);
                var da = a1 - a0;
                if (snap) da = MathF.Round(da / (MathF.PI / 12)) * (MathF.PI / 12);
                foreach (var (id, t) in _dragStart) result[id] = Rotated(t, da);
                break;
            case "scale":
                var factor = MathF.Exp(-(float)(m.Y - _dragStartMouse.Y) * 0.01f);
                if (snap) factor = MathF.Max(0.1f, MathF.Round(factor * 10) / 10);
                foreach (var (id, t) in _dragStart) result[id] = Scaled(t, factor);
                break;
        }
        _overrides = result;
        Invalidate();
    }

    private static TerryTransform Moved(TerryTransform t, Vector3 d) =>
        t with { Position = [t.Position[0] + d.X, t.Position[1] + d.Y, t.Position[2] + d.Z] };

    /// <summary>Turns about the pivot by <paramref name="da"/> (radians, atan2(z, x) sense): positions orbit the pivot,
    /// and yaw changes by -da (TerryTransform's yaw turns local x towards -z).</summary>
    private TerryTransform Rotated(TerryTransform t, float da)
    {
        double x = t.Position[0] - _gizmoPivot.X, z = t.Position[2] - _gizmoPivot.Z;
        double c = Math.Cos(da), s = Math.Sin(da);
        return t with
        {
            Position = [_gizmoPivot.X + x * c - z * s, t.Position[1], _gizmoPivot.Z + x * s + z * c],
            Rotation = [t.Rotation[0], TerryTransform.Wrap(t.Rotation[1] - da * 180 / Math.PI), t.Rotation[2]],
        };
    }

    private TerryTransform Scaled(TerryTransform t, float f)
    {
        var single = _dragStart.Count == 1;
        return t with
        {
            Scale = [t.Scale[0] * f, t.Scale[1] * f, t.Scale[2] * f],
            Position = single ? t.Position
                : [_gizmoPivot.X + (t.Position[0] - _gizmoPivot.X) * f, t.Position[1], _gizmoPivot.Z + (t.Position[2] - _gizmoPivot.Z) * f],
        };
    }

    // ---------------------------------------------------------------- picking

    private (Vector3 O, Vector3 D) Ray(Point m) => _cam.Ray((float)m.X, (float)m.Y, _r?.Width ?? 1, _r?.Height ?? 1);

    /// <summary>
    /// Wheel zoom toward the point under the cursor: the eye moves a sixth of the way to the ground (or model-free
    /// ground plane) hit, the view direction is kept, and the orbit pivot follows to the new hit distance. Zooming
    /// in therefore never stalls on a fixed pivot (the old orbit-distance zoom crept toward a point that, after
    /// flying, could be underground or off screen). No hit (sky): dolly along the view. Zooming out mirrors it.
    /// </summary>
    private void ZoomAt(Point m, bool zoomIn)
    {
        const float step = 1 / 6f;
        var eye = _cam.Eye;
        var forward = _cam.Forward;
        Vector3 move;
        if (GroundHit(m) is { } hit)
        {
            var toHit = hit - eye;
            if (zoomIn && toHit.Length() < 0.02f) return; // at the surface
            move = zoomIn ? toHit * step : -toHit * (step / (1 - step));
        }
        else move = forward * _cam.Distance * (zoomIn ? step : -step / (1 - step));
        var newEye = eye + move;
        // Pivot: where the view centre meets the ground from the new eye, else the old pivot distance scaled.
        var scale = zoomIn ? 1 - step : 1 / (1 - step);
        var dist = Math.Clamp(_cam.Distance * scale, 0.02f, 20000f);
        if (GroundAlong(newEye, forward) is { } t && t > 0.02f) dist = Math.Min(t, 20000f);
        _cam.Distance = dist;
        _cam.Target = newEye + forward * dist;
    }

    public int ViewportWidth => _r?.Width ?? 1;
    public int ViewportHeight => _r?.Height ?? 1;
    /// <summary>Height of the camera eye above the ground below it (self-test).</summary>
    public float EyeAboveGround => _cam.Eye.Y - (float)(_model?.Terrain is null ? 0 : _model.GroundY(_cam.Eye.X, _cam.Eye.Z));

    private Vector3? GroundHit(Point m)
    {
        var (o, d) = Ray(m);
        return GroundAlong(o, d) is { } t ? o + d * t : null;
    }

    /// <summary>Distance along a ray to the lf terrain (y = 0 plane without terrain), or null if it never comes down.</summary>
    private float? GroundAlong(Vector3 o, Vector3 d)
    {
        d = Vector3.Normalize(d);
        if (_model?.Terrain is null)
            return d.Y < -1e-5f && o.Y > 0 ? -o.Y / d.Y : null;
        float Above(float t) { var p = o + d * t; return p.Y - (float)_model.GroundY(p.X, p.Z); }
        var prev = 0f;
        var h0 = Above(0);
        if (h0 <= 0) return 0;
        var dt = Math.Max(0.005f, h0 * 0.05f);
        for (var i = 0; i < 4000; i++)
        {
            var t = prev + dt;
            var h = Above(t);
            if (h <= 0)
            {
                float lo = prev, hi = t;
                for (var k = 0; k < 24; k++) { var mid = (lo + hi) / 2; if (Above(mid) > 0) lo = mid; else hi = mid; }
                return hi;
            }
            prev = t;
            dt = Math.Max(0.005f, Math.Min(h * 0.5f, dt * 1.5f + 0.001f));
            if (t > 50000) break;
        }
        return null;
    }

    /// <summary>The entity under the mouse: nearest triangle of loaded models whose box the ray hits, else the nearest
    /// box / marker.</summary>
    private string? Pick(Point m)
    {
        var (o, d) = Ray(m);
        string? bestId = null;
        var best = float.MaxValue;
        string? boxId = null;
        var boxBest = float.MaxValue;
        var markerRadius = Math.Max(0.05f, _cam.Distance * 0.008f);
        foreach (var item in _items)
        {
            if (item.Frozen) continue;
            var tr = TransformOf(item);
            var b = ModelBounds(item);
            if (b is null)
            {
                if (SphereHit(o, d, V(tr.Position), markerRadius) is { } ts && ts < boxBest) { boxBest = ts; boxId = item.Id; }
                continue;
            }
            Matrix4x4.Invert(World(tr), out var inv);
            var lo = Vector3.Transform(o - new Vector3(0, DrapeLift(item.Gpu, V(tr.Position)), 0), inv);
            var ld = Vector3.TransformNormal(d, inv);
            if (BoxHit(lo, ld, b) is not { } tb) continue;
            if (tb < boxBest) { boxBest = tb; boxId = item.Id; }
            if (item.Parts.Count > 0)
            {
                // Inside the prefab's box: test its parts' models; a hit selects the instance.
                foreach (var part in item.Parts)
                {
                    if (part.ModelPath is null || !_gpuModels.TryGetValue(part.ModelPath, out var pm) || pm?.Cpu is not { } pcpu) continue;
                    Matrix4x4.Invert(World(tr.Compose(part.Local)), out var pinv);
                    var po = Vector3.Transform(o, pinv);
                    var pd = Vector3.TransformNormal(d, pinv);
                    if (BoxHit(po, pd, pm.Bounds) is null) continue;
                    if (TriangleHit(po, pd, pcpu) is { } pt && pt < best) { best = pt; bestId = item.Id; }
                }
                continue;
            }
            if (item.ModelPath is not null && _gpuModels.TryGetValue(item.ModelPath, out var gm) && gm?.Cpu is { } cpu
                && TriangleHit(lo, ld, cpu) is { } tt && tt < best)
            {
                best = tt;
                bestId = item.Id;
            }
        }
        return bestId ?? boxId;
    }

    private static float? SphereHit(Vector3 o, Vector3 d, Vector3 c, float r)
    {
        var oc = o - c;
        var b = Vector3.Dot(oc, d);
        var disc = b * b - (oc.LengthSquared() - r * r);
        if (disc < 0) return null;
        var t = -b - MathF.Sqrt(disc);
        return t >= 0 ? t : null;
    }

    private static float? BoxHit(Vector3 o, Vector3 d, float[] b)
    {
        float tmin = 0, tmax = float.MaxValue;
        for (var a = 0; a < 3; a++)
        {
            var oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            var da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            if (Math.Abs(da) < 1e-12f)
            {
                if (oa < b[a] || oa > b[a + 3]) return null;
                continue;
            }
            var t1 = (b[a] - oa) / da;
            var t2 = (b[a + 3] - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1);
            tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }

    private static float? TriangleHit(Vector3 o, Vector3 d, RenderModel model)
    {
        float? best = null;
        foreach (var m in model.Lods[0])
            for (var i = 0; i + 2 < m.Indices.Length; i += 3)
            {
                Vector3 P(int k) => new(m.Positions[k * 3], m.Positions[k * 3 + 1], m.Positions[k * 3 + 2]);
                var (a, b, c) = (P(m.Indices[i]), P(m.Indices[i + 1]), P(m.Indices[i + 2]));
                var e1 = b - a;
                var e2 = c - a;
                var p = Vector3.Cross(d, e2);
                var det = Vector3.Dot(e1, p);
                if (Math.Abs(det) < 1e-12f) continue;
                var inv = 1 / det;
                var s = o - a;
                var u = Vector3.Dot(s, p) * inv;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(s, e1);
                var v = Vector3.Dot(d, q) * inv;
                if (v < 0 || u + v > 1) continue;
                var t = Vector3.Dot(e2, q) * inv;
                if (t > 0 && (best is null || t < best)) best = t;
            }
        return best;
    }

    /// <summary>Ground point under a pixel: the campaign terrain (ray-marched), else the y = 0 plane.</summary>
    public Vector3? GroundUnder(Point m)
    {
        var (o, d) = Ray(m);
        if (_model?.Terrain is var (raster, worldW, worldH))
        {
            var step = (float)(worldW / raster.Width);
            var prev = o;
            for (var t = 0f; t < _cam.Far; t += step * Math.Max(1, t / 200))
            {
                var p = o + d * t;
                var col = (int)(p.X / worldW * raster.Width);
                var row = (int)((1 - p.Z / worldH) * raster.Height);
                if (col < 0 || row < 0 || col >= raster.Width || row >= raster.Height) { prev = p; continue; }
                var ground = raster[col, row] * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset;
                if (p.Y <= ground) return (prev + p) / 2;
                prev = p;
            }
            return null;
        }
        return PlaneHit(o, d, 0);
    }

    // ---------------------------------------------------------------- input

    private void OnHostMouseDown(MouseButton button, int x, int y)
    {
        var p = new Point(x, y);
        _mouse = p;
        switch (button)
        {
            case MouseButton.Right:
                _orbitFrom = p;
                break;
            case MouseButton.Middle:
                _panFrom = p;
                break;
            case MouseButton.Left when Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                _orbitFrom = p;
                break;
            case MouseButton.Left when PlaceAt is { } place:
                if (GroundUnder(p) is { } ground) place(ground);
                break;
            case MouseButton.Left:
                if (GizmoHit(p) is { } part) { BeginGizmoDrag(part, p); break; }
                _boxFrom = p;
                break;
        }
    }

    private void OnHostMouseMove(int x, int y)
    {
        var p = new Point(x, y);
        if (_orbitFrom is { } o)
        {
            _cam.Orbit((float)(p.X - o.X) * 0.006f, (float)(p.Y - o.Y) * 0.006f);
            _orbitFrom = p;
            Invalidate();
        }
        else if (_panFrom is { } pf)
        {
            _cam.Pan((float)(p.X - pf.X), (float)(p.Y - pf.Y), _r?.Height ?? 1);
            _panFrom = p;
            Invalidate();
        }
        else if (_gizmoPart is not null) UpdateGizmoDrag(p);
        else if (_boxFrom is not null) Invalidate();
        else if (Mode is var _ && GizmoHit(p) != _hoverPart)
        {
            _hoverPart = GizmoHit(p);
            Invalidate();
        }
        _mouse = p;
        if (_orbitFrom is null && _panFrom is null && GroundUnder(p) is { } g)
            HoverText?.Invoke($"x {g.X:F2}  y {g.Y:F2}  z {g.Z:F2}" + TileText(g.X, g.Z));
    }

    private string? _hoverPart;

    /// <summary>"  |  tile roads/junction_1 (roads, 90°)" for the hover readout when the overlay is on, and the
    /// region under the point when the region mask is on.</summary>
    private string TileText(double x, double z) =>
        (_model is { TilesShown: not SceneModel.TileOverlay.Off } m && m.TileAt(x, z) is { } t
            ? $"   |   tile {t.Name} ({t.TileSet}, {t.Degrees}°, {t.W}x{t.H}{(t.Climate.Length > 0 ? ", " + t.Climate : "")})" : "")
        + (_model is { ShowRegions: true } rm && rm.RegionAt(x, z) is { } region ? $"   |   region {region}" : "");

    public string HoverTileText(double x, double z) => TileText(x, z);

    private void OnHostMouseUp(MouseButton button, int x, int y)
    {
        var p = new Point(x, y);
        switch (button)
        {
            case MouseButton.Right:
            case MouseButton.Left when _orbitFrom is not null:
                _orbitFrom = null;
                // Flying ends with the button: drop WASD/QE so a key released elsewhere can't stay "held".
                _keysDown.RemoveWhere(k => k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E);
                break;
            case MouseButton.Middle:
                _panFrom = null;
                break;
            case MouseButton.Left when _gizmoPart is not null:
                var label = _gizmoPart switch { "ring" => "rotate", "scale" => "scale", _ => "move" };
                var changed = _overrides.Where(kv => !Same(kv.Value, _dragStart[kv.Key])).ToDictionary(kv => kv.Key, kv => kv.Value);
                _gizmoPart = null;
                if (changed.Count > 0) TransformsCommitted?.Invoke(changed, $"{label} {changed.Count}");
                _overrides = [];
                Invalidate();
                break;
            case MouseButton.Left when _boxFrom is { } b:
                _boxFrom = null;
                var mode = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? SceneView.SelectMode.Toggle
                    : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SceneView.SelectMode.Add : SceneView.SelectMode.Replace;
                if (Math.Abs(p.X - b.X) + Math.Abs(p.Y - b.Y) < 4)
                {
                    var hit = Pick(p);
                    SelectionRequested?.Invoke(hit is null ? [] : [hit], hit is null && mode != SceneView.SelectMode.Replace ? SceneView.SelectMode.Add : mode);
                }
                else
                {
                    double x0 = Math.Min(b.X, p.X), x1 = Math.Max(b.X, p.X), y0 = Math.Min(b.Y, p.Y), y1 = Math.Max(b.Y, p.Y);
                    var ids = _items.Where(i => !i.Frozen && _cam.ToScreen(V(TransformOf(i).Position), _r!.Width, _r.Height) is { } s
                                                && s.X >= x0 && s.X <= x1 && s.Y >= y0 && s.Y <= y1).Select(i => i.Id).ToList();
                    SelectionRequested?.Invoke(ids, mode == SceneView.SelectMode.Replace ? SceneView.SelectMode.Replace : SceneView.SelectMode.Add);
                }
                Invalidate();
                break;
        }
    }

    private static bool Same(TerryTransform a, TerryTransform b) =>
        a.Position.SequenceEqual(b.Position) && a.Rotation.SequenceEqual(b.Rotation) && a.Scale.SequenceEqual(b.Scale);

    private void KeyChanged(Key key, bool down)
    {
        if (down) _keysDown.Add(key);
        else _keysDown.Remove(key);
        if (!down) return;
        if (_orbitFrom is not null && key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E) return; // flying
        switch (key)
        {
            case Key.W when Keyboard.Modifiers == ModifierKeys.None: Mode = GizmoMode.Move; Invalidate(); return;
            case Key.E when Keyboard.Modifiers == ModifierKeys.None: Mode = GizmoMode.Rotate; Invalidate(); return;
            case Key.R when Keyboard.Modifiers == ModifierKeys.None: Mode = GizmoMode.Scale; Invalidate(); return;
            case Key.F when Keyboard.Modifiers == ModifierKeys.None: FrameSelection(); return;
            case Key.Home: FrameAll(); return;
        }
        KeyCommand?.Invoke(key, Keyboard.Modifiers);
    }

    // ---------------------------------------------------------------- screenshots

    /// <summary>Renders the current view offscreen (BGRA) at the given size, for tests and the MCP.</summary>
    public byte[]? Snapshot(int width, int height)
    {
        if (_r is null || _model is null) return null;
        return _r.RenderOffscreen(width, height, (rtv, dsv) => RenderFrame((rtv, dsv, width, height)));
    }

    /// <summary>True once every model the visible scene uses has finished loading (or failed).</summary>
    public bool ModelsSettled => _items.SelectMany(i => i.Parts.Select(p => p.ModelPath).Append(i.ModelPath))
        .Concat(ShowTrees ? _model!.Trees.Select(t => t.TreeId).Distinct().Select(TreeModelFor) : [])
        .Concat(ShowTileMeshes && _tileMeshCache is { } tiles ? tiles.Keys : []).Where(p => p is not null)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .All(p => _gpuModels.TryGetValue(p!, out var m) && m is not null || _model!.Models.Load(p!) is null);

    /// <summary>Starts the gizmo on the current selection programmatically (self-test): drag from a to b in pixels.</summary>
    public void SimulateGizmoDrag(GizmoMode mode, string part, Point a, Point b)
    {
        Mode = mode;
        BeginGizmoDrag(part, a);
        UpdateGizmoDrag(b);
        OnHostMouseUp(MouseButton.Left, (int)b.X, (int)b.Y);
    }

    /// <summary>Centre of an entity's model box (or its position), in world space.</summary>
    public Vector3? CentreOf(string id) =>
        _items.FirstOrDefault(i => i.Id == id) is { } item ? Corners(item).Aggregate(Vector3.Add) / 8 : null;

    /// <summary>The viewport's child window (for input tests).</summary>
    public IntPtr WindowHandle => _host.ChildHandle;

    public Point? ScreenOf(Vector3 p) => _r is null ? null : _cam.ToScreen(p, _r.Width, _r.Height) is { } s ? new Point(s.X, s.Y) : null;
    public Vector3? Pivot => SelectionPivot();
    public string? PickAt(Point p) => Pick(p);
}

/// <summary>View-frustum planes for sphere culling.</summary>
internal readonly struct Frustum
{
    private readonly Vector4[] _planes;

    public Frustum(Matrix4x4 m)
    {
        _planes =
        [
            new(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
            new(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
            new(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
            new(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
            new(m.M13, m.M23, m.M33, m.M43),
            new(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43),
        ];
        for (var i = 0; i < 6; i++)
        {
            var n = new Vector3(_planes[i].X, _planes[i].Y, _planes[i].Z).Length();
            _planes[i] /= n;
        }
    }

    public bool Sphere(Vector3 c, float r)
    {
        foreach (var p in _planes)
            if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -r) return false;
        return true;
    }
}
