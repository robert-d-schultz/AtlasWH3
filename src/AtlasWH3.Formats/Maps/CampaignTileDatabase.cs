using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>A campaign tile set from _settings.bin. Its colour is what tile_map.png uses.</summary>
public sealed record CampaignTileSet(int Index, string Name, string LinkingTile, string SharedGeometry, string AlsoPlaceTileSet,
                                     string LinkAsSet, byte R, byte G, byte B, bool ExcludeFromGlobalMesh)
{
    public uint Rgb => (uint)(R << 16 | G << 8 | B);
    /// <summary>WARSCAPE::TILE_DATABASE_TILE_SET::tile_set_to_link_as: link_as_set, else the set's own name.</summary>
    public string LinkAs => LinkAsSet.Length > 0 ? LinkAsSet : Name;
}

/// <summary>A campaign climate from _settings.bin. Its colour is what climate_map.png uses.</summary>
public sealed record CampaignClimate(int Index, string Name, byte R, byte G, byte B)
{
    public uint Rgb => (uint)(R << 16 | G << 8 | B);
}

/// <summary>TILE_LINK_TARGET: a sub-tile (x, y; y = 0 is the north row) that a link of this tile connects to.</summary>
public sealed record TileLinkTarget(string TargetSet, int X, int Y);

/// <summary>TILE_LINK: a point outside the tile (x, y, y = 0 north) whose tile set is tested.</summary>
public sealed record TileLink(string LinkSet, int X, int Y, int BaseX, int BaseY, bool IsEntry, int BlendSize,
                              bool NoOfflineBlend, string Test)
{
    public bool TestEquals => Test == "TLT_EQUALS";
}

/// <summary>One variation of a tile (its model folder and the climate it is for, if any).</summary>
public sealed record TileVariation(string Name, string Location, string Climate, byte R, byte G, byte B, byte[]? TextureSetBytes = null)
{
    public uint Rgb => (uint)(R << 16 | G << 8 | B);

    /// <summary>
    /// The texture set: u32 (2), then eight u16-length strings, one ground texture group per channel of the tile's
    /// blend0.dds (R, G, B, A; the last four are unused on the campaign). "climate" (or empty) = the global ground
    /// there; anything else is a texture_arrays group name ("arid_1", "imperial_road"...). Checked on every vanilla
    /// tile: the channels a blend0 uses are the slots that are named.
    /// </summary>
    public IReadOnlyList<string> TextureLayers
    {
        get
        {
            var result = new List<string>();
            if (TextureSetBytes is not { Length: >= 4 } b) return result;
            var o = 4;
            while (o + 2 <= b.Length && result.Count < 8)
            {
                int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
                o += 2;
                if (o + n > b.Length) break;
                result.Add(Encoding.Latin1.GetString(b, o, n));
                o += n;
            }
            return result;
        }
    }
}

/// <summary>A campaign tile (terrain\tiles\campaign\_tile_database\tiles\*.bin) with everything tile matching uses.</summary>
public sealed record CampaignTile(string File, int Version, string Name, string TileSet, string Mask, int Width, int Height,
                                  byte R, byte G, byte B, bool RandomRotatable,
                                  IReadOnlyList<TileVariation> Variations, IReadOnlyList<TileLinkTarget> LinkTargets,
                                  IReadOnlyList<TileLink> Links, bool Barbarian = false, bool UseAltLf = false)
{
    public uint Rgb => (uint)(R << 16 | G << 8 | B);

    /// <summary>WARSCAPE::TILE_DATABASE_TILE::subtile_masked_valid: mask rows are stored north first; empty = all valid.</summary>
    public bool SubtileValid(int col, int row)
    {
        if (col < 0 || row < 0 || col >= Width || row >= Height) return false;
        if (Mask.Length == 0) return true;
        return Mask.Length == Width * Height && Mask[row * Width + col] == '1';
    }
}

/// <summary>
/// The campaign tile database: _settings.bin (climates and tile sets) plus tiles\*.bin. Field order is BOB's reader
/// (research/bob_re/tiledb2/1803c5550 tile, 1803c5a50 tile set, 1803c43c0 climate, tilematch/1803c24c0 link,
/// tilematch/1803c5c70 variation). Strings are u16 length + Latin-1.
/// </summary>
public sealed class CampaignTileDatabase
{
    public const string PackFolder = "terrain/tiles/campaign/_tile_database/";

    public IReadOnlyList<CampaignClimate> Climates { get; }
    public IReadOnlyList<CampaignTileSet> TileSets { get; }
    public IReadOnlyList<CampaignTile> Tiles { get; }
    /// <summary>Tile files that did not parse, with the reason.</summary>
    public IReadOnlyList<string> Errors { get; }

    private readonly Dictionary<string, CampaignTileSet> _sets;

