using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// Moves every entity in a Terry .layer by a world offset, editing the text in place so all other bytes
/// stay identical. Only ECTransform positions are changed (river spline points are relative to their entity).
/// Entities whose mesh is a BOB-generated map model (terrain/campaigns/&lt;map&gt;/models/..., placed at the
/// origin with world-baked vertices) are skipped: BOB regenerates those from the shifted river splines.
/// </summary>
public static partial class LayerShifter
{
    [GeneratedRegex("<entity\\b.*?</entity>", RegexOptions.Singleline)]
    private static partial Regex EntityRegex();

    [GeneratedRegex("(<ECTransform\\s+position=\")([^\"]*)(\")")]
    private static partial Regex PositionRegex();

    [GeneratedRegex("model_path=\"terrain/campaigns/[^\"]*/models/", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedModelRegex();

    public sealed record Stats(int Shifted, int SkippedGenerated);

    public static Stats ShiftFile(string path, double dx, double dz, string? outputPath = null)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var (result, stats) = Shift(text, dx, dz);
        File.WriteAllText(outputPath ?? path, result, new UTF8Encoding(false));
        return stats;
    }

    public static (string Text, Stats Stats) Shift(string text, double dx, double dz)
    {
        var shifted = 0;
        var skipped = 0;
        var result = EntityRegex().Replace(text, entity =>
        {
            if (GeneratedModelRegex().IsMatch(entity.Value))
            {
                skipped++;
                return entity.Value;
            }
            return PositionRegex().Replace(entity.Value, m =>
            {
                var parts = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) return m.Value;
                var x = double.Parse(parts[0], CultureInfo.InvariantCulture) + dx;
                var y = parts[1];
                var z = double.Parse(parts[2], CultureInfo.InvariantCulture) + dz;
                shifted++;
                return $"{m.Groups[1].Value}{Fmt(x)} {y} {Fmt(z)}{m.Groups[3].Value}";
            });
        });
        return (result, new Stats(shifted, skipped));
    }

    private static string Fmt(double v) => ((float)v).ToString(CultureInfo.InvariantCulture);
}
