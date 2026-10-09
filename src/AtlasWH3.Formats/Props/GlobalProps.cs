using System.Text;

namespace AtlasWH3.Formats.Props;

// terrain\campaigns\<map>\global_props.bin: every compiled bmd_objects.*.bin of the campaign map in one file.
//   u32 entryCount, entryCount x (cstring name, u32 offset), then the FASTBIN0 bodies (offsets relative to the body start).
// The root entry "terrain/campaigns/<map>/bmd_objects.bin" lists one child bmd per region; children may nest further.
// Layout follows robert-d-schultz's "3K global_props bin file to Terry layers.py", plus the meta tags that script skipped:
// every prop, VFX, point light and composite scene carries u16 version (1), u64 flags, u64 mask, where bit i names the
// i-th enum value listed at the top of the same fastbin (building_level_3, settlement_level_5, night, ...).
// Props, VFX, point lights and composite scenes also carry a u32 season mask over the fastbin's season list (ha, au, wi, sp, su).
// Verified against vanilla 3k_dlc07_main_map: 63,852 props, 2,575 VFX, 355 point lights, 547 sounds, 2,573 composite scenes.

/// <summary>Position, Euler rotation (degrees, XYZ as Blender's Matrix.to_euler) and scale of a compiled object.</summary>
public readonly record struct PropTransform(float X, float Y, float Z, double RotX = 0, double RotY = 0, double RotZ = 0,
                                            double ScaleX = 1, double ScaleY = 1, double ScaleZ = 1)
{
    /// <summary>Builds the transform from the three stored 3x3 columns and the position. Mirrors Blender's
    /// Matrix.to_scale() / to_euler() (normalize_m3 + mat3_normalized_to_eul2), including their float precision,
    /// so rotations round to the same 5 decimals as layers made with the Python script.</summary>
    public static PropTransform FromColumns(float[] m, float x, float y, float z)
    {
        // Blender stores columns: c[i][j] is row j of column i, which is exactly the file layout.
        var c = new float[3][];
        var size = new float[3];
        for (var i = 0; i < 3; i++)
        {
            c[i] = [m[i * 3], m[i * 3 + 1], m[i * 3 + 2]];
            var d = c[i][0] * c[i][0] + c[i][1] * c[i][1] + c[i][2] * c[i][2];
            size[i] = MathF.Sqrt(d);
            if (d > 1.0e-35f)
            {
                var inv = 1.0f / MathF.Sqrt(d);
                for (var j = 0; j < 3; j++) c[i][j] *= inv;
            }
            else
                c[i] = [0, 0, 0];
        }

        // Two candidate solutions; keep the one with the smaller total angle.
        float ex, ey, ez;
        var cy = (float)Math.Sqrt((double)c[0][0] * c[0][0] + (double)c[0][1] * c[0][1]); // hypotf
        if (cy > 16.0f * 1.1920929e-07f) // 16 * FLT_EPSILON
        {
            float[] e1 = [MathF.Atan2(c[1][2], c[2][2]), MathF.Atan2(-c[0][2], cy), MathF.Atan2(c[0][1], c[0][0])];
            float[] e2 = [MathF.Atan2(-c[1][2], -c[2][2]), MathF.Atan2(-c[0][2], -cy), MathF.Atan2(-c[0][1], -c[0][0])];
            var pick = MathF.Abs(e1[0]) + MathF.Abs(e1[1]) + MathF.Abs(e1[2]) > MathF.Abs(e2[0]) + MathF.Abs(e2[1]) + MathF.Abs(e2[2]) ? e2 : e1;
            (ex, ey, ez) = (pick[0], pick[1], pick[2]);
        }
        else
            (ex, ey, ez) = (MathF.Atan2(-c[2][1], c[1][1]), MathF.Atan2(-c[0][2], cy), 0);

        const double deg = 180 / Math.PI; // math.degrees() on the float result
        return new PropTransform(x, y, z, ex * deg, ey * deg, ez * deg, size[0], size[1], size[2]);
    }

    /// <summary>Row-major 3x3 rotation * scale from the Euler angles (degrees) and scale: R = Rz * Ry * Rx, column i
    /// scaled by scale i (the inverse of <see cref="FromColumns"/>).</summary>
    public double[] Matrix()
    {
        const double rad = Math.PI / 180;
        double ci = Math.Cos(RotX * rad), si = Math.Sin(RotX * rad);
        double cj = Math.Cos(RotY * rad), sj = Math.Sin(RotY * rad);
        double ch = Math.Cos(RotZ * rad), sh = Math.Sin(RotZ * rad);
        double cc = ci * ch, cs = ci * sh, sc = si * ch, ss = si * sh;
        // Blender eul_to_mat3: mat[col][row]
        double[,] col =
        {
            { cj * ch, cj * sh, -sj },
            { sj * sc - cs, sj * ss + cc, cj * si },
            { sj * cc + ss, sj * cs - sc, cj * ci },
        };
        double[] s = [ScaleX, ScaleY, ScaleZ];
        var m = new double[9];
        for (var row = 0; row < 3; row++)
            for (var c = 0; c < 3; c++)
                m[row * 3 + c] = col[c, row] * s[c];
        return m;
    }
}

