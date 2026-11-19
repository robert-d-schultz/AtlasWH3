using System.Numerics;
using AtlasWH3.Formats.Dds;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Rendering;

/// <summary>
/// Measures whether the roads the scene's 3D view paints from the tiles' blend0.dds join up across tile borders. Along
/// every edge of every placed tile whose texture set names a road group, it samples the road just inside the edge; each
/// run of road there is a road end. A road end is <b>joined</b> when the tile across the edge has road at that point
/// too, <b>missed</b> when that tile has road layers but no road there, and <b>open</b> when the tile across has no road
/// layer at all (the tile map ends the road there, or a settlement or bridge takes over) or there is no tile.
/// <see cref="Mapping"/> picks the reading of the texture placement: the one the view draws, or a variant to compare.
/// </summary>
public sealed class TileRoadCheck
{
    public enum Mapping { Drawn, RowsFlipped, TurnsSwapped, NoApron }

    /// <param name="TileEdge">The edge in the tile's own space (south = b 0, before the turn).</param>
    /// <param name="Offset">Signed distance (cells) from this end's middle to the middle of the nearest road run across the
    /// edge, along the tile's own a (south/north edges) or b (west/east edges) axis; NaN when none is within a cell.</param>
    public sealed record RoadEnd(string Tile, string Path, int X, int Y, int Degrees, string Edge, float At, float Width,
        string? Across, float Overlap, float Gap, string TileEdge = "", float Offset = float.NaN)
    {
        public bool FacesRoad => Across is not null && !Across.EndsWith("(no road layer)", StringComparison.Ordinal);

        /// <summary>open (no road tile across), joined (the road across overlaps half this end or lies within 0.1 cell of
        /// its middle: a road crossing the edge at a shallow angle, or with soft sides, overlaps little while lining up),
        /// offset (0.1–0.3 cell) or missed.</summary>
        public string Grade => !FacesRoad ? "open" : Overlap >= 0.5f || Gap <= 0.1f ? "joined" : Gap <= 0.3f ? "offset" : "missed";
    }

    public sealed record Result(Mapping Mapping, int RoadTiles, IReadOnlyList<RoadEnd> Ends)
    {
        public int Count(string grade) => Ends.Count(e => e.Grade == grade);

        /// <summary>Joined among the road ends that face a tile with road layers.</summary>
        public double JoinedShare => Ends.Count(e => e.FacesRoad) is var facing && facing == 0 ? 0 : (double)Count("joined") / facing;
    }

    private sealed record Placed(int Index, int X, int Y, int W, int H, int TileW, int TileH, byte Orientation, string Name,
        string Path, int[] RoadSlots, Matrix4x4 ToTile);

    private const int SamplesPerCell = 32;
    private const float Inset = 0.03f;          // cells inside / outside an edge where the road is sampled

