using System.Numerics;
using System.Runtime.InteropServices;

namespace AtlasWH3.Core.Rendering.Vfx;

/// <summary>A particle vertex: world position, texture coordinate, and colour (rgb already times brightness and alpha;
/// a is the share of what lies behind that it covers, 0 for a purely additive particle).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleVertex(Vector3 position, Vector2 uv, Vector4 colour)
{
    public Vector3 Position = position;
    public Vector2 Uv = uv;
    public Vector4 Colour = colour;
}

/// <summary>The camera a frame's quads face: its eye and its right, up and forward axes (world space).</summary>
public readonly record struct VfxView(Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Forward);

/// <summary>
/// One placed effect running: per emitter, its clock and its particles, simulated in the emitter's own space and drawn
/// through <c>Local × world</c>. An emitter whose life ends (no InfiniteLife) starts over once its particles are gone,
/// so a one-shot effect keeps showing in the editor. The simulation is this editor's reading of the format, not CA's:
/// drag takes ParticleDeceleration as a share of speed lost per second, the spawn direction is SpawnAngle (azimuth
/// about Y from +X) and SpawnSpread (elevation, 90 straight up), a quad is ParticleInitialScale × ParticleScale across.
/// </summary>
public sealed class VfxInstance
{
    /// <summary>The most particles one emitter keeps.</summary>
    public const int ParticleCap = 2000;
    private const float Restart = 0.5f;

    private struct Particle
    {
        public Vector3 Position, Velocity;
        public Vector2 Size;
        public float Age, Life, Angle, Spin, Alpha;
        public float R0, R1, R2, R3;      // per-particle randoms for curves (scale, alpha, colour, cell)
        public Vector4 Tint;
        public bool FlipU, FlipV;
    }

    private sealed class EmitterState
    {
        public required VfxEmitterSpec Spec { get; init; }
        public Matrix4x4 World;
        public float Scale;
        public float Age, Start, Life, RateScale, Accumulator;
        public float BurstStart, BurstInterval, BurstLength;   // the current burst cycle (active time it began, drawn lengths)
        public float Random;               // the emitter's random for its own curves
        public Particle[] Particles = new Particle[16];
        public int Count;
    }

    private readonly EmitterState[] _emitters;
    private readonly Random _random;

    public VfxEffect Effect { get; }
    public Matrix4x4 World { get; private set; }
    public Vector3 Origin => World.Translation;
    public int ParticleCount => _emitters.Sum(e => e.Count);
    public int EmitterCount => _emitters.Length;

    public VfxInstance(VfxEffect effect, Matrix4x4 world, int seed)
    {
        Effect = effect;
        _random = new Random(seed);
        _emitters = effect.Drawn.Select(s => new EmitterState { Spec = s }).ToArray();
        foreach (var e in _emitters) StartEmitter(e);
        Place(world);
    }

    /// <summary>Moves the effect (a gizmo drag); its particles move with it.</summary>
    public void Place(Matrix4x4 world)
    {
        World = world;
        foreach (var e in _emitters)
        {
            e.World = e.Spec.Local * world;
            e.Scale = new Vector3(e.World.M11, e.World.M12, e.World.M13).Length();
        }
    }

    private float Next() => (float)_random.NextDouble();
    private float Between(Vector2 range) => range.X + (range.Y - range.X) * Next();

    private void StartEmitter(EmitterState e)
    {
        e.Age = 0;
        e.Start = Math.Max(0, Between(e.Spec.StartTime));
        e.Life = Math.Max(0.01f, Between(e.Spec.LifeTime));
        e.RateScale = Between(e.Spec.RateScale);
        e.Random = Next();
        e.Accumulator = 0;
        e.BurstStart = 0;
        DrawBurst(e);
    }

    private void DrawBurst(EmitterState e)
    {
        if (e.Spec.Burst is not { } b) return;
        e.BurstInterval = Between(b.Interval);
        e.BurstLength = Between(b.Length);
    }

    /// <summary>Runs every emitter forward by <paramref name="seconds"/>, in steps of at most <paramref name="step"/>.</summary>
    public void Advance(float seconds, float step = 1f / 15)
    {
        while (seconds > 1e-5f)
        {
            var dt = Math.Min(seconds, step);
            seconds -= dt;
            foreach (var e in _emitters) Step(e, dt * e.Spec.Speed);
        }
    }

