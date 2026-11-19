using System.Numerics;
using AtlasWH3.Formats.Vfx;

namespace AtlasWH3.Core.Rendering.Vfx;

/// <summary>
/// Reads one emitter and its behaviour chain into a <see cref="VfxEmitterSpec"/>. What the viewport draws: continuous_mesh
/// emitters in the plain alpha views (FULLRES/HALFRES_DEFERRED_ALPHA, or none), as textured quads. Measured over the
/// effects IEE places (34,272 placements, references followed), those are 86% of the emitters; the rest are dynamic
/// lights and distortion (they draw no colour of their own), rigid-model particles and a few decals, listed in
/// <see cref="VfxEmitterSpec.Skipped"/>.
/// </summary>
public static class VfxEmitterReader
{
    /// <summary>ForceA/B/C are cm/s² while speeds and sizes are metres: the common downward forces in vanilla's vfx are
    /// -500, -490.5, -98.1 and -981 (g), against metre-scale speeds (the torch fire: 0.15–0.6).</summary>
    public const float ForceScale = 0.01f;

    /// <summary>A rotation of X, then Y, then Z degrees (references and EmitterOrientation).</summary>
    public static Matrix4x4 Rotation(Vector3 degrees) =>
        Matrix4x4.CreateRotationX(degrees.X * MathF.PI / 180) * Matrix4x4.CreateRotationY(degrees.Y * MathF.PI / 180)
        * Matrix4x4.CreateRotationZ(degrees.Z * MathF.PI / 180);

