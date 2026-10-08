using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// Builds spd_data.esf (CAI_SIMPLE_PATH_DIRECTORY), the campaign AI's landmark table, the way
/// EMPIRECAMPAIGNAI::CAI_PATHFINDER::reprocess_spd_data does (empirecampaign FUN_180593f60):
///  1. Bounding box of the passable hexes (type ≠ 2); eight targets: its corners (min x,min y), (min x,max y),
///     (max x,min y), (max x,max y), then the edge midpoints (mid x,min y), (mid x,max y), (min x,mid y),
///     (max x,mid y) with mid = ((max − min + 1) &gt;&gt; 1) + min.
///  2. Landmark i = the passable hex nearest (hex distance) to target i, scanning x outer, y inner; first wins ties.
///  3. Per landmark a full search outwards (value 2i) and inwards (value 2i+1) over <see cref="CampaignPathGrid"/>.
///  4. The values live in the game's CAI_SPARSE_MAP&lt;1024&gt;: coordinates ≥ 1024 alias onto the last 32-hex strip
///     (992 + (c &amp; 31)); the later (= costlier) write wins. Output box = the cells written.
/// </summary>
public static class SpdBuilder
{
    public const int SparseMapSize = 1024;

    public static int HexDistance(int x1, int y1, int x2, int y2)
    {
        var s3 = (short)(x1 - x2);
        var s2 = (short)(((x1 + 1) >> 1) - ((x2 + 1) >> 1) - y2 + y1);
        var s1 = (short)(s3 - s2);
        return Math.Max(Math.Abs((int)s3), Math.Max(Math.Abs((int)s2), Math.Abs((int)s1)));
    }

    public static List<(int X, int Y)> Landmarks(CampaignPathGrid grid)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
        {
            if (grid.Types[grid.Index(x, y)] == 2) continue;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        var mx = ((x1 - x0 + 1) >> 1) + x0;
        var my = ((y1 - y0 + 1) >> 1) + y0;
        (int X, int Y)[] targets = [(x0, y0), (x0, y1), (x1, y0), (x1, y1), (mx, y0), (mx, y1), (x0, my), (x1, my)];
        var best = new (int X, int Y)[8];
        var bestD = new int[8];
        Array.Fill(bestD, int.MaxValue);
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            if (grid.Types[grid.Index(x, y)] == 2) continue;
            for (var i = 0; i < 8; i++)
            {
                var d = HexDistance(x, y, targets[i].X, targets[i].Y);
                if (d < bestD[i]) { bestD[i] = d; best[i] = (x, y); }
            }
        }
        return [.. best];
    }

    private static int Alias(int c) => c < SparseMapSize ? c : SparseMapSize - 32 + (c & 31);

    public static SpdData Build(CampaignPathGrid grid, uint timestamp, Action<string>? log = null, int maxThreads = 0)
    {
        var landmarks = Landmarks(grid);
        log?.Invoke($"landmarks {string.Join(" ", landmarks.Select(l => $"({l.X},{l.Y})"))}");
        var w = Math.Min(grid.Width, SparseMapSize);
        var h = Math.Min(grid.Height, SparseMapSize);
        var values = new uint[w * h * 16];
        Array.Fill(values, SpdData.NoPath);
        var written = new bool[w * h];
        grid.TraceSources = landmarks.Select(l => grid.Index(l.X, l.Y)).ToArray();
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxThreads > 0 ? maxThreads : Environment.ProcessorCount };
        Parallel.For(0, 16, po, k =>
        {
            var lm = landmarks[k / 2];
            if (grid.Width <= SparseMapSize && grid.Height <= SparseMapSize)
            {
                var dist = grid.Search(grid.Index(lm.X, lm.Y), (k & 1) == 1);
                for (var y = 0; y < grid.Height; y++)
                for (var x = 0; x < grid.Width; x++)
                {
                    var d = dist[y * grid.Width + x];
                    if (d == uint.MaxValue) continue;
                    var c = y * w + x;
                    values[c * 16 + k] = d;
                    written[c] = true; // benign race: every writer stores true
                }
            }
            else
            {
                // big maps: the game's own heap order and its 1024-wide sparse visited map decide which hexes settle
                var pops = Environment.GetEnvironmentVariable("SPD_POPS") is { } sp && sp.Split('|') is [var pk, var pp] && int.Parse(pk) == k
                    ? new BinaryWriter(File.Create(pp)) : null;
                grid.SearchGame(grid.Index(lm.X, lm.Y), (k & 1) == 1, true, (h, d) =>
                {
                    if (pops is not null) { pops.Write((ushort)(h % grid.Width)); pops.Write((ushort)(h / grid.Width)); pops.Write(d); }
                    var c = Alias(h / grid.Width) * w + Alias(h % grid.Width);
                    values[c * 16 + k] = d;
                    written[c] = true;
                });
                pops?.Dispose();
            }
        });
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!written[y * w + x]) continue;
            bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y);
        }
        var spd = new SpdData { Timestamp = timestamp, X0 = bx0, Y0 = by0, X1 = bx1, Y1 = by1 };
        var bw = spd.Width;
        spd.Values = new uint[bw * spd.Height * 16];
        for (var y = by0; y <= by1; y++)
            Array.Copy(values, (y * w + bx0) * 16, spd.Values, (y - by0) * bw * 16, bw * 16);
        foreach (var (x, y) in landmarks) spd.Landmarks.Add(((ushort)x, (ushort)y));
        return spd;
    }
}
