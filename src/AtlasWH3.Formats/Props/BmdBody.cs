using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Props;

/// <summary>
/// One FASTBIN0 v35 BMD_OBJECTS body of global_props.bin, parsed into sections of raw records so it can be written
/// back byte for byte and new bodies can be assembled from record bytes (see <see cref="BmdRecords"/>).
/// Section order (layout as in <see cref="GlobalProps"/>): preamble (enum tag lists, seasons), nested bmds (prefab
/// instances), props, VFX, light probes, terrain holes, point lights, projectile emitters + playable area, polygon
/// meshes, spot lights, sound emitters, composite scenes, trailing bytes.
/// Every record keeps its leading u16 version; lists are "u16 version, u32 count, records".
/// </summary>
public sealed class BmdBody
{
    public byte[] Preamble { get; set; } = [];
    public List<string> EnumValues { get; } = [];
    /// <summary>Enum types in preamble order with the index range of their values in <see cref="EnumValues"/>.</summary>
    public List<(string Name, int First, int Count)> EnumTypes { get; } = [];
    public List<string> Seasons { get; } = [];

    /// <summary>BOB's per-body tables (bob_terrain bmd export, checked against BOB 2026-10-04): the preamble lists only
    /// the enum types and season codes this body's objects use, in first-appearance order (a type with all its values;
    /// an empty season mask adds every season code in catalog order). When set, EncodeTags / EncodeSeasons grow the tables
    /// from these catalogs and ToBytes writes the preamble from them.</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Values)>? TypeCatalog { get; set; }
    public IReadOnlyList<string>? SeasonCatalog { get; set; }
    private byte[]? _template;

    public ushort NestedVersion { get; set; } = 1;
    public List<byte[]> Nested { get; } = [];
    public byte[] AfterNested { get; set; } = new byte[28];

    public ushort PropsVersion { get; set; }
    public List<string> PropPaths { get; } = [];
    public List<byte[]> Props { get; } = [];

    public ushort VfxVersion { get; set; }
    public List<byte[]> Vfx { get; } = [];
    public byte[] AfterVfx { get; set; } = new byte[26];

    public ushort ProbesVersion { get; set; }
    public List<byte[]> LightProbes { get; } = [];

    public byte[] TerrainHoles { get; set; } = new byte[6];

    public ushort LightsVersion { get; set; }
    public List<byte[]> PointLights { get; } = [];

    public byte[] EmittersAndPlayableArea { get; set; } = new byte[29];

    public ushort PolyVersion { get; set; }
    public List<byte[]> PolyMeshes { get; } = [];
    public byte[] AfterPoly { get; set; } = new byte[6];

    public byte[] SpotLights { get; set; } = new byte[6];

    public ushort SoundsVersion { get; set; }
    public List<byte[]> Sounds { get; } = [];

    public ushort ScenesVersion { get; set; }
    public List<byte[]> CompositeScenes { get; } = [];

    public byte[] Trailing { get; set; } = [];