    /// <summary>A fresh effect shows as it would after running a while: its particles' longest life, up to 4 s, in tenths.</summary>
    public void Warm() => Advance(Math.Min(4, _emitters.Select(e => e.Spec.MaxParticleLife).DefaultIfEmpty(0).Max()), 0.1f);

    private void Step(EmitterState e, float dt)
    {
        var s = e.Spec;
        e.Age += dt;
        var active = e.Age - e.Start;
        var emitting = active >= 0 && (s.Infinite || active < e.Life);

        // Age and move the particles; drop the dead ones (swap with the last).
        for (var i = 0; i < e.Count; i++)
        {
            ref var p = ref e.Particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                e.Particles[i--] = e.Particles[--e.Count];
                continue;
            }
            var t = p.Age / p.Life;
            if (s.Drag is { } drag) p.Velocity *= MathF.Exp(-Math.Max(0, drag.Evaluate(0, t, p.R0)) * dt);
            foreach (var f in s.Forces)
                if (f is not null)
                    p.Velocity += new Vector3(f.Evaluate(0, t, p.R1), f.Evaluate(1, t, p.R1), f.Evaluate(2, t, p.R1)) * (VfxEmitterReader.ForceScale * dt);
            p.Position += p.Velocity * dt;
            p.Angle += p.Spin * dt;
        }

