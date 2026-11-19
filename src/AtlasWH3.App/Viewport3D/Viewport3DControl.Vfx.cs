using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using AtlasWH3.Core.Rendering.Vfx;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Viewport3D;

/// <summary>
/// The VFX pass: every placed effect (ECVFX entities and the VFX inside prefabs) near the camera runs as a
/// <see cref="VfxInstance"/> and draws its particles after the water. Effects resolve and their textures load in the
/// background; an effect draws within its own cull distances (the vfx files' CullDistances, world units), at most
/// <see cref="MaxRunningEffects"/> of the nearest at once. While any draws, the view redraws every frame.
/// </summary>
public sealed partial class Viewport3DControl
{
    /// <summary>The most effects simulated at once (the nearest).</summary>
    private const int MaxRunningEffects = 256;
    /// <summary>The most effects started (and warmed up) in one frame, so flying into a dense area does not hitch.</summary>
    private const int MaxStartsPerFrame = 8;

    private sealed class VfxPlacement
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required Item Item { get; init; }
        public TerryTransform? Local { get; init; }     // inside a prefab: the part's transform
        public float Scale { get; init; } = 1;          // ECVFX scale
        public Vector3 Position;
        public VfxEffect? Effect;                       // once resolved
    }

    private sealed class RunningVfx(VfxInstance instance)
    {
        public VfxInstance Instance { get; } = instance;
        public int LastFrame;
    }

    private List<VfxPlacement> _vfxPlacements = [];
    private VfxLibrary? _vfxLibrary;
    private readonly ConcurrentDictionary<string, VfxEffect?> _vfxEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RunningVfx> _vfxRunning = [];
    private readonly List<AtlasWH3.Core.Rendering.Vfx.ParticleVertex> _vfxVertices = [];
    private float _vfxPendingTime;
    private bool _vfxLive;
    private int _vfxResolving;

    /// <summary>No effect is still resolving or loading its textures.</summary>
    public bool VfxSettled => Volatile.Read(ref _vfxResolving) == 0;

    /// <summary>Draw the placed VFX (particles) in the 3D view.</summary>
    public bool ShowVfx { get; set; } = true;

    /// <summary>Last frame: effects running, particles drawn.</summary>
    public string VfxStats { get; private set; } = "";

    private void ResetVfx()
    {
        _vfxLibrary = null;
        _vfxEffects.Clear();
        _vfxRunning.Clear();
        _vfxPlacements = [];
    }

    /// <summary>The effects an item places: its own ECVFX, or those of its prefab's parts (called from Rebuild).</summary>
    private static void AddVfx(List<VfxPlacement> placements, Item item, TerryEntityData entity)
    {
        if (item.Parts.Count == 0)
        {
            if (VfxOf(entity) is { } own)
                placements.Add(new VfxPlacement { Key = item.Id, Name = own.Name, Scale = own.Scale, Item = item, Position = V(item.Transform.Position) });
            return;
        }
        for (var i = 0; i < item.Parts.Count; i++)
            if (VfxOf(item.Parts[i].Entity) is { } part)
                placements.Add(new VfxPlacement
                {
                    Key = item.Id + "/" + i, Name = part.Name, Scale = part.Scale, Item = item, Local = item.Parts[i].Local,
                    Position = V(item.Transform.Compose(item.Parts[i].Local).Position),
                });
    }

    private static (string Name, float Scale)? VfxOf(TerryEntityData? e)
    {
        if (e?.Component("ECVFX") is not { } c || c["vfx"] is not { Length: > 0 } name) return null;
        var scale = float.TryParse(c["scale"], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
        return (name, scale);
    }

    /// <summary>The resolved effect, or null while it resolves (in the background, with its textures) or when it draws nothing.</summary>
    private VfxEffect? EffectFor(string name)
    {
        if (_vfxEffects.TryGetValue(name, out var known)) return known;
        if (!_vfxEffects.TryAdd(name, null)) return null;
        var lib = _model!.Models;
        var vfx = _vfxLibrary ??= new VfxLibrary(lib.Source.TryRead);
        var r = _r!;
        Interlocked.Increment(ref _vfxResolving);
        _ = Task.Run(async () =>
        {
            await _loadSlots.WaitAsync();
            try
            {
                var effect = vfx.Resolve(name);
                foreach (var e in effect.Drawn)
                    if (e.Texture is { } t) TextureFor(lib, r, t);
                if (effect.Drawn.Any()) _vfxEffects[name] = effect;
                _ = Dispatcher.BeginInvoke(Invalidate);
            }
            catch (Exception) { /* stays a marker */ }
            finally
            {
                _loadSlots.Release();
                Interlocked.Decrement(ref _vfxResolving);
            }
        });
        return null;
    }

    /// <summary>Simulates the effects near the camera by the time since the last frame and draws their particles.</summary>
    private void DrawVfx(Renderer3D r, Frustum frustum, Vector3 eye)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var dt = Math.Min(_vfxPendingTime, 0.25f);
        _vfxPendingTime = 0;
        if (!ShowVfx || _model is null)
        {
            _vfxLive = false;
            VfxStats = "";
            return;
        }

        // The nearest placed effects inside their cull range and the view.
        var near = new List<(float Distance, VfxPlacement P, VfxEffect E)>();
        foreach (var p in _vfxPlacements)
        {
            var d = Vector3.Distance(eye, p.Position);
            if (d > 2000) continue;
            if ((p.Effect ??= EffectFor(p.Name)) is not { } effect || d > effect.CullDistance) continue;
            if (!frustum.Sphere(p.Position, Math.Max(2, 10 * p.Scale * MaxScale(p.Item.Transform)))) continue;
            near.Add((d, p, effect));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (near.Count > MaxRunningEffects) near.RemoveRange(MaxRunningEffects, near.Count - MaxRunningEffects);

        // Run them; start the new ones warmed up.
        var started = 0;
        var startMs = 0.0;
        var view = new VfxView(eye, _cam.Right, Vector3.Normalize(Vector3.Cross(_cam.Forward, _cam.Right)), _cam.Forward);
        foreach (var (_, p, effect) in near)
        {
            var world = WorldOf(p);
            if (!_vfxRunning.TryGetValue(p.Key, out var running))
            {
                if (started >= MaxStartsPerFrame) continue;
                started++;
                var t0 = clock.Elapsed.TotalMilliseconds;
                var instance = new VfxInstance(effect, world, p.Key.GetHashCode(StringComparison.Ordinal));
                instance.Warm();
                startMs += clock.Elapsed.TotalMilliseconds - t0;
                _vfxRunning[p.Key] = running = new RunningVfx(instance);
            }
            else
            {
                if (running.Instance.World != world) running.Instance.Place(world);
                running.Instance.Advance(dt);
            }
            running.LastFrame = _frame;
        }
        // Effects out of range for a while are dropped (about 3 s at 60 fps).
        foreach (var key in _vfxRunning.Where(kv => _frame - kv.Value.LastFrame > 180).Select(kv => kv.Key).ToList())
            _vfxRunning.Remove(key);

        // Far to near, a draw per emitter with its texture.
        _vfxVertices.Clear();
        var ranges = new List<(GpuTexture, bool, int, int)>();
        var particles = 0;
        for (var n = near.Count - 1; n >= 0; n--)
        {
            if (!_vfxRunning.TryGetValue(near[n].P.Key, out var running) || running.LastFrame != _frame) continue;
            var instance = running.Instance;
            particles += instance.ParticleCount;
            for (var i = 0; i < instance.EmitterCount; i++)
            {
                var spec = instance.Spec(i);
                if (spec.Texture is null || !_gpuTextures.TryGetValue(spec.Texture, out var texture) || texture is null) continue;
                var start = _vfxVertices.Count;
                if (instance.Write(i, view, _vfxVertices)) ranges.Add((texture, spec.Premultiplied, start, _vfxVertices.Count - start));
            }
        }
        r.DrawParticles(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vfxVertices), ranges);
        _vfxLive = near.Count > 0;
        VfxStats = near.Count > 0 ? $"vfx {near.Count} effects, {particles} particles, {ranges.Count} draws, {clock.Elapsed.TotalMilliseconds:F1} ms ({started} started, {startMs:F1} ms of it)" : "";
    }

    private Matrix4x4 WorldOf(VfxPlacement p)
    {
        var tr = TransformOf(p.Item);
        var placed = p.Local is { } local ? tr.Compose(local) : tr;
        if (_overrides.ContainsKey(p.Item.Id)) p.Position = V(placed.Position);
        return Matrix4x4.CreateScale(p.Scale) * World(placed);
    }
}
