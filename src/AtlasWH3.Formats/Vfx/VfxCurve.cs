using System.Globalization;

namespace AtlasWH3.Formats.Vfx;

/// <summary>
/// A curve-valued property of a vfx/*.xml file, as CA's VFX editor writes it: <c>channels:outputs</c>.
/// <list type="bullet">
/// <item>channels, '|'-separated: <c>name,colour,flag;key;key…</c>; a key is <c>time,value,in,out,inX,inY,outX,outY,flag</c>.
/// Keys are not always written in time order (the torch fire's alpha is 0, 1, 0.25), so they are sorted.</item>
/// <item>outputs, '|'-separated: <c>name,random;min,max</c>, the indices of the two channels the output lies between.
/// random 1 picks a point between them per particle, random 0 takes the min channel.</item>
/// </list>
/// A segment eases by its left key's out-interpolation. The ease shapes are readings of the names, not CA's code.
/// </summary>
public sealed class VfxCurve
{
    public readonly record struct Key(float Time, float Value, string Out);
    public sealed record Output(string Name, bool Random, Key[] Min, Key[] Max);

    public IReadOnlyList<Output> Outputs { get; }

    private VfxCurve(IReadOnlyList<Output> outputs) => Outputs = outputs;

    /// <summary>A curve of constant outputs (tests, defaults).</summary>
    public static VfxCurve Constant(params float[] values) =>
        new(values.Select((v, i) => new Output("c" + i, false, [new Key(0, v, "linear")], [new Key(0, v, "linear")])).ToArray());

    /// <summary>Null when the text is empty or does not parse.</summary>
    public static VfxCurve? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var colon = text.LastIndexOf(':');
        if (colon <= 0) return null;
        try
        {
            var channels = text[..colon].Split('|').Select(Channel).ToArray();
            var outputs = new List<Output>();
            foreach (var mapping in text[(colon + 1)..].Split('|'))
            {
                var semi = mapping.Split(';');
                var head = semi[0].Split(',');
                var range = semi[1].Split(',');
                outputs.Add(new Output(head[0].Trim(), head.Length > 1 && head[1].Trim() == "1",
                    channels[int.Parse(range[0], CultureInfo.InvariantCulture)], channels[int.Parse(range[1], CultureInfo.InvariantCulture)]));
            }
            return new VfxCurve(outputs);
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    private static Key[] Channel(string channel)
    {
        var keys = new List<Key>();
        foreach (var key in channel.Split(';').Skip(1))
        {
            var f = key.Split(',');
            if (f.Length < 4) throw new FormatException("a curve key has fewer than 4 fields");
            var time = float.Parse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            var value = float.Parse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!float.IsFinite(time) || !float.IsFinite(value)) throw new FormatException("a curve key is not finite");
            keys.Add(new Key(time, value, f[3].Trim()));
        }
        return keys.OrderBy(k => k.Time).ToArray();
    }

    /// <summary>Output <paramref name="index"/> at <paramref name="t"/>; <paramref name="random"/> (0..1) places a random
    /// output between its channels. <paramref name="fallback"/> when the curve has no such output.</summary>
    public float Evaluate(int index, float t, float random = 0, float fallback = 0)
    {
        if (index >= Outputs.Count) return fallback;
        var o = Outputs[index];
        var min = Sample(o.Min, t);
        return o.Random ? min + (Sample(o.Max, t) - min) * random : min;
    }

    /// <summary>The largest value any key of any output's channels holds (used to bound particle lives).</summary>
    public float MaxValue(int index) =>
        index < Outputs.Count ? Outputs[index].Min.Concat(Outputs[index].Max).Select(k => k.Value).DefaultIfEmpty(0).Max() : 0;

    public static float Sample(Key[] keys, float t)
    {
        if (keys.Length == 0) return 0;
        if (t <= keys[0].Time) return keys[0].Value;
        for (var i = 0; i < keys.Length - 1; i++)
        {
            var (a, b) = (keys[i], keys[i + 1]);
            if (t >= b.Time) continue;
            var span = b.Time - a.Time;
            var u = span > 0 ? (t - a.Time) / span : 1;
            return a.Value + (b.Value - a.Value) * Ease(a.Out, u);
        }
        return keys[^1].Value;
    }

    /// <summary>The share of a segment covered at <paramref name="u"/> (0..1) for an interpolation name. logN rises fast and
    /// settles, expN starts slow, sine and s ease both ends, sine_recip is sine's inverse, step holds the left key.
    /// Unknown names (the bezier ones included) are linear.</summary>
    public static float Ease(string name, float u) => name switch
    {
        "step" => 0,
        "step_next" => 1,
        "sine" => (1 - MathF.Cos(MathF.PI * u)) / 2,
        "sine_recip" => MathF.Acos(Math.Clamp(1 - 2 * u, -1, 1)) / MathF.PI,
        "s" => u * u * (3 - 2 * u),
        "log1" => 1 - (1 - u) * (1 - u),
        "log3" => 1 - MathF.Pow(1 - u, 4),
        "exp1" => u * u,
        "exp3" => MathF.Pow(u, 4),
        _ => u,
    };
}