public sealed record PropRecord(string Path, PropTransform Transform, string Tags, string Seasons, bool IsDecal, bool ApplyToTerrain,
    bool ApplyToObjects, bool HasHeightPatch, bool ApplyHeightPatch, bool VisibleInsideSnow, bool VisibleOutsideSnow,
    bool VisibleInsideDestruction, bool VisibleOutsideDestruction, bool VisibleInUnseenShroud, bool VisibleInSeenShroud,
    string HeightMode = "");

public sealed record VfxRecord(string Name, PropTransform Transform, string Tags, string Seasons);

public sealed record LightProbeRecord(PropTransform Transform, float Radius);

public sealed record PointLightRecord(PropTransform Transform, string Tags, string Seasons, float R, float G, float B, float ColourScale,
    float Radius, string AnimationType, float AnimationScale1, float AnimationScale2, float ColourMin, float RandomOffset,
    string FalloffType, bool LightProbesOnly);

public sealed record PolyMeshRecord(PropTransform Transform, string Material, IReadOnlyList<(float X, float Y, float Z)> Vertices);

public sealed record SoundRecord(string Name, PropTransform Transform, string ShapeType,
    IReadOnlyList<(float X, float Y, float Z)> Coords, float Radius);

public sealed record CompositeSceneRecord(string Path, PropTransform Transform, string Tags, string Seasons);

/// <summary>Everything placed in one region layer, in file order.</summary>
public sealed class RegionObjects(string region)
{
    public string Region { get; } = region;
    public List<PropRecord> Props { get; } = new();
    /// <summary>The bmd entry each prop was read from (parallel to <see cref="Props"/>; filled by ReadBmdsSeparately).</summary>
    public List<string> PropSources { get; } = new();
    /// <summary>Each prop's stored 4x3 transform as read (3 columns of the 3x3, then the position; parallel to
    /// <see cref="Props"/>): the exact floats, which the Euler form in <see cref="PropRecord.Transform"/> rounds.</summary>
    public List<float[]> PropMatrices { get; } = new();
    public List<VfxRecord> Vfx { get; } = new();
    public List<LightProbeRecord> LightProbes { get; } = new();
    public List<PointLightRecord> PointLights { get; } = new();
    public List<PolyMeshRecord> PolyMeshes { get; } = new();
    public List<SoundRecord> Sounds { get; } = new();
    public List<CompositeSceneRecord> CompositeScenes { get; } = new();

    public int Count => Props.Count + Vfx.Count + LightProbes.Count + PointLights.Count + PolyMeshes.Count
                        + Sounds.Count + CompositeScenes.Count;

    internal void Add(RegionObjects other)
    {
        Props.AddRange(other.Props);
        PropMatrices.AddRange(other.PropMatrices);
        Vfx.AddRange(other.Vfx);
        LightProbes.AddRange(other.LightProbes);
        PointLights.AddRange(other.PointLights);
        PolyMeshes.AddRange(other.PolyMeshes);
        Sounds.AddRange(other.Sounds);
        CompositeScenes.AddRange(other.CompositeScenes);
    }
}

