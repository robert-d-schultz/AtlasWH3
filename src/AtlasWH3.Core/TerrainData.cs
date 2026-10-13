using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core;

/// <summary>
/// The editable terrain rasters of one campaign map, in the compiled (Vanilla) resolutions:
/// land height and blend at 7136x5620 ("lf" grid), sea surface at half resolution.
/// </summary>
public sealed class TerrainData
{
    /// <summary>Land/sea-floor height, u16 → world y via <see cref="HeightScale"/>.</summary>
    public Raster<ushort> Height { get; set; }
    /// <summary>Water surface height (half-res). Water exists wherever this is above the land height.</summary>
    public Raster<ushort> SeaHeight { get; set; }
    /// <summary>Texture group index per lf pixel (texture_arrays.xml order).</summary>
    public Raster<byte> BlendGroup { get; set; }
    /// <summary>global_blend channel 1 (0-3). Meaning unknown; preserved as-is.</summary>
    public Raster<byte> BlendExtra { get; set; }
    public TextureArrays TextureArrays { get; }
    public WorldCoords Coords { get; set; }

    public int Width => Height.Width;
    public int HeightPx => Height.Height;

    public TerrainData(Raster<ushort> height, Raster<ushort> seaHeight, Raster<byte> blendGroup,
                       Raster<byte> blendExtra, TextureArrays textureArrays, WorldCoords coords)
    {
        Height = height;
        SeaHeight = seaHeight;
        BlendGroup = blendGroup;
        BlendExtra = blendExtra;
        TextureArrays = textureArrays;
        Coords = coords;
    }

    /// <summary>Water surface at an lf pixel (sea raster is half resolution).</summary>
    public ushort SeaAt(int lfX, int lfY) =>
        SeaHeight.GetClamped(lfX * SeaHeight.Width / Width, lfY * SeaHeight.Height / HeightPx);
}
