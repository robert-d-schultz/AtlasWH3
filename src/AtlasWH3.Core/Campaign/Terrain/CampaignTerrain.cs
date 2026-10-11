using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Terrain;

/// <summary>
/// A WH3 campaign project's ground as the editors see it: the composited Height and HeightSea maps
/// (<see cref="TerrainComposite"/>) in world units, row 0 = north. They cover x 0..<see cref="WorldW"/> and
/// z 0..<see cref="WorldH"/>: the width is the .terry's world_width, the depth h · (width / w) ·
/// <see cref="TreeHeightField.ZScale"/> (square texels in tile space, as BOB's terrain provider maps the logic map).
/// <para>
/// The game draws each tile on one of the two: the tiles that use the alternative lf (the sea tiles, tile_list.bin)
/// on HeightSea, every other tile on Height (<see cref="Shown"/>). The sea itself is the water-plane props at sea level
/// (y = 0, <see cref="SeaLevel"/>). On IEE HeightSea is about −0.9 almost everywhere and Height about +0.5 under the
/// open sea, so drawing Height alone puts the sea above its water.
/// </para>
/// </summary>
public sealed class CampaignTerrain
{
    /// <summary>World y of the sea's water planes.</summary>
    public const float SeaLevel = 0f;

    /// <summary>Composited Height (world y), row 0 = north.</summary>
    public Raster<float> Land { get; }

    /// <summary>Composited HeightSea (world y), the same size as <see cref="Land"/>; null when the project has none.</summary>
    public Raster<float>? SeaFloor { get; }

    /// <summary>What covers each tile-map cell (row 0 = south, as tile_list.bin); null until a tile list is known
    /// (everything is drawn on Height).</summary>
    public (Cover[] Cells, int Width, int Height)? Tiles { get; set; }

    /// <summary>A tile-map cell: no tile (a void, black in tile_map.png), a tile on Height, or one on HeightSea.</summary>
    public enum Cover : byte { None, Land, AltLf }

    /// <summary>The tile-map cover of a pixel; <see cref="Cover.Land"/> without a tile list.</summary>
    public Cover CoverAt(int col, int row)
    {
        if (Tiles is not var (cells, w, h)) return Cover.Land;
        var tx = col * w / Land.Width;
        var ty = h - 1 - row * h / Land.Height;
        return (uint)tx < (uint)w && (uint)ty < (uint)h ? cells[ty * w + tx] : Cover.None;
    }

    public double WorldW { get; }
    public double WorldH { get; }

    public CampaignTerrain(Raster<float> land, Raster<float>? seaFloor, double worldW)
    {
        if (seaFloor is not null && (seaFloor.Width, seaFloor.Height) != (land.Width, land.Height))
            throw new InvalidDataException($"HeightSea is {seaFloor.Width}x{seaFloor.Height}, Height is {land.Width}x{land.Height}");
        Land = land;
        SeaFloor = seaFloor;
        WorldW = worldW;
        WorldH = DepthOf(land.Width, land.Height, worldW);
    }

    /// <summary>World depth of a map raster <paramref name="w"/> × <paramref name="h"/> over a world
    /// <paramref name="worldW"/> wide.</summary>
    public static double DepthOf(int w, int h, double worldW) => h * (worldW / w) * TreeHeightField.ZScale;

    public void Deconstruct(out Raster<float> land, out double worldW, out double worldH) =>
        (land, worldW, worldH) = (Land, WorldW, WorldH);

    /// <summary>Composites the project's Height and HeightSea maps; <paramref name="worldWidth"/> is the .terry's
    /// world_width, else map_data.esf's width.</summary>
    public static CampaignTerrain Load(TerryProject project, double worldWidth) =>
        new(TerrainComposite.Heights(project, "Height"),
            project.Find("HeightSea") is not null ? TerrainComposite.Heights(project, "HeightSea") : null, worldWidth);

    /// <summary>The ground the game draws at a pixel: HeightSea under an alt-lf (sea) tile, else Height (also under a
    /// void, see <see cref="CoverAt"/>).</summary>
    public float Shown(int col, int row) =>
        SeaFloor is { } sea && CoverAt(col, row) == Cover.AltLf ? sea[col, row] : Land[col, row];

    /// <summary>Shown ground at a world point, bilinear between texel centres.</summary>
    public double At(double x, double z)
    {
        var h = Land;
        var col = Math.Clamp(x / WorldW * h.Width - 0.5, 0, h.Width - 1.001);
        var row = Math.Clamp((1 - z / WorldH) * h.Height - 0.5, 0, h.Height - 1.001);
        int c0 = (int)col, r0 = (int)row;
        double fx = col - c0, fz = row - r0;
        return Shown(c0, r0) * (1 - fx) * (1 - fz) + Shown(c0 + 1, r0) * fx * (1 - fz) + Shown(c0, r0 + 1) * (1 - fx) * fz + Shown(c0 + 1, r0 + 1) * fx * fz;
    }

    /// <summary>Shown ground of the texel under a world point (clamped to the raster).</summary>
    public float Nearest(double x, double z)
    {
        var h = Land;
        var col = Math.Clamp((int)(x / WorldW * h.Width), 0, h.Width - 1);
        var row = Math.Clamp((int)((1 - z / WorldH) * h.Height), 0, h.Height - 1);
        return Shown(col, row);
    }
}