    public static VfxEmitterSpec Read(string file, VfxEmitter emitter, IReadOnlyList<VfxModifier>? chain, string? view,
        VfxEmitterOverride o, Matrix4x4 placed, Vector2 start, float speed, bool infinite)
    {
        var p = emitter.Properties;
        var s = new VfxEmitterSpec { Name = file + "/" + emitter.Id, Local = placed };
        VfxProperty? P(string name) => p.GetValueOrDefault(name);
        bool Flag(string name) => P(name)?.IsTrue ?? false;
        float Number(string name, float fallback) => P(name)?.Number("value", fallback) ?? fallback;
        Vector2 Range(string name, Vector2 fallback) => P(name) is { } r ? new Vector2(r.Number("min"), r.Number("max")) : fallback;
        Vector2 Xy(string name, Vector2 fallback) => P(name) is { } r ? new Vector2(r.Number("x"), r.Number("y")) : fallback;
        Vector4 Xyzw(string name, Vector4 fallback) => P(name) is { } r ? new Vector4(r.Number("x"), r.Number("y"), r.Number("z"), r.Number("w")) : fallback;
        VfxCurve? Curve(string name)
        {
            if (P(name) is not { } prop) return null;
            var c = prop.Curve;
            if (c is null) s.Skipped.Add($"{name} does not parse");
            return c;
        }

        s.Infinite = infinite && Flag("InfiniteLife");
        s.StartTime = Range("StartTime", Vector2.Zero) + start;
        s.LifeTime = Range("LifeTime", Vector2.One) + o.EmitterLifeBias;
        s.Speed = speed * Number("SimulationSpeedMultiplier", 1);
        s.EmitterPosition = Curve("EmitterPosition");
        s.EmitterOrientation = Curve("EmitterOrientation");
        s.LoopEmitter = Flag("EmitterPositionLoopAnim");

        s.Rate = Curve("EmissionRateCurve");
        if (P("EmissionRateCurve") is null)
            s.Skipped.Add("no EmissionRateCurve: the game's default rate is not known, so it spawns nothing here");
        s.LoopRate = Flag("EmissionLoopAnim");
        s.RateScale = o.RateScale;
        if (P("BurstInterval") is not null) s.Burst = (Range("BurstInterval", Vector2.Zero), Range("BurstIntervalLength", Vector2.Zero));
        var max = (int)Number("MaximumParticles", VfxInstance.ParticleCap);
        s.MaxParticles = max > 0 ? Math.Min(max, VfxInstance.ParticleCap) : VfxInstance.ParticleCap;
        s.SpawnBox = Curve("SpawnAreaBox");
        s.SpawnRing = Curve("SpawnAreaCurve");
        s.SpawnArc = Xy("SpawnArc", Vector2.Zero);
        s.Azimuth = Xy("SpawnAngle", new Vector2(0, 360));
        s.Elevation = Range("SpawnSpread", new Vector2(90, 90));
        s.InitialSpeed = Curve("ParticleInitialSpeed");
        s.InitialScale = Curve("ParticleInitialScale");
        s.InitialAlpha = Curve("ParticleInitialAlpha");
        s.ParticleLife = Curve("ParticleLifeTime");
        s.LoopInitial = Flag("ParticleInitialParamLoopAnim");
        s.LifeBias = o.ParticleLifeBias;
        s.TintMin = o.TintMin;
        s.TintMax = o.TintMax;
        s.Angle = new Vector2(P("ParticleOrientationMin")?.Number("z") ?? 0, P("ParticleOrientationMax")?.Number("z") ?? 0);
        s.Spin = new Vector2(P("ParticleSpin_Min")?.Number("z") ?? 0, P("ParticleSpin_Max")?.Number("z") ?? 0);
        s.SpinSign = P("ParticleSpinDirectionZ")?.Value == "CW" ? -1 : 1;
        s.Scale = Curve("ParticleScale");
        s.Drag = Curve("ParticleDeceleration");
        s.Forces = [Curve("ForceA"), Curve("ForceB"), Curve("ForceC")];
        s.EmitterCull = Xyzw("CullDistances", s.EmitterCull);
        s.ParticleCull = Xyzw("ParticleCullDistances", s.ParticleCull);

        if (emitter.Type != "continuous_mesh") NotDrawn(s, $"{emitter.Type} emitters are not drawn");
        switch (view)
        {
            case null or "FULLRES_DEFERRED_ALPHA" or "HALFRES_DEFERRED_ALPHA": break;
            case "DEFERRED_DYNAMIC_LIGHT": NotDrawn(s, "a dynamic light: it lights the scene and draws nothing itself"); break;
            case "DISTORTION": NotDrawn(s, "distortion: it shifts the scene behind it and draws no colour"); break;
            default: NotDrawn(s, $"scene view {view} is not drawn"); break;
        }
        if (Number("EffectedByWindFactor", 0) != 0) s.Skipped.Add("wind is not simulated");
        if (P("VectorFieldStrId")?.Value is { Length: > 0 }) s.Skipped.Add("its vector field is not simulated");
        if (P("ParticleSimulationSpace")?.Value is { } space && space != "WORLD_SPACE")
            s.Skipped.Add($"{space}: simulated in the effect's space, the same for an effect that does not move");

        if (chain is null)
        {
            NotDrawn(s, "no behaviour chain at quality 0");
            return s;
        }
        ReadChain(s, chain);
        return s;
    }

