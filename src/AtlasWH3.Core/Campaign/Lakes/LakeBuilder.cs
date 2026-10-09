using System.Buffers.Binary;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Models;

namespace AtlasWH3.Core.Campaign.Lakes;

/// <summary>One raised lake: its map.hex body, water level, centre, the generated mesh and how much ground was shaped.</summary>
public sealed record LakeResult(int Index, int Hexes, int AnchorCol, int AnchorRow, double Level, double CentreX, double CentreZ,
                                int Vertices, int Triangles, int ShapedCells, string ModelPath, (double X0, double Z0, double X1, double Z1) Bounds)
{
    /// <summary>The lake's map.hex water hexes.</summary>
    public IReadOnlyList<(int Col, int Row)> Body { get; init; } = [];

    /// <summary>The smoothed outline field (>= 0.5 = water) on the lf grid window starting at (Col0, Row0), Width wide.</summary>
    public float[] Field { get; init; } = [];
    public int Col0 { get; init; }
    public int Row0 { get; init; }
    public int Width { get; init; }
}

/// <summary>
/// Raised lakes as vanilla does its big lakes: one flat water mesh per lake (a clone of
/// rigidmodels/campaign/props/lakes/campaign_lake_01: same RMV2 layout, material and campaign_water shader) at a single
/// level, with the ground shaped so the shore meets it.
/// <para>
/// 3K draws sea water only below sea level, so lakes on the plateau need water props; small_lake_1 discs draped over a
/// sloping bed step and overlap (the 2026-10-02 lake_props.py approach). Here, per connected body of map.hex water
/// hexes in a lake region whose ground is above sea level:
/// </para>
/// <list type="number">
/// <item><description>outline = the body's hexes on the lf grid, smoothed (box blur, iso 0.5) to round the hex edges;</description></item>
/// <item><description>level L = the 30th percentile of the ground on the shore ring just outside the outline;</description></item>
/// <item><description>ground inside = a bowl L − min(3, 1.5·d) (vanilla big-lake depths); outside, ground above a bank L + 0.05 + 0.25·d is graded down
/// to it within 1.4 units; ground below it only gets a thin rim (L + 0.05, within 0.5 units), never a levee;</description></item>
/// <item><description>mesh = marching squares on the smoothed outline at 0.25 units, flat (y = 0) around the lake centre;
/// the prop goes at (centre x, L, centre z).</description></item>
/// </list>
/// </summary>
public static class LakeBuilder
{
    public const string TemplateModel = "rigidmodels/campaign/props/lakes/campaign_lake_01.rigid_model_v2";
    public const string LakeMaterial = "materials/environment/campaign/3k_main_campaign_lakes.xml.material";