public static class MetaTags
{
    /// <summary>Comma-joined enum values whose bit is set in <paramref name="flags"/> &amp; <paramref name="mask"/>,
    /// in natural order (settlement_level_2 before settlement_level_10). Empty when untagged.</summary>
    public static string Decode(IReadOnlyList<string> enumValues, ulong flags, ulong mask)
    {
        var bits = flags & mask;
        if (enumValues.Count < 64 && bits >> enumValues.Count != 0)
            throw new InvalidDataException($"Meta tag bits 0x{bits:x} reach past the {enumValues.Count} enum values.");
        var tags = new List<string>();
        for (var i = 0; i < enumValues.Count; i++)
            if ((bits >> i & 1) != 0) tags.Add(enumValues[i]);
        tags.Sort(NaturalCompare);
        return string.Join(",", tags);
    }

    private static readonly Dictionary<string, string> SeasonNames = new()
    {
        ["sp"] = "season_spring", ["su"] = "season_summer", ["ha"] = "season_harvest",
        ["au"] = "season_autumn", ["wi"] = "season_winter",
    };

    /// <summary>Terry season_mask for a season bitmask over a fastbin's season codes, in the list's order.
    /// Empty when the object shows in all five seasons (or the mask is empty).</summary>
    public static string DecodeSeasons(IReadOnlyList<string> seasonCodes, uint bits)
    {
        var names = new List<string>();
        for (var i = 0; i < seasonCodes.Count && i < 32; i++)
            if ((bits >> i & 1) != 0)
                names.Add(SeasonNames.GetValueOrDefault(seasonCodes[i], seasonCodes[i]));
        return names.Count == 0 || names.Distinct().Count() == SeasonNames.Count ? "" : string.Join(",", names);
    }

