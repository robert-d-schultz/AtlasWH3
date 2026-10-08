using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Battle;

/// <summary>A battle tile set from _settings.bin: its placement colour and the set BOB also-places / links as.</summary>
public sealed record BattleTileSet(string Name, string BlendTile, string LinkAs, byte R, byte G, byte B);

/// <summary>A climate from _settings.bin; its colour is what climate_map.png uses.</summary>
public sealed record BattleClimate(int Index, string Name, byte R, byte G, byte B);

/// <summary>One battle tile (terrain\tiles\battle\_tile_database\tiles\*.bin) with the variation folders it provides.</summary>
public sealed record BattleTile(string Name, string TileSet, string Mask, int Width, int Height, byte R, byte G, byte B,
                                IReadOnlyList<string> Locations)
{
    public bool HasColour => R != 0 || G != 0 || B != 0;

    /// <summary>WARSCAPE::TILE_DATABASE_TILE::subtile_masked_valid: mask rows are stored north first; empty = all valid.</summary>
    public bool SubtileValid(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Width || row >= Height) return false;
        if (Mask.Length != Width * Height) return true;
        return Mask[row * Width + col] == '1';
    }
}

/// <summary>
/// Reader for the battle tile database (terrain\tiles\battle\_tile_database): enough of it to build the tile-map
/// palette and to size/mask explicit tiles. Layouts (docs/bob-battle-terrain-sources.md in BattleMaps):
///  - _settings.bin climate record: u16 7, str name, f32 r, g, b, u32 texture count, ...
///  - _settings.bin tile-set record (runs to the end of the file): u16 2, str name, str blend tile, u32,
///    str link_as, f32 r, g, b, u8
///  - tiles\*.bin: "FASTBIN0", u16 version, str name, str tile set, str mask, [v6+ str nogo], u32 w, u32 h,
///    colour (v7: 3 x u8, older: 3 x f32), ..., variation folders "terrain\tiles\battle\...\"
/// Strings are u16 length + Latin-1.
/// </summary>
public sealed class BattleTileDatabase
{
    public IReadOnlyList<BattleClimate> Climates { get; }
    public IReadOnlyDictionary<string, BattleTileSet> TileSets { get; }
    public IReadOnlyList<BattleTile> Tiles { get; }

    private readonly Dictionary<string, BattleTile> _byLocation;