    /// <param name="lf">LowFrequencyHeight raster (row 0 = north); modified in place.</param>
    /// <param name="template">campaign_lake_01.rigid_model_v2 bytes.</param>
    /// <param name="waterTile">Optional: is the tile map water (generic_sea) at lf cell (col, row)? The tile map's 2×2-pixel
    /// hexes don't line up exactly with map.hex's hex geometry, so a map.hex-only outline covered a few land tiles at the
    /// shore — their own shrubs then stood in the water (Juyan, in game 2026-10-04). With it, water = lake hex AND water tile.</param>
    /// <param name="sea">LowFrequencyHeightSea raster (half resolution; modified in place). It is the SEABED: the
    /// generic_sea tiles on map.hex water hexes take their ground from it (use_alt_lf), and vanilla raises it along
    /// its river channels and lake shores to just under the water. Left at sea level under a raised lake the water
    /// tiles sat ~8 units down and the lake read as a pale film; set to the water level the bed met the surface and
    /// the lake vanished (in game, 2026-10-04). Here it gets the shaped lake bed over the lake and SeaMargin beyond.</param>
    public static List<LakeResult> Build(Raster<ushort> lf, HexRegionLookup hex, byte[] template, string assetsOut,
                                         string regionFilter = "sea_lake", double minGround = 0.1, Action<string>? log = null,
                                         Raster<ushort>? sea = null, Func<int, int, bool>? waterTile = null)
    {
        log ??= _ => { };
        double px = AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeX, pz = AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeZ, worldH = lf.Height * pz;
        double H(int c, int r) => lf[c, r] * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset;
        ushort Raw(double h) => (ushort)Math.Clamp(Math.Round((h - AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset) / AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep), 0, 65535);
        double CellX(int c) => (c + 0.5) * px;
        double CellZ(int r) => worldH - (r + 0.5) * pz;
        int Col(double x) => (int)Math.Floor(x / px);
        int Row(double z) => (int)Math.Floor((worldH - z) / pz);

        var results = new List<LakeResult>();
        foreach (var (body, index) in Bodies(hex, regionFilter).Select((b, i) => (b, i)))
        {
            var centres = body.Select(h => hex.HexCentre(h.Col, h.Row)).ToList();
            var grounds = centres.Select(c => H(Math.Clamp(Col(c.X), 0, lf.Width - 1), Math.Clamp(Row(c.Z), 0, lf.Height - 1))).OrderBy(v => v).ToList();
            if (grounds[grounds.Count / 2] <= minGround) continue; // at sea level: the game's own sea water fills it

            // ROI on the lf grid
            double bx0 = centres.Min(c => c.X) - 2.5, bx1 = centres.Max(c => c.X) + 2.5, bz0 = centres.Min(c => c.Z) - 2.5, bz1 = centres.Max(c => c.Z) + 2.5;
            int c0 = Math.Max(1, Col(bx0)), c1 = Math.Min(lf.Width - 2, Col(bx1)), r0 = Math.Max(1, Row(bz1)), r1 = Math.Min(lf.Height - 2, Row(bz0));
            int w = c1 - c0 + 1, h = r1 - r0 + 1;
            var set = body.ToHashSet();
            var mask = new float[w * h];
            for (var r = 0; r < h; r++)
                for (var c = 0; c < w; c++)
                    mask[r * w + c] = set.Contains(hex.HexAt((float)CellX(c0 + c), (float)CellZ(r0 + r)))
                                      && (waterTile is null || waterTile(c0 + c, r0 + r)) ? 1 : 0;
            // outline: the hex mask blurred twice; 0.3 units left the hex corners showing ("edges too rough", 2026-10-04)
            // wide blur rounds the convex corners; capped by a narrow one so concave corners don't spill the water onto
            // the neighbouring land hexes (their land tiles' own shrubs then stood in the water, Juyan in game)
            var k = (int)Math.Ceiling(OutlineSmoothing / px);
            var kn = (int)Math.Ceiling(0.3 / px);
            var wide = Blur(Blur(mask, w, h, k), w, h, k);
            var narrow = Blur(Blur(mask, w, h, kn), w, h, kn);
            var field = new float[w * h];
            for (var i = 0; i < field.Length; i++) field[i] = Math.Min(wide[i], narrow[i]);
            var inside = field.Select(v => v >= 0.5f).ToArray();
            if (!inside.Any(v => v)) continue;
            var dIn = Distance(inside, w, h, px, pz, true);
            var dOut = Distance(inside, w, h, px, pz, false);

            var shore = new List<double>();
            for (var i = 0; i < w * h; i++)
                if (!inside[i] && dOut[i] <= 0.35) shore.Add(H(c0 + i % w, r0 + i / w));
            shore.Sort();
            // 30th percentile, not the median: with half the shore below the water the bank shaping raised levee
            // ridges along the low sides (Yuanquan, Juyan in game, 2026-10-04)
            var level = shore[(int)(shore.Count * 0.3)];

            var shaped = 0;
            for (var i = 0; i < w * h; i++)
            {
                int c = c0 + i % w, r = r0 + i / w;
                var orig = H(c, r);
                double target;
                // depth as vanilla's big lakes (campaign_lake_01..05: median 1.5, max ~3.2 under the surface; the campaign_water
                // shader colours by depth, so the first 0.35-deep bowls read as near-clear water in game, 2026-10-04)
                if (inside[i]) target = level - Math.Min(MaxDepth, DepthSlope * dIn[i]);
                else if (dOut[i] < 1.4)
                {
                    var bank = level + 0.05 + 0.25 * dOut[i];
                    if (orig >= bank)
                    {
                        // high ground: graded down to a gentle bank
                        var t = 1 - dOut[i] / 1.4;
                        target = orig + (bank - orig) * t * t * (3 - 2 * t);
                    }
                    else
                    {
                        // low ground: only a thin flat rim at the water's edge, never a ridge
                        if (dOut[i] >= 0.5 || orig >= level + 0.05) continue;
                        var t = 1 - dOut[i] / 0.5;
                        target = orig + (level + 0.05 - orig) * t * t * (3 - 2 * t);
                    }
                }
                else continue;
                var raw = Raw(target);
                if (raw != lf[c, r]) { lf[c, r] = raw; shaped++; }
            }

            // Seabed (generic_sea tiles' ground) = the shaped lake bed, over the lake and SeaMargin beyond the shore.
            var seaSet = 0;
            if (sea is not null)
            {
                double sx = (double)lf.Width / sea.Width, sz = (double)lf.Height / sea.Height;
                for (var sr = (int)(r0 / sz); sr <= Math.Min(sea.Height - 1, (int)(r1 / sz) + 1); sr++)
                    for (var sc = (int)(c0 / sx); sc <= Math.Min(sea.Width - 1, (int)(c1 / sx) + 1); sc++)
                    {
                        int lc = (int)((sc + 0.5) * sx), lr = (int)((sr + 0.5) * sz);
                        int i = (lr - r0) * w + (lc - c0);
                        if (lc < c0 || lr < r0 || lc - c0 >= w || lr - r0 >= h) continue;
                        if (!inside[i] && dOut[i] > SeaMargin) continue;
                        if (sea[sc, sr] != lf[lc, lr]) { sea[sc, sr] = lf[lc, lr]; seaSet++; }
                    }
            }

            // Mesh: marching squares on the field at ~0.25 units, around the lake centre.
            double cx = (bx0 + bx1) / 2, cz = (bz0 + bz1) / 2;
            var (verts, tris) = Mesh(field, w, h, c0, r0, px, pz, worldH, cx, cz, 0.25);
            var step = 0.25;
            while (verts.Count >= 60000) { step *= 1.4; (verts, tris) = Mesh(field, w, h, c0, r0, px, pz, worldH, cx, cz, step); }
            var anchor = hex.HexAt((float)cx, (float)cz);
            var name = $"190e_lake_{anchor.Col}_{anchor.Row}";
            var model = $"rigidmodels/campaign/props/lakes/{name}";
            var (verts3, tris3) = AddSkirt(verts, tris, SkirtDepth);
            WriteModel(template, verts3, tris3, Path.Combine(assetsOut, model.Replace('/', Path.DirectorySeparatorChar)));
            results.Add(new LakeResult(index, body.Count, anchor.Col, anchor.Row, level, cx, cz, verts.Count, tris.Count / 3, shaped,
                model + ".wsmodel", (verts.Min(v => v.X) + cx, verts.Min(v => v.Z) + cz, verts.Max(v => v.X) + cx, verts.Max(v => v.Z) + cz))
                { Body = body, Field = field, Col0 = c0, Row0 = r0, Width = w });
            log($"lake {index}: {body.Count} hexes, level {level:F2}, {verts.Count} vertices, {shaped} lf cells shaped, {seaSet} seabed cells → {model}");
        }
        return results;
    }

