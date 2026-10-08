using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Operations;

/// <summary>
/// Padding to mirror CAIME's Edit → Resize, given in hexes per side. On the 3K main map one hex column is
/// exactly 8 lf pixels (7136 / 892), so every grid stays pixel-aligned:
///   lf (height, blend, climate_map) = 8 px/hex, sea height = 4 px/hex, tree/tile/climate_g = 2 px/hex.
/// Rows use the same 8 px/hex (5620 / 702 = 8.006, within 0.1%).
/// </summary>
public readonly record struct HexPadding(int Left, int Top, int Right, int Bottom)
{
    public const int LfPxPerHex = 8;

    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    /// <summary>Padding in pixels for a grid with <paramref name="pxPerHex"/> pixels per hex.</summary>
    public (int L, int T, int R, int B) Pixels(int pxPerHex) => (Left * pxPerHex, Top * pxPerHex, Right * pxPerHex, Bottom * pxPerHex);

    public HexPadding Add(HexPadding o) => new(Left + o.Left, Top + o.Top, Right + o.Right, Bottom + o.Bottom);
}

public sealed class ExpandOptions
{
    /// <summary>Height for new cells. Default 3084 = the vanilla open-sea floor.</summary>
    public ushort NewHeight { get; set; } = 3084;
    /// <summary>Water surface for new cells. Default 14219 = vanilla sea level (world y ≈ 0).</summary>
    public ushort NewSeaHeight { get; set; } = 14219;
    /// <summary>Copy the edge texture outwards (true) or fill with <see cref="NewBlendGroup"/>.</summary>
    public bool ExtendBlendEdges { get; set; } = true;
    public byte NewBlendGroup { get; set; } = 31;
}

public sealed record ExpandResult(
    HexPadding Padding,
    float OldWorldWidth, float OldWorldHeight,
    float NewWorldWidth, float NewWorldHeight,
    /// <summary>Add to every existing world x / z to keep things in place.</summary>
    double ShiftX, double ShiftZ);

public static class ExpandCanvas
{
    public static ExpandResult Apply(TerrainData terrain, CampaignTreeList? trees, HexPadding pad, ExpandOptions? options = null)
    {
        options ??= new ExpandOptions();
        var oldCoords = terrain.Coords;
        var unitsPerPxX = oldCoords.WorldWidth / terrain.Width;
        var unitsPerPxZ = oldCoords.WorldHeight / terrain.HeightPx;

        var (l, t, r, b) = pad.Pixels(HexPadding.LfPxPerHex);
        var (sl, st, sr, sb) = pad.Pixels(HexPadding.LfPxPerHex / 2);

        terrain.Height = terrain.Height.Pad(l, t, r, b, options.NewHeight);
        terrain.SeaHeight = terrain.SeaHeight.Pad(sl, st, sr, sb, options.NewSeaHeight);
        terrain.BlendGroup = options.ExtendBlendEdges
            ? PadReplicate(terrain.BlendGroup, l, t, r, b)
            : terrain.BlendGroup.Pad(l, t, r, b, options.NewBlendGroup);
        terrain.BlendExtra = terrain.BlendExtra.Pad(l, t, r, b, 0);

        var newCoords = new WorldCoords(
            (float)(terrain.Width * unitsPerPxX),
            (float)(terrain.HeightPx * unitsPerPxZ));
        terrain.Coords = newCoords;

        // World z grows northwards from the bottom row, so only south (bottom) padding moves existing z.
        var shiftX = l * unitsPerPxX;
        var shiftZ = b * unitsPerPxZ;

        if (trees != null)
        {
            trees.WorldWidth = newCoords.WorldWidth;
            trees.WorldHeight = newCoords.WorldHeight;
            if (shiftX != 0 || shiftZ != 0)
                foreach (var type in trees.Types)
                    for (var i = 0; i < type.Instances.Count; i++)
                    {
                        var inst = type.Instances[i];
                        inst.X += (float)shiftX;
                        inst.Z += (float)shiftZ;
                        type.Instances[i] = inst;
                    }
        }

        return new ExpandResult(pad, oldCoords.WorldWidth, oldCoords.WorldHeight,
            newCoords.WorldWidth, newCoords.WorldHeight, shiftX, shiftZ);
    }

    /// <summary>Pads by repeating the nearest edge pixel outwards.</summary>
    public static Raster<T> PadReplicate<T>(Raster<T> src, int left, int top, int right, int bottom) where T : unmanaged
    {
        var dst = new Raster<T>(src.Width + left + right, src.Height + top + bottom);
        for (var y = 0; y < dst.Height; y++)
        {
            var sy = Math.Clamp(y - top, 0, src.Height - 1);
            var srcRow = sy * src.Width;
            var dstRow = y * dst.Width;
            for (var x = 0; x < left; x++) dst.Data[dstRow + x] = src.Data[srcRow];
            Array.Copy(src.Data, srcRow, dst.Data, dstRow + left, src.Width);
            for (var x = left + src.Width; x < dst.Width; x++) dst.Data[dstRow + x] = src.Data[srcRow + src.Width - 1];
        }
        return dst;
    }
}
