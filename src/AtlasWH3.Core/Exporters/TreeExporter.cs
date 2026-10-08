using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Exporters;

public static class TreeExporter
{
    /// <summary>Pack-relative path of the compiled tree list.</summary>
    public static string PackPath(string mapName) =>
        Path.Combine("campaign_maps", mapName, "display", "trees", "trees.campaign_tree_list");

    /// <summary>
    /// Writes trees.campaign_tree_list under <paramref name="outputRoot"/> using the in-pack folder layout, so the
    /// folder can be dropped straight into an RPFM pack. Species left with no instances are omitted.
    /// </summary>
    public static string WriteCompiled(CampaignTreeList trees, string outputRoot, string mapName)
    {
        var copy = new CampaignTreeList
        {
            Version = trees.Version, UnknownA = trees.UnknownA, UnknownB = trees.UnknownB,
            WorldWidth = trees.WorldWidth, WorldHeight = trees.WorldHeight,
        };
        copy.Types.AddRange(trees.Types.Where(t => t.Instances.Count > 0));
        var path = Path.Combine(outputRoot, PackPath(mapName));
        copy.Save(path);
        return path;
    }

    /// <summary>
    /// Updates the AK tree paint (1 cell = 4x4 lf px) in every cell where trees were added or removed: the cell
    /// takes the colour group of its most common remaining species, or <see cref="AkExporter.NoTreeIndex"/> if empty.
    /// Returns the number of cells changed.
    /// </summary>
    public static int UpdatePaint(Raster<byte> paint, TiffMap.Palette palette, CampaignTreeList trees,
                                  TreeDatabase? db, TreeEditLog log)
    {
        var w = paint.Width;
        var h = paint.Height;
        (int, int) Cell(float x, float z) =>
            (Math.Clamp((int)(x / trees.WorldWidth * w), 0, w - 1),
             Math.Clamp((int)((1 - z / trees.WorldHeight) * h), 0, h - 1));

        var touched = log.Points.Select(p => Cell(p.X, p.Z)).ToHashSet();
        if (touched.Count == 0) return 0;

        var counts = new Dictionary<(int, int), Dictionary<string, int>>();
        foreach (var type in trees.Types)
            foreach (var t in type.Instances)
            {
                var cell = Cell(t.X, t.Z);
                if (!touched.Contains(cell)) continue;
                if (!counts.TryGetValue(cell, out var perSpecies)) counts[cell] = perSpecies = new();
                perSpecies[type.Name] = perSpecies.GetValueOrDefault(type.Name) + 1;
            }

        var indexCache = new Dictionary<string, byte>();
        var changed = 0;
        foreach (var cell in touched)
        {
            var value = AkExporter.NoTreeIndex;
            if (counts.TryGetValue(cell, out var perSpecies))
            {
                var species = perSpecies.MaxBy(kv => kv.Value).Key;
                if (!indexCache.TryGetValue(species, out value))
                    indexCache[species] = value = PaletteIndexFor(species, palette, db);
            }
            ref var px = ref paint[cell.Item1, cell.Item2];
            if (px != value) { px = value; changed++; }
        }
        return changed;
    }

    /// <summary>Palette entry whose colour matches the species' campaign_tree_ids colour_hex (nearest if inexact).</summary>
    public static byte PaletteIndexFor(string species, TiffMap.Palette palette, TreeDatabase? db)
    {
        if (db == null || !db.Ids.TryGetValue(species, out var id))
            return AkExporter.NoTreeIndex;
        int r = (int)(id.ColourRgb >> 16) & 0xFF, g = (int)(id.ColourRgb >> 8) & 0xFF, b = (int)id.ColourRgb & 0xFF;
        var best = AkExporter.NoTreeIndex;
        var bestDist = int.MaxValue;
        // Entries 0-18 are the tree colour groups; 19 is "no tree"; the rest of the palette is unused.
        for (var i = 0; i < Math.Min(palette.R.Length, AkExporter.NoTreeIndex); i++)
        {
            int dr = (palette.R[i] >> 8) - r, dg = (palette.G[i] >> 8) - g, db2 = (palette.B[i] >> 8) - b;
            var dist = dr * dr + dg * dg + db2 * db2;
            if (dist < bestDist) { bestDist = dist; best = (byte)i; }
            if (dist == 0) break;
        }
        return best;
    }
}