    /// <summary>
    /// Sea inlets whose banks stand above sea level (so the game's sea water does not show): lowers the ground under the
    /// map.hex sea hexes (terrain 1) around (<paramref name="x"/>, <paramref name="z"/>) to a bed −min(depth, 0.6·d)
    /// below sea level, with banks outside eased down to 0.05 + 0.25·d within 0.8 units. Full strength within
    /// <paramref name="radius"/>, fading out over the next 2 units so the carve meets the untouched channel. Only lowers.
    /// Returns the number of lf cells changed.
    /// </summary>
    public static int CarveSea(Raster<ushort> lf, HexRegionLookup hex, double x, double z, double radius, double depth = 0.35)
    {
        double px = AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeX, pz = AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeZ, worldH = lf.Height * pz;
        double H(int c, int r) => lf[c, r] * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset;
        ushort Raw(double h) => (ushort)Math.Clamp(Math.Round((h - AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset) / AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep), 0, 65535);
        var reach = radius + 2;
        int c0 = Math.Max(1, (int)Math.Floor((x - reach - 1) / px)), c1 = Math.Min(lf.Width - 2, (int)Math.Floor((x + reach + 1) / px));
        int r0 = Math.Max(1, (int)Math.Floor((worldH - z - reach - 1) / pz)), r1 = Math.Min(lf.Height - 2, (int)Math.Floor((worldH - z + reach + 1) / pz));
        int w = c1 - c0 + 1, h = r1 - r0 + 1;
        var mask = new float[w * h];
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
            {
                var (hc, hr) = hex.HexAt((float)((c0 + c + 0.5) * px), (float)(worldH - (r0 + r + 0.5) * pz));
                mask[r * w + c] = hex.Hex.TerrainAt(hc, hr) == 1 ? 1 : 0;
            }
        var k = (int)Math.Ceiling(0.3 / px);
        var inside = Blur(Blur(mask, w, h, k), w, h, k).Select(v => v >= 0.5f).ToArray();
        var dIn = Distance(inside, w, h, px, pz, true);
        var dOut = Distance(inside, w, h, px, pz, false);
        var changed = 0;
        for (var i = 0; i < w * h; i++)
        {
            int c = c0 + i % w, r = r0 + i / w;
            double cx = (c + 0.5) * px - x, cz = worldH - (r + 0.5) * pz - z;
            var fall = Math.Clamp((reach - Math.Sqrt(cx * cx + cz * cz)) / 2, 0, 1);
            if (fall <= 0) continue;
            fall = fall * fall * (3 - 2 * fall);
            var orig = H(c, r);
            double target;
            if (inside[i]) target = -Math.Min(depth, 0.6 * dIn[i]) - 0.02;
            else if (dOut[i] < 0.8)
            {
                var t = 1 - dOut[i] / 0.8;
                target = orig + (0.05 + 0.25 * dOut[i] - orig) * t * t * (3 - 2 * t);
            }
            else continue;
            target = orig + (target - orig) * fall;
            if (target >= orig) continue;
            var raw = Raw(target);
            if (raw != lf[c, r]) { lf[c, r] = raw; changed++; }
        }
        return changed;
    }

