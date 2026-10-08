using System.Xml.Linq;
using AtlasWH3.Core;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public class PropLayerTests
{
    private static readonly ProjectPaths Paths = TestKits.VanillaPaths;

    [Fact]
    public void MetaTags_Decode_UsesFlagsAndMask_InNaturalOrder()
    {
        string[] values =
        [
            "building_level_1", "building_level_2", "building_level_3", "building_level_4", "building_level_5",
            "campaign_map_object_regional_resource", "campaign_map_object_security",
        ];
        Assert.Equal("building_level_3,building_level_4,building_level_5,campaign_map_object_security",
            MetaTags.Decode(values, 0x5c, 0x7f));
        Assert.Equal("", MetaTags.Decode(values, 0x1f, 0));
        Assert.Throws<InvalidDataException>(() => MetaTags.Decode(values, 0x80, 0xff));

        string[] levels = ["settlement_level_0", "settlement_level_1", "settlement_level_10", "settlement_level_2"];
        Assert.Equal("settlement_level_1,settlement_level_2,settlement_level_10", MetaTags.Decode(levels, 0xe, 0xf));
    }

    [Fact]
    public void MetaTags_DecodeSeasons_ListOrder_AllFiveMeansNoMask()
    {
        string[] all = ["ha", "au", "wi", "sp", "su"];
        Assert.Equal("", MetaTags.DecodeSeasons(all, 0x1f));
        Assert.Equal("season_harvest", MetaTags.DecodeSeasons(all, 0x01));
        Assert.Equal("season_summer,season_harvest", MetaTags.DecodeSeasons(["su", "ha"], 0x3));
        Assert.Equal("season_spring,season_summer,season_harvest,season_autumn",
            MetaTags.DecodeSeasons(["sp", "su", "ha", "au", "wi"], 0x0f));
    }

    [Fact]
    public void Transform_FromColumns_MatchesBlenderEuler()
    {
        var t = PropTransform.FromColumns([1, 0, 0, 0, 1, 0, 0, 0, 1], 1, 2, 3);
        Assert.Equal((0.0, 0.0, 0.0), (t.RotX, t.RotY, t.RotZ));
        Assert.Equal((1.0, 1.0, 1.0), (t.ScaleX, t.ScaleY, t.ScaleZ));

        // 30 degrees about Y, uniform scale 0.5; columns as stored in the file.
        var (s, c) = (MathF.Sin(MathF.PI / 6), MathF.Cos(MathF.PI / 6));
        t = PropTransform.FromColumns([c * 0.5f, 0, -s * 0.5f, 0, 0.5f, 0, s * 0.5f, 0, c * 0.5f], 0, 0, 0);
        Assert.Equal(30, t.RotY, 4);
        Assert.Equal(0, t.RotX, 6);
        Assert.Equal(0.5, t.ScaleZ, 6);
    }

    [Fact]
    public void LayerWriter_PutsTaggedObjects_UnderTagLayers()
    {
        var region = new RegionObjects("3k_test_region");
        region.Props.Add(Prop("a.wsmodel", 10, "building_level_3,building_level_4", ""));
        region.Props.Add(Prop("b.wsmodel", 20, "", "season_harvest"));
        region.Props.Add(Prop("c.wsmodel", 30, "building_level_3,building_level_4", ""));
        region.Vfx.Add(new VfxRecord("fire", new PropTransform(5, 0, 5), "night", ""));

        var text = LayerWriter.Write(region);
        Assert.Equal(text, LayerWriter.Write(region)); // ids are deterministic

        var doc = XDocument.Parse(text);
        var entities = doc.Root!.Element("entities")!.Elements("entity").ToList();
        var layers = entities.Where(e => e.Element("ECLayerExportTags") != null).ToList();
        Assert.Equal(["building_level_3,building_level_4", "night"],
            layers.Select(l => (string)l.Element("ECLayerExportTags")!.Attribute("tags")!).ToArray());
        Assert.All(layers, l => Assert.Equal((string)l.Attribute("name")!, (string)l.Element("ECLayerExportTags")!.Attribute("tags")!));

        string IdOf(string model) => (string)entities.Single(e =>
            (string?)e.Element("ECMesh")?.Attribute("model_path") == model).Attribute("id")!;
        var links = doc.Root.Element("associations")!.Element("Logical")!.Elements("from")
            .ToDictionary(f => (string)f.Attribute("id")!, f => f.Elements("to").Select(t => (string)t.Attribute("id")!).ToList());
        Assert.Equal([IdOf("a.wsmodel"), IdOf("c.wsmodel")], links[(string)layers[0].Attribute("id")!]);
        Assert.DoesNotContain(IdOf("b.wsmodel"), links.Values.SelectMany(v => v));
        Assert.Equal(entities.Count, entities.Select(e => (string)e.Attribute("id")!).Distinct().Count());

        var seasons = entities.Single(e => (string)e.Attribute("id")! == IdOf("b.wsmodel")).Element("ECCampaignProperties")!;
        Assert.Equal("season_harvest", (string)seasons.Attribute("season_mask")!);

        // Moving the layer after an expansion shifts objects only; tag layers have no transform.
        var (_, stats) = LayerShifter.Shift(text, 1, 2);
        Assert.Equal(4, stats.Shifted);
    }

    [Fact]
    public void GlobalProps_Vanilla_KeepsTagsAndSeasons()
    {
        var path = Paths.GlobalPropsBin;
        if (!File.Exists(path)) return; // data not available on this machine
        var regions = GlobalProps.Load(path).ReadRegions(Paths.MapName);

        Assert.Equal(264, regions.Count);
        var tagged = regions.Sum(r => r.Props.Count(p => p.Tags != "") + r.Vfx.Count(v => v.Tags != "")
                                      + r.PointLights.Count(l => l.Tags != "") + r.CompositeScenes.Count(c => c.Tags != ""));
        Assert.Equal(42_512, tagged);

        var hulao = regions.Single(r => r.Region == "3k_dlc06_hulao_pass");
        Assert.Contains(hulao.Props, p => p.Tags == "building_level_1,building_level_2,building_level_3,building_level_4,building_level_5");
        Assert.Contains(regions.SelectMany(r => r.PointLights), l => l.Tags.Split(',').Contains("night"));
        // Harvest-coloured trees only show in harvest.
        Assert.All(regions.SelectMany(r => r.Props).Where(p => p.Path.Contains("katsura_harvest")),
            p => Assert.Equal("season_harvest", p.Seasons));
    }

    private static PropRecord Prop(string path, float x, string tags, string seasons) =>
        new(path, new PropTransform(x, 1, x), tags, seasons, false, true, false, false, false,
            true, true, true, true, false, true);
}
