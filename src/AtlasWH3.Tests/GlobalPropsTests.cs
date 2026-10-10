using AtlasWH3.Core;
using AtlasWH3.Core.Bob;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>WH3 global_props.bin (BMD v27) and the global_props step.</summary>
public class GlobalPropsTests
{
    private const string OldWorld = "cr_oldworld_map_1";

    [Fact]
    public void Bmd27_RoundTripsEveryBody()
    {
        foreach (var file in new[] { "global_props.bin", "global_props_sound.bin" })
        {
            var path = CompiledFormatTests.Built(OldWorld, file);
            if (!File.Exists(path)) return;
            foreach (var (name, body) in GlobalProps.Load(path).Bodies())
                Assert.True(Bmd27Body.Parse(body).ToBytes().AsSpan().SequenceEqual(body), name);
        }
    }

    private static uint[] Bits(IEnumerable<float> v) => v.Select(BitConverter.SingleToUInt32Bits).ToArray();

    /// <summary>A mountain of Old World's "mountains" layer: BOB's record matrix (columns), bit for bit.</summary>
    [Fact]
    public void MatrixWh3_MatchesBob()
    {
        var m = QtuTransform.MatrixWh3(0, 213.529785f, 0, 0.999999642f, 1, 0.999999642f);
        var columns = Enumerable.Range(0, 9).Select(k => (float)m[k % 3 * 3 + k / 3]);
        Assert.Equal([0xbf5566b1, 0, 0x3f0d682a, 0x80000000, 0x3f800000, 0, 0xbf0d682a, 0x80000000, 0xbf5566b1], Bits(columns));
    }

    /// <summary>A corruption-crack decal inside its prefab instance: BOB's record, bit for bit.</summary>
    [Fact]
    public void ComposeWh3_MatchesBob()
    {
        var parent = QtuTransform.MatrixWh3(0, 75.939003f, 0, 1.98300004f, 2, 2.08200002f).Select(v => (float)v).ToArray();
        var child = QtuTransform.MatrixWh3(0, 0, 0, 1.67280447f, 0.200000003f, 1.67280447f).Select(v => (float)v).ToArray();
        var (r, x, y, z, _) = QtuTransform.ComposeWh3(parent, 128.666f, 15, 12.3170004f, child, -0.0343456268f, -1.65284087e-06f, 0.0834004208f);
        var record = Enumerable.Range(0, 9).Select(k => r[k % 3 * 3 + k / 3]).Concat([x, y, z]);
        Assert.Equal([0x3f4e50fb, 0, 0xc04df01c, 0, 0x3ecccccd, 0, 0x4058381f, 0, 0x3f589dd5, 0x4300d162, 0x416ffffd, 0x4146cdd7], Bits(record));
    }

    /// <summary>Culture buckets, the sound file and hidden groups, on a small layer (no game data needed).</summary>
    [Fact]
    public void Builder_SplitsByCultureAndSendsSoundsToTheirOwnFile()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var layer = Path.Combine(dir.FullName, "a.layer");
            File.WriteAllText(layer, """
                <?xml version="1.0" encoding="UTF-8"?>
                <layer version="41">
                  <entities>
                    <entity id="1000000000000a1">
                      <ECPropMesh/><ECMesh model_path="rigidmodels/x.rigid_model_v2" opacity="1"/>
                      <ECCampaignProperties culture_mask="wh_main_emp_empire,wh3_main_ksl_kislev,wh3_main_pro_ksl_kislev,mixer_x"/>
                      <ECTransform position="10 0 10" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a2">
                      <ECDecal model_path="rigidmodels/d.rigid_model_v2" parallax_scale="0" tiling="0" apply_to_terrain="true" apply_to_objects="false"/>
                      <ECTransform position="10 0 10" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a3">
                      <ECSoundMarker key="snd"/>
                      <ECCampaignProperties culture_mask="wh_main_emp_empire,mixer_x"/>
                      <ECTransform position="60 0 60" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a4" name="hidden"><ECLayer export="false" bmd_export_type=""/></entity>
                    <entity id="1000000000000a5">
                      <ECPropMesh/><ECMesh model_path="rigidmodels/h.rigid_model_v2" opacity="1"/>
                      <ECTransform position="20 0 20" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                  </entities>
                  <associations><Logical><from id="1000000000000a4"><to id="1000000000000a5"/></from></Logical></associations>
                </layer>
                """);
            var cultures = new Dictionary<string, int> { ["wh_main_emp_empire"] = 11, ["wh3_main_ksl_kislev"] = 24, ["wh3_main_pro_ksl_kislev"] = 24 };
            var builder = new Wh3GlobalPropsBuilder(new PackSet([]), new PrefabLibrary(dir.FullName, "campaign"), cultures, "m", (0, 0, 100, 100), (_, _) => "r");
            builder.AddLayer(layer);
            var props = builder.BuildProps().ToDictionary(e => e.Name[(e.Name.LastIndexOf('/') + 1)..], e => Bmd27Body.Parse(e.Body));
            var sound = builder.BuildSound();

