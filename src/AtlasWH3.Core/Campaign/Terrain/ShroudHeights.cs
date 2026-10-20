using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.Terrain;

/// <summary>
/// shroud_heights.dds computed from the terrain; the .terry's HeightShroud layers are not read. The fill is Terry's
/// "Fill From Terrain" on a HeightShroud layer (tweak_terrainmetadataeditor 0x1802277c0, read 2026-10-18): each pixel
/// is TerrainSurface::height at its world point plus the offset in Terry's QSettings
/// (project/shroud_fill_from_terrain_offset).
///  - Half the Height map's resolution. Pixel (c, r), row 0 = north, samples its north-west corner: the Height texel
///    (2c, 2r) and the height patches at x = 2c · texel, z = (H − 2r) · texel · 1.15476.
///  - TerrainSurface::height is the composited Height raised by the layers' height patches
///    (<see cref="TreeHeightField.Raised"/>). The mountain props' patches are what lifts the shroud over the ranges.
///  - Offset 1, as CA used.
/// Vanilla Immortal Empires' shroud is this fill, from vanilla's heights, height-patched props and width, to within 1e-3
/// on 83% of its pixels and 88% away from the north and south edges, 91% within 0.5 (ShroudHeightsTests). The rest is
/// CA's own: ground edited after the fill, and walls painted by hand on stretches of the north and south edges (up to
/// about 18 over the fill, following no rule of the terrain) and softer ones along the west and east edges, which are
/// not reproduced.
/// </summary>
public static class ShroudHeights
{
    /// <summary>Terry's fill offset as CA set it for vanilla combi.</summary>
    public const float Offset = 1f;

    /// <summary>How far in from the west edge the shroud is 0 with the project's ZeroShroudWestEdge (world units): the
    /// strip a westward visual extension past x = 0 meets.</summary>
    public const float WestEdgeZeroWidth = 10f;

    /// <summary>The shroud (row 0 = north) from the composited Height map (row 0 = north) and the field holding the
    /// height patches; <paramref name="field"/>'s world width places the patches. The <see cref="ZeroedColumns"/> west
    /// of <paramref name="zeroWestOf"/> are 0 (no offset).</summary>
    public static Raster<float> Compute(Raster<float> land, TreeHeightField field, float zeroWestOf = 0)
    {
        int w = land.Width, h = land.Height, sw = (w + 1) / 2, sh = (h + 1) / 2;
        var texel = field.WorldWidth / w;
        var zeroed = ZeroedColumns(land, field, zeroWestOf);
        var zTexel = texel * TreeHeightField.ZScale;
        var shroud = new Raster<float>(sw, sh);
        Parallel.For(0, sh, r =>
        {
            var z = (h - 2 * r) * zTexel;
            var row = 2 * r * w;
            for (var c = 0; c < sw; c++)
                shroud.Data[r * sw + c] = c < zeroed ? 0 : field.Raised(2 * c * texel, z, land.Data[row + 2 * c]) + Offset;
        });
        return shroud;
    }

    /// <summary>How many of the shroud's columns, from the west, sample a point west of <paramref name="zeroWestOf"/>
    /// (<see cref="Compute"/>'s zeroed columns).</summary>
    public static int ZeroedColumns(Raster<float> land, TreeHeightField field, float zeroWestOf)
    {
        int sw = (land.Width + 1) / 2, n = 0;
        var texel = field.WorldWidth / land.Width;
        while (n < sw && 2 * n * texel < zeroWestOf) n++;
        return n;
    }
}