    public static BmdBody Parse(ReadOnlySpan<byte> data)
    {
        var body = new BmdBody();
        var r = new R(data);
        if (!data[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("BMD body is not FASTBIN0.");
        r.Pos = 8;
        if (r.U16() != 35) throw new InvalidDataException("Only BMD version 35 is supported.");
        r.Pos += 6;
        var enumTypes = r.U32();
        for (var t = 0; t < enumTypes; t++)
        {
            r.Pos += 2;
            var typeName = r.Str();
            var n = r.U32();
            body.EnumTypes.Add((typeName, body.EnumValues.Count, (int)n));
            for (var v = 0; v < n; v++) body.EnumValues.Add(r.Str());
        }
        r.Pos += 2;
        var seasons = r.U32();
        for (var s = 0; s < seasons; s++) body.Seasons.Add(r.Str());
        r.Pos += 47;
        body.Preamble = data[..r.Pos].ToArray();

        body.NestedVersion = r.U16();
        var nested = r.U32();
        for (var i = 0; i < nested; i++)
        {
            var start = r.Pos;
            r.Pos += 2; r.Str(); r.Pos += 64 + 8; r.Str(); r.Pos += 1; r.Str(); r.Pos += 28;
            body.Nested.Add(data[start..r.Pos].ToArray());
        }
        body.AfterNested = r.Take(28);

        body.PropsVersion = r.U16();
        var paths = r.U32();
        for (var i = 0; i < paths; i++) body.PropPaths.Add(r.Str());
        var props = r.U32();
        for (var i = 0; i < props; i++)
        {
            var start = r.Pos;
            r.Pos += BmdRecords.PropHeadSize; r.Str(); r.Pos += BmdRecords.PropTailSize;
            body.Props.Add(data[start..r.Pos].ToArray());
        }

        body.VfxVersion = r.U16();
        var vfx = r.U32();
        for (var i = 0; i < vfx; i++)
        {
            var start = r.Pos;
            r.Pos += 2; r.Str(); r.Pos += 48 + 4; r.Str(); r.Pos += 7 + 4 + 1; r.Str(); r.Pos += 4 + 18 + 6;
            body.Vfx.Add(data[start..r.Pos].ToArray());
        }
        body.AfterVfx = r.Take(26);

        body.ProbesVersion = r.U16();
        var probes = r.U32();
        for (var i = 0; i < probes; i++)
        {
            var start = r.Pos;
            r.Pos += 2 + 16 + 1; r.Str(); r.Pos += 18;
            body.LightProbes.Add(data[start..r.Pos].ToArray());
        }

        body.TerrainHoles = r.Take(6);
        if (BinaryPrimitives.ReadUInt32LittleEndian(body.TerrainHoles.AsSpan(2)) != 0)
            throw new InvalidDataException("Terrain hole triangles are not supported.");

        body.LightsVersion = r.U16();
        var lights = r.U32();
        for (var i = 0; i < lights; i++)
        {
            var start = r.Pos;
            r.Pos += 2 + 12 + 4 + 12 + 4 + 1 + 16; r.Str(); r.Pos += 1; r.Str(); r.Pos += 1 + 4 + 18 + 12;
            body.PointLights.Add(data[start..r.Pos].ToArray());
        }

        body.EmittersAndPlayableArea = r.Take(6 + 23);

        body.PolyVersion = r.U16();
        var polys = r.U32();
        for (var i = 0; i < polys; i++)
        {
            var start = r.Pos;
            r.Pos += 2;
            var nv = r.U32(); r.Pos += 12 * (int)nv;
            var ni = r.U32(); r.Pos += 2 * (int)ni;
            r.Str(); r.Str(); r.Pos += 22;
            body.PolyMeshes.Add(data[start..r.Pos].ToArray());
        }
        body.AfterPoly = r.Take(6);

        body.SpotLights = r.Take(6);
        if (BinaryPrimitives.ReadUInt32LittleEndian(body.SpotLights.AsSpan(2)) != 0)
            throw new InvalidDataException("Spot lights are not supported.");

        body.SoundsVersion = r.U16();
        var sounds = r.U32();
        for (var i = 0; i < sounds; i++)
        {
            var start = r.Pos;
            r.Pos += 2; r.Str(); r.Str();
            var nc = r.U32(); r.Pos += 12 * (int)nc;
            r.Pos += 4 + 4 + 53; r.Str(); r.Pos += 26;
            body.Sounds.Add(data[start..r.Pos].ToArray());
        }

        body.ScenesVersion = r.U16();
        var scenes = r.U32();
        for (var i = 0; i < scenes; i++)
        {
            var start = r.Pos;
            r.Pos += 2 + 48; r.Str(); r.Str(); r.Pos += 5 + 18 + 12;
            body.CompositeScenes.Add(data[start..r.Pos].ToArray());
        }
        body.Trailing = data[r.Pos..].ToArray();
        return body;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(TypeCatalog is null ? Preamble : DynamicPreamble());
        List(w, NestedVersion, Nested);
        w.Write(AfterNested);
        w.Write(PropsVersion);
        w.Write(PropPaths.Count);
        foreach (var p in PropPaths) BmdRecords.WriteStr(w, p);
        w.Write(Props.Count);
        foreach (var p in Props) w.Write(p);
        List(w, VfxVersion, Vfx);
        w.Write(AfterVfx);
        List(w, ProbesVersion, LightProbes);
        w.Write(TerrainHoles);
        List(w, LightsVersion, PointLights);
        w.Write(EmittersAndPlayableArea);
        List(w, PolyVersion, PolyMeshes);
        w.Write(AfterPoly);
        w.Write(SpotLights);
        List(w, SoundsVersion, Sounds);
        List(w, ScenesVersion, CompositeScenes);
        w.Write(Trailing);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A body whose preamble lists <paramref name="types"/> (enum types and values) and the template's seasons,
    /// with the template's framing bytes.</summary>
    public static BmdBody WithEnumTypes(BmdBody template, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> types)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var t = template.Preamble;
        w.Write(t, 0, 16);                                    // FASTBIN0, u16 version, 6 bytes
        w.Write(types.Count);
        var body = EmptyFraming(template);
        foreach (var (name, values) in types)
        {
            w.Write((ushort)1);
            BmdRecords.WriteStr(w, name);
            w.Write(values.Count);
            body.EnumTypes.Add((name, body.EnumValues.Count, values.Count));
            foreach (var v in values) { BmdRecords.WriteStr(w, v); body.EnumValues.Add(v); }
        }
        // season list and the 47 bytes that close the preamble, copied from the template
        var seasonsStart = SeasonsOffset(template);
        w.Write(t, seasonsStart, t.Length - seasonsStart);
        body.Seasons.AddRange(template.Seasons);
        w.Flush();
        body.Preamble = ms.ToArray();
        return body;
    }

    private static int SeasonsOffset(BmdBody b)
    {
        var span = b.Preamble.AsSpan();
        var types = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var o = 20;
        for (var i = 0; i < types; i++)
        {
            o += 2;
            o += 2 + BinaryPrimitives.ReadUInt16LittleEndian(span[o..]);
            var n = BinaryPrimitives.ReadUInt32LittleEndian(span[o..]);
            o += 4;
            for (var v = 0; v < n; v++) o += 2 + BinaryPrimitives.ReadUInt16LittleEndian(span[o..]);
        }
        return o;
    }

    /// <summary>(flags, mask) for a comma-separated tag list: a bit per tag, the mask covering every value of each enum
    /// type a tag belongs to (as vanilla does). Unknown tags are ignored and returned.</summary>
    public (ulong Flags, ulong Mask) EncodeTags(string tags, List<string>? unknown = null)
    {
        ulong flags = 0, mask = 0;
        foreach (var tag in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TypeCatalog is not null && !EnumValues.Contains(tag) &&
                TypeCatalog.FirstOrDefault(t => t.Values.Contains(tag)) is { Name: not null } type)
            {
                EnumTypes.Add((type.Name, EnumValues.Count, type.Values.Count));
                EnumValues.AddRange(type.Values);
            }
            var i = EnumValues.IndexOf(tag);
            if (i < 0 || i >= 64) { unknown?.Add(tag); continue; }
            flags |= 1UL << i;
            var (_, first, count) = EnumTypes.First(t => i >= t.First && i < t.First + t.Count);
            for (var k = first; k < first + count && k < 64; k++) mask |= 1UL << k;
        }
        return (flags, mask);
    }

    /// <summary>Season bitmask over this body's season codes for a Terry season_mask string ("" = all seasons).</summary>
    public uint EncodeSeasons(string seasonMask)
    {
        var names = seasonMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (SeasonCatalog is not null)
            foreach (var code in names.Length == 0 ? SeasonCatalog : names.Select(n => SeasonCatalog.FirstOrDefault(c => SeasonName(c) == n)))
                if (code is not null && !Seasons.Contains(code)) Seasons.Add(code);
        uint bits = 0;
        for (var i = 0; i < Seasons.Count; i++)
            if (names.Length == 0 || names.Contains(SeasonName(Seasons[i]))) bits |= 1u << i;
        return bits;
    }

    public static string SeasonName(string code) => code switch
    {
        "sp" => "season_spring", "su" => "season_summer", "ha" => "season_harvest", "au" => "season_autumn", "wi" => "season_winter",
        _ => code,
    };

    private static void List(BinaryWriter w, ushort version, List<byte[]> records)
    {
        w.Write(version);
        w.Write(records.Count);
        foreach (var r in records) w.Write(r);
    }

    /// <summary>An empty body with BOB's per-body preamble: no enum types or seasons until objects use them (catalogs and
    /// the framing bytes from <paramref name="template"/>).</summary>
    public static BmdBody Dynamic(BmdBody template)
    {
        var body = EmptyFraming(template);
        var types = new List<(string, IReadOnlyList<string>)>();
        foreach (var (name, first, count) in template.EnumTypes) types.Add((name, template.EnumValues.GetRange(first, count)));
        body.TypeCatalog = types;
        body.SeasonCatalog = template.Seasons.ToList();
        body._template = template.Preamble;
        return body;
    }

    /// <summary>empireutility BMD_META_TAG_COLLECTION::add_available_meta_data: every enum value, in preamble order,
    /// mixed into a u32 (0 without enum types). Matches all 20 distinct type lists in BOB's main190 global_props.</summary>
    private uint MetaTagChecksum()
    {
        uint h = 0;
        foreach (var (_, first, count) in EnumTypes)
            for (var k = first; k < first + count; k++) h = Mix(h, StringHash(EnumValues[k]));
        return h;
    }

    private static uint Mix(uint h, uint v)
    {
        var b = (int)(((v ^ h) + 13) & 0x1f);
        return ((h << (b ^ 31)) | (h >> b)) ^ v;
    }

    /// <summary>empireutility FUN_180f35730: big-endian 4-byte chunks mixed in turn.</summary>
    private static uint StringHash(string s)
    {
        var d = System.Text.Encoding.ASCII.GetBytes(s);
        uint h = 0;
        for (var i = 0; i < d.Length; i += 4)
        {
            uint v = 0;
            for (var j = i; j < Math.Min(i + 4, d.Length); j++) v = v * 256 + d[j];
            h = Mix(h, v);
        }
        return h;
    }

    private byte[] DynamicPreamble()
    {
        var t = _template ?? Preamble;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(t, 0, 12);                                    // FASTBIN0, u16 version, u16
        w.Write(MetaTagChecksum());                           // BMD_META_TAG_COLLECTION::checksum
        w.Write(EnumTypes.Count);
        foreach (var (name, first, count) in EnumTypes)
        {
            w.Write((ushort)1);
            BmdRecords.WriteStr(w, name);
            w.Write(count);
            for (var k = first; k < first + count; k++) BmdRecords.WriteStr(w, EnumValues[k]);
        }
        // season list: the template's u16 before it, then this body's codes, then the 47 closing bytes
        var so = SeasonsOffsetOf(t);
        w.Write(t, so, 2);
        w.Write(Seasons.Count);
        foreach (var c in Seasons) BmdRecords.WriteStr(w, c);
        w.Write(t, t.Length - 47, 47);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Offset of the u16 that precedes the season count in a preamble.</summary>
    private static int SeasonsOffsetOf(byte[] p)
    {
        var span = p.AsSpan();
        var types = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var o = 20;
        for (var i = 0; i < types; i++)
        {
            o += 2;
            o += 2 + BinaryPrimitives.ReadUInt16LittleEndian(span[o..]);
            var n = BinaryPrimitives.ReadUInt32LittleEndian(span[o..]); o += 4;
            for (var v = 0; v < n; v++) o += 2 + BinaryPrimitives.ReadUInt16LittleEndian(span[o..]);
        }
        return o;
    }

    /// <summary>An empty body with the same preamble and section framing as <paramref name="template"/>.</summary>
    public static BmdBody EmptyLike(BmdBody template)
    {
        var body = EmptyFraming(template);
        body.EnumValues.AddRange(template.EnumValues);
        body.EnumTypes.AddRange(template.EnumTypes);
        body.Seasons.AddRange(template.Seasons);
        return body;
    }

    private static BmdBody EmptyFraming(BmdBody template) => new()
    {
        Preamble = template.Preamble,
        NestedVersion = template.NestedVersion, AfterNested = template.AfterNested,
        PropsVersion = template.PropsVersion, VfxVersion = template.VfxVersion, AfterVfx = template.AfterVfx,
        ProbesVersion = template.ProbesVersion, TerrainHoles = template.TerrainHoles, LightsVersion = template.LightsVersion,
        EmittersAndPlayableArea = template.EmittersAndPlayableArea, PolyVersion = template.PolyVersion, AfterPoly = template.AfterPoly,
        SpotLights = template.SpotLights, SoundsVersion = template.SoundsVersion, ScenesVersion = template.ScenesVersion,
        Trailing = template.Trailing,
    };

    private ref struct R(ReadOnlySpan<byte> d)
    {
        private readonly ReadOnlySpan<byte> _d = d;
        public int Pos;
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[Pos..]); Pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[Pos..]); Pos += 4; return v; }
        public string Str() { var n = U16(); var s = Encoding.UTF8.GetString(_d.Slice(Pos, n)); Pos += n; return s; }
        public byte[] Take(int n) { var b = _d.Slice(Pos, n).ToArray(); Pos += n; return b; }
    }
}

