using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Tests;

/// <summary>Scene editor props tools: placing props from the asset browser, clamp-to-ground math and ground source.</summary>
public class PropToolsTests
{
    private const string Terry = """
        <?xml version="1.0" encoding="UTF-8"?>
        <project version="20" id="100000000000010">
          <pc type="QTU::ProjectPrefab">
            <data database="campaign"/>
          </pc>
          <pc type="QTU::Scene">
            <data version="35">
              <entity id="100000000000011" name="Default">
                <ECLayerFile/>
                <ECLayer/>
                <ECLayerExport export="true" export_as_separate_file_if_not_meta_tagged="false" buildings_have_linked_destruction="false"/>
              </entity>
            </data>
          </pc>
          <pc type="QTU::Terrain"/>
        </project>

        """;

    private const string EmptyLayer = """
        <?xml version="1.0" encoding="UTF-8"?>
        <layer version="35">
          <entities>
          </entities>
          <associations>
            <Logical/>
            <Transform/>
          </associations>
        </layer>

        """;

    private static (DirectoryInfo Dir, string Terry, string Layer, EntityEditor Editor) TempProject()
    {
        var dir = Directory.CreateTempSubdirectory();
        var terry = Path.Combine(dir.FullName, "p.terry");
        File.WriteAllText(terry, Terry.ReplaceLineEndings("\n"));
        var layer = Path.Combine(dir.FullName, "p.100000000000011.layer");
        File.WriteAllText(layer, EmptyLayer.ReplaceLineEndings("\n"));
        var paths = new ProjectPaths { OutputRoot = Path.Combine(dir.FullName, "out") };
        return (dir, terry, layer, new EntityEditor(paths, terry));
    }

