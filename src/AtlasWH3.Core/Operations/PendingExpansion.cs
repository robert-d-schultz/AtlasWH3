namespace AtlasWH3.Core.Operations;

/// <summary>
/// Expansion applied in the editor but not yet written to the assembly kit. Accumulates when the canvas is
/// expanded several times; cleared after a successful AK export.
/// </summary>
public sealed class PendingExpansion
{
    public HexPadding Padding { get; private set; }
    public double ShiftX { get; private set; }
    public double ShiftZ { get; private set; }
    public float NewWorldWidth { get; private set; }
    public float NewWorldHeight { get; private set; }

    public bool IsEmpty => Padding.IsZero;

    public void Add(ExpandResult result)
    {
        Padding = Padding.Add(result.Padding);
        ShiftX += result.ShiftX;
        ShiftZ += result.ShiftZ;
        NewWorldWidth = result.NewWorldWidth;
        NewWorldHeight = result.NewWorldHeight;
    }

    public void Clear()
    {
        Padding = default;
        ShiftX = ShiftZ = 0;
    }

    /// <summary>The DB values that must match the new world size (3K main map).</summary>
    public string DescribeDbChanges(string mapName) =>
        $"New world size: {NewWorldWidth:F3} x {NewWorldHeight:F3} (world units)\n" +
        $"Existing content shifted by x +{ShiftX:F3}, z +{ShiftZ:F3}\n\n" +
        $"Update these DB tables for '{mapName}':\n" +
        $"  campaign_maps: width/height -> {Math.Ceiling(NewWorldWidth)} x {Math.Ceiling(NewWorldHeight)}\n" +
        $"  campaign_map_playable_areas: max x/y bounds -> {NewWorldWidth:F1} / {NewWorldHeight:F1} (shift min bounds by the offsets above)\n" +
        $"  campaign_camera_map_bounds: extend by the same offsets\n" +
        $"In CAIME: Edit > Resize with padding L{Padding.Left} T{Padding.Top} R{Padding.Right} B{Padding.Bottom} hexes, then re-process.";
}
