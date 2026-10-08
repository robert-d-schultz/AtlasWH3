using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

/// <summary>How <see cref="GroundClamp"/> seats a prop.</summary>
public enum ClampMode
{
    /// <summary>The model's lowest point (scale · bounds min y) on the ground: nothing floats, nothing is buried.</summary>
    Base,
    /// <summary>The prop's origin on the ground (y = ground).</summary>
    Origin,
    /// <summary>The model's typical depth on vanilla (sink · scale under the ground, for big mountain / rock meshes that
    /// are buried so only the top shows); models without a known sink fall back to <see cref="Base"/>.</summary>
    VanillaSink,
}

/// <summary>One prop to seat: world x, stored y, z, its y scale and the model facts (bounds min y; the LF-offset share,
/// 0 for ordinary props: LF-offset mountains store y relative to the terrain, the shader adds share × lf).</summary>
public sealed record ClampProp(string Id, string Model, double X, double Y, double Z, double ScaleY, double MinY, double TerrainOffset);

/// <summary>A planned move: the prop's stored y before and after, and the rule that set it.</summary>
public sealed record ClampMove(string Id, double OldY, double NewY, string How)
{
    public double Delta => NewY - OldY;
}

/// <summary>
/// Clamp-to-ground math for props, independent of where the ground comes from (<see cref="GroundHeight"/>).
/// A prop's model base is at y + scaleY · minY − (1 − share) · ground above the terrain (share = LF-offset part the
/// shader adds, 0 for ordinary props), as <see cref="AtlasWH3.Core.Audit.MapAudit"/> measures it.
/// </summary>
public static class GroundClamp
{
    /// <summary>Normalised model key (forward slashes, lower case) for sink tables.</summary>
    public static string Key(string model) => model.Replace('\\', '/').Trim().ToLowerInvariant();

    /// <summary>Mountain and rock meshes: the ones vanilla buries by a model-specific depth.</summary>
    public static bool IsMountainOrRock(string model)
    {
        var k = Key(model);
        return k.Contains("/mountains/") || k.Contains("/rocks/");
    }

    /// <summary>The part of the ground the stored y must include (the shader adds the rest for LF-offset models).</summary>
    private static double Rest(ClampProp p, double ground) => (1 - p.TerrainOffset) * ground;

    /// <summary>Ground under each prop (<see cref="GroundHeight.For"/>) from a plain height function.</summary>
    public static Func<ClampProp, double> Under(Func<double, double, double> ground) => p => ground(p.X, p.Z);

    /// <summary>How far the model's base is above the ground (negative: below it).</summary>
    public static double BaseAboveGround(ClampProp p, Func<ClampProp, double> ground) => p.Y + p.ScaleY * p.MinY - Rest(p, ground(p));

    public static double BaseAboveGround(ClampProp p, Func<double, double, double> ground) => BaseAboveGround(p, Under(ground));

    /// <summary>How far the prop's lowest point, its origin or its model base, is above the ground: a prop is floating
    /// when this is clearly positive (a tree's roots below its origin don't hide a lifted origin).</summary>
    public static double LowestAboveGround(ClampProp p, Func<ClampProp, double> ground) =>
        p.Y + Math.Min(0, p.ScaleY * p.MinY) - Rest(p, ground(p));

    public static double LowestAboveGround(ClampProp p, Func<double, double, double> ground) => LowestAboveGround(p, Under(ground));

    /// <summary>The stored y that seats the prop, and which rule chose it ("base", "origin", "sink").</summary>
    public static (double Y, string How) Target(ClampProp p, Func<ClampProp, double> ground, ClampMode mode, double offset = 0,
                                               IReadOnlyDictionary<string, double>? sink = null)
    {
        var rest = Rest(p, ground(p));
        if (mode == ClampMode.VanillaSink && sink is not null && sink.TryGetValue(Key(p.Model), out var s))
            return (rest + s * p.ScaleY + offset, "sink");
        if (mode == ClampMode.Origin) return (rest + offset, "origin");
        return (rest - p.ScaleY * p.MinY + offset, "base");
    }

    public static (double Y, string How) Target(ClampProp p, Func<double, double, double> ground, ClampMode mode, double offset = 0,
                                               IReadOnlyDictionary<string, double>? sink = null) => Target(p, Under(ground), mode, offset, sink);