    [Fact]
    public void Placement_CreatesTerryCampaignProp_WrittenAtOnce_UndoRestores()
    {
        var (dir, _, layer, editor) = TempProject();
        try
        {
            var before = File.ReadAllBytes(layer);
            var op = PropPlacement.CreateOp("rigidmodels/campaign/vegetation/temperate/temperate_tree_pine_1.wsmodel", "Default",
                12.5, 3.25, 40, yawDegrees: 270, scale: 0.36);
            var created = (JsonArray)editor.Apply(new JsonArray(op), "place")[0]!["created"]!;
            var id = created[0]!["id"]!.ToString();

            // Written to the layer file at once: entity edits never leave pending, unsaved state.
            var doc = LayerDocument.Parse(File.ReadAllText(layer));
            var e = doc.Read(id);
            Assert.Equal("Prop", e.Type);
            Assert.Equal(["ECPropMesh", "ECMesh", "ECMeshRenderSettings", "ECVisibilitySettingsCampaign", "ECPropHeightPatch", "ECCampaignProperties", "ECTransform"],
                e.Components.Select(c => c.Name));
            Assert.Equal("RigidModels/campaign/vegetation/temperate/temperate_tree_pine_1.wsmodel", e.Component("ECMesh")!["model_path"]);
            Assert.Equal("12.5 3.25 40", e.Component("ECTransform")!["position"]);
            Assert.Equal("0 -90 0", e.Component("ECTransform")!["rotation"]);
            Assert.All(LayerDocument.ReadVector(e.Component("ECTransform")!["scale"]!), v => Assert.Equal(0.36f, (float)v)); // Terry's float32 text

            editor.Undo();
            Assert.Equal(before, File.ReadAllBytes(layer));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Placement_ComponentLayoutMatchesKitProps()
    {
        var dir = TestKits.Paths(TestKits.Iee).AkTerrainDir;
        if (!Directory.Exists(dir)) return; // kit not available on this machine
        var vanilla = Directory.EnumerateFiles(dir, "*.layer")
            .Select(f => File.ReadAllText(f))
            .Where(t => t.Contains("<ECPropMesh/>"))
            .Select(t => LayerDocument.Parse(t).ReadEntities().First(x => x.Component("ECPropMesh") is not null && x.Group is null))
            .First();
        var (tmp, _, layer, editor) = TempProject();
        try
        {
            var id = ((JsonArray)editor.Apply(new JsonArray(PropPlacement.CreateOp("rigidmodels/campaign/x.wsmodel", "Default", 1, 2, 3)))[0]!["created"]!)[0]!["id"]!.ToString();
            var ours = LayerDocument.Parse(File.ReadAllText(layer)).Read(id);
            Assert.Equal(vanilla.Components.Select(c => c.Name), ours.Components.Select(c => c.Name));
            // every field vanilla writes is there (the schema may add later Terry fields such as ECMesh opacity, as
            // CA's own 3K prefab layers have them)
            foreach (var c in vanilla.Components)
                Assert.Empty(c.Fields.Select(f => f.Key).Except(ours.Component(c.Name)!.Fields.Select(f => f.Key)));
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public void BrowsableModels_PreferWsmodelAndCampaign()
    {
        var list = PropPlacement.BrowsableModels([
            "rigidmodels/campaign/a/rock_1.wsmodel", "rigidmodels/campaign/a/rock_1.rigid_model_v2",
            "rigidmodels/campaign/a/bare.rigid_model_v2", "rigidmodels/buildings/b.wsmodel", "rigidmodels/campaign/a/tex.dds",
        ]);
        Assert.Equal(["rigidmodels/campaign/a/bare.rigid_model_v2", "rigidmodels/campaign/a/rock_1.wsmodel"], list);
        Assert.Equal(3, PropPlacement.BrowsableModels(["rigidmodels/campaign/a/x.wsmodel", "rigidmodels/buildings/b.wsmodel", "variantmeshes/c.rigid_model_v2"], all: true).Count);
        Assert.Equal(-170, PropPlacement.NormaliseYaw(190), 9);
        Assert.Equal(180, PropPlacement.NormaliseYaw(-180), 9);
    }

    private static double Ground(double x, double z) => 0.1 * x + 2;

    [Fact]
    public void Clamp_BaseOriginSinkAndLfOffset()
    {
        // origin 5 above the ground at x = 10 (ground 3), model base 0.5 under the origin at scale 2
        var p = new ClampProp("a", "RigidModels/campaign/props/crate.wsmodel", 10, 8, 0, 2, -0.25, 0);
        Assert.Equal(5.0, GroundClamp.BaseAboveGround(p, Ground) + 0.5, 9);
        Assert.Equal((3.5, "base"), GroundClamp.Target(p, Ground, ClampMode.Base));
        Assert.Equal((3.0, "origin"), GroundClamp.Target(p, Ground, ClampMode.Origin));
        Assert.Equal((2.9, "origin"), Round(GroundClamp.Target(p, Ground, ClampMode.Origin, offset: -0.1)));
        // vanilla sink: known model -> sink · scale under the ground; unknown model -> base rule
        var sink = new Dictionary<string, double> { ["rigidmodels/campaign/mountains/cold_ridge_small_1.wsmodel"] = -66.9 };
        var mountain = p with { Model = @"RigidModels\campaign\mountains\cold_ridge_small_1.wsmodel", ScaleY = 0.5 };
        var sunk = GroundClamp.Target(mountain, Ground, ClampMode.VanillaSink, sink: sink);
        Assert.Equal("sink", sunk.How);
        Assert.Equal(3 - 33.45, sunk.Y, 9);
        Assert.Equal("base", GroundClamp.Target(p, Ground, ClampMode.VanillaSink, sink: sink).How);
        // LF-offset mountain (share 1): y is relative to the terrain, so the origin on the ground is y = 0
        var lf = p with { TerrainOffset = 1 };
        Assert.Equal(0.0, GroundClamp.Target(lf, Ground, ClampMode.Origin).Y, 9);
        Assert.Equal(8 - 0.5, GroundClamp.BaseAboveGround(lf, Ground), 9);
    }

    private static (double, string) Round((double Y, string How) t) => (Math.Round(t.Y, 9), t.How);

    [Fact]
    public void ClampPlan_OnlyDownToleranceAndOps()
    {
        var props = new[]
        {
            new ClampProp("float", "m.wsmodel", 10, 8, 5, 1, 0, 0),       // 5 above -> lowered
            new ClampProp("buried", "m.wsmodel", 20, 1, 5, 1, 0, 0),      // 3 under -> raised
            new ClampProp("ok", "m.wsmodel", 30, 5.00001, 5, 1, 0, 0),    // within tolerance
        };
        var all = GroundClamp.Plan(props, Ground, ClampMode.Origin);
        Assert.Equal(["float", "buried"], all.Select(m => m.Id));
        Assert.Equal(-5, all[0].Delta, 9);
        Assert.Equal(3, all[1].Delta, 9);
        Assert.Equal(["float"], GroundClamp.Plan(props, Ground, ClampMode.Origin, onlyDown: true).Select(m => m.Id));
        Assert.Equal(["float"], GroundClamp.Floating(props, Ground, 0.15).Select(m => m.Id));
        // a tree's roots below its origin: floating is judged by the lowest of origin and model base
        var tree = new ClampProp("tree", "t.wsmodel", 10, 3, 0, 1, -0.18, 0);
        Assert.Equal(-0.18, GroundClamp.LowestAboveGround(tree, Ground), 9);
        Assert.Equal(0.82, GroundClamp.LowestAboveGround(tree with { Y = 4 }, Ground), 9);
        Assert.Equal(1.0, GroundClamp.LowestAboveGround(tree with { Y = 4, MinY = 0.5 }, Ground), 9);

        var ops = GroundClamp.Ops(all, id => id == "float" ? "10.0000005 8 5.25" : "20 1 5");
        Assert.Equal("10.0000005 3 5.25", ops[0]["fields"]!["ECTransform.position"]!.ToString()); // x / z kept as stored
        Assert.Equal("20 4 5", ops[1]["fields"]!["ECTransform.position"]!.ToString());
    }

    [Fact]
    public void Clamp_AppliedAsOneUndoableBatch()
    {
        var (dir, _, layer, editor) = TempProject();
        try
        {
            var ops = new JsonArray(
                PropPlacement.CreateOp("rigidmodels/campaign/a.wsmodel", "Default", 10, 9, 0),
                PropPlacement.CreateOp("rigidmodels/campaign/a.wsmodel", "Default", 20, 9, 0));
            var ids = editor.Apply(ops).Select(r => r!["created"]![0]!["id"]!.ToString()).ToList();
            var placed = File.ReadAllBytes(layer);
            var doc = LayerDocument.Parse(File.ReadAllText(layer));
            var props = ids.Select(id =>
            {
                var (p, _, s) = doc.Read(id).Transform!.Value;
                return new ClampProp(id, "a", p[0], p[1], p[2], s[1], 0, 0);
            });
            var moves = GroundClamp.Plan(props, Ground, ClampMode.Base);
            var history = editor.History().Count;
            editor.Apply(new JsonArray(GroundClamp.Ops(moves, id => LayerDocument.Parse(File.ReadAllText(layer)).Read(id).Component("ECTransform")!["position"])), "clamp");
            Assert.Equal(history + 1, editor.History().Count);
            var after = LayerDocument.Parse(File.ReadAllText(layer));
            Assert.Equal("10 3 0", after.Read(ids[0]).Component("ECTransform")!["position"]);
            Assert.Equal("20 4 0", after.Read(ids[1]).Component("ECTransform")!["position"]);
            editor.Undo();
            Assert.Equal(placed, File.ReadAllBytes(layer));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void SinkTable_LearntMedianAndFiles()
    {
        const string m = "RigidModels/campaign/mountains/ridge.wsmodel";
        var props = new[] { -60.0, -70, -66, -1000 }.Select((d, i) => new ClampProp($"{i}", m, 10, Ground(10, 0) + d * 0.5, 0, 0.5, -80, 0))
            .Append(new ClampProp("t", "RigidModels/campaign/vegetation/tree.wsmodel", 0, 0, 0, 1, 0, 0)).ToList();
        var sink = GroundClamp.LearnSink(props, Ground);
        Assert.Equal(["rigidmodels/campaign/mountains/ridge.wsmodel"], sink.Keys);
        Assert.Equal(-68, sink["rigidmodels/campaign/mountains/ridge.wsmodel"], 9);
        Assert.Empty(GroundClamp.LearnSink(props.Take(2), Ground));

        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{"RigidModels\\campaign\\mountains\\a.wsmodel": {"n": 12, "sink": -66.9}, "b.wsmodel": -3}""");
            var read = GroundClamp.ReadSinkTable(file);
            Assert.Equal(-66.9, read["rigidmodels/campaign/mountains/a.wsmodel"]);
            Assert.Equal(-3, read["b.wsmodel"]);
            File.WriteAllText(file, GroundClamp.WriteSinkTable(read));
            Assert.Equal(read, GroundClamp.ReadSinkTable(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void SceneGround_IsBobsTreeGround_OnIeeAndSkipsOwnPatch()
    {
        // the scene ground is what BOB stands WH3's trees on: full_logic_map + the layers' height patches
        var (map, pack) = CampaignTreeGeneratorTests.Fixtures[0];
        var tmp = Directory.CreateTempSubdirectory();
        try
        {
            var paths = new ProjectPaths
            {
                AssemblyKitRoot = TestKits.Kit(map), GameDataDir = TestKits.Wh3GameData, MapName = map, OutputRoot = tmp.FullName,
                ModPacks = [TestKits.Pack(pack)],
            };
            var treeList = Path.Combine(paths.AkWorkingCampaignMapDir, "display", "trees", "trees.campaign_tree_list");
            if (!File.Exists(Path.Combine(paths.AkTerrainDir, map + ".terry")) || !File.Exists(treeList) || !File.Exists(paths.ModPacks[0])) return;
            var scene = GroundHeight.Scene(paths, map, out var why);
            Assert.True(scene is not null, why);
            Assert.StartsWith("scene", scene!.Description);
            var trees = CampaignTreeList.Load(treeList).Types.SelectMany(t => t.Instances).Where((_, i) => i % 97 == 0).ToList();
            var close = trees.Count(t => Math.Abs(scene.At(t.X, t.Z) - t.Y) < 1e-3);
            Assert.True(close >= trees.Count * 0.99, $"{close} of {trees.Count} tree heights match");

            // a height-patched prop: the scene is raised by its patch, seating it ignores that patch, and the bare
            // terrain has none of it
            var bare = GroundHeight.Built(paths, map, out why);
            Assert.True(bare is not null, why);
            var patched = new EntityEditor(paths).ReadAll().SelectMany(l => l.Entities)
                .Where(e => e.Group is null && e.Component("ECPropHeightPatch")?["apply_height_patch"] == "true" && e.Component("ECMesh")?["model_path"] is { Length: > 0 })
                .Select(e => (Model: e.Component("ECMesh")!["model_path"]!, P: e.Transform!.Value.Position))
                .Select(m => new ClampProp("", m.Model, m.P[0], m.P[1], m.P[2], 1, 0, 0))
                .FirstOrDefault(m => scene.At(m.X, m.Z) - scene.For(m) > 0.01);
            Assert.True(patched is not null, "no prop whose own patch raises the scene height");
            Assert.True(bare!.At(patched!.X, patched.Z) < scene.At(patched.X, patched.Z));

            // a built terrain that does not match the project's own heights (another map, an old build) is skipped
            var (w, h) = (1000.0, 700.0);
            Assert.Equal(0, GroundHeight.Built(paths, map, out _, bare.At, w, h)!.ReferenceDifference, 9);
            Assert.Null(GroundHeight.Built(paths, map, out var whyNot, (x, z) => bare.At(x, z) + 5, w, h));
            Assert.Contains("another map", whyNot);
        }
        finally { tmp.Delete(true); }
    }
}
