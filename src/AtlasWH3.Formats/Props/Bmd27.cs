using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Props;

// WH3 BMD_OBJECTS bodies: FASTBIN0 version 27, the bodies of global_props.bin, global_props_sound.bin and the
// devastation pieces' objects files. Unlike 3K's v35 there is no per-body preamble (no enum tags, no seasons): every
// framing byte is the same in all 163,331 bodies of BOB's IEE and Old World global_props(_sound).bin (2026-10-09).
// Record layouts follow WH3_visual_map_decompiler's GlobalPropsParser, with the bytes it skips named where the layers
// explain them. Each list is "u16 version, u32 count, records"; each record starts with its own u16 version.
// Field names are the kit's: its BATTLE_MAP_DEFINITION serialisers in empireutility.modder.x64.dll (one versioned
// visitor per record that names each field and gates it on the record version, e.g. sound shapes at 0x2020a0, props at
// 0x2013cd, the body's list order at 0x1ff03a) give the order, type and name of every field read here, and Terry's
// .bmd.xml dumps match them. In BOB's IEE and Old World bodies many of these fields only ever hold their defaults.

/// <summary>The flag block several WH3 records share: u16 version (4), allow_in_outfield, clamp_to_water_surface,
/// per-season visibility (spring, summer, autumn, winter: always 1, WH3 has no seasons), visible_in_tactical_view,
/// visible_in_tactical_view_only.</summary>
public record struct Bmd27Flags(byte TacticalView = 0, byte TacticalViewOnly = 0, byte AllowInOutfield = 0, byte ClampToWaterSurface = 0)
{
    public void Write(BinaryWriter w)
    {
        w.Write((ushort)4);
        w.Write(AllowInOutfield);
        w.Write(ClampToWaterSurface);
        w.Write(0x01010101u);
        w.Write(TacticalView);
        w.Write(TacticalViewOnly);
    }

    internal static Bmd27Flags Read(ref Bmd27Reader r)
    {
        var start = r.Pos;
        if (r.U16() != 4) throw new InvalidDataException($"Unexpected BMD flag block version at {start}.");
        byte outfield = r.U8(), water = r.U8();
        if (r.U32() != 0x01010101u) throw new InvalidDataException($"Unexpected BMD season flags at {start}.");
        return new Bmd27Flags(r.U8(), r.U8(), outfield, water);
    }
}

/// <summary>A property_override of a PREFAB_INSTANCE: the properties of a battlefield building inside the referenced
/// prefab (BATTLEFIELD_BUILDING's properties record; the kit writes v13, vanilla WH3 battle prefabs hold v12 and v13).
/// Fields by the kit's version gates; the flags are bits of one word in the kit but each is stored as a bool.</summary>
public sealed class Bmd27BuildingProperties
{
    public ushort Version = 13;
    /// <summary>v2+.</summary>
    public string BuildingId = "";
    public float StartingDamageUnary;
    public byte OnFire, StartDisabled, WeakPoint, AiBreachable, Indestructible, Dockable, Toggleable, Lite;
    /// <summary>v3 to v7 only.</summary>
    public byte ClampToSurface;
    /// <summary>v4, v5, v6, v7, v9, v10, v11 and up.</summary>
    public byte CastShadows, KeyBuilding, KeyBuildingUseFort, IsPropInOutfield, SettlementLevelConfigurable, HideTooltip, IncludeInFog;
    /// <summary>v12+.</summary>
    public float BlendEdges;
    /// <summary>v13+.</summary>
    public byte ReceiveDecals;

    internal static Bmd27BuildingProperties Read(ref Bmd27Reader r)
    {
        var p = new Bmd27BuildingProperties { Version = r.U16() };
        var v = p.Version;
        if (v >= 2) p.BuildingId = r.Str();
        p.StartingDamageUnary = r.F32();
        p.OnFire = r.U8(); p.StartDisabled = r.U8(); p.WeakPoint = r.U8(); p.AiBreachable = r.U8();
        p.Indestructible = r.U8(); p.Dockable = r.U8(); p.Toggleable = r.U8(); p.Lite = r.U8();
        if (v is >= 3 and < 8) p.ClampToSurface = r.U8();
        if (v >= 4) p.CastShadows = r.U8();
        if (v >= 5) p.KeyBuilding = r.U8();
        if (v >= 6) p.KeyBuildingUseFort = r.U8();
        if (v >= 7) p.IsPropInOutfield = r.U8();
        if (v >= 9) p.SettlementLevelConfigurable = r.U8();
        if (v >= 10) p.HideTooltip = r.U8();
        if (v >= 11) p.IncludeInFog = r.U8();
        if (v >= 12) p.BlendEdges = r.F32();
        if (v >= 13) p.ReceiveDecals = r.U8();
        return p;
    }

