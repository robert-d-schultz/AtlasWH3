using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public class LayerDocumentTests
{
    private static string Sample()
    {
        var region = new RegionObjects("wh3_test_region");
        region.Props.Add(Prop("a.wsmodel", 10, "building_level_3,building_level_4", ""));
        region.Props.Add(Prop("b.wsmodel", 20, "", "season_harvest"));
        region.Props.Add(Prop("c.wsmodel", 30, "building_level_3,building_level_4", ""));
        region.Vfx.Add(new VfxRecord("fire", new PropTransform(5, 0, 5), "night", ""));
        return LayerWriter.Write(region);
    }

    private static string IdOf(LayerDocument doc, string asset) => doc.Entities().Single(e => e.Asset == asset).Id;

    [Fact]
    public void Unedited_RoundTripsByteIdentical()
    {
        var text = Sample();
        Assert.Equal(text, LayerDocument.Parse(text).ToText());
    }

    /// <summary>The fixture maps' layers: Old World's (Terry's) byte for byte, IEE's decompiler-written ones with the same
    /// content (<see cref="TerryLayout"/>).</summary>
    [Fact]
    public void Unedited_AkLayers_RoundTrip()
    {
        var dirs = new[] { TestKits.Iee, TestKits.OldWorld }.Select(m => TestKits.Paths(m).AkTerrainDir).Where(Directory.Exists).ToList();
        if (dirs.Count == 0) return; // kit not available on this machine
        var files = dirs.SelectMany(d => Directory.GetFiles(d, "*.layer")).ToList();
        Assert.NotEmpty(files);
        Parallel.ForEach(files, f =>
        {
            var text = File.ReadAllText(f);
            Assert.Null(TerryLayout.Check(text, LayerDocument.Parse(text).ToText()) is { } why ? $"{Path.GetFileName(f)}: {why}" : null);
        });
    }

    [Fact]
    public void Entities_ResolveKindsAssetsAndTags()
    {
        var doc = LayerDocument.Parse(Sample());
        var a = doc.Entities().Single(e => e.Asset == "a.wsmodel");
        Assert.Equal("prop", a.Kind);
        Assert.Equal("building_level_3,building_level_4", a.Tags);
        Assert.Equal([10.0, 1.0, 10.0], a.Position);
        Assert.Equal("night", doc.Entities().Single(e => e.Kind == "vfx").Tags);
        Assert.Equal("season_harvest", doc.Entities().Single(e => e.Asset == "b.wsmodel").Seasons);
    }

    [Fact]
    public void SetTransform_ChangesOnlyThatLine()
    {
        var text = Sample();
        var doc = LayerDocument.Parse(text);
        var id = IdOf(doc, "b.wsmodel");
        doc.SetTransform(id, position: [21.5, null, 22.25], rotation: [null, 45, null]);
        var before = text.Split('\n');
        var after = doc.ToText().Split('\n');
        Assert.Equal(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.Single(changed);
        Assert.Contains("position=\"21.5 1 22.25\" rotation=\"0 45 0\" scale=\"1 1 1\"", after[changed[0]]);
    }

    [Fact]
    public void AddThenDelete_RestoresOriginalBytes()
    {
        var text = Sample();
        var doc = LayerDocument.Parse(text);
        var id = doc.AddProp(Prop("new.wsmodel", 40, "night", "season_winter"));
        var added = doc.Get(id);
        Assert.Equal("night", added.Tags);
        Assert.Equal("season_winter", added.Seasons);
        Assert.Equal(15, id.Length);

        // the new entity sits among the objects, before the tag layers
        var kinds = doc.Entities().Select(e => e.Kind).ToList();
        Assert.True(kinds.LastIndexOf("prop") < kinds.IndexOf("tag_layer"));

        doc.Delete(id);
        Assert.Equal(text, doc.ToText());
    }

    [Fact]
    public void SetTags_CreatesAndRemovesTagLayers()
    {
        var text = Sample();
        var doc = LayerDocument.Parse(text);
        var b = IdOf(doc, "b.wsmodel");

        doc.SetTags(b, "building_level_4,building_level_3"); // same set as the existing layer, other order
        Assert.Equal(2, doc.Entities().Count(e => e.Kind == "tag_layer"));
        Assert.Equal("building_level_3,building_level_4", doc.Get(b).Tags);

        doc.SetTags(b, "settlement_level_2");
        Assert.Equal(3, doc.Entities().Count(e => e.Kind == "tag_layer"));
        Assert.Equal("settlement_level_2", doc.Get(b).Tags);

        doc.SetTags(b, "");
        Assert.Equal(text, doc.ToText()); // the new tag layer went away with its last member

        // emptying the original layer removes it and its Logical links
        doc.SetTags(IdOf(doc, "a.wsmodel"), "");
        doc.SetTags(IdOf(doc, "c.wsmodel"), "");
        Assert.Equal(1, doc.Entities().Count(e => e.Kind == "tag_layer"));
        Assert.DoesNotContain("building_level_3", doc.ToText());
    }

    [Fact]
    public void Duplicate_CopiesTagsAndOffsets()
    {
        var doc = LayerDocument.Parse(Sample());
        var a = IdOf(doc, "a.wsmodel");
        var copy = doc.Duplicate(a, 2, 0, -1);
        var e = doc.Get(copy);
        Assert.NotEqual(a, copy);
        Assert.Equal([12.0, 1.0, 9.0], e.Position);
        Assert.Equal(doc.Get(a).Tags, e.Tags);
        Assert.Throws<InvalidOperationException>(() => doc.Delete(doc.Entities().First(x => x.Kind == "tag_layer").Id));
    }

    [Fact]
    public void PropEditor_EditsUndoesAndRollsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "terryclone_props_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ProjectPaths { AssemblyKitRoot = Path.Combine(root, "ak"), OutputRoot = Path.Combine(root, "out"), MapName = "m" };
            Directory.CreateDirectory(paths.AkTerrainDir);
            File.WriteAllText(Path.Combine(paths.AkTerrainDir, "m.terry"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <project version="14"><entities>
                  <entity id="1aaa" name="wh3_test_region"><ECLayerFile/><ECLayerExport export="true"/></entity>
                </entities></project>
                """);
            var layerPath = Path.Combine(paths.AkTerrainDir, "m.1aaa.layer");
            var original = Sample();
            File.WriteAllText(layerPath, original);

            var editor = new PropEditor(paths);
            var b = editor.Find(new PropEditor.Query(Asset: "b.wsmodel")).Single().Entity.Id;
            editor.Checkpoint("start");
            editor.Apply(JsonNode.Parse($$"""[{"op":"move","id":"{{b}}","by":[1,0,1]},{"op":"duplicate","id":"{{b}}","by":[5,0,0]}]""")!.AsArray());
            var after1 = File.ReadAllText(layerPath);
            Assert.NotEqual(original, after1);

            var editor2 = new PropEditor(paths);
            editor2.Apply(JsonNode.Parse($$"""[{"op":"delete","ids":["{{b}}"]}]""")!.AsArray());
            Assert.Equal(2, editor2.History().Count);
            Assert.Equal(4, editor2.Find(new PropEditor.Query()).Count()); // a, c, b's duplicate, vfx

            // a failing batch writes nothing
            Assert.Throws<InvalidOperationException>(() =>
                editor2.Apply(JsonNode.Parse("""[{"op":"delete","ids":["nope"]}]""")!.AsArray()));
            Assert.Equal(2, editor2.History().Count);

            editor2.Undo();
            Assert.Equal(after1, File.ReadAllText(layerPath));
            editor2.Rollback("start");
            Assert.Equal(original, File.ReadAllText(layerPath));
            Assert.Empty(editor2.History());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static PropRecord Prop(string path, float x, string tags, string seasons) =>
        new(path, new PropTransform(x, 1, x), tags, seasons, false, true, false, false, false,
            true, true, true, true, false, true);
}
