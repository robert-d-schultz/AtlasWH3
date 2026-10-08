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
/// The per-tile files' footprint (name, set, mask, size, folder), read with <see cref="CampaignTileDatabase.ReadTile"/>.
/// </summary>
public static class TileDatabase
{
    public const string Folder = "terrain/tiles/campaign/_tile_database/tiles/";

    public static TileInfo Parse(ReadOnlySpan<byte> b)
    {
        var t = CampaignTileDatabase.ReadTile("", b);
        var path = t.Variations.Count > 0 ? t.Variations[0].Location : "";
        return new TileInfo(t.Name, t.TileSet, t.Mask, t.Width, t.Height, path) { UseAltLf = t.UseAltLf };
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