    internal void Write(BinaryWriter w, Action<BinaryWriter, string> str)
    {
        var v = Version;
        w.Write(v);
        if (v >= 2) str(w, BuildingId);
        w.Write(StartingDamageUnary);
        w.Write(OnFire); w.Write(StartDisabled); w.Write(WeakPoint); w.Write(AiBreachable);
        w.Write(Indestructible); w.Write(Dockable); w.Write(Toggleable); w.Write(Lite);
        if (v is >= 3 and < 8) w.Write(ClampToSurface);
        if (v >= 4) w.Write(CastShadows);
        if (v >= 5) w.Write(KeyBuilding);
        if (v >= 6) w.Write(KeyBuildingUseFort);
        if (v >= 7) w.Write(IsPropInOutfield);
        if (v >= 9) w.Write(SettlementLevelConfigurable);
        if (v >= 10) w.Write(HideTooltip);
        if (v >= 11) w.Write(IncludeInFog);
        if (v >= 12) w.Write(BlendEdges);
        if (v >= 13) w.Write(ReceiveDecals);
    }
}

/// <summary>A reference to another body (cell to bucket, root to cell; Terry's PREFAB_INSTANCE): v9.</summary>
public sealed class Bmd27Nested
{
    public string Name = "";
    /// <summary>4x4 matrix as stored (always identity in BOB's files).</summary>
    public float[] Matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    /// <summary>Empty in every BOB body.</summary>
    public List<Bmd27BuildingProperties> PropertyOverrides = [];
    /// <summary>Terry's campaign_type_mask: prefab_types bits (bit = value - 1).</summary>
    public ulong CultureMask;
    public string Region = "";
    public string HeightMode = "BHM_CLASSIC";
    /// <summary>Terry's uid (0 in BOB's files).</summary>
    public ulong Uid;
}

/// <summary>A prop (ECMesh, ECDecal, river model): v30.</summary>
public sealed class Bmd27Prop
{
    public uint PathIndex;
    /// <summary>The 3x3 rotation-scale as three columns, then the position (12 floats, as stored).</summary>
    public float[] Transform = new float[12];
    public byte Decal, LogicalDecal, Fauna, VisibleInsideSnow = 1, VisibleOutsideSnow = 1, VisibleInsideDestruction = 1,
        VisibleOutsideDestruction = 1, Animated;
    public float DecalParallaxScale, DecalTiling;
    public byte DecalOverrideGbufferNormal;
    public Bmd27Flags Flags;
    public byte VisibleInShroud, DecalApplyToTerrain, DecalApplyToGbufferObjects, DecalRenderAboveSnow;
    public string HeightMode = "BHM_CLASSIC";
    /// <summary>Terry's pdlc_mask (1 in BOB's files, all bits in a Terry-compiled prefab).</summary>
    public ulong CultureMask = 1;
    public byte CastShadow = 1, NoCulling, HasHeightPatch, ApplyHeightPatch, IncludeInFog;
    /// <summary>1 unless the object is visible in the shroud only.</summary>
    public byte NotShroudOnly = 1;
    public byte DynamicShadows, UsesTerrainVertexOffset;
    public float BlendEdgesWithTerrain, DecalFadeStart = 0.875f, DecalAngleThreshold = 90;
    public byte DecalMirrorX, DecalMirrorZ;
    public int DecalLayer;
    public byte DecalTilingAlpha;
}

public sealed class Bmd27Vfx
{
    public string Name = "";
    public float[] Transform = new float[12];
    public float EmissionRate;
    public string Instance = "";
    public Bmd27Flags Flags;
    public string HeightMode = "BHM_CLASSIC";
    /// <summary>The kit's pdlc_mask, as on props, lights, spots and scenes.</summary>
    public ulong CultureMask = 1;
    public byte Autoplay = 1, VisibleInShroud;
    public int ParentId = -1;
    public byte NotShroudOnly = 1, NoCulling;
}

