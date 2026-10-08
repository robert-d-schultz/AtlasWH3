using System.Text.Json.Nodes;
using System.Xml.Linq;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public class PrefabTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;

    // ---------------------------------------------------------------- transforms

    private static double[] Mul(double[] a, double[] b)
    {
        var m = new double[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return m;
    }

    [Fact]
    public void Transform_MatchesBuildConvention()
    {
        var t = new TerryTransform([1, 2, 3], [10, 20, 30], [1, 2, 3]);
        var expected = Core.Campaign.CameraHeightmapStep.Matrix(new Formats.Props.PropTransform(1, 2, 3, 10, 20, 30, 1, 2, 3));
        Assert.Equal(expected, t.Matrix(), (a, b) => Math.Abs(a - b) < 1e-12);
    }

    [Theory]
    [InlineData(0, 90, 0, 0, -45, 0)]
    [InlineData(10, 20, 30, -5, 60, 15)]
    [InlineData(0, 180, 0, 30, 0, 0)]
    [InlineData(80, -40, 170, 12, 33, -100)]
    public void Compose_EqualsMatrixProduct(double ax, double ay, double az, double bx, double by, double bz)
    {
        var parent = new TerryTransform([5, 1, -2], [ax, ay, az], [2, 2, 2]);
        var child = new TerryTransform([1, 0.5, 3], [bx, by, bz], [1, 1.5, 0.5]);
        var c = parent.Compose(child);
        Assert.Equal(Mul(parent.Matrix(), child.Matrix()), c.Matrix(), (a, b) => Math.Abs(a - b) < 1e-9);
        var (x, y, z) = parent.Apply(1, 0.5, 3);
        Assert.Equal([x, y, z], c.Position, (a, b) => Math.Abs(a - b) < 1e-9);
    }

    [Fact]
    public void Compose_YawOnlyAddsAngles()
    {
        var c = new TerryTransform([6, 0, -6], [0, -90, 0], [1, 1, 1]).Compose(new TerryTransform([1, 0, 0], [0, 30, 0], [1, 1, 1]));
        Assert.Equal([0, -60, 0], c.Rotation);
        Assert.Equal(6, c.Position[0], 9);
        Assert.Equal(-5, c.Position[2], 9); // Blender Ry: x' = cos·x + sin·z, z' = -sin·x + cos·z
    }

    // ---------------------------------------------------------------- library and expansion

    private const string PrefabTerry = """
        <?xml version="1.0" encoding="UTF-8"?>
        <project version="20" id="1000000000000a0">
          <pc type="QTU::ProjectPrefab">
            <data database="campaign"/>
          </pc>
          <pc type="QTU::Scene">
            <data version="35">
              <entity id="1000000000000a1" name="Default">
                <ECLayerFile/>
                <ECLayer/>
                <ECLayerExport export="true" export_as_separate_file_if_not_meta_tagged="false" buildings_have_linked_destruction="false"/>
              </entity>
            </data>
          </pc>
          <pc type="QTU::Terrain"/>
        </project>

        """;

    private static string Prop(string id, string model, string pos, string rot = "0 0 0") => $"""
            <entity id="{id}">
              <ECPropMesh/>
              <ECMesh model_path="{model}" animation_path=""/>
              <ECMeshRenderSettings inherit_from_parent="false" cast_shadow="true" alpha="1" tint_colour="255 255 255 255" set_tint_colour_from_colour_overlay="false" faction_colour="255 255 255 255"/>
              <ECPropHeightPatch has_height_patch="false" apply_height_patch="false"/>
              <ECCampaignProperties visible_inside_snow_region="true" visible_outside_snow_region="true" visible_inside_destruction_region="true" visible_outside_destruction_region="true" visible_in_unseen_shroud="false" visible_in_seen_shroud="true" no_culling="false" culture_mask="" season_mask=""/>
              <ECDLCMask type="Exclude" mask=""/>
              <ECTransform position="{pos}" rotation="{rot}" scale="1 1 1" pivot="0 0 0"/>
              <ECTerrainClamp active="false" clamp_to_sea_level="false" terrain_oriented="false"/>
            </entity>
        """;

    private static string Instance(string id, string key, string pos, string rot = "0 0 0", string overrides = "") => $"""
            <entity id="{id}">
              <ECPrefab key="{key}" turn_buildings_into_props="false">{overrides}</ECPrefab>
              <ECMeshRenderSettings inherit_from_parent="false" cast_shadow="true" alpha="1" tint_colour="255 255 255 255" set_tint_colour_from_colour_overlay="false" faction_colour="255 255 255 255"/>
              <ECTransform position="{pos}" rotation="{rot}" scale="1 1 1" pivot="0 0 0"/>
              <ECTerrainClamp active="false" clamp_to_sea_level="false" terrain_oriented="false"/>
            </entity>
        """;

    private static string Layer(string entities, string logical = "") =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<layer version=\"35\">\n  <entities>\n{entities}  </entities>\n  <associations>\n    <Logical>{logical}</Logical>\n    <Transform/>\n  </associations>\n</layer>\n";

    /// <summary>A prefab library with "hut" (two props, one tagged) and "village" (two huts, one rotated and overridden).</summary>
    private static string MakeLibrary(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "hut.terry"), PrefabTerry.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(root, "hut.1000000000000a1.layer"), Layer(
            Prop("1000000000000b1", "m/hut.wsmodel", "1 0 0") + Prop("1000000000000b2", "m/fence.wsmodel", "0 0 2", "0 90 0")
            + """
                <entity id="1000000000000b9" name="night">
                  <ECLayerInternal/>
                  <ECLayer/>
                  <ECLayerExport export="true" export_as_separate_file_if_not_meta_tagged="false" buildings_have_linked_destruction="false"/>
                  <ECLayerExportTags tags="night"/>
                </entity>
            """, "<from id=\"1000000000000b9\"><to id=\"1000000000000b2\"/></from>"));
        File.WriteAllText(Path.Combine(root, "sub", "village.terry"), PrefabTerry.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(root, "sub", "village.1000000000000a1.layer"), Layer(
            Instance("1000000000000c1", "hut", "10 0 0")
            + Instance("1000000000000c2", "hut", "20 0 0", "0 90 0",
                "<override id=\"1000000000000b1\" name=\"x\"><ECMesh model_path=\"m/hut_big.wsmodel\"/></override>")));
        return root;
    }

    [Fact]
    public void Expander_NestsTransformsOverridesAndTags()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var lib = new PrefabLibrary(MakeLibrary(dir.FullName), "campaign");
            Assert.Equal(["hut", "village"], lib.Keys.Order());
            var instance = XElement.Parse(Instance("1000000000000d1", "village", "100 5 100", "0 180 0"));
            var missing = new List<string>();
            var flat = PrefabExpander.Expand(instance, lib, recursive: true, missing);
            Assert.Empty(missing);
            Assert.Equal(4, flat.Count);
            // Second hut: village pos (20,0,0) rotated 180° about y → (-20, 0, -0) + (100, 5, 100); hut prop at (1,0,0)
            // with the inner 90° then outer 180°.
            var big = flat.Single(f => (string?)f.Entity.Element("ECMesh")!.Attribute("model_path") == "m/hut_big.wsmodel");
            var t = TerryTransform.Read(big.Entity.Element("ECTransform"));
            var expected = new TerryTransform([100, 5, 100], [0, 180, 0], [1, 1, 1])
                .Compose(new TerryTransform([20, 0, 0], [0, 90, 0], [1, 1, 1]))
                .Compose(new TerryTransform([1, 0, 0], [0, 0, 0], [1, 1, 1]));
            Assert.Equal(expected.Position, t.Position, (a, b) => Math.Abs(a - b) < 1e-4);
            Assert.Equal(TerryTransform.Wrap(expected.Rotation[1]), TerryTransform.Wrap(t.Rotation[1]), 4);
            // The override only hits the overridden instance; tags carry through.
            Assert.Single(flat, f => (string?)f.Entity.Element("ECMesh")!.Attribute("model_path") == "m/hut.wsmodel");
            Assert.Equal(2, flat.Count(f => f.Tags == "night"));
            // Not recursive: the two nested huts stay instances.
            Assert.Equal(2, PrefabExpander.Expand(instance, lib, recursive: false).Count(f => PrefabExpander.KeyOf(f.Entity) == "hut"));
            // Unknown keys are reported, not thrown.
            PrefabExpander.Expand(XElement.Parse(Instance("1000000000000d2", "nope", "0 0 0")), lib, true, missing);
            Assert.Equal(["nope"], missing);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void LayerExpand_FreshIdsFolderAndTags()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var lib = new PrefabLibrary(MakeLibrary(dir.FullName), "campaign");
            var doc = LayerDocument.Parse(Layer(Instance("1000000000000d1", "village", "0 0 0")));
            var made = doc.ExpandPrefab("1000000000000d1", lib);
            Assert.Equal(4, made.Count);
            Assert.Equal(4, made.Distinct().Count());
            Assert.Null(doc.Find("1000000000000d1"));
            var all = doc.ReadEntities();
            var folder = all.Single(e => e.Type == TerryEntityTypes.Layer);
            Assert.Equal("village", folder.Name);
            Assert.All(made, id => Assert.True(all.Single(e => e.Id == id).Parents.Count == 1));
            // The tagged copies sit in a "night" tag layer, the others in the folder.
            Assert.Equal(2, made.Count(id => doc.Read(id).Parents.Contains(folder.Id)));
            Assert.Equal(2, all.Single(e => e.Type == TerryEntityTypes.TagLayer).Id is var tag ? made.Count(id => doc.Read(id).Parents.Contains(tag)) : 0);
        }
        finally { dir.Delete(true); }
    }

    // ---------------------------------------------------------------- editor ops (sandbox kit)

    [Fact]
    public void Editor_PlaceMakeExpand_UndoRestoresEverything()
    {
        var kit = Directory.CreateTempSubdirectory();
        try
        {
            var lib = Path.Combine(kit.FullName, "raw_data", "art", "campaign", "prefabs");
            MakeLibrary(lib);
            var map = Path.Combine(kit.FullName, "raw_data", "terrain", "campaigns", "m");
            Directory.CreateDirectory(map);
            var terry = Path.Combine(map, "m.terry");
            File.WriteAllText(terry, PrefabTerry.ReplaceLineEndings("\n").Replace("QTU::ProjectPrefab", "QTU::ProjectTileMap")
                .Replace("<data database=\"campaign\"/>", "<data terrain_setup=\"terrain/campaigns/m/\"/>"));
            File.WriteAllText(Path.Combine(map, "m.1000000000000a1.layer"),
                Layer(Prop("1000000000000e1", "m/a.wsmodel", "10 1 10") + Prop("1000000000000e2", "m/b.wsmodel", "14 3 12")));
            var before = SnapshotTree(kit.FullName);

            var paths = new ProjectPaths { AssemblyKitRoot = kit.FullName, MapName = "m", OutputRoot = Path.Combine(kit.FullName, "out") };
            var editor = new EntityEditor(paths, terry);
            Assert.Equal("campaign", editor.Prefabs.Database);
            Assert.Equal(2, editor.Prefabs.Keys.Count);

            var placed = editor.Apply(JsonNode.Parse("""[{"op": "place_prefab", "key": "village", "layer": "Default", "position": [50, 0, 50], "rotation": [0, 90, 0]}]""")!.AsArray());
            var instance = placed[0]!["created"]![0]!["id"]!.ToString();
            Assert.Equal("Prefab", placed[0]!["created"]![0]!["type"]!.ToString());

            var expanded = editor.Apply(JsonNode.Parse($$"""[{"op": "expand_prefab", "id": "{{instance}}"}]""")!.AsArray());
            Assert.Equal(4, ((JsonArray)expanded[0]!["entities"]!).Count);

            var made = editor.Apply(JsonNode.Parse("""[{"op": "make_prefab", "ids": ["1000000000000e1", "1000000000000e2"], "key": "pair", "folder": "mine"}]""")!.AsArray());
            Assert.Equal(2, made[0]!["entities"]!.GetValue<int>());
            Assert.Equal([12.0, 1, 11], made[0]!["pivot"]!.AsArray().Select(n => n!.GetValue<double>()));
            var pairTerry = Path.Combine(lib, "mine", "pair.terry");
            Assert.True(File.Exists(pairTerry));
            var pair = PrefabLibrary.Read("pair", pairTerry, "campaign");
            Assert.Equal(2, pair.Entities.Count);
            Assert.Equal([-2.0, 0, -1], TerryTransform.Read(pair.Entities[0].Entity.Element("ECTransform")).Position);
            // The originals became one instance at the pivot that expands back to the same places.
            var newInstance = made[0]!["instance"]!["id"]!.ToString();
            var editor2 = new EntityEditor(paths, terry);
            var back = editor2.Apply(JsonNode.Parse($$"""[{"op": "expand_prefab", "id": "{{newInstance}}", "into_folder": false}]""")!.AsArray());
            var positions = ((JsonArray)back[0]!["entities"]!).Select(e => e!["position"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray()).ToList();
            Assert.Contains(positions, p => Math.Abs(p[0] - 10) < 1e-4 && Math.Abs(p[1] - 1) < 1e-4 && Math.Abs(p[2] - 10) < 1e-4);
            Assert.Contains(positions, p => Math.Abs(p[0] - 14) < 1e-4 && Math.Abs(p[1] - 3) < 1e-4 && Math.Abs(p[2] - 12) < 1e-4);

            editor2.Undo(4);
            Assert.Equal(before, SnapshotTree(kit.FullName, exclude: Path.Combine(kit.FullName, "out")));
        }
        finally { kit.Delete(true); }
    }

    private static Dictionary<string, string> SnapshotTree(string root, string? exclude = null) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => exclude is null || !f.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f))));

    // ---------------------------------------------------------------- build

    [Fact]
    public void GlobalProps_FlattensPrefabInstancesLikeHandPlacedProps()
    {
        if (!Directory.Exists(Paths.GameDataDir)) return; // game data not available on this machine
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var libRoot = MakeLibrary(Path.Combine(dir.FullName, "lib"));
            var lib = new PrefabLibrary(libRoot, "campaign");
            var withInstance = Path.Combine(dir.FullName, "a.layer");
            File.WriteAllText(withInstance, Layer(Instance("1000000000000d1", "village", "100 5 100", "0 30 0")));
            // The same layer expanded by hand (the expander's output written as plain props).
            var expanded = LayerDocument.Parse(File.ReadAllText(withInstance));
            expanded.ExpandPrefab("1000000000000d1", lib, intoFolder: false);
            var byHand = Path.Combine(dir.FullName, "b.layer");
            expanded.Save(byHand);

            var packs = Formats.Packs.PackSet.OpenVanilla(Paths.GameDataDir);
            var a = new Core.Campaign.Props.GlobalPropsBuilder(packs, 595.1, 541.78619) { Prefabs = lib }.Build("m", [("r", withInstance)]);
            var b = new Core.Campaign.Props.GlobalPropsBuilder(packs, 595.1, 541.78619).Build("m", [("r", byHand)]);
            Assert.Equal(b.Select(x => x.Name), a.Select(x => x.Name));
            Assert.Equal(b.Select(x => Convert.ToHexString(x.Body)), a.Select(x => Convert.ToHexString(x.Body)));
            Assert.True(a.Count > 1);
        }
        finally { dir.Delete(true); }
    }
}