    public CampaignTileDatabase(IReadOnlyList<CampaignClimate> climates, IReadOnlyList<CampaignTileSet> sets,
                                IReadOnlyList<CampaignTile> tiles, IReadOnlyList<string>? errors = null)
    {
        Climates = climates;
        TileSets = sets;
        Tiles = tiles;
        Errors = errors ?? [];
        _sets = new Dictionary<string, CampaignTileSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets) _sets.TryAdd(s.Name, s);
    }

    public CampaignTileSet? TileSet(string name) => _sets.GetValueOrDefault(name);

    /// <summary>Loads a loose _tile_database folder (holding _settings.bin and tiles\).</summary>
    public static CampaignTileDatabase LoadFolder(string databaseDir)
    {
        var files = Directory.EnumerateFiles(Path.Combine(databaseDir, "tiles"), "*.bin")
            .Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)));
        return Load(File.ReadAllBytes(Path.Combine(databaseDir, "_settings.bin")), files);
    }

    /// <summary>Builds the database from _settings.bin and (file name, bytes) tile entries, ordered by file name.</summary>
    public static CampaignTileDatabase Load(byte[] settings, IEnumerable<(string Name, byte[] Bytes)> tileFiles)
    {
        var tiles = new List<CampaignTile>();
        var errors = new List<string>();
        foreach (var (name, bytes) in tileFiles.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            try { tiles.Add(ReadTile(name, bytes)); }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                errors.Add($"{name}: {e.Message}");
            }
        }
        return new CampaignTileDatabase(ReadClimates(settings), ReadTileSets(settings), tiles, errors);
    }

    private ref struct Reader(ReadOnlySpan<byte> data, int offset)
    {
        private readonly ReadOnlySpan<byte> _b = data;
        public int O = offset;
        public readonly int Length => _b.Length;
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_b[O..]); O += 2; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_b[O..]); O += 4; return v; }
        public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_b[O..]); O += 4; return v; }
        public byte U8() => _b[O++];
        public bool Bool() => _b[O++] != 0;
        public string Str()
        {
            var n = U16();
            var s = Encoding.Latin1.GetString(_b.Slice(O, n));
            O += n;
            return s;
        }
        public readonly byte[] Slice(int from, int to) => _b[from..to].ToArray();
        public readonly int IndexOf(ReadOnlySpan<byte> needle, int from)
        {
            var i = _b[from..].IndexOf(needle);
            return i < 0 ? -1 : from + i;
        }
    }

    private static byte ColourByte(float v) => (byte)Math.Clamp((int)v, 0, 255);

    private static readonly byte[] LocationPrefix = Encoding.ASCII.GetBytes("terrain\\tiles\\");

    /// <summary>Parses one tile file; throws if the layout does not consume the file exactly.</summary>
    public static CampaignTile ReadTile(string file, ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || !data[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("not FASTBIN0");
        var r = new Reader(data, 8);
        var version = r.U16();
        if (version < 2) throw new InvalidDataException($"tile version {version} not supported");
        var name = r.Str();
        var set = r.Str();
        var mask = r.Str();
        if (version > 5) r.Str();                                           // second mask
        var w = r.I32();
        var h = r.I32();
        byte cr, cg, cb;
        if (version < 7) { cr = ColourByte(r.F32()); cg = ColourByte(r.F32()); cb = ColourByte(r.F32()); }
        else { cr = r.U8(); cg = r.U8(); cb = r.U8(); }
        r.Bool();                                                           // requires_infield_lodding
        var rotatable = r.Bool();
        r.Str();                                                            // custom_alpha_blend_texture
        r.Bool();                                                           // scalable
        r.Bool();                                                           // encampable
        r.Str();                                                            // custom_blend_tile
        if (version < 4) throw new InvalidDataException($"tile version {version} (TEXTURE_GROUP) not supported");

        var variations = new List<TileVariation>();
        var nv = r.I32();
        for (var i = 0; i < nv; i++) variations.Add(ReadVariation(ref r));
        var targets = new List<TileLinkTarget>();
        var nt = r.I32();
        for (var i = 0; i < nt; i++)
        {
            r.U16();
            targets.Add(new TileLinkTarget(r.Str(), r.I32(), r.I32()));
        }
        var links = new List<TileLink>();
        var nl = r.I32();
        for (var i = 0; i < nl; i++)
        {
            r.U16();
            var linkSet = r.Str();
            int x = r.I32(), y = r.I32(), bx = r.I32(), by = r.I32();
            var entry = r.Bool();
            var quad = r.I32();
            if (quad != 0) throw new InvalidDataException("blend_quad points not supported");
            var blendSize = r.I32();
            var noOffline = r.Bool();
            links.Add(new TileLink(linkSet, x, y, bx, by, entry, blendSize, noOffline, r.Str()));
        }
        var barbarian = version > 2 && r.Bool();
        var useAltLf = version > 4 && r.Bool();
        if (r.O != r.Length) throw new InvalidDataException($"{r.Length - r.O} bytes left over");
        return new CampaignTile(file, version, name, set, mask, w, h, cr, cg, cb, rotatable, variations, targets, links, barbarian, useAltLf);
    }

    // Variation v5+: u16 version, texture_set (skipped up to the location string), location, name, min_height, scale,
    // normal_strength, overlap_border_size, i32 raw_data_tri_density, blend/index/normal_common, colour,
    // requires_sea_in_infield, [v6 shadow_camera_depth], [v7 enable_sea_water_plane], [v8 is_subterranean], [v9 fog_mask].
    private static TileVariation ReadVariation(ref Reader r)
    {
        var version = r.U16();
        if (version < 5) throw new InvalidDataException($"variation version {version} not supported");
        var loc = r.IndexOf(LocationPrefix, r.O);
        if (loc < 0) throw new InvalidDataException("variation without a location");
        var textureSet = r.O;
        r.O = loc - 2;
        var climate = ClimateKey(ref r, textureSet, loc - 2);
        var textureSetBytes = r.Slice(textureSet, loc - 2);
        var location = r.Str();
        var name = r.Str();
        for (var i = 0; i < 4; i++) r.F32();
        r.I32();
        r.Str(); r.Str(); r.Str();
        byte cr, cg, cb;
        if (version < 10) { cr = ColourByte(r.F32()); cg = ColourByte(r.F32()); cb = ColourByte(r.F32()); }
        else { cr = r.U8(); cg = r.U8(); cb = r.U8(); }
        r.Bool();
        if (version > 5) r.F32();
        if (version > 6) r.Bool();
        if (version > 7) r.Bool();
        if (version > 8) r.Str();
        return new TileVariation(name, location, climate, cr, cg, cb, textureSetBytes);
    }

    /// <summary>The texture_set block holds key/value strings such as "climate", "arid_1": returns the climate value.</summary>
    private static string ClimateKey(ref Reader r, int from, int to)
    {
        var saved = r.O;
        var result = "";
        r.O = from;
        try
        {
            while (r.O + 2 <= to)
            {
                var s = r.Str();
                if (r.O > to) break;
                if (s == "climate" && r.O + 2 <= to) { result = r.Str(); break; }
            }
        }
        catch (ArgumentOutOfRangeException) { }
        r.O = saved;
        return result;
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

    /// <summary>Climate records: u16 7, name, f32 r, g, b (0-255), ...; the first run of them in the file.</summary>
    public static List<CampaignClimate> ReadClimates(byte[] settings)
    {
        var result = new List<CampaignClimate>();
        var b = settings.AsSpan();
        for (var o = 0; o + 4 < b.Length; o++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(b[o..]) != 7 || !IsNameAt(b, o, out var n)) continue;
            var p = o + 4 + n;
            if (p + 12 > b.Length) break;
            var name = Encoding.Latin1.GetString(b.Slice(o + 4, n));
            var cr = BinaryPrimitives.ReadSingleLittleEndian(b[p..]);
            var cg = BinaryPrimitives.ReadSingleLittleEndian(b[(p + 4)..]);
            var cb = BinaryPrimitives.ReadSingleLittleEndian(b[(p + 8)..]);
            if (!(cr is >= 0 and <= 255 && cg is >= 0 and <= 255 && cb is >= 0 and <= 255)) continue;
            result.Add(new CampaignClimate(result.Count, name, ColourByte(cr), ColourByte(cg), ColourByte(cb)));
            o = p + 11;
        }
        return result;
    }

    /// <summary>Tile-set records end the file: u32 count, then u16 2, name, linking_tile, shared_geometry,
    /// also_place_tile_set, link_as_set, f32 r, g, b, u8 exclude_from_global_mesh.</summary>
    public static List<CampaignTileSet> ReadTileSets(byte[] settings)
    {
        for (var start = 4; start + 4 < settings.Length; start++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(settings.AsSpan(start)) != 2 || !IsNameAt(settings, start, out _)) continue;
            var count = BinaryPrimitives.ReadInt32LittleEndian(settings.AsSpan(start - 4));
            if (count < 1 || count > 4096) continue;
            if (TryReadTileSets(settings, start, count) is { } sets) return sets;
        }
        throw new InvalidDataException("No tile-set records found in _settings.bin.");
    }

    private static List<CampaignTileSet>? TryReadTileSets(byte[] data, int start, int count)
    {
        var r = new Reader(data, start);
        var sets = new List<CampaignTileSet>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (r.U16() != 2) return null;
                var name = r.Str();
                var linking = r.Str();
                var shared = r.Str();
                var also = r.Str();
                var linkAs = r.Str();
                var cr = r.F32(); var cg = r.F32(); var cb = r.F32();
                if (!(cr is >= 0 and <= 255 && cg is >= 0 and <= 255 && cb is >= 0 and <= 255)) return null;
                sets.Add(new CampaignTileSet(i, name, linking, shared, also, linkAs, ColourByte(cr), ColourByte(cg), ColourByte(cb), r.Bool()));
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        return r.O == r.Length ? sets : null;
    }
}