public sealed class Bmd27Probe
{
    public float X, Y, Z, OuterRadius, InnerRadius;
    /// <summary>Terry's is_cylinder: the probe's shape is an ECDoubleCylinder, not an ECDoubleSphere.</summary>
    public byte IsCylinder;
    public byte Primary;
    public string HeightMode = "BHM_CLASSIC";
}

public sealed class Bmd27Hole
{
    /// <summary>Three vertices (9 floats).</summary>
    public float[] Vertices = new float[9];
    public string HeightMode = "BHM_CLASSIC";
    public Bmd27Flags Flags;
}

public sealed class Bmd27Light
{
    public float X, Y, Z, Radius, R, G, B, ColourScale;
    public byte Animation;
    /// <summary>AnimationScale1/2: the animation's params.</summary>
    public float AnimationScale1, AnimationScale2, ColourMin, RandomOffset;
    public string Falloff = "";
    /// <summary>Terry's lf_relative (0 in every BOB light).</summary>
    public byte LfRelative;
    public string HeightMode = "BHM_CLASSIC";
    public byte LightProbesOnly;
    public ulong CultureMask = 1;
    public Bmd27Flags Flags;
}

public sealed class Bmd27Poly
{
    public List<(float X, float Y, float Z)> Vertices = [];
    public List<ushort> Indices = [];
    public string Material = "";
    public string HeightMode = "BHM_CLASSIC";
    public Bmd27Flags Flags;
    public float[] Transform = new float[12];
    public byte VisibleInsideSnow = 1, VisibleOutsideSnow = 1, VisibleInsideDestruction = 1, VisibleOutsideDestruction = 1;
    public byte VisibleInShroud;
    /// <summary>Terry's visible_without_shroud: 1 unless the object is visible in the shroud only.</summary>
    public byte NotShroudOnly = 1;
}

public sealed class Bmd27Spot
{
    public float X, Y, Z;
    /// <summary>Rotation quaternion (x, y, z, w); the kit calls it "end".</summary>
    public float[] Rotation = [0, 0, 0, 1];
    public float Length, InnerAngle, OuterAngle, R, G, B, Falloff;
    public string Gobo = "";
    public byte Volumetric;
    public string HeightMode = "BHM_CLASSIC";
    public ulong CultureMask = 1;
    public Bmd27Flags Flags;
}

/// <summary>A river node of an SST_RIVER sound (v1): vertex, width, flow_speed. No BOB sound has one.</summary>
public record struct Bmd27SoundRiverNode(ushort Version, float X, float Y, float Z, float Width, float FlowSpeed);

public sealed class Bmd27Sound
{
    public string Name = "";
    public string Shape = "SST_POINT";
    public List<(float X, float Y, float Z)> Points = [];
    public float InnerRadius, OuterRadius;
    /// <summary>Min x, y, z then max x, y, z.</summary>
    public float[] InnerCube = new float[6], OuterCube = new float[6];
    public List<Bmd27SoundRiverNode> RiverNodes = [];
    public byte ClampToSurface;
    public string HeightMode = "BHM_CLASSIC";
    /// <summary>Terry's campaign_type_mask.</summary>
    public ulong CultureMask;
    /// <summary>Terry's pdlc_mask (1 in every BOB sound).</summary>
    public ulong PdlcMask = 1;
    /// <summary>direction (x, y, z) then up (x, y, z).</summary>
    public float[] Axes = [1, 0, 0, 0, 1, 0];
    /// <summary>The SOUND_SHAPE_SCOPE (SSS_*).</summary>
    public string Scope = "SSS_SOUND_MARKER";
}

public sealed class Bmd27Scene
{
    public float[] Transform = new float[12];
    public string Path = "";
    public string HeightMode = "BHM_CLASSIC";
    public ulong CultureMask = 1;
    public byte Autoplay = 1, VisibleInShroud, NoCulling;
    public string ScriptId = "", ParentScriptId = "";
    public byte NotShroudOnly = 1, TacticalView, TacticalViewOnly;
    /// <summary>ECCompositeScene hold_first_frame / hold_last_frame.</summary>
    public byte HoldFirstFrame, HoldLastFrame;
}