    private static void ReadChain(VfxEmitterSpec s, IReadOnlyList<VfxModifier> chain)
    {
        VfxModifier? Find(string fragment) => chain.FirstOrDefault(m => m.Fragment == fragment);
        float Number(VfxModifier? m, string name, float fallback) => m?.Properties.GetValueOrDefault(name)?.Number("value", fallback) ?? fallback;
        bool Flag(VfxModifier? m, string name) => m?.Properties.GetValueOrDefault(name)?.IsTrue ?? false;

        var texture = Find("ps_texture");
        s.Texture = texture?.Textures.GetValueOrDefault("diffuse") is { Length: > 0 } path ? path.Replace('\\', '/') : null;
        if (s.Texture is null && s.Draws)
            NotDrawn(s, Find("ps_custom_shader") is not null ? "it draws through a custom shader material" : "its chain names no diffuse texture");
        s.Premultiplied = texture?.Properties.GetValueOrDefault("pre_multiplied_alpha")?.IsTrue ?? true;
        s.Additive = Number(Find("ps_lighting"), "additive_blend_mul", 0);
        s.Emissive = Number(Find("ps_emissive"), "emissive_mul", 1);
        s.Alpha = Find("vs_vertex_alpha")?.Properties.GetValueOrDefault("curve_alpha")?.Curve;
        s.Colour = Find("vs_colour")?.Properties.GetValueOrDefault("curve_colour")?.Curve;
        var flip = Find("vs_flip_uv");
        s.FlipU = Number(flip, "flip_u", 0);
        s.FlipV = Number(flip, "flip_v", 0);

        var velocity = Find("vs_translation_velocity_v2");
        var translation = velocity ?? Find("vs_translation");
        s.Pivot = new Vector2(Number(translation, "pivot_x", 0.5f), Number(translation, "pivot_y", 0.5f));
        if (velocity is not null)
        {
            s.Stretch = velocity.Properties.GetValueOrDefault("velocity_stretch_mul")?.Curve;
            s.StretchCap = Number(velocity, "velocity_stretch_cap", 0);
            var mode = velocity.Properties.GetValueOrDefault("orientation_mode")?.Value;
            s.Facing = mode switch
            {
                null => VfxFacing.Camera,
                "Camera-aligned with stretching factor" or "Camera-facing velocity-aligned" => VfxFacing.Velocity,
                "Horizontally-oriented" => VfxFacing.Flat,
                "Vertically-oriented" => VfxFacing.Upright,
                _ => VfxFacing.Camera,
            };
            if (mode is not (null or "Camera-aligned with stretching factor" or "Camera-facing velocity-aligned" or "Horizontally-oriented" or "Vertically-oriented"))
                s.Skipped.Add($"orientation \"{mode}\" is drawn facing the camera");
        }
        else
            // vs_translation's restriction: 0 faces the camera, 1 stands up, 2 lies flat, 3 is a 3D-oriented quad.
            s.Facing = (int)Number(translation, "restriction", 0) switch { 1 => VfxFacing.Upright, 2 => VfxFacing.Flat, 3 => VfxFacing.Flat, _ => VfxFacing.Camera };

        var animated = Find("vs_animated_texture");
        var cells = Find("vs_cell_texture");
        var atlas = animated ?? cells;
        s.AtlasX = Math.Max(1, (int)Number(atlas, "atlas_number_of_cells_x", 1));
        s.AtlasY = Math.Max(1, (int)Number(atlas, "atlas_number_of_cells_y", 1));
        s.AtlasCell = (int)Number(atlas, "atlas_cell", 0);
        if (animated is not null)
        {
            var count = animated.Properties.GetValueOrDefault("no_cells_xy")?.Float2(Vector2.One) ?? Vector2.One;
            (s.CellsX, s.CellsY) = (Math.Max(1, (int)count.X), Math.Max(1, (int)count.Y));
            s.Animated = true;
            s.AnimationLoops = Flag(animated, "loop");
            s.Loops = (animated.Properties.GetValueOrDefault("no_loops")?.Float2(Vector2.One) ?? Vector2.One).X;
            s.FixedRate = animated.Properties.GetValueOrDefault("time_mode")?.Value == "fixed_animation_speed";
            s.RandomCell = Flag(animated, "randomize_cell");
        }
        else if (cells is not null)
        {
            s.CellsX = Math.Max(1, (int)Number(cells, "number_of_cells_x", 1));
            s.CellsY = Math.Max(1, (int)Number(cells, "number_of_cells_y", 1));
            s.Cell = (int)Number(cells, "cell", 0);
            s.RandomCell = Flag(cells, "randomize_cell");
        }
        foreach (var f in new[] { "ps_soft_edge", "ps_alpha_crush", "ps_gradient", "ps_normal" })
            if (Find(f) is not null) s.Skipped.Add($"{f} is not drawn");
    }

    private static void NotDrawn(VfxEmitterSpec s, string why)
    {
        s.Draws = false;
        s.Skipped.Add(why);
    }
}