    private BattleTileDatabase(List<BattleClimate> climates, Dictionary<string, BattleTileSet> sets, List<BattleTile> tiles)
    {
        Climates = climates;
        TileSets = sets;
        Tiles = tiles;
        _byLocation = new Dictionary<string, BattleTile>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tiles)
            foreach (var loc in t.Locations)
                _byLocation.TryAdd(NormaliseLocation(loc), t);
    }

    /// <summary>Tile providing a variation folder such as terrain/tiles/battle/settlement_cities/settlement_city_han_f/medium.</summary>
    public BattleTile? TileAt(string location) => _byLocation.GetValueOrDefault(NormaliseLocation(location));

    /// <summary>All variation folders, normalised (lower case, backslashes, trailing backslash).</summary>
    public IEnumerable<string> Locations => _byLocation.Keys;

    public static string NormaliseLocation(string location)
    {
        var p = location.Replace('/', '\\').Trim().ToLowerInvariant();
        return p.EndsWith('\\') ? p : p + "\\";
    }

    /// <summary>Loads from a _tile_database folder (holding _settings.bin and tiles\).</summary>
    public static BattleTileDatabase Load(string databaseDir)
    {
        var settings = File.ReadAllBytes(Path.Combine(databaseDir, "_settings.bin"));
        var tiles = Directory.EnumerateFiles(Path.Combine(databaseDir, "tiles"), "*.bin")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => ReadTile(File.ReadAllBytes(f)))
            .Where(t => t != null)
            .Select(t => t!)
            .ToList();
        return new BattleTileDatabase(ReadClimates(settings), ReadTileSets(settings), tiles);
    }

    private static string Str(ReadOnlySpan<byte> b, ref int o)
    {
        var n = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
        var s = Encoding.Latin1.GetString(b.Slice(o + 2, n));
        o += 2 + n;
        return s;
    }

    private static bool IsNameAt(ReadOnlySpan<byte> b, int o, out int length)
    {
        length = 0;
        if (o + 4 > b.Length) return false;
        length = BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 2)..]);
        if (length < 3 || o + 4 + length > b.Length) return false;
        foreach (var c in b.Slice(o + 4, length))
            if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
                return false;
        return true;
    }

    private static byte ColourByte(float v) => (byte)Math.Clamp((int)v, 0, 255);

    internal static List<BattleClimate> ReadClimates(byte[] settings)
    {
        var result = new List<BattleClimate>();
        var b = settings.AsSpan();
        for (var o = 0; o + 4 < b.Length; o++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(b[o..]) != 7 || !IsNameAt(b, o, out var n)) continue;
            var p = o + 4 + n;
            if (p + 16 > b.Length) break;
            var name = Encoding.Latin1.GetString(b.Slice(o + 4, n));
            var r = BinaryPrimitives.ReadSingleLittleEndian(b[p..]);
            var g = BinaryPrimitives.ReadSingleLittleEndian(b[(p + 4)..]);
            var bl = BinaryPrimitives.ReadSingleLittleEndian(b[(p + 8)..]);
            if (!(r is >= 0 and <= 255 && g is >= 0 and <= 255 && bl is >= 0 and <= 255)) continue;
            result.Add(new BattleClimate(result.Count, name, ColourByte(r), ColourByte(g), ColourByte(bl)));
            o = p + 15;
        }
        return result;
    }

    /// <summary>The tile-set records are the tail of _settings.bin: find the offset from which they parse exactly to EOF.</summary>
    internal static Dictionary<string, BattleTileSet> ReadTileSets(byte[] settings)
    {
        for (var start = 0; start + 4 < settings.Length; start++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(settings.AsSpan(start)) != 2 || !IsNameAt(settings, start, out _)) continue;
            if (TryReadTileSets(settings, start) is { Count: >= 5 } sets) return sets;
        }
        throw new InvalidDataException("No tile-set records found in _settings.bin.");
    }

    private static Dictionary<string, BattleTileSet>? TryReadTileSets(byte[] data, int start)
    {
        var b = data.AsSpan();
        var sets = new Dictionary<string, BattleTileSet>(StringComparer.OrdinalIgnoreCase);
        var o = start;
        try
        {
            while (o < b.Length)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(b[o..]) != 2 || !IsNameAt(b, o, out _)) return null;
                o += 2;
                var name = Str(b, ref o);
                var blend = Str(b, ref o);
                o += 4;
                var link = Str(b, ref o);
                var r = BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
                var g = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 4)..]);
                var bl = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 8)..]);
                o += 13;
                sets[name] = new BattleTileSet(name, blend, link, ColourByte(r), ColourByte(g), ColourByte(bl));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        return o == b.Length ? sets : null;
    }

    private static readonly byte[] LocationPrefix = Encoding.ASCII.GetBytes("terrain\\tiles\\battle\\");

    internal static BattleTile? ReadTile(byte[] data)
    {
        var b = data.AsSpan();
        if (b.Length < 10 || !b[..8].SequenceEqual("FASTBIN0"u8)) return null;
        var version = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        var o = 10;
        var name = Str(b, ref o);
        var set = Str(b, ref o);
        var mask = Str(b, ref o);
        if (version >= 6) Str(b, ref o);                                   // nogo
        var w = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(b[(o + 4)..]);
        o += 8;
        byte r, g, bl;
        if (version >= 7)
        {
            r = b[o]; g = b[o + 1]; bl = b[o + 2];
        }
        else
        {
            r = ColourByte(BinaryPrimitives.ReadSingleLittleEndian(b[o..]));
            g = ColourByte(BinaryPrimitives.ReadSingleLittleEndian(b[(o + 4)..]));
            bl = ColourByte(BinaryPrimitives.ReadSingleLittleEndian(b[(o + 8)..]));
        }

        var locations = new List<string>();
        for (var i = b.IndexOf(LocationPrefix); i >= 2; )
        {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(b[(i - 2)..]);
            if (i + n <= b.Length)
            {
                var loc = Encoding.Latin1.GetString(b.Slice(i, n));
                if (loc.EndsWith('\\') && !loc.Contains('\0')) locations.Add(loc);
            }
            var next = b[(i + 1)..].IndexOf(LocationPrefix);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return new BattleTile(name, set, mask, w, h, r, g, bl, locations);
    }
}