    public static int NaturalCompare(string? a, string? b)
    {
        if (a == null || b == null) return string.CompareOrdinal(a, b);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i; while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                var sj = j; while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var c = long.Parse(a.AsSpan(si, i - si)).CompareTo(long.Parse(b.AsSpan(sj, j - sj)));
                if (c != 0) return c;
            }
            else
            {
                if (a[i] != b[j]) return a[i].CompareTo(b[j]);
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

public sealed class GlobalProps
{
    private readonly byte[] _data;
    private readonly int _bodyStart;

    /// <summary>(parent bmd, child bmd, 4x4 matrix as stored) for every nested bmd met by <see cref="ReadRegions"/>.</summary>
    public List<(string Parent, string Child, float[] Matrix)> NestedMatrices { get; } = [];

    /// <summary>Entry name to body offset, in file order.</summary>
    public IReadOnlyDictionary<string, int> Entries { get; }

    private GlobalProps(byte[] data)
    {
        _data = data;
        var r = new Reader(data, 0);
        var count = r.U32();
        var entries = new Dictionary<string, int>((int)count);
        for (var i = 0; i < count; i++)
        {
            var name = r.CString();
            entries[name] = (int)r.U32();
        }
        _bodyStart = r.Pos;
        Entries = entries;
    }

    public static GlobalProps Load(string path) => new(File.ReadAllBytes(path));
    public static GlobalProps Read(byte[] data) => new(data);

    /// <summary>Every entry's FASTBIN0 body, in file order (bodies are stored in the same order as the table).</summary>
    public IReadOnlyList<(string Name, byte[] Body)> Bodies()
    {
        var ordered = Entries.OrderBy(e => e.Value).ToList();
        var result = new List<(string, byte[])>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = _bodyStart + ordered[i].Value;
            var end = i + 1 < ordered.Count ? _bodyStart + ordered[i + 1].Value : _data.Length;
            result.Add((ordered[i].Key, _data[start..end]));
        }
        return result;
    }

    /// <summary>Writes the container: u32 count, (C string name, u32 offset relative to the first body) per entry,
    /// then the bodies back to back in the same order.</summary>
    public static byte[] Pack(IEnumerable<(string Name, byte[] Body)> entries)
    {
        var list = entries.ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(list.Count);
        var offset = 0u;
        foreach (var (name, body) in list)
        {
            w.Write(Encoding.UTF8.GetBytes(name));
            w.Write((byte)0);
            w.Write(offset);
            offset += (uint)body.Length;
        }
        foreach (var (_, body) in list) w.Write(body);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// All objects, one entry per region in the root bmd's order of first appearance. Like the Python script, exact
    /// duplicates inside one child bmd (same object, transform and tags) are dropped, but duplicates across child bmds
    /// are kept.
    /// </summary>
    public IReadOnlyList<RegionObjects> ReadRegions(string mapName)
    {
        var rootName = $"terrain/campaigns/{mapName}/bmd_objects.bin";
        if (!Entries.TryGetValue(rootName, out var rootOffset))
            throw new InvalidDataException($"global_props.bin has no root entry {rootName}.");

        var r = Body(rootOffset);
        SkipPreamble(ref r, out _, out _);
        r.Skip(2);
        var children = new List<(string Region, string Bmd)>();
        var n = r.U32();
        for (var i = 0; i < n; i++)
        {
            r.Skip(2);
            var bmd = r.Str();
            r.Skip(64 + 4 + 4);
            var region = r.Str();
            r.Skip(1);
            r.Str();
            r.Skip(28);
            children.Add((region, bmd));
        }

        var regions = new List<RegionObjects>();
        foreach (var group in children.GroupBy(c => c.Region))
        {
            var objects = new RegionObjects(group.Key);
            foreach (var (_, bmd) in group)
            {
                var one = new RegionObjects(group.Key);
                ReadBmd(bmd, one);
                objects.Add(Deduplicate(one));
            }
            regions.Add(objects);
        }
        return regions;
    }

    /// <summary>Every child bmd of the root (region, bmd entry) and its objects, read separately (no merging).</summary>
    public IEnumerable<(string Region, string Bmd, RegionObjects Objects)> ReadBmdsSeparately(string mapName)
    {
        var rootName = $"terrain/campaigns/{mapName}/bmd_objects.bin";
        var r = Body(Entries[rootName]);
        SkipPreamble(ref r, out _, out _);
        r.Skip(2);
        var n = r.U32();
        var children = new List<(string, string)>();
        for (var i = 0; i < n; i++)
        {
            r.Skip(2);
            var bmd = r.Str();
            r.Skip(64 + 4 + 4);
            var region = r.Str();
            r.Skip(1);
            r.Str();
            r.Skip(28);
            children.Add((region, bmd));
        }
        foreach (var (region, bmd) in children)
        {
            var one = new RegionObjects(region);
            ReadBmd(bmd, one);
            yield return (region, bmd, one);
        }
    }

    /// <summary>
    /// The props and VFX of one standalone FASTBIN0 body, such as a campaign tile's bmd_data.bin (same v35 layout as
    /// the global_props bodies; nested bmds are not followed). Sections the reader does not know (e.g. the river
    /// tiles' SST_RIVER sound emitters) end the read: what came before them (props, VFX...) is kept.
    /// </summary>
    public static RegionObjects ReadBody(byte[] body)
    {
        var props = Read(Pack([("body", body)]));
        var objects = new RegionObjects("body");
        try { props.ReadBmd("body", objects); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException) { }
        return objects;
    }

    private Reader Body(int offset) => new(_data, _bodyStart + offset);

    private static void SkipPreamble(ref Reader r, out List<string> enumValues, out List<string> seasons)
    {
        if (Encoding.ASCII.GetString(r.Bytes(8)) != "FASTBIN0") throw new InvalidDataException($"No FASTBIN0 at {r.Pos - 8}.");
        var version = r.U16();
        if (version != 35) throw new InvalidDataException($"Unsupported fastbin version {version}.");
        r.Skip(2 + 4);
        enumValues = new List<string>();
        var enumTypes = r.U32();
        for (var t = 0; t < enumTypes; t++)
        {
            r.Skip(2);
            r.Str();
            var values = r.U32();
            for (var v = 0; v < values; v++) enumValues.Add(r.Str());
        }
        r.Skip(2);
        seasons = new List<string>();
        var seasonCount = r.U32();
        for (var s = 0; s < seasonCount; s++) seasons.Add(r.Str());
        r.Skip(47);
    }

    private void ReadBmd(string entryName, RegionObjects into)
    {
        if (!Entries.TryGetValue(entryName, out var offset))
            throw new InvalidDataException($"global_props.bin has no entry {entryName}.");
        var r = Body(offset);
        SkipPreamble(ref r, out var enums, out var seasonCodes);

        // Nested bmds
        r.Skip(2);
        var nested = r.U32();
        for (var i = 0; i < nested; i++)
        {
            r.Skip(2);
            var name = r.Str();
            var matrix = new float[16];
            for (var k = 0; k < 16; k++) matrix[k] = r.F32();
            r.Skip(4 + 4);
            NestedMatrices.Add((entryName, name, matrix));
            ReadBmd(name, into);
            r.Str();
            r.Skip(1);
            r.Str();
            r.Skip(28);
        }
        r.Skip(14 + 14);

        // Props
        r.Skip(2);
        var paths = new string[r.U32()];
        for (var i = 0; i < paths.Length; i++) paths[i] = r.Str();
        var props = r.U32();
        for (var i = 0; i < props; i++)
        {
            r.Skip(2);
            var path = paths[r.U32()];
            var tags = ReadTags(ref r, enums);
            var raw = new float[12];
            for (var k = 0; k < 12; k++) raw[k] = r.F32();
            var t = PropTransform.FromColumns(raw[..9], raw[9], raw[10], raw[11]);
            var isDecal = r.Bool();
            r.Skip(2); // logical decal, fauna
            var inSnow = r.Bool();
            var outSnow = r.Bool();
            var inDestruction = r.Bool();
            var outDestruction = r.Bool();
            r.Skip(1 + 4 + 4 + 1); // animated, decal parallax scale, decal tiling, override gbuffer normal
            r.Skip(2 + 1 + 1 + 3);
            var seasons = MetaTags.DecodeSeasons(seasonCodes, r.U32());
            var seenShroud = r.Bool();
            var unseenShroud = r.Bool();
            r.Skip(1); // visible in shroud
            var applyToTerrain = r.U8() == 0;
            var applyToObjects = r.Bool();
            var heightMode = r.Str(); // BHM_TERRAIN / BHM_ABSOLUTE...
            r.Skip(4 + 1); // unknown, casts shadow
            var hasHeightPatch = r.Bool();
            r.Skip(4 + 4 + 1 + 8); // tint, faction colour, alpha, unknown
            var applyHeightPatch = r.Bool();
            into.Props.Add(new PropRecord(path, t, tags, seasons, isDecal, applyToTerrain, applyToObjects, hasHeightPatch,
                applyHeightPatch, inSnow, outSnow, inDestruction, outDestruction, unseenShroud, seenShroud, heightMode));
            into.PropMatrices.Add(raw);
            into.PropSources.Add(entryName);
        }

        // VFX
        r.Skip(2);
        var vfx = r.U32();
        for (var i = 0; i < vfx; i++)
        {
            r.Skip(2);
            var name = r.Str();
            var t = ReadTransform(ref r);
            r.Skip(4); // emission rate
            r.Str();   // instance name
            r.Skip(2 + 1 + 1 + 3);
            var seasons = MetaTags.DecodeSeasons(seasonCodes, r.U32());
            r.Skip(1);
            r.Str();
            r.Skip(1 + 3); // autoplay, unknown
            var tags = ReadTags(ref r, enums);
            r.Skip(6);
            into.Vfx.Add(new VfxRecord(name, t, tags, seasons));
        }
        r.Skip(26);

        // Light probes
        r.Skip(2);
        var probes = r.U32();
        for (var i = 0; i < probes; i++)
        {
            r.Skip(2);
            var (x, y, z) = (r.F32(), r.F32(), r.F32());
            var radius = r.F32();
            r.Skip(1);
            r.Str();
            r.Skip(18);
            into.LightProbes.Add(new LightProbeRecord(new PropTransform(x, y, z), radius));
        }

        // Terrain hole triangles (none in 3K)
        r.Skip(2);
        if (r.U32() != 0) throw new InvalidDataException("Terrain hole triangles are not supported.");

        // Point lights
        r.Skip(2);
        var lights = r.U32();
        for (var i = 0; i < lights; i++)
        {
            r.Skip(2);
            var (x, y, z) = (r.F32(), r.F32(), r.F32());
            var radius = r.F32();
            var (red, green, blue) = (r.F32(), r.F32(), r.F32());
            var colourScale = r.F32();
            var anim = r.U8() switch { 0 => "LAT_NONE", 1 => "LAT_RADIUS_SIN", 2 => "LAT_RADIUS_SIN_SIN", var a => a.ToString() };
            var (s1, s2, colourMin, randomOffset) = (r.F32(), r.F32(), r.F32(), r.F32());
            var falloff = r.Str();
            r.Skip(1);
            r.Str();
            var probesOnly = r.Bool();
            r.Skip(4);
            var tags = ReadTags(ref r, enums);
            var seasons = ReadTrailingSeasons(ref r, seasonCodes);
            into.PointLights.Add(new PointLightRecord(new PropTransform(x, y, z), tags, seasons, red, green, blue, colourScale,
                radius, anim, s1, s2, colourMin, randomOffset, falloff, probesOnly));
        }

        r.Skip(6);      // building projectile emitters (unused in campaign)
        r.Skip(2 + 21); // playable area

        // Polygon meshes
        r.Skip(2);
        var meshes = r.U32();
        for (var i = 0; i < meshes; i++)
        {
            r.Skip(2);
            var vertices = new (float, float, float)[r.U32()];
            for (var v = 0; v < vertices.Length; v++) vertices[v] = (r.F32(), r.F32(), r.F32());
            r.Skip(2 * (int)r.U32());
            var material = r.Str();
            r.Str();
            r.Skip(18 + 4);
            into.PolyMeshes.Add(new PolyMeshRecord(new PropTransform(0, vertices[0].Item2, 0), material, vertices));
        }
        r.Skip(6);

        // Spot lights (none in 3K)
        r.Skip(2);
        if (r.U32() != 0) throw new InvalidDataException("Spot lights are not supported.");

        // Sound emitters
        r.Skip(2);
        var sounds = r.U32();
        for (var i = 0; i < sounds; i++)
        {
            r.Skip(2);
            var name = r.Str();
            var shape = r.Str();
            var coords = new (float, float, float)[r.U32()];
            for (var c = 0; c < coords.Length; c++) coords[c] = (r.F32(), r.F32(), r.F32());
            r.Skip(4);
            var radius = r.F32();
            r.Skip(53);
            r.Str();
            r.Skip(26);
            into.Sounds.Add(new SoundRecord(name, new PropTransform(coords[0].Item1, coords[0].Item2, coords[0].Item3),
                shape, coords, radius));
        }

        // Composite scenes
        r.Skip(2);
        var scenes = r.U32();
        for (var i = 0; i < scenes; i++)
        {
            r.Skip(2);
            var t = ReadTransform(ref r);
            var path = r.Str();
            r.Str();
            r.Skip(5);
            var tags = ReadTags(ref r, enums);
            var seasons = ReadTrailingSeasons(ref r, seasonCodes);
            into.CompositeScenes.Add(new CompositeSceneRecord(path, t, tags, seasons));
        }
    }

    private static PropTransform ReadTransform(ref Reader r)
    {
        var m = new float[9];
        for (var i = 0; i < 9; i++) m[i] = r.F32();
        return PropTransform.FromColumns(m, r.F32(), r.F32(), r.F32());
    }

    /// <summary>The 12 bytes after a point light's or composite scene's meta tags: 5 unknown, u16 version (1),
    /// u32 season mask, 1 unknown.</summary>
    private static string ReadTrailingSeasons(ref Reader r, List<string> seasonCodes)
    {
        r.Skip(5);
        var version = r.U16();
        if (version != 1) throw new InvalidDataException($"Unexpected season block version {version} at {r.Pos - 2}.");
        var seasons = MetaTags.DecodeSeasons(seasonCodes, r.U32());
        r.Skip(1);
        return seasons;
    }

    private static string ReadTags(ref Reader r, List<string> enums)
    {
        var version = r.U16();
        if (version != 1) throw new InvalidDataException($"Unexpected meta tag version {version} at {r.Pos - 2}.");
        return MetaTags.Decode(enums, r.U64(), r.U64());
    }

    /// <summary>Drops repeats of the same object, the way the script's combine_and_simplify does (culture masks are
    /// always 0 in 3K, so merging is just dropping): same identity and every transform value within 1e-5.</summary>
    private static RegionObjects Deduplicate(RegionObjects o)
    {
        var result = new RegionObjects(o.Region);
        if (o.PropMatrices.Count == o.Props.Count)
        {
            var props = Distinct(o.Props.Zip(o.PropMatrices), p => (p.First.Path, p.First.Tags, p.First.Seasons), p => p.First.Transform);
            result.Props.AddRange(props.Select(p => p.First));
            result.PropMatrices.AddRange(props.Select(p => p.Second));
        }
        else
            result.Props.AddRange(Distinct(o.Props, p => (p.Path, p.Tags, p.Seasons), p => p.Transform));
        result.Vfx.AddRange(Distinct(o.Vfx, v => (v.Name, v.Tags, v.Seasons), v => v.Transform));
        result.LightProbes.AddRange(o.LightProbes);
        result.PointLights.AddRange(Distinct(o.PointLights, l => (l.R, l.G, l.B, l.Tags, l.Seasons), l => l.Transform));
        result.PolyMeshes.AddRange(Distinct(o.PolyMeshes, p => (p.Material, string.Join(";", p.Vertices)), p => p.Transform));
        result.Sounds.AddRange(Distinct(o.Sounds, s => s.Name, s => s.Transform));
        result.CompositeScenes.AddRange(Distinct(o.CompositeScenes, c => (c.Path, c.Tags, c.Seasons), c => c.Transform));
        return result;
    }

    private const double SameEpsilon = 0.00001;

    private static bool Same(PropTransform a, PropTransform b) =>
        Math.Abs(a.X - b.X) < SameEpsilon && Math.Abs(a.Y - b.Y) < SameEpsilon && Math.Abs(a.Z - b.Z) < SameEpsilon &&
        Math.Abs(a.RotX - b.RotX) < SameEpsilon && Math.Abs(a.RotY - b.RotY) < SameEpsilon && Math.Abs(a.RotZ - b.RotZ) < SameEpsilon &&
        Math.Abs(a.ScaleX - b.ScaleX) < SameEpsilon && Math.Abs(a.ScaleY - b.ScaleY) < SameEpsilon && Math.Abs(a.ScaleZ - b.ScaleZ) < SameEpsilon;

    /// <summary>Keeps the first of each group of equal objects. Objects are bucketed on a 0.01 grid in x/z and each
    /// lookup also checks the neighbouring cells an epsilon away, so nothing near a cell edge is missed.</summary>
    private static List<T> Distinct<T>(IEnumerable<T> items, Func<T, object> identity, Func<T, PropTransform> transform)
    {
        static long Cell(double v) => (long)Math.Floor(v * 100);
        var kept = new List<T>();
        var buckets = new Dictionary<(object, long, long), List<T>>();
        foreach (var item in items)
        {
            var id = identity(item);
            var t = transform(item);
            var duplicate = false;
            foreach (var cx in new[] { Cell(t.X - SameEpsilon), Cell(t.X + SameEpsilon) }.Distinct())
            foreach (var cz in new[] { Cell(t.Z - SameEpsilon), Cell(t.Z + SameEpsilon) }.Distinct())
                if (!duplicate && buckets.TryGetValue((id, cx, cz), out var list))
                    duplicate = list.Any(other => Same(transform(other), t));
            if (duplicate) continue;
            var key = (id, Cell(t.X), Cell(t.Z));
            if (!buckets.TryGetValue(key, out var own)) buckets[key] = own = new List<T>();
            own.Add(item);
            kept.Add(item);
        }
        return kept;
    }

    private ref struct Reader(byte[] data, int pos)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        public int Pos = pos;

        public void Skip(int n) => Pos += n;
        public ReadOnlySpan<byte> Bytes(int n) { var s = _d.Slice(Pos, n); Pos += n; return s; }
        public byte U8() => _d[Pos++];
        public bool Bool() => _d[Pos++] == 1;
        public ushort U16() => BitConverter.ToUInt16(Bytes(2));
        public uint U32() => BitConverter.ToUInt32(Bytes(4));
        public ulong U64() => BitConverter.ToUInt64(Bytes(8));
        public float F32() => BitConverter.ToSingle(Bytes(4));
        public string Str() => Encoding.UTF8.GetString(Bytes(U16()));

        public string CString()
        {
            var end = _d[Pos..].IndexOf((byte)0);
            var s = Encoding.UTF8.GetString(_d.Slice(Pos, end));
            Pos += end + 1;
            return s;
        }
    }
}
