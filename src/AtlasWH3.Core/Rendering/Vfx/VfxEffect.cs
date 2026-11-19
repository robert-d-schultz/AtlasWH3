using System.Collections.Concurrent;
using System.Numerics;
using AtlasWH3.Formats.Vfx;

namespace AtlasWH3.Core.Rendering.Vfx;

/// <summary>How a particle quad turns: toward the camera, standing up (about the effect's Y), lying flat (the effect's
/// XZ plane), or long along its velocity.</summary>
public enum VfxFacing { Camera, Upright, Flat, Velocity }

/// <summary>
/// One emitter of a resolved effect, read into what <see cref="VfxInstance"/> simulates and draws. Curves are over the
/// emitter's life (rate, the spawn area, the initial values) or the particle's (scale, alpha, colour, drag, forces).
/// Lengths are in the emitter's own units; <see cref="Local"/> places them in the effect.
/// </summary>
public sealed class VfxEmitterSpec
{
    public required string Name { get; init; }
    public bool Draws { get; set; } = true;
    public List<string> Skipped { get; } = [];

    // Placement and timing
    public Matrix4x4 Local { get; set; } = Matrix4x4.Identity;
    public bool Infinite { get; set; }
    public Vector2 StartTime { get; set; }
    public Vector2 LifeTime { get; set; } = Vector2.One;
    public float Speed { get; set; } = 1;
    public VfxCurve? EmitterPosition { get; set; }
    public VfxCurve? EmitterOrientation { get; set; }
    public bool LoopEmitter { get; set; }

    // Spawning
    public VfxCurve? Rate { get; set; }
    public bool LoopRate { get; set; }
    public Vector2 RateScale { get; set; } = Vector2.One;
    /// <summary>BurstInterval and BurstIntervalLength ranges, seconds: every interval the emitter spawns for the length.</summary>
    public (Vector2 Interval, Vector2 Length)? Burst { get; set; }
    public int MaxParticles { get; set; } = VfxInstance.ParticleCap;
    public VfxCurve? SpawnBox { get; set; }
    public VfxCurve? SpawnRing { get; set; }
    public Vector2 SpawnArc { get; set; }
    public Vector2 Azimuth { get; set; } = new(0, 360);
    public Vector2 Elevation { get; set; } = new(90, 90);
    public VfxCurve? InitialSpeed { get; set; }
    public VfxCurve? InitialScale { get; set; }
    public VfxCurve? InitialAlpha { get; set; }
    public VfxCurve? ParticleLife { get; set; }
    public bool LoopInitial { get; set; }
    public Vector2 LifeBias { get; set; }
    public Vector4 TintMin { get; set; } = Vector4.One;
    public Vector4 TintMax { get; set; } = Vector4.One;
    public Vector2 Angle { get; set; }           // degrees about the view axis, min/max
    public Vector2 Spin { get; set; }            // degrees per second, min/max
    public float SpinSign { get; set; } = 1;

    // Over a particle's life
    public VfxCurve? Scale { get; set; }
    public VfxCurve? Drag { get; set; }
    public VfxCurve?[] Forces { get; set; } = [];
    public VfxCurve? Alpha { get; set; }
    public VfxCurve? Colour { get; set; }
    public float Emissive { get; set; } = 1;
    public VfxCurve? Stretch { get; set; }
    public float StretchCap { get; set; }

    // Look
    public string? Texture { get; set; }
    public bool Premultiplied { get; set; } = true;
    /// <summary>ps_lighting's additive_blend_mul: 0 blends by alpha, 1 adds.</summary>
    public float Additive { get; set; }
    public VfxFacing Facing { get; set; }
    public Vector2 Pivot { get; set; } = new(0.5f, 0.5f);
    public float FlipU { get; set; }
    public float FlipV { get; set; }
    public int CellsX { get; set; } = 1;
    public int CellsY { get; set; } = 1;
    public bool Animated { get; set; }
    public bool AnimationLoops { get; set; } = true;
    public float Loops { get; set; } = 1;
    public bool FixedRate { get; set; }
    public bool RandomCell { get; set; }
    public int Cell { get; set; }
    /// <summary>The atlas region the cells divide: atlas_number_of_cells_x/y and atlas_cell.</summary>
    public int AtlasX { get; set; } = 1;
    public int AtlasY { get; set; } = 1;
    public int AtlasCell { get; set; }

    /// <summary>CullDistances and ParticleCullDistances, world units from the camera: fade in over x..y, out over z..w.</summary>
    public Vector4 EmitterCull { get; set; } = new(0, 0, float.MaxValue, float.MaxValue);
    public Vector4 ParticleCull { get; set; } = new(0, 0, float.MaxValue, float.MaxValue);

    /// <summary>The longest a particle lives, seconds (from the life curve's keys and the bias).</summary>
    public float MaxParticleLife => Math.Max(0.05f, (ParticleLife?.MaxValue(0) ?? 1) + Math.Max(LifeBias.X, LifeBias.Y));
}