/// <summary>Byte layouts of individual BMD records (offsets from the record start) and builders for new ones.</summary>
public static class BmdRecords
{
    /// <summary>Prop: u16 version, u32 path index, meta tags (u16 1, u64 flags, u64 mask), 3x3 matrix columns + position
    /// (12 f32), decal, logic decal, fauna, snow in, snow out, destruction in, destruction out, animated,
    /// f32 decal parallax scale, f32 decal tiling, override gbuffer normal, 7 bytes, u32 season mask, visible in seen
    /// shroud, visible in unseen shroud, visible in shroud, apply to terrain (0 = yes), apply to objects, then a string,
    /// then 24 bytes: 4, cast shadow, has height patch, tint (4), faction colour (4), alpha, 8, apply height patch.</summary>
    public const int PropHeadSize = 105, PropTailSize = 24;
    public const int PropPathIndex = 2, PropTags = 6, PropTransform = 24, PropDecal = 72, PropSnowIn = 75, PropSnowOut = 76,
        PropDestructionIn = 77, PropDestructionOut = 78, PropSeasonMask = 96, PropSeenShroud = 100, PropUnseenShroud = 101,
        PropApplyToTerrain = 102, PropRenderAboveSnow = 103, PropApplyToObjects = 104, PropDecalParallaxScale = 80;
    public const int PropTailCastShadow = 4, PropTailHasHeightPatch = 5, PropTailApplyHeightPatch = 23;
    /// <summary>Byte 91 (in the 7 bytes after override gbuffer normal): 1 on river props only (vanilla and BOB);
    /// without it the game culls the river water.</summary>
    public const int PropRiver = 91;
    /// <summary>Byte 79 ("animated", after destruction outside): BOB sets it on every model whose path has "_anim" (716 of
    /// 716 main190 props).</summary>
    public const int PropAnimated = 79;

