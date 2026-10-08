using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Battle;

/// <summary>
/// A battle-map editing tool. Coordinates are in view pixels = land-height pixels (<see cref="BattleProject.LandPerCell"/>
/// per tile-map cell). A stroke is Begin → Dab* → End; End returns the undo record.
/// </summary>
public interface IBattleTool
{
    string Name { get; }
    /// <summary>Brush radius in view pixels (for the cursor outline).</summary>
    double ViewRadius { get; }
    void Begin(BattleProject project, double vx, double vy);
    void Dab(double vx, double vy);
    IUndoable? End();
}

/// <summary>Paints one exact palette colour into tile_map.png (hard round brush, no blending: BOB needs exact colours).</summary>
public sealed class TilePaintTool : IBattleTool
{
    /// <summary>Colour to paint, 0xRRGGBB.</summary>
    public uint Rgb { get; set; }
    /// <summary>Brush radius in cells (0.5 = a single cell).</summary>
    public double RadiusCells { get; set; } = 2;
    /// <summary>When set, only cells of this colour (0xRRGGBB) are repainted.</summary>
    public uint? OnlyReplace { get; set; }
    /// <summary>Repaint only cells of the colour under the start of each stroke.</summary>
    public bool ReplaceColourUnderStart { get; set; }

    private BattleProject _project = null!;
    private RasterStroke<uint>? _stroke;

    public string Name => $"Paint tile map #{Rgb:X6}";
    public double ViewRadius => RadiusCells * (_project?.LandPerCell ?? 8);

    public void Begin(BattleProject project, double vx, double vy)
    {
        _project = project;
        _stroke = new RasterStroke<uint>(project.TileMap, Name);
        var cx = (int)(vx / project.LandPerCell);
        var cy = (int)(vy / project.LandPerCell);
        if (ReplaceColourUnderStart)
            OnlyReplace = project.TileMap.Contains(cx, cy) ? BattleProject.RgbAt(project.TileMap, cx, cy) : null;
    }

    public void Dab(double vx, double vy)
    {
        var map = _project.TileMap;
        var cx = vx / _project.LandPerCell;
        var cy = vy / _project.LandPerCell;
        var r = Math.Max(0.5, RadiusCells);
        int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(map.Width - 1, (int)Math.Floor(cx + r));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(map.Height - 1, (int)Math.Floor(cy + r));
        if (x0 > x1 || y0 > y1) return;
        _stroke!.Capture(x0, y0, x1, y1);
        var pixel = BattleProject.ToPixel(Rgb);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var dx = x + 0.5 - cx;
            var dy = y + 0.5 - cy;
            if (dx * dx + dy * dy > r * r) continue;
            if (OnlyReplace is { } only && BattleProject.RgbAt(map, x, y) != only) continue;
            map[x, y] = pixel;
        }
    }

    public IUndoable? End()
    {
        var stroke = _stroke;
        _stroke = null;
        if (stroke == null || stroke.IsEmpty) return null;
        stroke.Finish();
        return stroke;
    }
}

public enum HeightTarget { Land, Sea }

/// <summary>Runs a campaign <see cref="HeightBrush"/> on the battle land or sea raster, scaling view coordinates to it.</summary>
public sealed class HeightTool(HeightBrush brush) : IBattleTool
{
    public HeightBrush Brush { get; } = brush;
    public HeightTarget Target { get; set; } = HeightTarget.Land;
    /// <summary>Brush radius in cells.</summary>
    public double RadiusCells { get; set; } = 4;

    private double _scale = 1;    // target pixels per view pixel
    private BattleProject? _project;

    public string Name => $"{Target} height: {Brush.Mode}";
    public double ViewRadius => RadiusCells * (_project?.LandPerCell ?? 8);

    public void Begin(BattleProject project, double vx, double vy)
    {
        _project = project;
        var terrain = Target == HeightTarget.Land ? project.LandTerrain : project.SeaTerrain;
        _scale = (double)terrain.Width / project.Land.Width;
        Brush.Radius = Math.Max(1, RadiusCells * project.LandPerCell * _scale);
        Brush.Begin(terrain, vx * _scale, vy * _scale);
    }

    public void Dab(double vx, double vy) => Brush.Dab(vx * _scale, vy * _scale);

    public IUndoable? End() => Brush.End();
}

/// <summary>Flood fill of one colour region in tile_map.png (4-connected), for recolouring a whole patch.</summary>
public static class TileFill
{
    public static IUndoable? Fill(BattleProject project, int cx, int cy, uint rgb)
    {
        var map = project.TileMap;
        if (!map.Contains(cx, cy)) return null;
        var from = BattleProject.RgbAt(map, cx, cy);
        if (from == rgb) return null;
        var pixel = BattleProject.ToPixel(rgb);
        var stroke = new RasterStroke<uint>(map, $"Fill #{rgb:X6}");
        var stack = new Stack<(int, int)>();
        stack.Push((cx, cy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (!map.Contains(x, y) || BattleProject.RgbAt(map, x, y) != from) continue;
            stroke.Capture(x, y, x, y);
            map[x, y] = pixel;
            stack.Push((x + 1, y)); stack.Push((x - 1, y)); stack.Push((x, y + 1)); stack.Push((x, y - 1));
        }
        stroke.Finish();
        return stroke;
    }
}