    /// <summary>True where (x, z) is in a lake's water (outline field ≥ <paramref name="threshold"/>; 0.5 = the shore
    /// line, lower reaches a little onto the bank).</summary>
    public static bool InWater(IReadOnlyList<LakeResult> lakes, double x, double z, int lfHeight, float threshold = 0.5f)
    {
        double px = AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeX, pz = AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeZ;
        int c = (int)Math.Floor(x / px), r = (int)Math.Floor((lfHeight * pz - z) / pz);
        foreach (var l in lakes)
        {
            int lc = c - l.Col0, lr = r - l.Row0, h = l.Field.Length / Math.Max(1, l.Width);
            if (lc >= 0 && lr >= 0 && lc < l.Width && lr < h && l.Field[lr * l.Width + lc] >= threshold) return true;
        }
        return false;
    }

    /// <summary>Connected bodies of map.hex water hexes (terrain 1) whose region name contains <paramref name="filter"/>.</summary>
    public static List<List<(int Col, int Row)>> Bodies(HexRegionLookup hex, string filter)
    {
        var map = hex.Hex;
        var (ax, az) = hex.HexCentre(10, 10);
        var (bx, bz) = hex.HexCentre(10, 11);
        var spacing = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        bool Lake(int c, int r) => map.TerrainAt(c, r) == 1 && map.RegionName(map.RegionIndexAt(c, r))?.Contains(filter, StringComparison.Ordinal) == true;
        var seen = new HashSet<(int, int)>();
        var bodies = new List<List<(int, int)>>();
        for (var r = 0; r < map.Height; r++)
            for (var c = 0; c < map.Width; c++)
            {
                if (!Lake(c, r) || !seen.Add((c, r))) continue;
                var body = new List<(int, int)>();
                var q = new Queue<(int, int)>([(c, r)]);
                while (q.Count > 0)
                {
                    var (hc, hr) = q.Dequeue();
                    body.Add((hc, hr));
                    var (x, z) = hex.HexCentre(hc, hr);
                    for (var a = 0; a < 6; a++)
                    {
                        var ang = Math.PI / 6 + a * Math.PI / 3;
                        var n = hex.HexAt((float)(x + Math.Cos(ang) * spacing), (float)(z + Math.Sin(ang) * spacing));
                        if (Lake(n.Col, n.Row) && seen.Add(n)) q.Enqueue(n);
                    }
                }
                bodies.Add(body);
            }
        return bodies;
    }

