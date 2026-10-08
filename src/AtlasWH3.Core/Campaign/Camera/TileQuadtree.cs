using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.Camera;

/// <summary>
/// Which tiles BOB's camera fallback (warscape FUN_180350820) can reach at a point: it queries the scene quadtree
/// (FUN_1803f32e0, inclusive box tests down the tree) and tries only the tiles stored in nodes whose bounds touch the
/// point. Reproduced from a Frida dump of the whole tree (vanilla 3k_dlc07, 2026-10-05):
///  - the tree is 7 levels of midpoint splits of (−1, −1)..(scene width, scene depth), children ordered
///    (min x, max z), (max x, max z), (min x, min z), (max x, min z) (FUN_1803f1240);
///  - it holds the tiles whose global_map\tile_list.bin flag has bit 0 (the instance flag 0x100), each in the leaf whose
///    box holds the centre of its extent: its cells, widened by a blockout cliff's custom mesh bounds;
///  - a leaf's bounds grow to its tiles' rectangles (and blockout cliffs' custom meshes) and every inner node's to
///    its children's.
/// So a tile answers only where the point is inside its leaf's grown bounds: a point exactly on a tile's edge can miss
/// it by an ulp where the leaf ends at that edge.
/// </summary>
public sealed class TileQuadtree
{
    private const int Depth = 7;
    private readonly float[]?[] _leafOf;

    private sealed class Node
    {
        public float X0, Z0, X1, Z1;
        public Node[]? Kids;
        public float[]? Grown;
    }

    /// <param name="size">the record's unturned tile size in cells (null: not a tile)</param>
    /// <param name="inTree">whether the record is stored (BOB: global_map tile_list flag bit 0)</param>
    /// <param name="customBounds">the tile's custom_mesh bounds (min x, y, z, max x, y, z; blockout cliffs), or null</param>
    public TileQuadtree(TileList list, Func<int, (int W, int H)?> size, Func<int, bool> inTree, Func<int, float[]?> customBounds,
                        float tileSize, float sceneW, float sceneD)
    {
        var root = Build(-1f, -1f, sceneW, sceneD, 0);
        _leafOf = new float[]?[list.Records.Count];
        var s = tileSize / 128f;
        for (var r = 0; r < list.Records.Count; r++)
        {
            if (!inTree(r) || size(r) is not var (uw, uh)) continue;
            var rec = list.Records[r];
            var turned = (rec.Orientation & 0xF0) is 0x20 or 0x80;
            var (w, h) = turned ? (uh, uw) : (uw, uh);
            // BOB's sums (fitted on every grown leaf edge): x = X·128·s + w·128·s, z = (Y·128·s + h·128·s) · 1.15476
            float ax0 = rec.X * 128f * s, ax1 = rec.X * 128f * s + w * 128f * s;
            float az0 = rec.Y * 128f * s * CameraHeightField.ZScale, az1 = (rec.Y * 128f * s + h * 128f * s) * CameraHeightField.ZScale;
            if (customBounds(r) is { } cb)
            {
                // a custom mesh (blockout cliffs) widens the tile's extent by its bounds, without the tile's turn (fitted
                // on BOB's leaf bounds): tile space a over x min..x max and b over −z max..−z min, or for the 90° turns
                // a over x min..−z min and b over −z max..x max
                float px = rec.X * 128f, pz = rec.Y * 128f;
                var (a0, a1, b0, b1) = turned ? (cb[0], -cb[2], -cb[5], cb[3]) : (cb[0], cb[3], -cb[5], -cb[2]);
                foreach (var (ta, tb) in new[] { (a0, b0), (a1, b0), (a0, b1), (a1, b1) })
                {
                    float wx = px * s + ta * s, wz = (pz * s + tb * s) * CameraHeightField.ZScale;
                    ax0 = MathF.Min(ax0, wx); ax1 = MathF.Max(ax1, wx); az0 = MathF.Min(az0, wz); az1 = MathF.Max(az1, wz);
                }
            }
            float cx = (ax0 + ax1) * 0.5f, cz = (az0 + az1) * 0.5f;     // placed by the centre of its whole extent
            var node = root;
            if (!(node.X0 <= cx && cx <= node.X1 && node.Z0 <= cz && cz <= node.Z1)) continue;
            while (node.Kids is { } kids)
            {
                var next = kids.FirstOrDefault(k => k.X0 <= cx && cx <= k.X1 && k.Z0 <= cz && cz <= k.Z1);
                if (next is null) break;
                node = next;
            }
            var g = node.Grown ??= [node.X0, node.Z0, node.X1, node.Z1];
            g[0] = MathF.Min(g[0], ax0);
            g[1] = MathF.Min(g[1], az0);
            g[2] = MathF.Max(g[2], ax1);
            g[3] = MathF.Max(g[3], az1);
            _leafOf[r] = g;
        }
    }

    private static Node Build(float x0, float z0, float x1, float z1, int depth)
    {
        var n = new Node { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1 };
        if (depth < Depth)
        {
            var mx = (x1 + x0) * 0.5f;
            var mz = (z1 + z0) * 0.5f;
            n.Kids = [Build(x0, mz, mx, z1, depth + 1), Build(mx, mz, x1, z1, depth + 1),
                      Build(x0, z0, mx, mz, depth + 1), Build(mx, z0, x1, mz, depth + 1)];
        }
        return n;
    }

    /// <summary>The grown bounds (x0, z0, x1, z1) of the leaf holding record <paramref name="r"/>, null if not stored.</summary>
    public float[]? LeafBounds(int r) => _leafOf[r];

    /// <summary>Whether the fallback tries record <paramref name="r"/> at the world point (x, z).</summary>
    public bool Reaches(int r, float x, float z) =>
        _leafOf[r] is { } b && b[0] <= x && x <= b[2] && b[1] <= z && z <= b[3];
}