    public static void WriteStr(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    /// <summary>Writes a 3x3 matrix (as columns) and a position: 12 floats.</summary>
    public static void WriteTransform(Span<byte> s, double[] rowMajor3x3, double x, double y, double z)
    {
        for (var c = 0; c < 3; c++)
            for (var r = 0; r < 3; r++)
                BinaryPrimitives.WriteSingleLittleEndian(s[((c * 3 + r) * 4)..], (float)rowMajor3x3[r * 3 + c]);
        BinaryPrimitives.WriteSingleLittleEndian(s[36..], (float)x);
        BinaryPrimitives.WriteSingleLittleEndian(s[40..], (float)y);
        BinaryPrimitives.WriteSingleLittleEndian(s[44..], (float)z);
    }

    public static void WriteTags(Span<byte> s, ulong flags, ulong mask)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(s, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(s[2..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(s[10..], mask);
    }

    /// <summary>A prop record from a template record: path index, tags, transform, flags and the middle string replaced.</summary>
    public static byte[] Prop(byte[] template, uint pathIndex, ulong tagFlags, ulong tagMask, double[] matrix, (double X, double Y, double Z) position,
        bool decal, bool snowIn, bool snowOut, bool destructionIn, bool destructionOut, uint seasonMask, bool seenShroud, bool unseenShroud,
        bool castShadow, bool hasHeightPatch, bool applyHeightPatch)
    {
        var head = template[..PropHeadSize];
        var s = head.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[PropPathIndex..], pathIndex);
        WriteTags(s[PropTags..], tagFlags, tagMask);
        WriteTransform(s[PropTransform..], matrix, position.X, position.Y, position.Z);
        s[PropDecal] = B(decal);
        s[PropSnowIn] = B(snowIn); s[PropSnowOut] = B(snowOut);
        s[PropDestructionIn] = B(destructionIn); s[PropDestructionOut] = B(destructionOut);
        BinaryPrimitives.WriteUInt32LittleEndian(s[PropSeasonMask..], seasonMask);
        s[PropSeenShroud] = B(seenShroud); s[PropUnseenShroud] = B(unseenShroud);
        var strLen = BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(PropHeadSize));
        var tail = template[(PropHeadSize + 2 + strLen)..];
        tail[PropTailCastShadow] = B(castShadow);
        tail[PropTailHasHeightPatch] = B(hasHeightPatch);
        tail[PropTailApplyHeightPatch] = B(applyHeightPatch);
        return [.. head, .. template.AsSpan(PropHeadSize, 2 + strLen), .. tail];
    }

    private static byte B(bool v) => v ? (byte)1 : (byte)0;

    /// <summary>Copies runs from a template record while writing replacement fields.</summary>
    private sealed class Rebuild(byte[] template)
    {
        private int _pos;
        public readonly MemoryStream Out = new();
        public BinaryWriter W => _w ??= new BinaryWriter(Out);
        private BinaryWriter? _w;
        public Rebuild Copy(int n) { W.Write(template, _pos, n); _pos += n; return this; }
        public Rebuild Skip(int n) { _pos += n; return this; }
        public int StrLen() => 2 + BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(_pos));
        public Rebuild CopyStr() => Copy(StrLen());
        public Rebuild Str(string s) { Skip(StrLen()); WriteStr(W, s); return this; }
        public Rebuild F(float v) { Skip(4); W.Write(v); return this; }
        public Rebuild U32(uint v) { Skip(4); W.Write(v); return this; }
        public Rebuild U8(byte v) { Skip(1); W.Write(v); return this; }
        public Rebuild Transform(double[] m, (double X, double Y, double Z) p)
        {
            Skip(48);
            var b = new byte[48];
            WriteTransform(b, m, p.X, p.Y, p.Z);
            W.Write(b);
            return this;
        }
        public Rebuild Tags(ulong flags, ulong mask) { Skip(18); var b = new byte[18]; WriteTags(b, flags, mask); W.Write(b); return this; }
        public Rebuild Rest() { W.Write(template, _pos, template.Length - _pos); _pos = template.Length; return this; }
        public byte[] Bytes() { W.Flush(); return Out.ToArray(); }
    }

