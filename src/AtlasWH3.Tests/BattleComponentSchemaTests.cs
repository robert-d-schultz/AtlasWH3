using System.Xml.Linq;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>The battle component fields added to component_schema.json from the qttoolutility / Terry constructor
/// decompiles (research/battle_components), checked against what Terry wrote in real projects.</summary>
public class BattleComponentSchemaTests
{
    private static readonly ComponentSchema Schema = ComponentSchema.Embedded;

    [Fact]
    public void DecompiledFields_AreInTheEmbeddedSchema()
    {
        var slot = Schema.Find("ECBuildingSlot")!;
        Assert.Equal(["slot_id", "group"], slot.Fields.Select(f => f.Name));
        Assert.Equal(FieldType.Int, slot.Fields[0].Type);
        Assert.All(slot.Fields, f => Assert.Equal("decompile", f.Source));

        Assert.Equal("60", Schema.Field("ECCamera", "vertical_fov")!.Default);
        Assert.Equal("true", Schema.Field("ECLineOfSight", "active")!.Default);
        Assert.Equal(["type", "redirect_to", "ambient_light_environments", "culture", "procedural"],
            Schema.Find("ECBattleCatchmentArea")!.Fields.Select(f => f.Name));
        var purpose = Schema.Field("ECEFLine", "formation_purpose")!;
        Assert.Equal(FieldType.Enum, purpose.Type);
        Assert.Contains("EFP_BOARDING", purpose.Values!);

        // corpus-derived entries are untouched
        Assert.All(Schema.Find("ECCaptureLocation")!.Fields, f => Assert.Equal("corpus", f.Source));
        Assert.Equal(3, Schema.Find("ECTerryBattlefieldZone")!.Fields.Count);
        // marker components have no fields of their own (their shape is the entity's ECPolyline / ECRectangle)
        Assert.Empty(Schema.Find("ECPlayableArea")!.Fields);
        Assert.Empty(Schema.Find("ECBattlefieldZone")!.Fields);
    }

    [Fact]
    public void DecompiledFields_MatchAUserBattleProject()
    {
        // A battle map built in the 190E kit (Terry wrote it): its ECBuildingSlot attributes are the decompiled
        // fields, in the same order, and validate.
        var dir = Path.Combine(TestKits.Expanded, "raw_data", "terrain", "tiles", "battle", "_assembly_kit");
        if (!Directory.Exists(dir)) return;   // data not available
        var slots = Directory.EnumerateFiles(dir, "*.layer", SearchOption.AllDirectories)
            .SelectMany(f => XDocument.Load(f).Descendants("ECBuildingSlot")).ToList();
        if (slots.Count == 0) return;
        foreach (var s in slots)
        {
            Assert.Equal(Schema.Find("ECBuildingSlot")!.Fields.Select(f => f.Name), s.Attributes().Select(a => a.Name.LocalName));
            foreach (var a in s.Attributes()) Assert.Null(Schema.Validate("ECBuildingSlot", a.Name.LocalName, a.Value));
        }
    }

    [Fact]
    public void DeploymentPrefab_FieldsAndOrderMatchTheDecompile()
    {
        // Vanilla default deployment prefab: ECDeploymentZone / ECDeploymentZoneRegion attributes in the order the
        // constructors register them (facing_direction ... configuration), which is the order the schema keeps.
        var file = Directory.EnumerateFiles(Path.Combine(TestKits.Vanilla, "raw_data", "art", "prefabs", "battle", "logic", "default_deployment"),
            "deploy_land_normal_1024x1024_north.*.layer").FirstOrDefault();
        if (file is null) return;
        var doc = XDocument.Load(file);
        var zone = doc.Descendants("ECDeploymentZone").First();
        Assert.Equal(["facing_direction", "deployment_type", "deployment_category", "deployment_zone_id", "alliance_id", "configuration"],
            zone.Attributes().Select(a => a.Name.LocalName));
        Assert.Equal(Schema.Find("ECDeploymentZone")!.Fields.Select(f => f.Name), zone.Attributes().Select(a => a.Name.LocalName));
        Assert.Equal("DZRT_ADDITIVE", doc.Descendants("ECDeploymentZoneRegion").First().Attribute("region_type")!.Value);
    }
}