    private static float[] Blur(float[] src, int w, int h, int k)
    {
        var tmp = new float[w * h];
        var dst = new float[w * h];
        for (var r = 0; r < h; r++)
        {
            float sum = 0; int n = 0;
            for (var c = -k; c < w + k; c++)
            {
                if (c + k < w) { sum += src[r * w + Math.Clamp(c + k, 0, w - 1)]; n++; }
                if (c - k - 1 >= 0) { sum -= src[r * w + c - k - 1]; n--; }
                if (c >= 0 && c < w) tmp[r * w + c] = sum / Math.Max(1, n);
            }
        }
        for (var c = 0; c < w; c++)
        {
            float sum = 0; int n = 0;
            for (var r = -k; r < h + k; r++)
            {
                if (r + k < h) { sum += tmp[Math.Clamp(r + k, 0, h - 1) * w + c]; n++; }
                if (r - k - 1 >= 0) { sum -= tmp[(r - k - 1) * w + c]; n--; }
                if (r >= 0 && r < h) dst[r * w + c] = sum / Math.Max(1, n);
            }
        }
        return dst;
    }

    /// <summary>Chamfer distance (world units) from every cell on one side of the outline to the other side.</summary>
    private static double[] Distance(bool[] inside, int w, int h, double px, double pz, bool fromInside)
    {
        var d = new double[w * h];
        var diag = Math.Sqrt(px * px + pz * pz);
        for (var i = 0; i < d.Length; i++) d[i] = inside[i] == fromInside ? double.MaxValue : 0;
        void Pass(int r0, int r1, int dr, int ca, int cb, int dc)
        {
            for (var r = r0; r != r1; r += dr)
                for (var c = ca; c != cb; c += dc)
                {
                    var i = r * w + c;
                    if (d[i] == 0) continue;
                    var best = d[i];
                    if (c - dc >= 0 && c - dc < w) best = Math.Min(best, d[i - dc] + px);
                    if (r - dr >= 0 && r - dr < h)
                    {
                        best = Math.Min(best, d[i - dr * w] + pz);
                        if (c - dc >= 0 && c - dc < w) best = Math.Min(best, d[i - dr * w - dc] + diag);
                        if (c + dc >= 0 && c + dc < w) best = Math.Min(best, d[i - dr * w + dc] + diag);
                    }
                    d[i] = best;
                }
        }
        Pass(0, h, 1, 0, w, 1);
        Pass(h - 1, -1, -1, w - 1, -1, -1);
        return d;
    }