    /// <summary>VFX: u16, name, transform, f32 emission rate, instance name, 7 bytes, u32 season mask, 1, string, 4,
    /// meta tags, 6.</summary>
    public static byte[] Vfx(byte[] template, string name, double[] matrix, (double X, double Y, double Z) position, string instance,
        uint seasonMask, ulong tagFlags, ulong tagMask) =>
        new Rebuild(template).Copy(2).Str(name).Transform(matrix, position).Copy(4).Str(instance).Copy(7).U32(seasonMask)
            .Copy(1).CopyStr().Copy(4).Tags(tagFlags, tagMask).Rest().Bytes();

    /// <summary>Point light: u16, position, radius, colour (3 f32, 0-1), colour scale, u8 animation, 4 f32 (animation
    /// speed x2, colour min, random offset), falloff type, 1, string, light probes only, 4, meta tags, then 5, u16 1,
    /// u32 season mask, 1.</summary>
    public static byte[] PointLight(byte[] template, (double X, double Y, double Z) position, float radius, (float R, float G, float B) colour,
        float colourScale, byte animation, float speed1, float speed2, float colourMin, float randomOffset, string falloff,
        bool probesOnly, ulong tagFlags, ulong tagMask, uint seasonMask) =>
        new Rebuild(template).Copy(2).F((float)position.X).F((float)position.Y).F((float)position.Z).F(radius)
            .F(colour.R).F(colour.G).F(colour.B).F(colourScale).U8(animation).F(speed1).F(speed2).F(colourMin).F(randomOffset)
            .Str(falloff).Copy(1).CopyStr().U8(B(probesOnly)).Copy(4).Tags(tagFlags, tagMask).Copy(5 + 2).U32(seasonMask).Rest().Bytes();

