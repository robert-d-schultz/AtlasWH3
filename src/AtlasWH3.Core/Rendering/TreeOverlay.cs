using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Rendering;

/// <summary>Draws campaign tree instances as coloured dots (campaign_tree_ids colour_hex) over the terrain.</summary>
public sealed class TreeOverlay
{
    private readonly CampaignTreeList _trees;
    private readonly TreeDatabase? _db;
    private readonly Dictionary<string, uint> _colours = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> HiddenTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Visible { get; set; } = true;
    /// <summary>Only draw instances present in this season id (null = all).</summary>
    public uint? Season { get; set; }

    public TreeOverlay(CampaignTreeList trees, TreeDatabase? db)
    {
        _trees = trees;
        _db = db;
        foreach (var type in trees.Types)
            _colours[type.Name] = ColourFor(type.Name);
    }

    public uint ColourFor(string treeId)
    {
        if (_db != null && _db.Ids.TryGetValue(treeId, out var id))
            return 0xFF000000u | id.ColourRgb;                     // 0xAARRGGBB (BGRA bytes)
        var hash = (uint)treeId.GetHashCode();
        return 0xFF000000u | (hash & 0x00FFFFFF);
    }

    /// <summary>Draws into a BGRA buffer rendered with <paramref name="view"/> over an lf grid of lfW x lfH.</summary>
    public void Draw(Viewport view, int width, int height, uint[] bgra, int lfW, int lfH, WorldCoords coords)
    {
        if (!Visible) return;
        // Dot radius grows as you zoom in (scale = lf px per screen px).
        var radius = view.Scale >= 4 ? 0 : view.Scale >= 1.5 ? 1 : view.Scale >= 0.5 ? 2 : 3;
        var alphaSkip = view.Scale >= 6 ? 3 : 1; // thin out when very zoomed out

        foreach (var type in _trees.Types)
        {
            if (HiddenTypes.Contains(type.Name)) continue;
            if (!_colours.TryGetValue(type.Name, out var colour))
                _colours[type.Name] = colour = ColourFor(type.Name);
            var list = type.Instances;
            for (var i = 0; i < list.Count; i += alphaSkip)
            {
                var inst = list[i];
                if (Season is { } s && !(inst.Seasons.Length == 1 && inst.Seasons[0] == CampaignTreeList.NoSeason)
                                    && Array.IndexOf(inst.Seasons, s) < 0)
                    continue;
                var (mx, my) = coords.ToPixel(inst.X, inst.Z, lfW, lfH);
                var sx = (int)((mx - view.OriginX) / view.Scale);
                var sy = (int)((my - view.OriginY) / view.Scale);
                if (sx < -radius || sy < -radius || sx >= width + radius || sy >= height + radius) continue;
                Plot(bgra, width, height, sx, sy, radius, colour);
            }
        }
    }

    private static void Plot(uint[] buffer, int width, int height, int cx, int cy, int radius, uint colour)
    {
        for (var dy = -radius; dy <= radius; dy++)
        {
            var y = cy + dy;
            if ((uint)y >= (uint)height) continue;
            for (var dx = -radius; dx <= radius; dx++)
            {
                var x = cx + dx;
                if ((uint)x >= (uint)width || dx * dx + dy * dy > radius * radius + radius) continue;
                // Dark outline pixel on the rim for contrast at larger sizes.
                buffer[y * width + x] = radius >= 2 && dx * dx + dy * dy > (radius - 1) * (radius - 1) + radius - 1
                    ? 0xFF101010u
                    : colour;
            }
        }
    }
}
