using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public class EntityEditingTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;

    private const string Layer = """
        <?xml version="1.0" encoding="UTF-8"?>
        <layer version="35">
          <entities>
            <entity id="100000000000001">
              <ECPointLight colour="255 210 131 255" colour_scale="100000" radius="0.5" animation_type="LAT_NONE" animation_speed_scale="0 0" colour_min="0" random_offset="0" falloff_type="WPLFT_SMOOTH" for_light_probes_only="false"/>
              <ECCampaignProperties visible_inside_snow_region="true" visible_outside_snow_region="true" visible_inside_destruction_region="true" visible_outside_destruction_region="true" visible_in_unseen_shroud="false" visible_in_seen_shroud="true" no_culling="false" culture_mask="" season_mask=""/>
              <ECTransform position="1 2 3" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
            </entity>
            <entity id="100000000000002">
              <ECGroup>
                <group version="35">
                  <entities>
                    <entity id="100000000000003">
                      <ECSignature/>
                      <ECTransform position="0 0 0" rotation="0 0 0" scale="1 1 1"/>
                    </entity>
                  </entities>
                </group>
              </ECGroup>
              <ECTransform position="5 0 5" rotation="0 0 0" scale="1 1 1" pivot="0 0 0"/>
            </entity>
            <entity id="100000000000004" name="night">
              <ECLayerInternal/>
              <ECLayer/>
              <ECLayerExport export="true" export_as_separate_file_if_not_meta_tagged="false" buildings_have_linked_destruction="false"/>
              <ECLayerExportTags tags="night"/>
            </entity>
          </entities>
          <associations>
            <Logical>
              <from id="100000000000004">
                <to id="100000000000001"/>
              </from>
            </Logical>
            <Transform/>
          </associations>
        </layer>

        """;

    private static LayerDocument Doc() => LayerDocument.Parse(Layer.ReplaceLineEndings("\n"));

    [Fact]
    public void TerryXml_KeepsLineEndingsAndEmptyForms()
    {
        foreach (var nl in new[] { "\n", "\r\n" })
        {
            var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + nl + "<layer version=\"35\">" + nl + "  <entities>" + nl + "  </entities>" + nl
                       + "  <a></a>" + nl + "  <b/>" + nl + "</layer>" + nl;
            Assert.Equal(text, LayerDocument.Parse(text).ToText());
        }
    }

    [Fact]
    public void Read_TypesParentsAndGroups()
    {
        var all = Doc().ReadEntities().ToDictionary(e => e.Id);
        Assert.Equal("PointLight", all["100000000000001"].Type);
        Assert.Equal(["100000000000004"], all["100000000000001"].Parents);
        Assert.Equal(TerryEntityTypes.Group, all["100000000000002"].Type);
        Assert.Equal(TerryEntityTypes.Signature, all["100000000000003"].Type);
        Assert.Equal("100000000000002", all["100000000000003"].Group);
        Assert.Equal(TerryEntityTypes.TagLayer, all["100000000000004"].Type);
    }

    [Fact]
    public void SetField_ValidatesAgainstSchema()
    {
        var doc = Doc();
        doc.SetField("100000000000001", "ECPointLight", "radius", "25");
        Assert.Equal("25", doc.Read("100000000000001").Component("ECPointLight")!["radius"]);
        Assert.Throws<ArgumentException>(() => doc.SetField("100000000000001", "ECPointLight", "radius", "big"));
        Assert.Throws<ArgumentException>(() => doc.SetField("100000000000001", "ECTransform", "position", "1 2"));
        Assert.Throws<InvalidOperationException>(() => doc.SetField("100000000000001", "ECPointLight", "nonsense", "1"));
        // Only the edited attribute changes.
        var before = Doc().ToText().Split('\n');
        var after = doc.ToText().Split('\n');
        Assert.Single(before.Zip(after), p => p.First != p.Second);
    }

    [Fact]
    public void CreateEntity_UsesTemplateAndFileLayout()
    {
        var doc = Doc();
        var id = doc.CreateEntity("PointLight", new Dictionary<string, string> { ["ECTransform.position"] = "7 8 9" }, parentLayer: "100000000000004");
        var e = doc.Read(id);
        Assert.Equal("PointLight", e.Type);
        Assert.Equal(["ECPointLight", "ECVisibilitySettingsCampaign", "ECCampaignProperties", "ECTransform"], e.Components.Select(c => c.Name));
        Assert.Equal("7 8 9", e.Component("ECTransform")!["position"]);
        Assert.Equal("1 1 1", e.Component("ECTransform")!["scale"]);
        Assert.Equal(["100000000000004"], e.Parents);
        // Attribute order copied from the existing light in this file.
        Assert.Equal(Doc().Read("100000000000001").Component("ECPointLight")!.Fields.Select(f => f.Key),
                     e.Component("ECPointLight")!.Fields.Select(f => f.Key));
    }

    [Fact]
    public void Layers_ParentDeleteAndMove()
    {
        var doc = Doc();
        var folder = doc.CreateLayer("folder");
        doc.SetParent("100000000000001", folder);
        // The emptied tag layer goes away; the folder stays even when emptied later.
        Assert.Null(doc.Find("100000000000004"));
        Assert.Equal([folder], doc.Read("100000000000001").Parents);
        Assert.Throws<InvalidOperationException>(() => doc.SetParent(folder, folder));
        doc.SetParent("100000000000001", null);
        Assert.NotNull(doc.Find(folder));

        var other = LayerDocument.Empty();
        other.ImportEntity(doc.DetachEntity("100000000000002"));
        Assert.Null(doc.Find("100000000000003"));
        Assert.Equal("100000000000002", other.Read("100000000000003").Group);
        Assert.Equal(["100000000000002", "100000000000003"], other.DeleteEntity("100000000000002").Order());
    }

    [Fact]
    public void DeleteLayer_MembersMoveUpOrGoWithIt()
    {
        var doc = Doc();
        doc.DeleteEntity("100000000000004");
        Assert.NotNull(doc.Find("100000000000001"));
        Assert.Empty(doc.Read("100000000000001").Parents);

        doc = Doc();
        var removed = doc.DeleteEntity("100000000000004", withMembers: true);
        Assert.Contains("100000000000001", removed);
        Assert.Null(doc.Find("100000000000001"));
    }

    [Fact]
    public void Editor_BatchUndoIsByteIdentical()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var terry = Path.Combine(dir.FullName, "p.terry");
            File.WriteAllText(terry, """
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

                """.ReplaceLineEndings("\n"));
            var layerPath = Path.Combine(dir.FullName, "p.100000000000011.layer");
            File.WriteAllText(layerPath, Layer.ReplaceLineEndings("\n"));
            var snapshot = Directory.GetFiles(dir.FullName).ToDictionary(f => f, File.ReadAllBytes);

            var paths = new ProjectPaths { OutputRoot = Path.Combine(dir.FullName, "out") };
            var editor = new EntityEditor(paths, terry);
            Assert.Equal("prefab", editor.Project.ProjectType);
            var results = editor.Apply(JsonNode.Parse("""
                [
                  {"op": "set", "id": "100000000000001", "fields": {"ECPointLight.radius": 3, "ECPointLight.colour": [1, 2, 3, 255]}},
                  {"op": "create", "type": "PointLight", "layer": "Default", "position": [1, 2, 3]},
                  {"op": "create_layer", "name": "Second"},
                  {"op": "move_to_layer", "id": "100000000000002", "layer": "Second"},
                  {"op": "layer_state", "id": "Second", "visible": false},
                  {"op": "set", "query": {"types": ["PointLight"]}, "fields": {"ECPointLight.for_light_probes_only": true}}
                ]
                """)!.AsArray());
            Assert.Equal(6, results.Count);
            Assert.Equal(2, ((JsonArray)results[5]!["entities"]!).Count);
            var second = editor.Layer("Second");
            Assert.True(File.Exists(second.FilePath));
            Assert.Contains(second.Id, TerryUserFile.Load(terry).Invisible);

            // A failing batch writes nothing.
            var history = editor.History().Count;
            Assert.Throws<InvalidOperationException>(() => editor.Apply(JsonNode.Parse("""[{"op": "set", "id": "100000000000001", "fields": {"ECPointLight.radius": "x"}}]""")!.AsArray()));
            Assert.Equal(history, editor.History().Count);

            editor.Undo();
            Assert.False(File.Exists(second.FilePath));
            Assert.False(File.Exists(TerryUserFile.PathFor(terry)));
            foreach (var (f, bytes) in snapshot) Assert.Equal(bytes, File.ReadAllBytes(f));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void KitProjects_RoundTripByteIdentical()
    {
        var raw = Path.Combine(Paths.AssemblyKitRoot, "raw_data");
        if (!Directory.Exists(raw)) return; // kit not available on this machine
        var files = Directory.EnumerateFiles(raw, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".terry") || f.EndsWith(".terry.user") || f.EndsWith(".layer")).ToList();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(files, f =>
        {
            var text = File.ReadAllText(f);
            System.Xml.Linq.XDocument doc;
            try { doc = TerryXml.Parse(text); }
            catch (System.Xml.XmlException) { return; } // one CA prefab layer is malformed XML
            if (TerryXml.ToText(doc.Root!, TerryXml.NewlineOf(text)) != text) failures.Add(f);
        });
        Assert.Empty(failures);
    }
}
