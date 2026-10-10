using System.Xml.Linq;
using AtlasWH3.Core;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public class TerrySchemaTests
{

    private const string Config = """
        <configuration version="1">
          <entity type="Prop">
            <allow_if project_type="tile,tile_map,prefab" />
            <component type="ECPropMesh" />
            <component type="ECMesh" />
            <component type="ECTransform" />
          </entity>
          <entity type="Zone">
            <default_name>Zone</default_name>
            <allow_if project_type="tile_map" tile_database="battle" />
            <component type="ECTransform" />
            <component parameter="shape">
              <component value="circle" type="ECCircle" />
              <component value="polyline" type="ECPolyline" readonly="true" />
            </component>
          </entity>
          <entity type="Boundary">
            <allow_if_child_of type="Zone" />
            <component type="ECRectangle" />
          </entity>
        </configuration>
        """;

    [Fact]
    public void Configuration_ParsesConditionalSlotsAndRules()
    {
        var cfg = EntityConfiguration.Parse(XDocument.Parse(Config));
        var zone = cfg.Find("Zone")!;
        Assert.Equal("Zone", zone.DefaultName);
        Assert.Equal(["ECTransform", "ECCircle", "ECPolyline"], zone.Components.Select(c => c.Type));
        Assert.True(zone.Components[2] is { Conditional: true, Parameter: "shape", Value: "polyline", ReadOnly: true });
        Assert.True(zone.AllowedIn("tile_map", "battle"));
        Assert.False(zone.AllowedIn("tile_map", "campaign"));
        Assert.True(cfg.Find("Prop")!.AllowedIn("prefab", "campaign"));
        Assert.False(cfg.Find("Boundary")!.AllowedIn("tile_map", "battle"));
    }

    [Fact]
    public void Configuration_ClassifiesByComponents()
    {
        var cfg = EntityConfiguration.Parse(XDocument.Parse(Config));
        Assert.Equal("Prop", cfg.Classify(["ECPropMesh", "ECMesh", "ECTransform"])!.Type);
        // A conditional slot that is absent does not count against the type.
        Assert.Equal("Zone", cfg.Classify(["ECTransform", "ECCircle"])!.Type);
        Assert.Null(cfg.Classify(["ECUnknown"]));
    }

    [Fact]
    public void Scan_InfersFieldTypes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var f = Path.Combine(dir.FullName, "a.layer");
            File.WriteAllText(f, """
                <layer version="35"><entities>
                  <entity id="1000000000000aa"><ECPropMesh/><ECMesh model_path="a/b.wsmodel"/><ECTransform position="1 2 3" flag="true" mode="Exclude" tint="255 0 10 255" n="4"/></entity>
                  <entity id="1000000000000ab"><ECPropMesh/><ECMesh model_path="a/c.wsmodel"/><ECTransform position="1.5 2 3" flag="false" mode="Include" tint="1 2 3 4" n="4.5"/></entity>
                </entities></layer>
                """);
            var s = ComponentSchema.Scan([f], EntityConfiguration.Parse(XDocument.Parse(Config)));
            var t = s.Find("ECTransform")!;
            FieldType TypeOf(string n) => t.Fields.Single(x => x.Name == n).Type;
            Assert.Equal(FieldType.Vec3, TypeOf("position"));
            Assert.Equal(FieldType.Bool, TypeOf("flag"));
            Assert.Equal(FieldType.Enum, TypeOf("mode"));
            Assert.Equal(FieldType.Colour, TypeOf("tint"));
            Assert.Equal(FieldType.Float, TypeOf("n"));
            Assert.Equal(FieldType.Path, s.Find("ECMesh")!.Fields.Single().Type);
            Assert.Equal(["position", "flag", "mode", "tint", "n"], t.Fields.Select(x => x.Name)); // Terry's attribute order
            Assert.Empty(s.UnclassifiedSignatures);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Embedded_CoversEveryKitComponent()
    {
        var schema = ComponentSchema.Embedded;
        Assert.True(schema.EntitiesScanned > 0);
        Assert.Contains(schema.Find("ECTransform")!.Fields, f => f.Name == "position" && f.Type == FieldType.Vec3);
        var cfgPath = Path.Combine(TestKits.Wh3Kit, EntityConfiguration.RelativePath);
        if (!File.Exists(cfgPath)) return; // kit not available on this machine
        var cfg = EntityConfiguration.Load(cfgPath);
        Assert.Equal(59, cfg.Types.Count);
        Assert.All(cfg.ComponentTypes, c => Assert.NotNull(schema.Find(c)));
    }
}