    private static (List<(float X, float Z)> V, List<int> T) Mesh(float[] field, int w, int h, int c0, int r0, double px, double pz,
                                                                   double worldH, double cx, double cz, double step)
    {
        double F(double x, double z)
        {
            var fc = x / px - 0.5 - c0;
            var fr = (worldH - z) / pz - 0.5 - r0;
            if (fc < 0 || fr < 0 || fc > w - 1.001 || fr > h - 1.001) return 0;
            int ic = (int)fc, ir = (int)fr;
            double tx = fc - ic, tz = fr - ir;
            return (field[ir * w + ic] * (1 - tx) + field[ir * w + ic + 1] * tx) * (1 - tz)
                 + (field[(ir + 1) * w + ic] * (1 - tx) + field[(ir + 1) * w + ic + 1] * tx) * tz;
        }
        double x0 = (c0 + 0.5) * px, x1 = (c0 + w - 1.5) * px, zTop = worldH - (r0 + 0.5) * pz, zBot = worldH - (r0 + h - 1.5) * pz;
        int nx = (int)Math.Ceiling((x1 - x0) / step), nz = (int)Math.Ceiling((zTop - zBot) / step);
        var verts = new List<(float, float)>();
        var tris = new List<int>();
        for (var j = 0; j < nz; j++)
            for (var i = 0; i < nx; i++)
            {
                // corners counter-clockwise seen from above (x east, z north)
                (double X, double Z)[] q = [(x0 + i * step, zBot + j * step), (x0 + (i + 1) * step, zBot + j * step),
                                            (x0 + (i + 1) * step, zBot + (j + 1) * step), (x0 + i * step, zBot + (j + 1) * step)];
                var v = q.Select(p => F(p.X, p.Z) - 0.5).ToArray();
                if (v.All(a => a < 0)) continue;
                var poly = new List<(double X, double Z)>();
                for (var k = 0; k < 4; k++)
                {
                    var a = q[k]; var b = q[(k + 1) % 4];
                    double va = v[k], vb = v[(k + 1) % 4];
                    if (va >= 0) poly.Add(a);
                    if (va >= 0 != vb >= 0)
                    {
                        var t = va / (va - vb);
                        poly.Add((a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t));
                    }
                }
                if (poly.Count < 3) continue;
                var start = verts.Count;
                foreach (var p in poly) verts.Add(((float)(p.X - cx), (float)(p.Z - cz)));
                // clockwise seen from above (x east, z north), as vanilla's lake meshes
                for (var k = 1; k + 1 < poly.Count; k++) tris.AddRange([start, start + k + 1, start + k]);
            }
        return (verts, tris);
    }

    /// <summary>
    /// Depth of the rim skirt below the water surface (world units). Vanilla's campaign_lake_01..05 are not flat: a
    /// third of their vertices sit at y = −1.66 model units (≈ −0.7 world at their 0.42 scale), a rim dipping into the
    /// bank. A perfectly flat lake (zero-height bounds) did not draw in game (2026-10-04 in-game check).
    /// </summary>
    public const float SkirtDepth = 0.7f;

    /// <summary>Lake bed: depth = min(MaxDepth, DepthSlope · distance inside the shore), as vanilla's big lakes.</summary>
    public const double MaxDepth = 3.0, DepthSlope = 1.5;

    /// <summary>Box-blur radius (world units, applied twice) that rounds the hex outline into the shore line.</summary>
    public const double OutlineSmoothing = 0.8;

    /// <summary>How far beyond the shore the seabed (sea map) follows the shaped ground.</summary>
    public const double SeaMargin = 1.0;


