namespace AtlasWH3.Core.Campaign.Terrain;

/// <summary>3K's lf height raster (LowFrequencyHeight, u16) in world units. The scene editor, terrain tools, lake
/// commands and map audit still read 3K's lf TIF; they move to WH3's float Height maps with the editors' port (Phase 5).</summary>
public static class Lf3K
{
    /// <summary>World units per lf pixel (x, z); the same on every 3K map (vanilla 595.1 / 7136, 541.786 / 5620).</summary>
    public const double PixelSizeX = 595.1 / 7136, PixelSizeZ = 541.78619 / 5620;
    /// <summary>Source u16 → world y (fitted exactly on the vanilla land meshes).</summary>
    public const double HeightStep = 0.000218712, HeightOffset = -3.12725;
}