    private readonly List<Placed> _tiles = [];
    private readonly int[] _top;                // per cell: the tile drawn on top (a feature over a base tile), -1 none
    private readonly int _gw, _gh;
    private readonly Func<string, byte[]?> _read;
    private readonly Dictionary<string, DdsTexture.Image?> _blend = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="isRoadLayer">Which texture_arrays groups are road (the check's own rule, e.g. the name holds "road").</param>
    public TileRoadCheck(TileList list, CampaignTileDatabase db, Func<string, byte[]?> read, Func<string, bool> isRoadLayer, Mapping mapping = Mapping.Drawn)
    {
        _read = read;
        _gw = list.Ints[1];
        _gh = list.Ints[2];
        static string Key(string p) => p.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var byFolder = new Dictionary<string, (CampaignTile Tile, IReadOnlyList<string> Layers)>();
        foreach (var t in db.Tiles)
            foreach (var v in t.Variations)
                byFolder.TryAdd(Key(v.Location), (t, v.TextureLayers));
        _top = new int[_gw * _gh];
        Array.Fill(_top, -1);
        var placed = new List<(Placed P, bool Base)>();
        foreach (var r in list.Records)
        {
            var path = Key(list.Paths[(int)r.Path]);
            if (!byFolder.TryGetValue(path, out var entry)) continue;
            int tw = entry.Tile.Width, th = entry.Tile.Height;
            var (w, h) = (r.Orientation & 0xA0) != 0 ? (th, tw) : (tw, th);
            var orientation = mapping == Mapping.TurnsSwapped ? (byte)((r.Orientation & 0x0F) | ((r.Orientation & 0xF0) switch { 0x20 => 0x80, 0x80 => 0x20, var o => o })) : r.Orientation;
            Matrix4x4.Invert(TileGroundMapping.TileToWorld(r.X, r.Y, tw, th, orientation, 1f / 128, 1f / 128), out var toTile);
            var roadSlots = Enumerable.Range(0, Math.Min(4, entry.Layers.Count)).Where(k => isRoadLayer(entry.Layers[k])).ToArray();
            placed.Add((new Placed(placed.Count, r.X, r.Y, w, h, tw, th, r.Orientation, entry.Tile.Name, path, roadSlots, toTile), TileList.IsBaseTile(path)));
        }
        foreach (var pass in new[] { true, false })
            foreach (var (p, isBase) in placed)
            {
                if (isBase != pass) continue;
                for (var y = Math.Max(0, p.Y); y < Math.Min(_gh, p.Y + p.H); y++)
                    for (var x = Math.Max(0, p.X); x < Math.Min(_gw, p.X + p.W); x++)
                        _top[y * _gw + x] = p.Index;
            }
        _tiles.AddRange(placed.Select(x => x.P));
        MappingUsed = mapping;
    }

    public Mapping MappingUsed { get; }

    private DdsTexture.Image? Blend(string folder)
    {
        if (_blend.TryGetValue(folder, out var image)) return image;
        try { image = _read(folder + "/blend0.dds") is { } dds ? DdsTexture.DecodeMip(dds, 8192) : null; }
        catch (Exception) { image = null; }
        return _blend[folder] = image;
    }

    /// <summary>How much road tile <paramref name="t"/> paints at a map point (cells), 0..1: its road channels summed.</summary>
    private float Road(Placed t, Vector2 cell)
    {
        if (t.RoadSlots.Length == 0 || Blend(t.Path) is not { } img) return 0;
        var local = Vector3.Transform(new Vector3(cell.X, 0, cell.Y), t.ToTile);
        var uv = MappingUsed == Mapping.NoApron
            ? new Vector2(local.X / 128f / t.TileW, local.Z / 128f / t.TileH)
            : TileGroundMapping.Uv(local, t.TileW, t.TileH);
        if (MappingUsed == Mapping.RowsFlipped) uv.Y = 1 - uv.Y;
        int px = Math.Clamp((int)(uv.X * img.Width), 0, img.Width - 1), py = Math.Clamp((int)(uv.Y * img.Height), 0, img.Height - 1);
        var o = (py * img.Width + px) * 4;
        return Math.Min(1, t.RoadSlots.Sum(k => img.Rgba[o + k]) / 255f);
    }

    private Placed? TileAt(Vector2 cell)
    {
        int x = (int)MathF.Floor(cell.X), y = (int)MathF.Floor(cell.Y);
        return x < 0 || y < 0 || x >= _gw || y >= _gh || _top[y * _gw + x] < 0 ? null : _tiles[_top[y * _gw + x]];
    }

    public Result Run()
    {
        var ends = new List<RoadEnd>();
        var roadTiles = 0;
        foreach (var t in _tiles)
        {
            if (t.RoadSlots.Length == 0 || Blend(t.Path) is null) continue;
            roadTiles++;
            // Edges: start, direction along it, outward normal, length in cells.
            var edges = new (string Name, Vector2 From, Vector2 Along, Vector2 Out, int Length)[]
            {
                ("south", new(t.X, t.Y), Vector2.UnitX, -Vector2.UnitY, t.W),
                ("north", new(t.X, t.Y + t.H), Vector2.UnitX, Vector2.UnitY, t.W),
                ("west", new(t.X, t.Y), Vector2.UnitY, -Vector2.UnitX, t.H),
                ("east", new(t.X + t.W, t.Y), Vector2.UnitY, Vector2.UnitX, t.H),
            };
            foreach (var (name, from, along, outward, length) in edges)
            {
                var n = length * SamplesPerCell;
                var inside = new bool[n];
                var acrossRoad = new bool[n];
                var acrossTile = new Placed?[n];
                for (var i = 0; i < n; i++)
                {
                    var p = from + along * ((i + 0.5f) / SamplesPerCell);
                    inside[i] = Road(t, p - outward * Inset) >= 0.5f;
                    var other = TileAt(p + outward * Inset);
                    acrossTile[i] = other;
                    acrossRoad[i] = other is not null && other != t && Road(other, p + outward * Inset) >= 0.5f;
                }
                // The edge in tile space: its outward normal and its direction there.
                var normalTile = Vector3.TransformNormal(new Vector3(outward.X, 0, outward.Y), t.ToTile);
                var alongTile = Vector3.TransformNormal(new Vector3(along.X, 0, along.Y), t.ToTile);
                var tileEdge = MathF.Abs(normalTile.Z) > MathF.Abs(normalTile.X) ? (normalTile.Z < 0 ? "south" : "north") : (normalTile.X < 0 ? "west" : "east");
                var axisSign = MathF.Sign(MathF.Abs(alongTile.X) > MathF.Abs(alongTile.Z) ? alongTile.X : alongTile.Z);
                var acrossRuns = new List<float>();
                for (var k = 0; k < n;)
                {
                    if (!acrossRoad[k]) { k++; continue; }
                    var s0 = k;
                    while (k < n && acrossRoad[k]) k++;
                    acrossRuns.Add((s0 + k) / 2f);
                }
                for (var i = 0; i < n;)
                {
                    if (!inside[i]) { i++; continue; }
                    var start = i;
                    while (i < n && inside[i]) i++;
                    var run = i - start;
                    var both = 0;
                    for (var k = start; k < i; k++) if (acrossRoad[k]) both++;
                    var across = acrossTile[(start + i) / 2];
                    // How far the nearest road across the edge is from this end's middle (cells), within 2 cells.
                    var middle = (start + i) / 2;
                    var gap = float.NaN;
                    for (var k = 0; k < 2 * SamplesPerCell && float.IsNaN(gap); k++)
                        if (middle - k >= 0 && acrossRoad[middle - k] || middle + k < n && acrossRoad[middle + k]) gap = k / (float)SamplesPerCell;
                    ends.Add(new RoadEnd(t.Name, t.Path, t.X, t.Y, (t.Orientation & 0xF0) switch { 0x20 => 90, 0x40 => 180, 0x80 => 270, _ => 0 },
                        name, (start + i) / 2f / SamplesPerCell, run / (float)SamplesPerCell,
                        across is null ? null : across.Name + (across.RoadSlots.Length == 0 ? " (no road layer)" : ""), both / (float)run, gap,
                        tileEdge, Nearest(acrossRuns, (start + i) / 2f) is { } c ? (c - (start + i) / 2f) / SamplesPerCell * axisSign : float.NaN));
                }
            }
        }
        return new Result(MappingUsed, roadTiles, ends);
    }

    private static float? Nearest(List<float> centres, float at)
    {
        float? best = null;
        foreach (var c in centres)
            if (MathF.Abs(c - at) <= SamplesPerCell && (best is null || MathF.Abs(c - at) < MathF.Abs(best.Value - at))) best = c;
        return best;
    }
}