/// <summary>One FASTBIN0 v27 BMD_OBJECTS body.</summary>
public sealed class Bmd27Body
{
    public const ushort Version = 27;

    // The lists between the ones read here, empty in every BOB body (kept as read, so unexpected files still round-trip).
    // An empty list is u16 version + u32 0, or u32 0 alone for the unversioned outline lists.
    /// <summary>BATTLEFIELD_BUILDING_LIST, BATTLEFIELD_BUILDING_LIST_FAR, CAPTURE_LOCATION_SET (v11), EF_LINE_LIST,
    /// GO_OUTLINES, NON_TERRAIN_OUTLINES, ZONES_TEMPLATE_LIST.</summary>
    private static readonly byte[] DefaultPreamble = Convert.FromHexString("0100000000000100000000000b0000000000000000000000000000000000010000000000");
    /// <summary>BMD_OUTLINE_LIST, TERRAIN_OUTLINES, LITE_BUILDING_OUTLINES, CAMERA_ZONES, CIVILIAN_DEPLOYMENT_LIST,
    /// CIVILIAN_SHELTER_LIST.</summary>
    private static readonly byte[] DefaultAfterNested = Convert.FromHexString("01000000000000000000000000000100000000000000000000000000");
    /// <summary>AI_HINTS (v1): separators, directed_points, polylines, polylines_list.</summary>
    private static readonly byte[] DefaultAfterVfx = Convert.FromHexString("0100010000000000010000000000010000000000010000000000");
    /// <summary>BUILDING_PROJECTILE_EMITTER_LIST, then PLAYABLE_AREA (v3): area (min x, min y, max x, max y), has_been_set,
    /// valid_location_flags (v1: north, south, east, west). global_props.bin bodies carry min (64, 64) where
    /// global_props_sound.bin's carry (0, 0).</summary>
    public static readonly byte[] AfterLightsProps = Convert.FromHexString("010000000000030000008042000080420000f0440000f04400010001010101");
    public static readonly byte[] AfterLightsSound = Convert.FromHexString("010000000000030000000000000000000000f0440000f04400010001010101");
    /// <summary>TERRAIN_STENCIL_BLEND_TRIANGLE_LIST.</summary>
    private static readonly byte[] DefaultAfterPoly = Convert.FromHexString("010000000000");
    /// <summary>DEPLOYMENT_LIST, BMD_CATCHMENT_AREA_LIST, TOGGLEABLE_BUILDINGS_SLOT_LIST (v7), TERRAIN_DECAL_LIST,
    /// TREE_LIST_REFERENCE_LIST, GRASS_LIST_REFERENCE_LIST, WATER_OUTLINES.</summary>
    private static readonly byte[] DefaultTrailing = Convert.FromHexString("01000000000001000000000007000000000001000000000001000000000001000000000000000000");

    public byte[] Preamble = DefaultPreamble;
    public byte[] AfterNested = DefaultAfterNested;
    public byte[] AfterVfx = DefaultAfterVfx;
    public byte[] AfterLights = AfterLightsProps;
    public byte[] AfterPoly = DefaultAfterPoly;
    public byte[] Trailing = DefaultTrailing;

    public List<Bmd27Nested> Nested { get; } = [];
    public List<string> PropPaths { get; } = [];
    public List<Bmd27Prop> Props { get; } = [];
    public List<Bmd27Vfx> Vfx { get; } = [];
    public List<Bmd27Probe> Probes { get; } = [];
    public List<Bmd27Hole> Holes { get; } = [];
    public List<Bmd27Light> Lights { get; } = [];
    public List<Bmd27Poly> Polys { get; } = [];
    public List<Bmd27Spot> Spots { get; } = [];
    public List<Bmd27Sound> Sounds { get; } = [];
    public List<Bmd27Scene> Scenes { get; } = [];

    public int ObjectCount => Props.Count + Vfx.Count + Probes.Count + Holes.Count + Lights.Count + Polys.Count + Spots.Count
                              + Sounds.Count + Scenes.Count;