    /// <summary>
    /// Moves for <paramref name="props"/> (those already within <paramref name="tolerance"/> are left out).
    /// <paramref name="onlyDown"/> leaves props that would have to rise (buried ones) alone.
    /// </summary>
    public static List<ClampMove> Plan(IEnumerable<ClampProp> props, Func<ClampProp, double> ground, ClampMode mode, double offset = 0,
                                       IReadOnlyDictionary<string, double>? sink = null, bool onlyDown = false, double tolerance = 1e-4)
    {
        var moves = new List<ClampMove>();
        foreach (var p in props)
        {
            var (y, how) = Target(p, ground, mode, offset, sink);
            if (Math.Abs(y - p.Y) <= tolerance || (onlyDown && y > p.Y)) continue;
            moves.Add(new ClampMove(p.Id, p.Y, y, how));
        }
        return moves;
    }

    public static List<ClampMove> Plan(IEnumerable<ClampProp> props, Func<double, double, double> ground, ClampMode mode, double offset = 0,
                                       IReadOnlyDictionary<string, double>? sink = null, bool onlyDown = false, double tolerance = 1e-4) =>
        Plan(props, Under(ground), mode, offset, sink, onlyDown, tolerance);

    /// <summary>Props whose lowest point (origin or model base) is more than <paramref name="threshold"/> above the ground.</summary>
    public static List<ClampProp> Floating(IEnumerable<ClampProp> props, Func<ClampProp, double> ground, double threshold) =>
        props.Where(p => LowestAboveGround(p, ground) > threshold).ToList();

    public static List<ClampProp> Floating(IEnumerable<ClampProp> props, Func<double, double, double> ground, double threshold) =>
        Floating(props, Under(ground), threshold);

    /// <summary>
    /// "set" ops (one per prop) writing the new y into ECTransform.position, x and z kept as stored.
    /// <paramref name="position"/> gives each prop's current position string.
    /// </summary>
    public static JsonObject[] Ops(IEnumerable<ClampMove> moves, Func<string, string?> position) =>
        moves.Select(m =>
        {
            var parts = (position(m.Id) ?? "0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var x = parts.Length == 3 ? parts[0] : "0";
            var z = parts.Length == 3 ? parts[2] : "0";
            return new JsonObject
            {
                ["op"] = "set", ["id"] = m.Id,
                ["fields"] = new JsonObject { ["ECTransform.position"] = $"{x} {LayerWriter.F(m.NewY)} {z}" },
            };
        }).ToArray();

    /// <summary>
    /// Per-model sink (median of (y − (1 − share) · ground) / scaleY) learnt from placed props of mountain / rock
    /// models with at least <paramref name="minSamples"/> instances; on a map built from vanilla this is vanilla's
    /// own depth (research/main190/vanilla_sink.py does the same on the original 190E layers).
    /// </summary>
    public static Dictionary<string, double> LearnSink(IEnumerable<ClampProp> props, Func<double, double, double> ground, int minSamples = 3) =>
        LearnSink(props, Under(ground), minSamples);

    public static Dictionary<string, double> LearnSink(IEnumerable<ClampProp> props, Func<ClampProp, double> ground, int minSamples = 3)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var g in props.Where(p => IsMountainOrRock(p.Model) && Math.Abs(p.ScaleY) > 1e-6).GroupBy(p => Key(p.Model)))
        {
            var v = g.Select(p => (p.Y - Rest(p, ground(p))) / p.ScaleY).OrderBy(d => d).ToList();
            if (v.Count < minSamples) continue;
            result[g.Key] = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
        return result;
    }

    /// <summary>A sink table file: {"model path": depth} or vanilla_sink.py's {"model path": {"n": …, "sink": depth}}.</summary>
    public static Dictionary<string, double> ReadSinkTable(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return result;
        foreach (var (k, v) in o)
        {
            var depth = v switch
            {
                JsonValue n when n.TryGetValue<double>(out var d) => d,
                JsonObject e when e["sink"] is JsonValue s && s.TryGetValue<double>(out var d) => d,
                _ => (double?)null,
            };
            if (depth is { } x) result[Key(k)] = x;
        }
        return result;
    }

    /// <summary>The sink table as JSON ({"model": depth}), sorted by model.</summary>
    public static string WriteSinkTable(IReadOnlyDictionary<string, double> sink) =>
        new JsonObject(sink.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Math.Round(kv.Value, 4))))
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
