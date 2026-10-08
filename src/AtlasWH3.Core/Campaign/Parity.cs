namespace AtlasWH3.Core.Campaign;

/// <summary>Byte comparison of a native build against a reference (vanilla or a BOB build).</summary>
public static class Parity
{
    public sealed record FileResult(string RelativePath, string Status, long BuiltSize, long ReferenceSize, long FirstDiff, long DiffBytes);

    /// <summary>Compares every file under <paramref name="builtDir"/> with the same relative path under
    /// <paramref name="referenceDir"/>. With <paramref name="maskJunk"/>, bytes BOB fills from uninitialised memory in
    /// .rigid_model_v2 headers are ignored.</summary>
    public static IReadOnlyList<FileResult> Compare(string builtDir, string referenceDir, bool maskJunk)
    {
        var results = new List<FileResult>();
        foreach (var built in Directory.EnumerateFiles(builtDir, "*", SearchOption.AllDirectories).Order())
        {
            var rel = Path.GetRelativePath(builtDir, built);
            var reference = Path.Combine(referenceDir, rel);
            var builtSize = new FileInfo(built).Length;
            if (!File.Exists(reference))
            {
                results.Add(new FileResult(rel, "no-reference", builtSize, -1, -1, -1));
                continue;
            }
            var a = File.ReadAllBytes(built);
            var b = File.ReadAllBytes(reference);
            var mask = !maskJunk || !rel.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) ? NoMask
                : Path.GetFileName(rel).StartsWith("river_", StringComparison.OrdinalIgnoreCase) ? RiverJunk : TileJunk;
            long first = -1, count = 0;
            var n = Math.Min(a.Length, b.Length);
            for (var i = 0; i < n; i++)
            {
                if (a[i] == b[i] || mask.Any(r => i >= r.Start && i < r.End)) continue;
                if (first < 0) first = i;
                count++;
            }
            count += Math.Abs(a.Length - b.Length);
            if (first < 0 && a.Length != b.Length) first = n;
            results.Add(new FileResult(rel, count == 0 ? "identical" : "differs", a.Length, b.Length, first, count));
        }
        return results;
    }

    /// <summary>LOD quality padding and the shader block tail; rivers also have two stray bytes in the material.</summary>
    private static readonly (int Start, int End)[] NoMask = [];
    private static readonly (int Start, int End)[] TileJunk = [(0xA5, 0xA8), (0xE8, 0xF0)];
    private static readonly (int Start, int End)[] RiverJunk = [(0xA5, 0xA8), (0xE8, 0xF0), (0x31A, 0x31C)];
}
