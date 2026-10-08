using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Undo record for tree edits: snapshots the type list and the instance lists of every species a stroke
/// touched, so undo/redo restores them exactly.
/// </summary>
public sealed class TreeStroke : IUndoable
{
    private readonly CampaignTreeList _trees;
    private readonly List<TreeType> _typesBefore;
    private readonly Dictionary<TreeType, List<TreeInstance>> _before = new();
    private List<TreeType>? _typesAfter;
    private Dictionary<TreeType, List<TreeInstance>>? _after;

    public string Description { get; }
    public bool IsEmpty => _before.Count == 0;

    public TreeStroke(CampaignTreeList trees, string description)
    {
        _trees = trees;
        Description = description;
        _typesBefore = trees.Types.ToList();
    }

    /// <summary>Call before modifying a species' instance list.</summary>
    public void Capture(TreeType type)
    {
        if (!_before.ContainsKey(type))
            _before[type] = type.Instances.ToList();
    }

    public void Finish()
    {
        _typesAfter = _trees.Types.ToList();
        _after = _before.Keys.ToDictionary(t => t, t => t.Instances.ToList());
    }

    public void Undo() => Restore(_typesBefore, _before);
    public void Redo() => Restore(_typesAfter!, _after!);

    private void Restore(List<TreeType> types, Dictionary<TreeType, List<TreeInstance>> lists)
    {
        _trees.Types.Clear();
        _trees.Types.AddRange(types);
        foreach (var (type, instances) in lists)
        {
            type.Instances.Clear();
            type.Instances.AddRange(instances);
        }
    }
}

/// <summary>World positions where trees were added/removed this session; the AK tree paint is updated there on export.</summary>
public sealed class TreeEditLog
{
    private readonly List<(float X, float Z)> _points = new();
    public IReadOnlyList<(float X, float Z)> Points => _points;
    public bool IsEmpty => _points.Count == 0;
    public void Add(float x, float z) => _points.Add((x, z));
    public void Clear() => _points.Clear();

    /// <summary>Keeps logged positions in step with a canvas expansion.</summary>
    public void Shift(double dx, double dz)
    {
        for (var i = 0; i < _points.Count; i++)
            _points[i] = ((float)(_points[i].X + dx), (float)(_points[i].Z + dz));
    }
}

public enum TreeBrushMode { Place, Scatter, Erase }

/// <summary>
/// Adds or removes campaign tree instances. Works in lf map pixels like the terrain brushes; positions are
/// converted to world units with the terrain's current <see cref="WorldCoords"/>. New trees get their
/// height from the heightmap, a random variant (0-5) and the season list used by existing trees of that
/// species (or derived from campaign_tree_variants).
/// </summary>
public sealed class TreeBrush : TerrainBrush
{
    private readonly CampaignTreeList _trees;
    private readonly TreeDatabase? _db;
    private readonly TreeEditLog _log;
    private readonly Random _random = new();
    private TreeStroke? _stroke;
    private double _lastPlaceX = double.NaN, _lastPlaceY;

    public TreeBrushMode Mode { get; set; } = TreeBrushMode.Scatter;
    /// <summary>Species placed by Place/Scatter.</summary>
    public string Species { get; set; } = "temperate_tree_katsura_medium_1";
    /// <summary>Trees per square world unit at full strength (vanilla dense forest is roughly 8-15).</summary>
    public double Density { get; set; } = 6;
    /// <summary>Minimum distance between trees, in world units.</summary>
    public double MinSpacing { get; set; } = 0.18;
    public bool AvoidWater { get; set; } = true;
    /// <summary>Erase only this species (null = every visible species).</summary>
    public string? EraseOnlySpecies { get; set; }
    /// <summary>Species that are hidden in the view are left alone by Erase.</summary>
    public ISet<string>? HiddenSpecies { get; set; }

    public TreeBrush(CampaignTreeList trees, TreeDatabase? db, TreeEditLog log)
    {
        _trees = trees;
        _db = db;
        _log = log;
    }

    public override string Name => Mode switch
    {
        TreeBrushMode.Place => $"Place {Species}",
        TreeBrushMode.Scatter => $"Scatter {Species}",
        _ => "Erase trees",
    };

    public override void Begin(TerrainData terrain, double mx, double my)
    {
        base.Begin(terrain, mx, my);
        _stroke = new TreeStroke(_trees, Name);
        _lastPlaceX = double.NaN;
    }

    public override PixelRect Dab(double mx, double my)
    {
        var rect = Bounds(mx, my, Terrain.Width, Terrain.HeightPx);
        switch (Mode)
        {
            case TreeBrushMode.Place: PlaceSingle(mx, my); break;
            case TreeBrushMode.Scatter: Scatter(mx, my); break;
            case TreeBrushMode.Erase: Erase(mx, my); break;
        }
        return rect;
    }

    public override IUndoable? End()
    {
        var stroke = _stroke;
        _stroke = null;
        if (stroke == null || stroke.IsEmpty) return null;
        stroke.Finish();
        return stroke;
    }

    private (double X, double Z) ToWorld(double mx, double my) =>
        Terrain.Coords.ToWorld(mx, my, Terrain.Width, Terrain.HeightPx);

    private double WorldRadius => Radius * Terrain.Coords.WorldWidth / Terrain.Width;