    /// <summary>Welds the flat mesh, then hangs a skirt (double-sided quads, depth <paramref name="depth"/>) on every
    /// boundary edge, as vanilla's lake meshes have.</summary>
    private static (List<(float X, float Y, float Z)> V, List<int> T) AddSkirt(List<(float X, float Z)> verts, List<int> tris, float depth)
    {
        var weld = new Dictionary<(long, long), int>();
        var v = new List<(float X, float Y, float Z)>();
        var map = new int[verts.Count];
        for (var i = 0; i < verts.Count; i++)
        {
            var key = ((long)Math.Round(verts[i].X * 1000), (long)Math.Round(verts[i].Z * 1000));
            if (!weld.TryGetValue(key, out var w)) { w = v.Count; weld[key] = w; v.Add((verts[i].X, 0, verts[i].Z)); }
            map[i] = w;
        }
        var t = new List<int>(tris.Count);
        for (var i = 0; i + 2 < tris.Count; i += 3)
        {
            int a = map[tris[i]], b = map[tris[i + 1]], c = map[tris[i + 2]];
            if (a != b && b != c && a != c) t.AddRange([a, b, c]);
        }
        var edges = new Dictionary<(int, int), int>();
        for (var i = 0; i < t.Count; i += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = t[i + k], b = t[i + (k + 1) % 3];
                var e = a < b ? (a, b) : (b, a);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        var below = new Dictionary<int, int>();
        int Low(int i)
        {
            if (!below.TryGetValue(i, out var j)) { j = v.Count; below[i] = j; v.Add((v[i].X, -depth, v[i].Z)); }
            return j;
        }
        foreach (var ((a, b), n) in edges)
        {
            if (n != 1) continue;
            int la = Low(a), lb = Low(b);
            t.AddRange([a, b, lb, a, lb, la]);   // both windings: the skirt shows from either side
            t.AddRange([a, lb, b, a, la, lb]);
        }
        return (v, t);
    }

    /// <summary>The template RMV2 (campaign_lake_01) with our vertices and indices; .wsmodel next to it.</summary>
    private static void WriteModel(byte[] template, List<(float X, float Y, float Z)> verts, List<int> tris, string pathNoExt)
    {
        var model = RigidModelV2.Read(template);
        var stride = model.VertexStride; // 32: half4 position, half2 uv, half2 uv2, byte4 normal, byte4 tangent, byte4 bitangent, 4 bytes
        var bytes = new byte[verts.Count * stride];
        // normal / tangent / bitangent / tail bytes as in the template's flat water vertices
        var tail = model.Vertices.AsSpan(12, stride - 12).ToArray();
        for (var i = 0; i < verts.Count; i++)
        {
            var o = i * stride;
            var (x, y, z) = verts[i];
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(o), (Half)x);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(o + 2), (Half)y);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(o + 4), (Half)z);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(o + 6), (Half)1f);
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(o + 8), (Half)(x / 20f + 0.5f));
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(o + 10), (Half)(z / 20f + 0.5f));
            tail.CopyTo(bytes.AsSpan(o + 12));
        }
        model.Vertices = bytes;
        model.Indices = tris.Select(t => (ushort)t).ToArray();
        model.Bounds = [verts.Min(v => v.X), verts.Min(v => v.Y), verts.Min(v => v.Z), verts.Max(v => v.X), verts.Max(v => v.Y), verts.Max(v => v.Z)];
        var file = model.ToBytes();
        template.AsSpan(0x9C, 4).CopyTo(file.AsSpan(0x9C)); // LOD distance as the template (the writer's default is 1e6)
        // Pivot (material block +548..559) to 0: campaign_lake_01 stores its surface at y = +0.83 with pivot y = −0.83;
        // our vertices are at the true height, so the inherited pivot sank the water 0.83 under the lake bed (in game,
        // 2026-10-04). small_lake_1, which draws on raised ground, has pivot y 0.
        var meshOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(140 + 12));
        file.AsSpan(meshOffset + 80 + 548, 12).Clear();
        Directory.CreateDirectory(Path.GetDirectoryName(pathNoExt)!);
        File.WriteAllBytes(pathNoExt + ".rigid_model_v2", file);
        var geometry = pathNoExt[(pathNoExt.Replace('\\', '/').IndexOf("rigidmodels/", StringComparison.OrdinalIgnoreCase))..].Replace('\\', '/');
        File.WriteAllText(pathNoExt + ".wsmodel",
            $"<model version=\"1\">\n <geometry>{geometry}.rigid_model_v2</geometry>\n <materials>\n" +
            $"  <material lod_index=\"0\" part_index=\"0\">{LakeMaterial}</material>\n </materials>\n</model>\n");
    }
}
