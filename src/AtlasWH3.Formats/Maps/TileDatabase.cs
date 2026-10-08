using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>One campaign tile from terrain\tiles\campaign\_tile_database\tiles\*.bin.</summary>
/// <param name="Mask">Row-major, rows stored north first; '1' = sub-tile belongs to the tile. Empty = all valid.</param>
public sealed record TileInfo(string Name, string Category, string Mask, int Width, int Height, string Path)
{
    /// <summary>WARSCAPE::TILE_DATABASE_TILE::subtile_masked_valid.</summary>
    public bool SubtileValid(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Width || row >= Height) return false;
        if (Mask.Length == 0) return true;
        return Mask.Length == Width * Height && Mask[row * Width + col] == '1';
    }

    /// <summary>TILE_DATABASE_TILE::use_alt_lf, the record's last field (v5+): the tile sits on the alternative (sea)
    /// lf map and feeds the sea global meshes (vanilla: the generic_sea and sea tile sets).</summary>
    public bool UseAltLf { get; init; }
}

/// <summary>
/// Reader for the per-tile FASTBIN0 v6 files: u16 version, name, category and mask strings (u16 length + ASCII),
/// u16, u32 width, u32 height (in tile-map pixels), ..., and the tile folder path ("terrain\tiles\campaign\...\").
/// </summary>
public static class TileDatabase
{
    public const string Folder = "terrain/tiles/campaign/_tile_database/tiles/";

    public static TileInfo Parse(ReadOnlySpan<byte> b)
    {
        if (!b[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("Tile database entry is not FASTBIN0.");
        var o = 10;
        string S(ReadOnlySpan<byte> d)
        {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            var s = Encoding.ASCII.GetString(d.Slice(o + 2, n));
            o += 2 + n;
            return s;
        }
        var name = S(b);
        var category = S(b);
        var mask = S(b);
        var width = BinaryPrimitives.ReadInt32LittleEndian(b[(o + 2)..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(b[(o + 6)..]);
        var path = "";
        var i = b.IndexOf("terrain\\tiles\\"u8);
        if (i >= 2)
        {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(b[(i - 2)..]);
            path = Encoding.ASCII.GetString(b.Slice(i, n));
        }
        var version = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]);
        return new TileInfo(name, category, mask, width, height, path) { UseAltLf = version > 4 && b[^1] != 0 };
    }

    /// <summary>Tiles keyed by normalised folder path (lower case, backslashes, trailing backslash).</summary>
    public static Dictionary<string, TileInfo> Load(IEnumerable<byte[]> entries)
    {
        var result = new Dictionary<string, TileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var bytes in entries)
        {
            var tile = Parse(bytes);
            if (tile.Path.Length > 0) result[NormalisePath(tile.Path)] = tile;
        }
        return result;
    }

    public static string NormalisePath(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return p.EndsWith('\\') ? p : p + "\\";
    }
}
