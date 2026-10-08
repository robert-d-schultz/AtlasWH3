using System.Xml.Linq;

namespace AtlasWH3.Formats.Battle;

public enum PaletteKind { Group, TileSet, Tile }

/// <summary>One tile_map.png colour. <see cref="Rgb"/> is 0xRRGGBB.</summary>
public sealed record PaletteEntry(uint Rgb, string Name, PaletteKind Kind, string Detail)
{
    public byte R => (byte)(Rgb >> 16);
    public byte G => (byte)(Rgb >> 8);
    public byte B => (byte)Rgb;

    public string Label => Kind switch
    {
        PaletteKind.Group => $"{Name}  (mix)",
        PaletteKind.Tile => $"{Name}  (tile)",
        _ => Name,
    };
}

/// <summary>
/// The colours BOB understands in a battle tile_map.png (WARSCAPE::TILE_PLACEMENT_GROUPS, exact RGB match):
/// the groups in raw_data\terrain\battles\tile_placement_groups.xml first, then one group per tile set and one per tile
/// that has its own colour (TILE_PLACEMENT_GROUPS::init_from_tile_database). Anything else (e.g. black) places no tile.
/// </summary>
public sealed class BattlePalette
{
    public IReadOnlyList<PaletteEntry> Entries { get; }
    private readonly Dictionary<uint, PaletteEntry> _byRgb = new();

    public BattlePalette(IReadOnlyList<PaletteEntry> entries)
    {
        Entries = entries;
        foreach (var e in entries) _byRgb.TryAdd(e.Rgb, e);
    }

    public PaletteEntry? Find(uint rgb) => _byRgb.GetValueOrDefault(rgb & 0xFFFFFF);

    public static uint Pack(byte r, byte g, byte b) => ((uint)r << 16) | ((uint)g << 8) | b;

    public static BattlePalette Build(BattleTileDatabase db, string? placementGroupsXml)
    {
        var entries = new List<PaletteEntry>();
        if (placementGroupsXml != null && File.Exists(placementGroupsXml))
        {
            var root = XDocument.Load(placementGroupsXml).Root!;
            foreach (var g in root.Descendants("group"))
            {
                var c = g.Element("colour");
                if (c == null) continue;
                var rgb = Pack(Byte(c, "r"), Byte(c, "g"), Byte(c, "b"));
                var sets = g.Descendants("tile_set").Select(s => s.Element("name")?.Value.Trim()).Where(s => !string.IsNullOrEmpty(s));
                var tiles = g.Descendants("tile").Count();
                var detail = $"sets: {string.Join(", ", sets)}" + (tiles > 0 ? $"; {tiles} tiles" : "");
                entries.Add(new PaletteEntry(rgb, g.Element("name")?.Value.Trim() ?? "?", PaletteKind.Group, detail));
            }
        }
        foreach (var set in db.TileSets.Values)
            entries.Add(new PaletteEntry(Pack(set.R, set.G, set.B), set.Name, PaletteKind.TileSet,
                set.LinkAs.Length > 0 ? $"tile set (links as {set.LinkAs})" : "tile set"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in db.Tiles.Where(t => t.HasColour))
            if (seen.Add(tile.Name))
                entries.Add(new PaletteEntry(Pack(tile.R, tile.G, tile.B), tile.Name, PaletteKind.Tile,
                    $"{tile.TileSet} tile {tile.Width}x{tile.Height}"));
        return new BattlePalette(entries);
    }

    private static byte Byte(XElement colour, string channel) =>
        (byte)Math.Clamp(int.TryParse(colour.Element(channel)?.Value.Trim(), out var v) ? v : 0, 0, 255);
}
