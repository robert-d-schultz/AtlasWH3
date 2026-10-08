namespace AtlasWH3.Core.Editing;

/// <summary>Axis-aligned dirty rectangle in lf map pixels (inclusive).</summary>
public readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
{
    public static readonly PixelRect Empty = new(0, 0, -1, -1);
    public bool IsEmpty => X1 < X0 || Y1 < Y0;

    public PixelRect Union(PixelRect o) =>
        IsEmpty ? o : o.IsEmpty ? this
            : new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
}

/// <summary>
/// A terrain brush working on the lf grid (7136x5620 for vanilla). A stroke is Begin → Dab* → End;
/// End returns the undo record for the whole stroke.
/// </summary>
public abstract class TerrainBrush
{
    /// <summary>Radius in lf map pixels.</summary>
    public double Radius { get; set; } = 40;
    /// <summary>0..1.</summary>
    public double Strength { get; set; } = 0.5;
    /// <summary>0 = hard edge, 1 = fully smooth falloff.</summary>
    public double Softness { get; set; } = 0.7;

    public abstract string Name { get; }

    protected TerrainData Terrain { get; private set; } = null!;

    public virtual void Begin(TerrainData terrain, double mx, double my) => Terrain = terrain;
    public abstract PixelRect Dab(double mx, double my);
    public abstract IUndoable? End();

    /// <summary>Weight 0..1 of a pixel at distance d from the centre.</summary>
    protected double Falloff(double d)
    {
        if (d >= Radius) return 0;
        var hardCore = 1 - Softness;
        var t = d / Radius;
        if (t <= hardCore) return 1;
        var s = (t - hardCore) / Math.Max(1e-6, Softness);
        return 0.5 * (1 + Math.Cos(Math.PI * s));        // cosine falloff
    }

    protected PixelRect Bounds(double mx, double my, int width, int height)
    {
        var r = (int)Math.Ceiling(Radius);
        return new PixelRect(Math.Max(0, (int)mx - r), Math.Max(0, (int)my - r),
                             Math.Min(width - 1, (int)mx + r), Math.Min(height - 1, (int)my + r));
    }
}

public enum HeightMode { Raise, Lower, Smooth, Flatten, Noise }

public sealed class HeightBrush : TerrainBrush
{
    /// <summary>Maximum change per dab at full strength, in u16 height steps (1000 ≈ 0.22 world units).</summary>
    public double MaxStepPerDab { get; set; } = 600;
    public HeightMode Mode { get; set; } = HeightMode.Raise;

    private RasterStroke<ushort>? _stroke;
    private double _flattenTarget;
    private readonly Random _random = new(1234);

    public override string Name => $"Height: {Mode}";

    public override void Begin(TerrainData terrain, double mx, double my)
    {
        base.Begin(terrain, mx, my);
        _stroke = new RasterStroke<ushort>(terrain.Height, Name);
        _flattenTarget = terrain.Height.GetClamped((int)mx, (int)my);
    }

    public override PixelRect Dab(double mx, double my)
    {
        var h = Terrain.Height;
        var rect = Bounds(mx, my, h.Width, h.Height);
        if (rect.IsEmpty) return rect;
        _stroke!.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);

        // Smooth reads from a snapshot of the brush area (+ blur margin) so results don't cascade.
        const int blur = 4;
        ushort[]? source = null;
        int sx0 = 0, sy0 = 0, sw = 0;
        if (Mode == HeightMode.Smooth)
        {
            sx0 = Math.Max(0, rect.X0 - blur); sy0 = Math.Max(0, rect.Y0 - blur);
            var sx1 = Math.Min(h.Width - 1, rect.X1 + blur);
            var sy1 = Math.Min(h.Height - 1, rect.Y1 + blur);
            sw = sx1 - sx0 + 1;
            source = new ushort[sw * (sy1 - sy0 + 1)];
            for (var y = sy0; y <= sy1; y++)
                Array.Copy(h.Data, y * h.Width + sx0, source, (y - sy0) * sw, sw);
        }
        var sh = source == null ? 0 : source.Length / sw;

        for (var y = rect.Y0; y <= rect.Y1; y++)
        for (var x = rect.X0; x <= rect.X1; x++)
        {
            var w = Falloff(Math.Sqrt((x - mx) * (x - mx) + (y - my) * (y - my))) * Strength;
            if (w <= 0) continue;
            ref var cell = ref h[x, y];
            double v = cell;
            switch (Mode)
            {
                case HeightMode.Raise: v += MaxStepPerDab * w; break;
                case HeightMode.Lower: v -= MaxStepPerDab * w; break;
                case HeightMode.Flatten: v += (_flattenTarget - v) * w * 0.5; break;
                case HeightMode.Noise: v += (_random.NextDouble() * 2 - 1) * MaxStepPerDab * w; break;
                case HeightMode.Smooth:
                {
                    double sum = 0; var n = 0;
                    for (var dy = -blur; dy <= blur; dy += 2)
                    for (var dx = -blur; dx <= blur; dx += 2)
                    {
                        var sx = Math.Clamp(x + dx - sx0, 0, sw - 1);
                        var sy = Math.Clamp(y + dy - sy0, 0, sh - 1);
                        sum += source![sy * sw + sx]; n++;
                    }
                    v += (sum / n - v) * w;
                    break;
                }
            }
            cell = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return rect;
    }

    public override IUndoable? End()
    {
        var stroke = _stroke;
        _stroke = null;
        if (stroke == null || stroke.IsEmpty) return null;
        stroke.Finish();
        return stroke;
    }
}

/// <summary>Paints a texture group into global_blend channel 0.</summary>
public sealed class BlendBrush : TerrainBrush
{
    public byte Group { get; set; }
    /// <summary>When set, only pixels currently of this group are repainted.</summary>
    public byte? OnlyReplace { get; set; }

    private RasterStroke<byte>? _stroke;
    private readonly Random _random = new(99);

    public override string Name => $"Paint texture {Group}";

    public override void Begin(TerrainData terrain, double mx, double my)
    {
        base.Begin(terrain, mx, my);
        _stroke = new RasterStroke<byte>(terrain.BlendGroup, Name);
    }

    public override PixelRect Dab(double mx, double my)
    {
        var g = Terrain.BlendGroup;
        var rect = Bounds(mx, my, g.Width, g.Height);
        if (rect.IsEmpty) return rect;
        _stroke!.Capture(rect.X0, rect.Y0, rect.X1, rect.Y1);
        for (var y = rect.Y0; y <= rect.Y1; y++)
        for (var x = rect.X0; x <= rect.X1; x++)
        {
            // Blend is an index map, so soft edges are dithered rather than mixed.
            var w = Falloff(Math.Sqrt((x - mx) * (x - mx) + (y - my) * (y - my)));
            if (w <= 0 || _random.NextDouble() > w * Math.Max(Strength, 0.05) * 2) continue;
            ref var cell = ref g[x, y];
            if (OnlyReplace is { } only && cell != only) continue;
            cell = Group;
        }
        return rect;
    }

    public override IUndoable? End()
    {
        var stroke = _stroke;
        _stroke = null;
        if (stroke == null || stroke.IsEmpty) return null;
        stroke.Finish();
        return stroke;
    }
}
