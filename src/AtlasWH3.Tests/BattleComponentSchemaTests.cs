using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>The battle component fields added to component_schema.json from the qttoolutility / Terry constructor
/// decompiles (research/battle_components). Atlas3K also checked them against 3K battle projects and prefabs; WH3's kit
/// has neither.</summary>
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

        // corpus-derived entries are untouched (WH3's kit has no battle prefabs: these come from Atlas3K's 3K scan)
        Assert.All(Schema.Find("ECCaptureLocation")!.Fields, f => Assert.Equal("atlas3k-corpus", f.Source));
        Assert.Equal(3, Schema.Find("ECTerryBattlefieldZone")!.Fields.Count);
        // marker components have no fields of their own (their shape is the entity's ECPolyline / ECRectangle)
        Assert.Empty(Schema.Find("ECPlayableArea")!.Fields);
        Assert.Empty(Schema.Find("ECBattlefieldZone")!.Fields);
    }
}