    /// <summary>Composite scene: u16, transform, path, string, 5, meta tags, then 5, u16 1, u32 season mask, 1.</summary>
    public static byte[] CompositeScene(byte[] template, double[] matrix, (double X, double Y, double Z) position, string path,
        ulong tagFlags, ulong tagMask, uint seasonMask) =>
        new Rebuild(template).Copy(2).Transform(matrix, position).Str(path).CopyStr().Copy(5).Tags(tagFlags, tagMask)
            .Copy(5 + 2).U32(seasonMask).Rest().Bytes();

    /// <summary>Sound emitter: u16, key, shape type, u32 point count, points, 4, f32 radius, 53, string, 26.</summary>
    public static byte[] Sound(byte[] template, string key, string shape, IReadOnlyList<(double X, double Y, double Z)> points, float? radius)
    {
        var o = 2;
        o += 2 + BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(o));
        o += 2 + BinaryPrimitives.ReadUInt16LittleEndian(template.AsSpan(o));
        var old = BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(o));
        var r = new Rebuild(template).Copy(2).Str(key).Str(shape);
        r.Skip(4 + 12 * (int)old);
        r.W.Write(points.Count);
        foreach (var (x, y, z) in points) { r.W.Write((float)x); r.W.Write((float)y); r.W.Write((float)z); }
        r.Copy(4);
        if (radius is { } rad) r.F(rad); else r.Copy(4);
        return r.Rest().Bytes();
    }

    /// <summary>Light probe: u16, position, radius, 1, string, 18.</summary>
    public static byte[] LightProbe(byte[] template, (double X, double Y, double Z) position, float radius) =>
        new Rebuild(template).Copy(2).F((float)position.X).F((float)position.Y).F((float)position.Z).F(radius).Rest().Bytes();

    /// <summary>Polygon mesh: u16, u32 vertex count, vertices, u32 index count, u16 indices, material, string, 22.</summary>
    public static byte[] PolyMesh(byte[] template, IReadOnlyList<(double X, double Y, double Z)> vertices, IReadOnlyList<ushort> indices, string material)
    {
        var span = template.AsSpan();
        var nv = BinaryPrimitives.ReadUInt32LittleEndian(span[2..]);
        var ni = BinaryPrimitives.ReadUInt32LittleEndian(span[(6 + 12 * (int)nv)..]);
        var r = new Rebuild(template).Copy(2).Skip(4 + 12 * (int)nv + 4 + 2 * (int)ni);
        r.W.Write(vertices.Count);
        foreach (var (x, y, z) in vertices) { r.W.Write((float)x); r.W.Write((float)y); r.W.Write((float)z); }
        r.W.Write(indices.Count);
        foreach (var i in indices) r.W.Write(i);
        return r.Str(material).Rest().Bytes();
    }

    /// <summary>Nested bmd (prefab instance): u16, key, 4x4 matrix (identity), u32 campaign mask, 4, campaign region
    /// key, 1, string, 28.</summary>
    public static byte[] Nested(byte[] template, string key, uint campaignMask, string region)
    {
        var r = new Rebuild(template).Copy(2).Str(key);
        r.Skip(64);
        for (var i = 0; i < 16; i++) r.W.Write(i % 5 == 0 ? 1f : 0f);
        return r.U32(campaignMask).Copy(4).Str(region).Rest().Bytes();
    }

}