    private void PlaceSingle(double mx, double my)
    {
        // While dragging, place one tree every MinSpacing*3 world units.
        var (x, z) = ToWorld(mx, my);
        if (!double.IsNaN(_lastPlaceX))
        {
            var d = Math.Sqrt((x - _lastPlaceX) * (x - _lastPlaceX) + (z - _lastPlaceY) * (z - _lastPlaceY));
            if (d < MinSpacing * 3) return;
        }
        if (TryAdd(Species, x, z, Neighbours(x, z, MinSpacing * 2)))
        {
            _lastPlaceX = x;
            _lastPlaceY = z;
        }
    }

    private void Scatter(double mx, double my)
    {
        var (cx, cz) = ToWorld(mx, my);
        var r = WorldRadius;
        var nearby = Neighbours(cx, cz, r + MinSpacing);
        // Trees per dab ≈ density * area * strength, spread over the dabs of a stroke (spacing = radius/4).
        var expected = Density * Math.PI * r * r * Strength * 0.25;
        var count = (int)expected + (_random.NextDouble() < expected - (int)expected ? 1 : 0);
        for (var i = 0; i < count; i++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var dist = Math.Sqrt(_random.NextDouble()) * r;
            if (_random.NextDouble() > Falloff(dist / r * Radius)) continue;
            var x = cx + Math.Cos(angle) * dist;
            var z = cz + Math.Sin(angle) * dist;
            TryAdd(Species, x, z, nearby);
        }
    }

    private void Erase(double mx, double my)
    {
        var (cx, cz) = ToWorld(mx, my);
        var r = WorldRadius;
        var r2 = r * r;
        foreach (var type in _trees.Types)
        {
            if (EraseOnlySpecies != null && !string.Equals(type.Name, EraseOnlySpecies, StringComparison.OrdinalIgnoreCase)) continue;
            if (HiddenSpecies != null && HiddenSpecies.Contains(type.Name)) continue;
            var list = type.Instances;
            List<TreeInstance>? kept = null;
            for (var i = 0; i < list.Count; i++)
            {
                var t = list[i];
                var dx = t.X - cx;
                var dz = t.Z - cz;
                var inside = dx * dx + dz * dz <= r2 &&
                             (Strength >= 0.99 || _random.NextDouble() < Strength * Falloff(Math.Sqrt(dx * dx + dz * dz) / r * Radius));
                if (inside)
                {
                    if (kept == null)
                    {
                        _stroke!.Capture(type);
                        kept = list.Take(i).ToList();
                    }
                    _log.Add(t.X, t.Z);
                }
                else kept?.Add(t);
            }
            if (kept != null)
            {
                list.Clear();
                list.AddRange(kept);
            }
        }
    }

    /// <summary>Existing trees (any species) within <paramref name="radius"/> of a point, for spacing checks.</summary>
    private List<(float X, float Z)> Neighbours(double x, double z, double radius)
    {
        var result = new List<(float, float)>();
        var r2 = radius * radius;
        foreach (var type in _trees.Types)
            foreach (var t in type.Instances)
            {
                var dx = t.X - x;
                var dz = t.Z - z;
                if (dx * dx + dz * dz <= r2) result.Add((t.X, t.Z));
            }
        return result;
    }

    private bool TryAdd(string species, double x, double z, List<(float X, float Z)> nearby)
    {
        if (x < 0 || z < 0 || x > Terrain.Coords.WorldWidth || z > Terrain.Coords.WorldHeight) return false;
        var s2 = MinSpacing * MinSpacing;
        foreach (var (nx, nz) in nearby)
            if ((nx - x) * (nx - x) + (nz - z) * (nz - z) < s2) return false;

        var (col, row) = Terrain.Coords.ToPixel(x, z, Terrain.Width, Terrain.HeightPx);
        var ix = Math.Clamp((int)col, 0, Terrain.Width - 1);
        var iy = Math.Clamp((int)row, 0, Terrain.HeightPx - 1);
        var land = Terrain.Height[ix, iy];
        if (AvoidWater && Terrain.SeaAt(ix, iy) > land) return false;

        var type = _trees.Types.FirstOrDefault(t => string.Equals(t.Name, species, StringComparison.OrdinalIgnoreCase));
        if (type == null)
        {
            type = new TreeType { Name = species };
            _trees.Types.Add(type);
        }
        _stroke!.Capture(type);
        type.Instances.Add(new TreeInstance
        {
            X = (float)x,
            Y = (float)HeightScale.ToWorld(land),
            Z = (float)z,
            Flag = 1,
            Variant = (byte)_random.Next(6),
            Seasons = SeasonsFor(type),
        });
        nearby.Add(((float)x, (float)z));
        _log.Add((float)x, (float)z);
        return true;
    }

    private uint[] SeasonsFor(TreeType type)
    {
        // Existing instances of the species are the most reliable source (matches what BOB wrote).
        foreach (var t in type.Instances)
            if (t.Seasons is { Length: > 0 }) return (uint[])t.Seasons.Clone();
        return _db?.SeasonsFor(type.Name) ?? [0, 1, 2, 3, 4];
    }
}

public static class TreeBulkOps
{
    /// <summary>Removes every instance of a species. Returns the undo record (null if nothing removed).</summary>
    public static IUndoable? RemoveSpecies(CampaignTreeList trees, string species, TreeEditLog log)
    {
        var type = trees.Types.FirstOrDefault(t => string.Equals(t.Name, species, StringComparison.OrdinalIgnoreCase));
        if (type == null || type.Instances.Count == 0) return null;
        var stroke = new TreeStroke(trees, $"Remove all {species}");
        stroke.Capture(type);
        foreach (var t in type.Instances) log.Add(t.X, t.Z);
        type.Instances.Clear();
        stroke.Finish();
        return stroke;
    }
}