            // the prop: one bucket per culture name (both Kislevs share value 24), NONE (0) for the unknown culture, + 15
            var propBuckets = props.Where(kv => kv.Value.Props.Any(p => p.Decal == 0)).ToDictionary(kv => GlobalPropsParity.Parts(kv.Key).Bucket, kv => kv.Value.Props.Count);
            Assert.Equal(new Dictionary<int, int> { [15] = 1, [191] = 1, [399] = 2 }, propBuckets);
            Assert.Contains(props, kv => kv.Value.Props.Any(p => p.Decal == 1) && GlobalPropsParity.Parts(kv.Key).Bucket == 16);
            Assert.DoesNotContain(props.Values, b => b.PropPaths.Contains("rigidmodels/h.rigid_model_v2"));
            // no packs: the prop's model is reported missing (and boxed as [-1, 1]^3); the hidden one is never looked up
            Assert.Contains("rigidmodels/x.rigid_model_v2", builder.MissingModels);
            Assert.DoesNotContain("rigidmodels/h.rigid_model_v2", builder.MissingModels);

            // the sound: its own file, the known culture's bit only; the props file gets an empty bucket-16 body at its cell
            var (soundName, soundBody) = Assert.Single(sound);
            var emitter = Assert.Single(Bmd27Body.Parse(soundBody).Sounds);
            Assert.Equal(1UL << 10, emitter.CultureMask);
            Assert.Equal(0, props[soundName[(soundName.LastIndexOf('/') + 1)..]].ObjectCount);
        }
        finally { dir.Delete(true); }
    }

    /// <summary>Devastation pieces' objects files on a small layer: one flat body per bmd_export_type (a typed group's
    /// nested "sound" group included), .culture masks (0 for no culture, bit 63 for one without a prefab_types row),
    /// light probes and rivers left out, an entity turned about its pivot, a hole's (triangle count, mask) run, and the
    /// lava river in the rivers file.</summary>
    [Fact]
    public void Builder_Pieces_SplitByExportTypeWithCultureFiles()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var layer = Path.Combine(dir.FullName, "a.layer");
            File.WriteAllText(layer, """
                <?xml version="1.0" encoding="UTF-8"?>
                <layer version="41">
                  <entities>
                    <entity id="1000000000000a1">
                      <ECPropMesh/><ECMesh model_path="rigidmodels/x.rigid_model_v2" opacity="1"/>
                      <ECCampaignProperties culture_mask="wh_main_emp_empire,mixer_x"/>
                      <ECTransform position="10 0 10" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a2">
                      <ECPropMesh/><ECMesh model_path="rigidmodels/p.rigid_model_v2" opacity="1"/>
                      <ECTransform position="10 0 10" rotation="90 0 0" scale="1 1 1" pivot="0 2 0"/>
                    </entity>
                    <entity id="1000000000000a3">
                      <ECPropMesh/><ECMesh model_path="rigidmodels/chaos.rigid_model_v2" opacity="1"/>
                      <ECTransform position="12 0 12" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a4">
                      <ECSoundMarker key="snd"/>
                      <ECTransform position="14 0 14" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a5">
                      <ECLightProbe primary="true"/>
                      <ECTransform position="16 0 16" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000a6">
                      <ECPropMesh/><ECMesh model_path="rigidmodels/far.rigid_model_v2" opacity="1"/>
                      <ECTransform position="80 0 80" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000d1">
                      <ECTerrainHole/>
                      <ECPolyline closed="true"><point x="0" y="0"/><point x="1" y="0"/><point x="1" y="1"/><point x="0" y="1"/></ECPolyline>
                      <ECCampaignProperties culture_mask="wh_main_emp_empire"/>
                      <ECTransform position="30 0 30" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                    </entity>
                    <entity id="1000000000000c1">
                      <ECRiver/>
                      <ECTransform position="20 1.5 20" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                      <ECRiverSpline terrain_relative="false" reverse_direction="false" material="materials/environment/campaign/cwb_campaign_river_lava.xml.material">
                        <spline closed="false">
                          <point position="0,0,0" tangent_in="0,0,1" tangent_out="0,0,-1" width="2"/>
                          <point position="0,0,-4" tangent_in="0,0,1" tangent_out="0,0,-1" width="2"/>
                        </spline>
                      </ECRiverSpline>
                    </entity>
                    <entity id="1000000000000c2">
                      <ECRiver/>
                      <ECTransform position="22 0 22" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
                      <ECRiverSpline terrain_relative="false" reverse_direction="false" material="materials/environment/campaign/cr_campaign_water_plane_river_lava.xml.material">
                        <spline closed="false">
                          <point position="0,0,0" tangent_in="0,0,1" tangent_out="0,0,-1" width="2"/>
                          <point position="0,0,-4" tangent_in="0,0,1" tangent_out="0,0,-1" width="2"/>
                        </spline>
                      </ECRiverSpline>
                    </entity>
                    <entity id="1000000000000b1" name="devastation_chaos"><ECLayer export="true" bmd_export_type="devastation_chaos"/></entity>
                    <entity id="1000000000000b2" name="sound"><ECLayer export="true" bmd_export_type=""/></entity>
                  </entities>
                  <associations><Logical>
                    <from id="1000000000000b1"><to id="1000000000000a3"/><to id="1000000000000b2"/></from>
                    <from id="1000000000000b2"><to id="1000000000000a4"/></from>
                  </Logical></associations>
                </layer>
                """);
            var cultures = new Dictionary<string, int> { ["wh_main_emp_empire"] = 11 };
            var builder = new Wh3GlobalPropsBuilder(new PackSet([]), new PrefabLibrary(dir.FullName, "campaign"), cultures, "m", (0, 0, 100, 100), (_, _) => "r");
            builder.AddLayer(layer);
            var files = Assert.Single(builder.BuildPieces((x, _) => x < 50 ? 0 : -1, 1)).ToDictionary(f => f.Stem);
            Assert.Equal(["bmd_objects_sound", "bmd_objects_sound_devastation_chaos", "objects", "objects_devastation_chaos"], files.Keys.Order());

            var objects = Bmd27Body.Parse(files["objects"].Bin);
            Assert.Equal(["rigidmodels/x.rigid_model_v2", "rigidmodels/p.rigid_model_v2"], objects.PropPaths);
            Assert.Empty(objects.Probes);
            Assert.All(objects.Props, p => Assert.Equal(1UL, p.CultureMask));
            // turned 90° about x around (0, 2, 0): the origin moves to (10, 2, 8) or (10, 2, 12)
            var pivoted = objects.Props[1].Transform;
            Assert.Equal(10f, pivoted[9], 4);
            Assert.Equal(2f, pivoted[10], 4);
            Assert.Equal(2f, MathF.Abs(pivoted[11] - 10), 4);
            var culture = PieceCulture.Read(files["objects"].Culture);
            Assert.Equal(["p", "ht"], culture.Select(c => c.Tag));
            Assert.Equal([1UL << 10 | 1UL << 63, 0UL], culture[0].Masks);
            // the hole's two triangles: one (count, mask) run
            Assert.Equal(2, objects.Holes.Count);
            Assert.Equal([2UL, 1UL << 10], culture[1].Masks);

            Assert.Equal(["rigidmodels/chaos.rigid_model_v2"], Bmd27Body.Parse(files["objects_devastation_chaos"].Bin).PropPaths);
            var sound = Assert.Single(Bmd27Body.Parse(files["bmd_objects_sound_devastation_chaos"].Bin).Sounds);
            Assert.Equal(0UL, sound.CultureMask);
            Assert.Equal("ss", Assert.Single(PieceCulture.Read(files["bmd_objects_sound_devastation_chaos"].Culture)).Tag);
            Assert.Empty(Bmd27Body.Parse(files["bmd_objects_sound"].Bin).Sounds);
            Assert.Empty(files["bmd_objects_sound"].Culture);

            // rivers stay map-wide; only the cwb_campaign_river_lava one is listed, by its entity position
            Assert.DoesNotContain(objects.PropPaths, p => p.Contains("river_"));
            var rivers = Assert.Single(builder.BuildPieceRivers((x, _) => x < 50 ? 0 : -1, 1));
            Assert.Equal(new byte[] { 0, 0, 0xa0, 0x41, 0, 0, 0xc0, 0x3f, 0, 0, 0xa0, 0x41, 0, 0, 0, 0 }, rivers);

            // the typed objects stay out of global_props(_sound).bin
            Assert.DoesNotContain(builder.BuildProps(), e => Bmd27Body.Parse(e.Body).PropPaths.Contains("rigidmodels/chaos.rigid_model_v2"));
            Assert.Empty(builder.BuildSound());
        }
        finally { dir.Delete(true); }
    }

    /// <summary>The .culture sections round-trip, empty ones left out.</summary>
    [Fact]
    public void PieceCulture_RoundTrips()
    {
        var bytes = PieceCulture.Write([("p", [1UL, 1UL << 63]), ("m", []), ("ht", [0UL])]);
        Assert.Equal(8 + 16 + 8 + 8, bytes.Length);
        var sections = PieceCulture.Read(bytes);
        Assert.Equal(["p", "ht"], sections.Select(s => s.Tag));
        Assert.Equal(bytes, PieceCulture.Write(sections.Select(s => (s.Tag, (IReadOnlyList<ulong>)s.Masks))));
    }

    /// <summary>
    /// The step against a BOB run of the same sources (the scratch kit's Old World, 2026-10-09): content parity (record
    /// order and one-prop numbering aside, which differ between two BOB runs) 98.8%. Most of the rest is 436 objects in a
    /// coarser cell than BOB's, which boxes models it can't find loose as [-1, 1]^3; then one-ulp transforms of the
    /// tow_torch prefab's effects, two holes and one polygon BOB triangulates otherwise, and duplicate entities.
    /// </summary>
    [Fact]
    public void Step_OldWorld_MatchesBobContent()
    {
        var kit = ScratchKit.DefaultRoot(TestKits.Wh3Kit);
        var bobFile = Path.Combine(kit, "working_data", "terrain", "campaigns", OldWorld, "global_props.bin");
        var pack = Path.Combine(TestKits.Wh3GameData, "!cr_oldworld_campaign.pack");
        if (!File.Exists(bobFile) || !File.Exists(pack)) return;
        var target = Path.Combine(Path.GetTempPath(), "atlaswh3_global_props_test");
        try
        {
            var paths = new ProjectPaths { MapName = OldWorld, AssemblyKitRoot = kit, GameDataDir = TestKits.Wh3GameData, ModPacks = [pack] };
            var ctx = new CampaignBuildContext(paths, target);
            Assert.Empty(new GlobalPropsStep().CheckInputs(ctx));
            new GlobalPropsStep().Run(ctx);
            var parity = GlobalPropsParity.Compare(GlobalPropsParity.Load(ctx.OutFile("global_props.bin")), GlobalPropsParity.Load(bobFile));
            Assert.True(parity.Share > 0.985, $"{parity.Same} same, {parity.Differ} differ: {string.Join("; ", parity.Examples)}");
        }
        finally { if (Directory.Exists(target)) Directory.Delete(target, true); }
    }
}
