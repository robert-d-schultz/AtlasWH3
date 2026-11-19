using System.Numerics;

namespace AtlasWH3.Core.Rendering;

/// <summary>
/// Where a placed campaign tile's own textures (blend0.dds, normal.dds) lie on the map: the scene's 3D view draws
/// with this, and <see cref="TileRoadCheck"/> measures it.
/// <para>
/// From warscape's TERRAIN_RENDER_SETUP::get_tile_transform: tile space is 128 units per tile-map cell, the tile's
/// south-west corner at (X·128, Y·128), x east and z north; for the orientations 0x10/0x20/0x40/0x80 (0°/90°/180°/270°)
/// the tile point (a, b) lands at (a, b), (b, W−a), (W−a, H−b), (H−b, a) from that corner, W×H the unturned size.
/// World x/z then scale by the cell size / 128, y by the x scale.
/// </para>
/// <para>
/// blend0.dds ((W+2)·128 px) and normal.dds ((W+2)·64) carry a one-cell apron round the tile, so tile cell (a, b) is at
/// uv ((a + 1) / (W + 2), (b + 1) / (H + 2)), with image row 0 the tile's south edge.
/// </para>
/// </summary>
public static class TileGroundMapping
{
    /// <summary>Tile space → world (row vectors). <paramref name="x"/>, <paramref name="y"/>: the placed rectangle's
    /// south-west cell; <paramref name="tileW"/>, <paramref name="tileH"/>: the unturned size; <paramref name="sx"/>,
    /// <paramref name="sz"/>: world units per tile unit (cell size / 128).</summary>
    public static Matrix4x4 TileToWorld(int x, int y, int tileW, int tileH, byte orientation, float sx, float sz, float baseY = 0)
    {
        float w = tileW * 128f, h = tileH * 128f, px = x * 128f, pz = y * 128f, sy = sx;
        var (r0, r2, tx, tz) = (orientation & 0xF0) switch
        {
            0x20 => (new Vector3(0, 0, -sz), new Vector3(sx, 0, 0), sx * px, sz * (w + pz)),
            0x40 => (new Vector3(-sx, 0, 0), new Vector3(0, 0, -sz), sx * (w + px), sz * (h + pz)),
            0x80 => (new Vector3(0, 0, sz), new Vector3(-sx, 0, 0), sx * (h + px), sz * pz),
            _ => (new Vector3(sx, 0, 0), new Vector3(0, 0, sz), sx * px, sz * pz),
        };
        return new Matrix4x4(r0.X, r0.Y, r0.Z, 0, 0, sy, 0, 0, r2.X, r2.Y, r2.Z, 0, tx, baseY, tz, 1);
    }

    /// <summary>The blend0 / normal.dds coordinate of a tile-space point (v = 0 is image row 0, the south edge).</summary>
    public static Vector2 Uv(Vector3 tileLocal, int tileW, int tileH) =>
        new((tileLocal.X / 128f + 1) / (tileW + 2), (tileLocal.Z / 128f + 1) / (tileH + 2));
}