    /// <summary>Index of a prop path in this body's path table, added on first use (BOB's order).</summary>
    public uint PathIndex(string path)
    {
        var i = PropPaths.IndexOf(path);
        if (i < 0) { i = PropPaths.Count; PropPaths.Add(path); }
        return (uint)i;
    }

    public static Bmd27Body Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10 || !data[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("BMD body is not FASTBIN0.");
        var r = new Bmd27Reader(data) { Pos = 8 };
        var version = r.U16();
        if (version != Version) throw new InvalidDataException($"Unsupported BMD version {version} (WH3 is {Version}).");
        var b = new Bmd27Body { Preamble = r.Take(DefaultPreamble.Length) };

        ListVersion(ref r, 1, "nested");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 9, "nested");
            var x = new Bmd27Nested { Name = r.Str(), Matrix = r.Floats(16) };
            for (var k = r.U32(); k > 0; k--) x.PropertyOverrides.Add(Bmd27BuildingProperties.Read(ref r));
            x.CultureMask = r.U64(); x.Region = r.Str(); x.HeightMode = r.Str();
            x.Uid = r.U64();
            b.Nested.Add(x);
        }
        b.AfterNested = r.Take(DefaultAfterNested.Length);

        ListVersion(ref r, 2, "props");
        for (var n = r.U32(); n > 0; n--) b.PropPaths.Add(r.Str());
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 30, "prop");
            var p = new Bmd27Prop { PathIndex = r.U32(), Transform = r.Floats(12) };
            p.Decal = r.U8(); p.LogicalDecal = r.U8(); p.Fauna = r.U8(); p.VisibleInsideSnow = r.U8(); p.VisibleOutsideSnow = r.U8();
            p.VisibleInsideDestruction = r.U8(); p.VisibleOutsideDestruction = r.U8(); p.Animated = r.U8();
            p.DecalParallaxScale = r.F32(); p.DecalTiling = r.F32(); p.DecalOverrideGbufferNormal = r.U8();
            p.Flags = Bmd27Flags.Read(ref r);
            p.VisibleInShroud = r.U8(); p.DecalApplyToTerrain = r.U8(); p.DecalApplyToGbufferObjects = r.U8(); p.DecalRenderAboveSnow = r.U8();
            p.HeightMode = r.Str(); p.CultureMask = r.U64();
            p.CastShadow = r.U8(); p.NoCulling = r.U8(); p.HasHeightPatch = r.U8(); p.ApplyHeightPatch = r.U8(); p.IncludeInFog = r.U8();
            p.NotShroudOnly = r.U8(); p.DynamicShadows = r.U8(); p.UsesTerrainVertexOffset = r.U8();
            p.BlendEdgesWithTerrain = r.F32(); p.DecalFadeStart = r.F32(); p.DecalAngleThreshold = r.F32();
            p.DecalMirrorX = r.U8(); p.DecalMirrorZ = r.U8(); p.DecalLayer = r.I32(); p.DecalTilingAlpha = r.U8();
            b.Props.Add(p);
        }

        ListVersion(ref r, 1, "vfx");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 11, "vfx");
            var v = new Bmd27Vfx { Name = r.Str(), Transform = r.Floats(12), EmissionRate = r.F32(), Instance = r.Str() };
            v.Flags = Bmd27Flags.Read(ref r);
            v.HeightMode = r.Str(); v.CultureMask = r.U64(); v.Autoplay = r.U8(); v.VisibleInShroud = r.U8();
            v.ParentId = r.I32(); v.NotShroudOnly = r.U8(); v.NoCulling = r.U8();
            b.Vfx.Add(v);
        }
        b.AfterVfx = r.Take(DefaultAfterVfx.Length);

        ListVersion(ref r, 1, "light probes");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 3, "light probe");
            b.Probes.Add(new Bmd27Probe
            {
                X = r.F32(), Y = r.F32(), Z = r.F32(), OuterRadius = r.F32(), InnerRadius = r.F32(), IsCylinder = r.U8(), Primary = r.U8(),
                HeightMode = r.Str(),
            });
        }

        ListVersion(ref r, 1, "terrain holes");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 3, "terrain hole");
            var h = new Bmd27Hole { Vertices = r.Floats(9), HeightMode = r.Str() };
            h.Flags = Bmd27Flags.Read(ref r);
            b.Holes.Add(h);
        }

        ListVersion(ref r, 1, "point lights");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 7, "point light");
            var l = new Bmd27Light
            {
                X = r.F32(), Y = r.F32(), Z = r.F32(), Radius = r.F32(), R = r.F32(), G = r.F32(), B = r.F32(), ColourScale = r.F32(),
                Animation = r.U8(), AnimationScale1 = r.F32(), AnimationScale2 = r.F32(), ColourMin = r.F32(), RandomOffset = r.F32(),
                Falloff = r.Str(), LfRelative = r.U8(), HeightMode = r.Str(), LightProbesOnly = r.U8(), CultureMask = r.U64(),
            };
            l.Flags = Bmd27Flags.Read(ref r);
            b.Lights.Add(l);
        }
        b.AfterLights = r.Take(AfterLightsProps.Length);

        ListVersion(ref r, 1, "polygon meshes");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 4, "polygon mesh");
            var p = new Bmd27Poly();
            for (var k = r.U32(); k > 0; k--) p.Vertices.Add((r.F32(), r.F32(), r.F32()));
            for (var k = r.U32(); k > 0; k--) p.Indices.Add(r.U16());
            p.Material = r.Str(); p.HeightMode = r.Str(); p.Flags = Bmd27Flags.Read(ref r);
            p.Transform = r.Floats(12);
            p.VisibleInsideSnow = r.U8(); p.VisibleOutsideSnow = r.U8(); p.VisibleInsideDestruction = r.U8(); p.VisibleOutsideDestruction = r.U8();
            p.VisibleInShroud = r.U8(); p.NotShroudOnly = r.U8();
            b.Polys.Add(p);
        }
        b.AfterPoly = r.Take(DefaultAfterPoly.Length);

        ListVersion(ref r, 1, "spot lights");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 8, "spot light");
            var s = new Bmd27Spot
            {
                X = r.F32(), Y = r.F32(), Z = r.F32(), Rotation = r.Floats(4), Length = r.F32(), InnerAngle = r.F32(), OuterAngle = r.F32(),
                R = r.F32(), G = r.F32(), B = r.F32(), Falloff = r.F32(), Gobo = r.Str(), Volumetric = r.U8(), HeightMode = r.Str(),
                CultureMask = r.U64(),
            };
            s.Flags = Bmd27Flags.Read(ref r);
            b.Spots.Add(s);
        }

        ListVersion(ref r, 1, "sound emitters");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 10, "sound emitter");
            var s = new Bmd27Sound { Name = r.Str(), Shape = r.Str() };
            for (var k = r.U32(); k > 0; k--) s.Points.Add((r.F32(), r.F32(), r.F32()));
            s.InnerRadius = r.F32(); s.OuterRadius = r.F32(); s.InnerCube = r.Floats(6); s.OuterCube = r.Floats(6);
            for (var k = r.U32(); k > 0; k--) s.RiverNodes.Add(new Bmd27SoundRiverNode(r.U16(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32()));
            s.ClampToSurface = r.U8(); s.HeightMode = r.Str(); s.CultureMask = r.U64();
            s.PdlcMask = r.U64(); s.Axes = r.Floats(6); s.Scope = r.Str();
            b.Sounds.Add(s);
        }

        ListVersion(ref r, 1, "composite scenes");
        for (var n = r.U32(); n > 0; n--)
        {
            Record(ref r, 12, "composite scene");
            b.Scenes.Add(new Bmd27Scene
            {
                Transform = r.Floats(12), Path = r.Str(), HeightMode = r.Str(), CultureMask = r.U64(), Autoplay = r.U8(),
                VisibleInShroud = r.U8(), NoCulling = r.U8(), ScriptId = r.Str(), ParentScriptId = r.Str(), NotShroudOnly = r.U8(),
                TacticalView = r.U8(), TacticalViewOnly = r.U8(), HoldFirstFrame = r.U8(), HoldLastFrame = r.U8(),
            });
        }
        b.Trailing = data[r.Pos..].ToArray();
        return b;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("FASTBIN0"u8);
        w.Write(Version);
        w.Write(Preamble);

        Begin(w, 1, Nested.Count);
        foreach (var x in Nested)
        {
            w.Write((ushort)9); Str(w, x.Name); Floats(w, x.Matrix);
            w.Write(x.PropertyOverrides.Count);
            foreach (var o in x.PropertyOverrides) o.Write(w, Str);
            w.Write(x.CultureMask); Str(w, x.Region);
            Str(w, x.HeightMode); w.Write(x.Uid);
        }
        w.Write(AfterNested);

        w.Write((ushort)2);
        w.Write(PropPaths.Count);
        foreach (var p in PropPaths) Str(w, p);
        w.Write(Props.Count);
        foreach (var p in Props)
        {
            w.Write((ushort)30); w.Write(p.PathIndex); Floats(w, p.Transform);
            w.Write(p.Decal); w.Write(p.LogicalDecal); w.Write(p.Fauna); w.Write(p.VisibleInsideSnow); w.Write(p.VisibleOutsideSnow);
            w.Write(p.VisibleInsideDestruction); w.Write(p.VisibleOutsideDestruction); w.Write(p.Animated);
            w.Write(p.DecalParallaxScale); w.Write(p.DecalTiling); w.Write(p.DecalOverrideGbufferNormal);
            p.Flags.Write(w);
            w.Write(p.VisibleInShroud); w.Write(p.DecalApplyToTerrain); w.Write(p.DecalApplyToGbufferObjects); w.Write(p.DecalRenderAboveSnow);
            Str(w, p.HeightMode); w.Write(p.CultureMask);
            w.Write(p.CastShadow); w.Write(p.NoCulling); w.Write(p.HasHeightPatch); w.Write(p.ApplyHeightPatch); w.Write(p.IncludeInFog);
            w.Write(p.NotShroudOnly); w.Write(p.DynamicShadows); w.Write(p.UsesTerrainVertexOffset);
            w.Write(p.BlendEdgesWithTerrain); w.Write(p.DecalFadeStart); w.Write(p.DecalAngleThreshold);
            w.Write(p.DecalMirrorX); w.Write(p.DecalMirrorZ); w.Write(p.DecalLayer); w.Write(p.DecalTilingAlpha);
        }

        Begin(w, 1, Vfx.Count);
        foreach (var v in Vfx)
        {
            w.Write((ushort)11); Str(w, v.Name); Floats(w, v.Transform); w.Write(v.EmissionRate); Str(w, v.Instance); v.Flags.Write(w);
            Str(w, v.HeightMode); w.Write(v.CultureMask); w.Write(v.Autoplay); w.Write(v.VisibleInShroud); w.Write(v.ParentId);
            w.Write(v.NotShroudOnly); w.Write(v.NoCulling);
        }
        w.Write(AfterVfx);

        Begin(w, 1, Probes.Count);
        foreach (var p in Probes)
        {
            w.Write((ushort)3); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(p.OuterRadius); w.Write(p.InnerRadius);
            w.Write(p.IsCylinder); w.Write(p.Primary); Str(w, p.HeightMode);
        }

        Begin(w, 1, Holes.Count);
        foreach (var h in Holes) { w.Write((ushort)3); Floats(w, h.Vertices); Str(w, h.HeightMode); h.Flags.Write(w); }

        Begin(w, 1, Lights.Count);
        foreach (var l in Lights)
        {
            w.Write((ushort)7); w.Write(l.X); w.Write(l.Y); w.Write(l.Z); w.Write(l.Radius); w.Write(l.R); w.Write(l.G); w.Write(l.B);
            w.Write(l.ColourScale); w.Write(l.Animation); w.Write(l.AnimationScale1); w.Write(l.AnimationScale2); w.Write(l.ColourMin);
            w.Write(l.RandomOffset); Str(w, l.Falloff); w.Write(l.LfRelative); Str(w, l.HeightMode); w.Write(l.LightProbesOnly);
            w.Write(l.CultureMask); l.Flags.Write(w);
        }
        w.Write(AfterLights);

        Begin(w, 1, Polys.Count);
        foreach (var p in Polys)
        {
            w.Write((ushort)4);
            w.Write(p.Vertices.Count);
            foreach (var (x, y, z) in p.Vertices) { w.Write(x); w.Write(y); w.Write(z); }
            w.Write(p.Indices.Count);
            foreach (var i in p.Indices) w.Write(i);
            Str(w, p.Material); Str(w, p.HeightMode); p.Flags.Write(w); Floats(w, p.Transform);
            w.Write(p.VisibleInsideSnow); w.Write(p.VisibleOutsideSnow); w.Write(p.VisibleInsideDestruction); w.Write(p.VisibleOutsideDestruction);
            w.Write(p.VisibleInShroud); w.Write(p.NotShroudOnly);
        }
        w.Write(AfterPoly);

        Begin(w, 1, Spots.Count);
        foreach (var s in Spots)
        {
            w.Write((ushort)8); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); Floats(w, s.Rotation); w.Write(s.Length); w.Write(s.InnerAngle);
            w.Write(s.OuterAngle); w.Write(s.R); w.Write(s.G); w.Write(s.B); w.Write(s.Falloff); Str(w, s.Gobo); w.Write(s.Volumetric);
            Str(w, s.HeightMode); w.Write(s.CultureMask); s.Flags.Write(w);
        }

        Begin(w, 1, Sounds.Count);
        foreach (var s in Sounds)
        {
            w.Write((ushort)10); Str(w, s.Name); Str(w, s.Shape);
            w.Write(s.Points.Count);
            foreach (var (x, y, z) in s.Points) { w.Write(x); w.Write(y); w.Write(z); }
            w.Write(s.InnerRadius); w.Write(s.OuterRadius); Floats(w, s.InnerCube); Floats(w, s.OuterCube);
            w.Write(s.RiverNodes.Count);
            foreach (var n in s.RiverNodes) { w.Write(n.Version); w.Write(n.X); w.Write(n.Y); w.Write(n.Z); w.Write(n.Width); w.Write(n.FlowSpeed); }
            w.Write(s.ClampToSurface); Str(w, s.HeightMode); w.Write(s.CultureMask); w.Write(s.PdlcMask);
            Floats(w, s.Axes); Str(w, s.Scope);
        }

        Begin(w, 1, Scenes.Count);
        foreach (var s in Scenes)
        {
            w.Write((ushort)12); Floats(w, s.Transform); Str(w, s.Path); Str(w, s.HeightMode); w.Write(s.CultureMask); w.Write(s.Autoplay);
            w.Write(s.VisibleInShroud); w.Write(s.NoCulling); Str(w, s.ScriptId); Str(w, s.ParentScriptId); w.Write(s.NotShroudOnly);
            w.Write(s.TacticalView); w.Write(s.TacticalViewOnly); w.Write(s.HoldFirstFrame); w.Write(s.HoldLastFrame);
        }
        w.Write(Trailing);
        w.Flush();
        return ms.ToArray();
    }

    private static void Begin(BinaryWriter w, ushort version, int count) { w.Write(version); w.Write(count); }

    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    private static void Floats(BinaryWriter w, float[] f) { foreach (var v in f) w.Write(v); }

    private static void ListVersion(ref Bmd27Reader r, ushort expected, string what)
    {
        var v = r.U16();
        if (v != expected) throw new InvalidDataException($"Unexpected {what} list version {v} at {r.Pos - 2} (expected {expected}).");
    }

    private static void Record(ref Bmd27Reader r, ushort expected, string what)
    {
        var v = r.U16();
        if (v != expected) throw new InvalidDataException($"Unsupported {what} record version {v} at {r.Pos - 2} (expected {expected}).");
    }
}

internal ref struct Bmd27Reader(ReadOnlySpan<byte> d)
{
    private readonly ReadOnlySpan<byte> _d = d;
    public int Pos;
    public byte U8() => _d[Pos++];
    public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[Pos..]); Pos += 2; return v; }
    public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[Pos..]); Pos += 4; return v; }
    public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_d[Pos..]); Pos += 4; return v; }
    public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_d[Pos..]); Pos += 8; return v; }
    public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_d[Pos..]); Pos += 4; return v; }
    public float[] Floats(int n) { var f = new float[n]; for (var i = 0; i < n; i++) f[i] = F32(); return f; }
    public string Str() { var n = U16(); var s = Encoding.UTF8.GetString(_d.Slice(Pos, n)); Pos += n; return s; }
    public byte[] Take(int n) { var b = _d.Slice(Pos, n).ToArray(); Pos += n; return b; }
}