        if (emitting)
        {
            var u = EmitterTime(active, e.Life, s.LoopRate || s.Infinite);
            var inBurst = true;
            if (s.Burst is not null && e.BurstInterval > 0)
            {
                while (active - e.BurstStart >= e.BurstInterval)
                {
                    e.BurstStart += e.BurstInterval;
                    DrawBurst(e);
                    if (e.BurstInterval <= 0) break;
                }
                inBurst = active - e.BurstStart < e.BurstLength;
            }
            if (inBurst)
            {
                e.Accumulator += Math.Max(0, s.Rate?.Evaluate(0, u, e.Random) ?? 0) * e.RateScale * dt;
                while (e.Accumulator >= 1)
                {
                    e.Accumulator -= 1;
                    if (e.Count < s.MaxParticles) Spawn(e, active);
                }
            }
        }
        else if (!s.Infinite && active >= e.Life && e.Count == 0 && active >= e.Life + Restart)
            StartEmitter(e);
    }

    private static float EmitterTime(float age, float life, bool loop)
    {
        if (life <= 0) return 0;
        var u = age / life;
        return loop ? u - MathF.Floor(u) : Math.Min(u, 1);
    }

    private void Spawn(EmitterState e, float active)
    {
        var s = e.Spec;
        var u = EmitterTime(active, e.Life, s.LoopInitial || s.Infinite);
        var ue = EmitterTime(active, e.Life, s.LoopEmitter || s.Infinite);
        var life = (s.ParticleLife?.Evaluate(0, u, Next(), 1) ?? 1) + Between(s.LifeBias);
        if (life <= 0.001f) return;

        // Where: the emitter's offset, a point in the spawn box, a point on the spawn ring.
        var position = s.EmitterPosition is { } ep ? new Vector3(ep.Evaluate(0, ue, e.Random), ep.Evaluate(1, ue, e.Random), ep.Evaluate(2, ue, e.Random)) : Vector3.Zero;
        var local = Vector3.Zero;
        if (s.SpawnBox is { } box)
            local += new Vector3(box.Evaluate(0, u, Next()) * (Next() - 0.5f), box.Evaluate(1, u, Next()) * (Next() - 0.5f), box.Evaluate(2, u, Next()) * (Next() - 0.5f));
        if (s.SpawnRing is { } ring)
        {
            var r = Next();
            var inner = ring.Evaluate(0, u, r);
            var outer = ring.Evaluate(1, u, r);
            if (outer > 0 || inner > 0)
            {
                var (from, to) = s.SpawnArc == Vector2.Zero ? (0f, 360f) : (s.SpawnArc.X, s.SpawnArc.Y);
                var a = (from + (to - from) * Next()) * MathF.PI / 180;
                var radius = inner + (outer - inner) * MathF.Sqrt(Next());
                local += new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius);
            }
        }

        // Which way: an azimuth and an elevation, turned by the emitter's orientation.
        var azimuth = Between(s.Azimuth) * MathF.PI / 180;
        var elevation = Between(s.Elevation) * MathF.PI / 180;
        var direction = new Vector3(MathF.Cos(elevation) * MathF.Cos(azimuth), MathF.Sin(elevation), MathF.Cos(elevation) * MathF.Sin(azimuth));
        if (s.EmitterOrientation is { } eo)
        {
            var turn = VfxEmitterReader.Rotation(new Vector3(eo.Evaluate(0, ue, e.Random), eo.Evaluate(1, ue, e.Random), eo.Evaluate(2, ue, e.Random)));
            direction = Vector3.TransformNormal(direction, turn);
            local = Vector3.TransformNormal(local, turn);
        }
        var speed = s.InitialSpeed?.Evaluate(0, u, Next()) ?? 0;

        var sr = Next();
        var size = s.InitialScale is { } scale ? new Vector2(scale.Evaluate(0, u, sr, 1), scale.Evaluate(1, u, sr, 1)) : Vector2.One;
        var tr = Next();
        if (e.Count == e.Particles.Length) Array.Resize(ref e.Particles, Math.Min(e.Particles.Length * 2, ParticleCap));
        e.Particles[e.Count++] = new Particle
        {
            Position = position + local,
            Velocity = direction * speed,
            Size = size,
            Life = life,
            Angle = Between(s.Angle) * MathF.PI / 180,
            Spin = Between(s.Spin) * s.SpinSign * MathF.PI / 180,
            Alpha = s.InitialAlpha?.Evaluate(0, u, Next(), 1) ?? 1,
            R0 = Next(), R1 = Next(), R2 = Next(), R3 = Next(),
            Tint = Vector4.Lerp(s.TintMin, s.TintMax, tr),
            FlipU = Next() < s.FlipU,
            FlipV = Next() < s.FlipV,
        };
    }

    /// <summary>
    /// Writes emitter <paramref name="index"/>'s particles as quads (6 vertices each) facing <paramref name="view"/>.
    /// Returns false when the emitter is out of its cull range or has nothing to draw.
    /// </summary>
    public bool Write(int index, in VfxView view, List<ParticleVertex> output)
    {
        var e = _emitters[index];
        var s = e.Spec;
        if (e.Count == 0) return false;
        var origin = e.World.Translation;
        var emitterFade = Fade(Vector3.Distance(view.Eye, origin), s.EmitterCull);
        if (emitterFade <= 0) return false;

        var cellsX = s.CellsX;
        var cellsY = s.CellsY;
        var cellCount = cellsX * cellsY;
        var atlasW = 1f / s.AtlasX;
        var atlasH = 1f / s.AtlasY;
        var atlasU = s.AtlasCell % s.AtlasX * atlasW;
        var atlasV = s.AtlasCell / s.AtlasX % s.AtlasY * atlasH;
        var cellW = atlasW / cellsX;
        var cellH = atlasH / cellsY;
        var alphaWeight = 1 - Math.Clamp(s.Additive, 0, 1);
        var upAxis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, e.World));
        var flatRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, e.World));
        var flatUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, e.World));
        var written = false;

        for (var i = 0; i < e.Count; i++)
        {
            ref var p = ref e.Particles[i];
            var t = p.Age / p.Life;
            var centre = Vector3.Transform(p.Position, e.World);
            var fade = emitterFade * Fade(Vector3.Distance(view.Eye, centre), s.ParticleCull);
            var alpha = p.Alpha * (s.Alpha?.Evaluate(0, t, p.R1, 1) ?? 1) * p.Tint.W * fade;
            if (alpha <= 0.002f) continue;
            var w = p.Size.X * (s.Scale?.Evaluate(0, t, p.R0, 1) ?? 1) * e.Scale;
            var h = p.Size.Y * (s.Scale?.Evaluate(1, t, p.R0, 1) ?? 1) * e.Scale;
            if (w <= 0 || h <= 0) continue;

            var colour = s.Colour is { } c
                ? new Vector3(c.Evaluate(0, t, p.R2, 1), c.Evaluate(1, t, p.R2, 1), c.Evaluate(2, t, p.R2, 1)) * Math.Max(0, c.Evaluate(3, t, p.R2, 1))
                : Vector3.One;
            colour *= new Vector3(p.Tint.X, p.Tint.Y, p.Tint.Z) * s.Emissive;
            var vertexColour = new Vector4(colour * alpha, alpha * alphaWeight);

            // The quad's axes.
            Vector3 right, up;
            switch (s.Facing)
            {
                case VfxFacing.Flat:
                    (right, up) = (flatRight, flatUp);
                    Turn(ref right, ref up, p.Angle);
                    break;
                case VfxFacing.Upright:
                    up = upAxis;
                    right = Vector3.Cross(up, centre - view.Eye);
                    right = right.LengthSquared() > 1e-12f ? Vector3.Normalize(right) : view.Right;
                    break;
                case VfxFacing.Velocity:
                    var v = Vector3.TransformNormal(p.Velocity, e.World);
                    var across = v - Vector3.Dot(v, view.Forward) * view.Forward;
                    if (across.LengthSquared() < 1e-12f) goto default;
                    up = Vector3.Normalize(across);
                    right = Vector3.Normalize(Vector3.Cross(view.Forward, up));
                    var stretch = 1 + v.Length() * (s.Stretch?.Evaluate(0, t, p.R0) ?? 0);
                    h *= Math.Clamp(stretch, 1, s.StretchCap > 0 ? s.StretchCap : float.MaxValue);
                    break;
                default:
                    (right, up) = (view.Right, view.Up);
                    Turn(ref right, ref up, p.Angle);
                    break;
            }

            // The texture cell: a flipbook's frame over the particle's life (or per second), or a fixed or random cell.
            int cell;
            if (s.Animated)
            {
                var f = (s.FixedRate ? p.Age : t) * s.Loops + (s.RandomCell ? p.R3 : 0);
                f = s.AnimationLoops ? f - MathF.Floor(f) : Math.Clamp(f, 0, 0.9999f);
                cell = Math.Min((int)(f * cellCount), cellCount - 1);
            }
            else cell = s.RandomCell ? Math.Min((int)(p.R3 * cellCount), cellCount - 1) : Math.Clamp(s.Cell, 0, cellCount - 1);
            var u0 = atlasU + cell % cellsX * cellW;
            var v0 = atlasV + cell / cellsX * cellH;
            // y up on the quad, v down in the texture.
            var (uLeft, uRight) = p.FlipU ? (u0 + cellW, u0) : (u0, u0 + cellW);
            var (vBottom, vTop) = p.FlipV ? (v0, v0 + cellH) : (v0 + cellH, v0);
            var dx = right * w;
            var dy = up * h;
            var bottomLeft = centre - dx * s.Pivot.X - dy * s.Pivot.Y;
            var a = new ParticleVertex(bottomLeft, new Vector2(uLeft, vBottom), vertexColour);
            var b = new ParticleVertex(bottomLeft + dx, new Vector2(uRight, vBottom), vertexColour);
            var cc = new ParticleVertex(bottomLeft + dx + dy, new Vector2(uRight, vTop), vertexColour);
            var d = new ParticleVertex(bottomLeft + dy, new Vector2(uLeft, vTop), vertexColour);
            output.Add(a); output.Add(b); output.Add(cc);
            output.Add(a); output.Add(cc); output.Add(d);
            written = true;
        }
        return written;
    }

    /// <summary>The emitter's texture and blend, for the draw call.</summary>
    public VfxEmitterSpec Spec(int index) => _emitters[index].Spec;

    private static void Turn(ref Vector3 right, ref Vector3 up, float angle)
    {
        if (angle == 0) return;
        var (sin, cos) = MathF.SinCos(angle);
        (right, up) = (right * cos + up * sin, up * cos - right * sin);
    }

    /// <summary>Cull distances x..y fade in, z..w fade out, linearly; a band of no width is a hard edge.</summary>
    public static float Fade(float distance, Vector4 cull)
    {
        var near = cull.Y > cull.X ? Math.Clamp((distance - cull.X) / (cull.Y - cull.X), 0, 1) : distance >= cull.X ? 1 : 0;
        var far = cull.W > cull.Z ? Math.Clamp((cull.W - distance) / (cull.W - cull.Z), 0, 1) : distance <= cull.W ? 1 : 0;
        return near * far;
    }
}
