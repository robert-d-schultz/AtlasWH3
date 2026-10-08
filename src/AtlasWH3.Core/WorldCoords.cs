namespace AtlasWH3.Core;

/// <summary>
/// Converts between campaign world coordinates and raster pixels. World x runs west→east over
/// [0, WorldWidth]; world z runs south→north over [0, WorldHeight]; row 0 of every raster is north.
/// Each axis is scaled independently (the rasters are not square-pixelled in world units).
/// Verified by correlating tree heights against lf_height_map (r = 0.998).
/// </summary>
public readonly record struct WorldCoords(float WorldWidth, float WorldHeight)
{
    public static readonly WorldCoords Vanilla3K = new(595.1f, 541.78619f);

    public (double Col, double Row) ToPixel(double x, double z, int rasterWidth, int rasterHeight) =>
        (x / WorldWidth * rasterWidth, (1.0 - z / WorldHeight) * rasterHeight);

    public (double X, double Z) ToWorld(double col, double row, int rasterWidth, int rasterHeight) =>
        (col / rasterWidth * WorldWidth, (1.0 - row / rasterHeight) * WorldHeight);
}

/// <summary>lf_height_map u16 → world height (y). Fitted against vanilla tree y values (residual σ 0.18).</summary>
public static class HeightScale
{
    public const double UnitsPerStep = 0.00021849;
    public const double Offset = -3.12;

    public static double ToWorld(ushort h) => h * UnitsPerStep + Offset;
    public static ushort FromWorld(double y) => (ushort)Math.Clamp(Math.Round((y - Offset) / UnitsPerStep), 0, 65535);
}