/// <summary>An effect with its references followed: every enabled emitter, placed in the effect, and what could not be read.</summary>
public sealed record VfxEffect(string Name, IReadOnlyList<VfxEmitterSpec> Emitters, IReadOnlyList<string> Problems)
{
    public bool Found => Emitters.Count > 0 || Problems.Count == 0;
    public IEnumerable<VfxEmitterSpec> Drawn => Emitters.Where(e => e.Draws);

    /// <summary>Beyond this distance from the camera (world units) none of its emitters draws. Stored: the viewport asks it of
    /// every placed effect every frame.</summary>
    public float CullDistance { get; } = Emitters.Where(e => e.Draws).Select(e => Math.Min(e.EmitterCull.W, e.ParticleCull.W)).DefaultIfEmpty(0).Max();
}

/// <summary>
/// Reads vfx/&lt;name&gt;.xml through <paramref name="read"/> and resolves effects: a reference places the referenced effect
/// (scale, then rotation, then position) and lists the emitters it plays. A reference that lists emitters plays only
/// those it enables (CA's editor writes every emitter of the library into it, so one the library gained later is unlisted
/// and silent); an outer reference's override wins over an inner one. Files and resolved effects are cached; thread-safe.
/// </summary>
public sealed class VfxLibrary(Func<string, byte[]?> read)
{
    private readonly ConcurrentDictionary<string, Lazy<VfxDocument?>> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<VfxEffect>> _effects = new(StringComparer.OrdinalIgnoreCase);

    public static string PathOf(string name)
    {
        var n = name.Trim().Replace('\\', '/');
        if (!n.StartsWith("vfx/", StringComparison.OrdinalIgnoreCase)) n = "vfx/" + n;
        return n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? n : n + ".xml";
    }

    public VfxEffect Resolve(string name) => _effects.GetOrAdd(name.Trim(), n => new Lazy<VfxEffect>(() => Build(n))).Value;

    private VfxDocument? Document(string name, List<string> problems)
    {
        var doc = _documents.GetOrAdd(PathOf(name), p => new Lazy<VfxDocument?>(() =>
        {
            try { return read(p) is { } bytes ? VfxDocument.Parse(bytes) : null; }
            catch (Exception) { return null; }
        })).Value;
        if (doc is null) problems.Add($"{PathOf(name)} is not in the packs or does not parse");
        return doc;
    }

    private VfxEffect Build(string name)
    {
        var problems = new List<string>();
        var emitters = new List<VfxEmitterSpec>();
        if (Document(name, problems) is { } doc)
            Collect(name, doc, Matrix4x4.Identity, Vector2.Zero, 1, true, new Dictionary<string, VfxEmitterOverride>(), false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PathOf(name) }, emitters, problems);
        return new VfxEffect(name, emitters, problems);
    }

    private void Collect(string name, VfxDocument doc, Matrix4x4 placed, Vector2 start, float speed, bool infinite,
        IReadOnlyDictionary<string, VfxEmitterOverride> overrides, bool listed, HashSet<string> stack, List<VfxEmitterSpec> output, List<string> problems)
    {
        foreach (var e in doc.Emitters)
        {
            var known = overrides.TryGetValue(e.Id, out var o);
            if (listed && !known) continue;
            o ??= VfxEmitterOverride.None;
            if (!o.Enable || e.Properties.GetValueOrDefault("Enable") is { IsTrue: false }) continue;
            doc.Chains.TryGetValue(e.Id, out var chain);
            doc.SceneViews.TryGetValue(e.Id, out var view);
            output.Add(VfxEmitterReader.Read(name, e, chain, view, o, placed, start, speed, infinite));
        }
        foreach (var r in doc.References)
        {
            var path = PathOf(r.Target);
            if (!stack.Add(path))
            {
                problems.Add($"{name}: reference cycle through {r.Target}, skipped");
                continue;
            }
            if (Document(r.Target, problems) is { } target)
            {
                var merged = new Dictionary<string, VfxEmitterOverride>(r.Overrides);
                foreach (var (id, outer) in overrides) merged[id] = outer;
                // One scale per reference: the middle of its range (vanilla's campaign references give min = max).
                var local = Matrix4x4.CreateScale((r.Scale.X + r.Scale.Y) / 2) * VfxEmitterReader.Rotation(r.RotationDegrees)
                            * Matrix4x4.CreateTranslation(r.Position);
                // Start times add up as ranges, drawn per placed instance: the 1,124 shroud lightning bolts IEE places each
                // strike at their own moment in 0..60 s.
                Collect(r.Target, target, local * placed, start + r.StartTime, speed * r.SpeedMultiplier,
                    infinite && r.InfiniteLife, merged, r.Overrides.Count > 0, stack, output, problems);
            }
            stack.Remove(path);
        }
    }
}
