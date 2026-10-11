using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Terrain;

/// <summary>
/// A WH3 campaign project's ground as the editors see it: the composited Height map (<see cref="TerrainComposite"/>,
/// the full_logic_map's source) in world units, row 0 = north. It covers x 0..<see cref="WorldW"/> and
/// z 0..<see cref="WorldH"/>: the width is the .terry's world_width, the depth h · (width / w) ·
/// <see cref="TreeHeightField.ZScale"/> (square texels in tile space, as BOB's terrain provider maps the logic map).
/// <para>
/// The sea is not a raster: it is the water-plane props at sea level (y = 0, <see cref="SeaLevel"/>). HeightSea is the
/// ground of the tiles that use the alternative lf (the sea tiles), not a water surface.
/// </para>
/// </summary>
public sealed class CampaignTerrain
{
    /// <summary>World y of the sea's water planes.</summary>
    public const float SeaLevel = 0f;

    /// <summary>Composited Height (world y), row 0 = north.</summary>
    public Raster<float> Land { get; }

    public double WorldW { get; }
    public double WorldH { get; }

    public CampaignTerrain(Raster<float> land, double worldW)
    {
        Land = land;
        WorldW = worldW;
        WorldH = DepthOf(land.Width, land.Height, worldW);
    }

    /// <summary>World depth of a map raster <paramref name="w"/> × <paramref name="h"/> over a world
    /// <paramref name="worldW"/> wide.</summary>
    public static double DepthOf(int w, int h, double worldW) => h * (worldW / w) * TreeHeightField.ZScale;

    public void Deconstruct(out Raster<float> land, out double worldW, out double worldH) =>
        (land, worldW, worldH) = (Land, WorldW, WorldH);

    /// <summary>Composites the project's Height map; <paramref name="worldWidth"/> is the .terry's world_width, else
    /// map_data.esf's width.</summary>
    public static CampaignTerrain Load(TerryProject project, double worldWidth) =>
        new(TerrainComposite.Heights(project, "Height"), worldWidth);

    /// <summary>Land height at a world point, bilinear between texel centres.</summary>
    public double At(double x, double z)
    {
        var h = Land;
        var col = Math.Clamp(x / WorldW * h.Width - 0.5, 0, h.Width - 1.001);
        var row = Math.Clamp((1 - z / WorldH) * h.Height - 0.5, 0, h.Height - 1.001);
        int c0 = (int)col, r0 = (int)row;
        double fx = col - c0, fz = row - r0;
        return h[c0, r0] * (1 - fx) * (1 - fz) + h[c0 + 1, r0] * fx * (1 - fz) + h[c0, r0 + 1] * (1 - fx) * fz + h[c0 + 1, r0 + 1] * fx * fz;
    }

    /// <summary>Land height of the texel under a world point (clamped to the raster).</summary>
    public float Nearest(double x, double z)
    {
        var h = Land;
        var col = Math.Clamp((int)(x / WorldW * h.Width), 0, h.Width - 1);
        var row = Math.Clamp((int)((1 - z / WorldH) * h.Height), 0, h.Height - 1);
        return h[col, row];
    }
}
