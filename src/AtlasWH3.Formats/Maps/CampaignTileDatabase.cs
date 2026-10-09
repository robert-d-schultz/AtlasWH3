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

/// <summary>A campaign tile (terrain\tiles\campaign\_tile_database\tiles\*.bin) with everything tile matching uses.
/// <paramref name="HeaderByte"/> is the byte where 3K stored "scalable": 0-255 in WH3 (0 on most tiles), meaning not
/// known.</summary>
public sealed record CampaignTile(string File, int Version, string Name, string TileSet, string Mask, int Width, int Height,
                                  byte R, byte G, byte B, bool RandomRotatable,
                                  IReadOnlyList<TileVariation> Variations, IReadOnlyList<TileLinkTarget> LinkTargets,
                                  IReadOnlyList<TileLink> Links, bool Barbarian = false, bool UseAltLf = false, byte HeaderByte = 0)
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
/// (Atlas3K's 3K research: research/bob_re/tiledb2/1803c5550 tile, 1803c5a50 tile set, 1803c43c0 climate,
/// tilematch/1803c24c0 link, tilematch/1803c5c70 variation), with WH3's versions measured on the 320 tiles of
/// tiles_campaign.pack (tile v5-v9, variation v8-v13; every file parses to its last byte). Strings are u16 length +
/// Latin-1.
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

    /// <summary>Parses one WH3 tile file; throws if the layout does not consume the file exactly.
    ///  - header: name, tile set, mask, [v8+ a second mask], i32 width, height, colour (f32 × 3 before v7, else u8 × 3),
    ///    requires_infield_lodding, random_rotatable, custom_alpha_blend_texture, u8 (<see cref="CampaignTile.HeaderByte"/>),
    ///    encampable, custom_blend_tile;
    ///  - i32 variation count, variations; [v8+ 2 bytes]; i32 link-target count, targets (u16, set, x, y); i32 link
    ///    count, links (u16, set, x, y, base x, base y, entry, i32 blend-quad count (0 on every WH3 tile), blend size,
    ///    no_offline_blend, test);
    ///  - v5: 1 unknown byte, barbarian, use_alt_lf; v6+: barbarian, use_alt_lf, the 4 geometry_blend_edge flags, and
    ///    1 more unknown byte in v9.</summary>
    public static CampaignTile ReadTile(string file, ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || !data[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("not FASTBIN0");
        var r = new Reader(data, 8);
        var version = r.U16();
        if (version is < 5 or > 9) throw new InvalidDataException($"tile version {version} not supported (WH3: 5-9)");
        var name = r.Str();
        var set = r.Str();
        var mask = r.Str();
        if (version > 7) r.Str();                                           // second mask
        var w = r.I32();
        var h = r.I32();
        byte cr, cg, cb;
        if (version < 7) { cr = ColourByte(r.F32()); cg = ColourByte(r.F32()); cb = ColourByte(r.F32()); }
        else { cr = r.U8(); cg = r.U8(); cb = r.U8(); }
        r.Bool();                                                           // requires_infield_lodding
        var rotatable = r.Bool();
        r.Str();                                                            // custom_alpha_blend_texture
        var headerByte = r.U8();
        r.Bool();                                                           // encampable
        r.Str();                                                            // custom_blend_tile

        var variations = new List<TileVariation>();
        var nv = r.I32();
        for (var i = 0; i < nv; i++) variations.Add(ReadVariation(ref r));
        if (version > 7) r.O += 2;
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
            if (quad != 0) throw new InvalidDataException("blend_quad points not supported (no WH3 tile has any)");
            var blendSize = r.I32();
            var noOffline = r.Bool();
            links.Add(new TileLink(linkSet, x, y, bx, by, entry, blendSize, noOffline, r.Str()));
        }
        // v5 has one more byte in front; v6+ follow with geometry_blend_edge_left/top/right/bottom (+1 byte in v9). Field
        // order from the XML names in warscape (barbarian, use_alt_lf, geometry_blend_edge_*); measured: only the v5 sea
        // tiles set use_alt_lf, and BOB takes their tile_list heights from HeightSea.
        if (version == 5) r.O++;
        var barbarian = r.Bool();
        var useAltLf = r.Bool();
        r.O += version == 9 ? 5 : version == 5 ? 0 : 4;
        if (r.O != r.Length) throw new InvalidDataException($"{r.Length - r.O} bytes left over");
        return new CampaignTile(file, version, name, set, mask, w, h, cr, cg, cb, rotatable, variations, targets, links, barbarian, useAltLf, headerByte);
    }

    /// <summary>Raw bytes after a variation's known fields, by variation version (all zero but the last byte on the
    /// vanilla tiles).</summary>
    private static int VariationTail(int version) => version switch
    {
        < 10 => 0,
        11 => 19,
        12 or 13 => 10,
        _ => throw new InvalidDataException($"variation version {version} not supported (WH3: 8, 9, 11-13)"),
    };

    // Variation: u16 version, texture set (i32 2, eight layer names), location, name, min_height, scale,
    // normal_strength, overlap_border_size, i32 raw_data_tri_density, blend/index/normal_common, colour (f32 × 3 before
    // v10, else u8 × 3), requires_sea_in_infield, [v6 f32 shadow_camera_depth], [v7 enable_sea_water_plane], [v9 u8],
    // then the raw tail of v11-v13.
    private static TileVariation ReadVariation(ref Reader r)
    {
        var version = r.U16();
        if (version < 8) throw new InvalidDataException($"variation version {version} not supported");
        var textureSet = r.O;
        r.I32();
        for (var i = 0; i < 8; i++) r.Str();
        var textureSetBytes = r.Slice(textureSet, r.O);
        var location = r.Str();
        var name = r.Str();
        for (var i = 0; i < 4; i++) r.F32();
        r.I32();
        r.Str(); r.Str(); r.Str();
        byte cr, cg, cb;
        if (version < 10) { cr = ColourByte(r.F32()); cg = ColourByte(r.F32()); cb = ColourByte(r.F32()); }
        else { cr = r.U8(); cg = r.U8(); cb = r.U8(); }
        r.Bool();
        r.F32();
        r.Bool();
        if (version > 8) r.U8();
        r.O += VariationTail(version);
        // WH3 has one climate ("default"): a variation is not tied to one; its texture layers name ground groups
        return new TileVariation(name, location, "", cr, cg, cb, textureSetBytes);
    }

    /// <summary>
    /// The CLIMATES list, in file order: the structure just before TILE_SETS (<see cref="ReadTileSets"/>). CLIMATE v8:
    /// u16 version, name, u8 r, g, b, u32 texture count, TEXTURE (u16 version, name, a fixed payload), vampire and chaos
    /// creep climates, f32 grass saturation. The list offset and the TEXTURE payload size are solved together, keeping
    /// the one combination that ends exactly where TILE_SETS starts (WH3_visual_map_decompiler's TileSettings).
    /// </summary>
    public static List<CampaignClimate> ReadClimates(byte[] settings)
    {
        var (setsAt, _) = FindTileSets(settings) ?? throw new InvalidDataException("No tile-set records found in _settings.bin.");
        for (var offset = setsAt - 4; offset >= 10; offset--)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(settings.AsSpan(offset));
            if (count is < 1 or > 1024) continue;
            for (var payload = 0; payload <= 256; payload += 4)
                if (TryReadClimates(settings, offset + 4, count, payload, setsAt) is { } climates) return climates;
        }
        return [];
    }

    private static List<CampaignClimate>? TryReadClimates(byte[] data, int start, int count, int payload, int end)
    {
        var r = new Reader(data, start);
        var result = new List<CampaignClimate>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                r.U16();
                var name = r.Str();
                if (name.Length == 0 || !IsPlain(name)) return null;
                byte cr = r.U8(), cg = r.U8(), cb = r.U8();
                var textures = r.I32();
                if (textures is < 0 or > 4096) return null;
                for (var t = 0; t < textures; t++)
                {
                    r.U16();
                    if (!IsPlain(r.Str())) return null;
                    r.O += payload;
                    if (r.O > end) return null;
                }
                if (!IsPlain(r.Str()) || !IsPlain(r.Str())) return null;
                r.F32();
                if (r.O > end) return null;
                result.Add(new CampaignClimate(i, name, cr, cg, cb));
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        return r.O == end ? result : null;
    }

    private static bool IsPlain(string s) => s.All(c => c is >= ' ' and <= '~');

    /// <summary>Tile-set records end the file: u32 count, then TILE_SET v3: u16 version, name, linking_tile,
    /// shared_geometry, also_place_tile_set, link_as_set, u8 r, g, b, exclude_from_global_mesh.</summary>
    public static List<CampaignTileSet> ReadTileSets(byte[] settings) =>
        FindTileSets(settings)?.Sets ?? throw new InvalidDataException("No tile-set records found in _settings.bin.");

    /// <summary>The TILE_SETS list: the last structure of the file, so its count is the offset whose records end at
    /// EOF.</summary>
    private static (int Offset, List<CampaignTileSet> Sets)? FindTileSets(byte[] settings)
    {
        for (var offset = settings.Length - 4; offset >= 10; offset--)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(settings.AsSpan(offset));
            if (count is < 1 or > 4096) continue;
            if (TryReadTileSets(settings, offset + 4, count) is { } sets) return (offset, sets);
        }
        return null;
    }

    private static List<CampaignTileSet>? TryReadTileSets(byte[] data, int start, int count)
    {
        var r = new Reader(data, start);
        var sets = new List<CampaignTileSet>();
        try
        {
            var version = -1;
            for (var i = 0; i < count; i++)
            {
                var v = r.U16();
                if (v is 0 or > 64 || version >= 0 && v != version) return null;
                version = v;
                var name = r.Str();
                var linking = r.Str();
                var shared = r.Str();
                var also = r.Str();
                var linkAs = r.Str();
                if (name.Length == 0 || !new[] { name, linking, shared, also, linkAs }.All(IsPlain)) return null;
                sets.Add(new CampaignTileSet(i, name, linking, shared, also, linkAs, r.U8(), r.U8(), r.U8(), r.Bool()));
            }
        }
        catch (ArgumentOutOfRangeException) { return null; }
        return r.O == r.Length ? sets : null;
    }
}
