using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

public class LayerTreeFilterTests
{
    private static TerryEntityData E(string id, string type, string? name = null, string[]? parents = null, string? group = null) =>
        new(id, name, type, parents ?? [], [], group);

    private static (TerryEntityData, string)[] With(params TerryEntityData[] es) => es.Select(e => (e, e.Name ?? e.Id)).ToArray();

    [Fact]
    public void Empty_KeepsEverything()
    {
        var f = LayerTreeFilter.None;
        Assert.True(f.IsEmpty);
        Assert.True(f.KeepFileLayer("anything", hidden: true, []));
    }

    [Fact]
    public void Region_MatchesLayerName_CaseInsensitive()
    {
        var f = new LayerTreeFilter(Region: "LUOYANG");
        Assert.True(f.KeepFileLayer("3k_main_luoyang_capital", false, []));
        Assert.False(f.KeepFileLayer("3k_main_xuchang_capital", false, []));
    }

    [Fact]
    public void Visibility_UsesSavedState()
    {
        Assert.True(new LayerTreeFilter(Visibility: LayerVisibilityFilter.Hidden).KeepFileLayer("a", true, []));
        Assert.False(new LayerTreeFilter(Visibility: LayerVisibilityFilter.Hidden).KeepFileLayer("a", false, []));
        Assert.True(new LayerTreeFilter(Visibility: LayerVisibilityFilter.Visible).KeepFileLayer("a", false, []));
    }

    [Fact]
    public void Text_MatchesLayerNameOrAnEntity()
    {
        var f = new LayerTreeFilter(Text: "pine");
        Assert.True(f.KeepFileLayer("forest_pine", false, []));
        Assert.True(f.KeepFileLayer("props", false, With(E("1", "PropMesh", "tall_pine_tree"))));
        Assert.False(f.KeepFileLayer("props", false, With(E("1", "PropMesh", "rock"))));
    }

    [Fact]
    public void Type_NeedsAMatchingEntity_LayerNameAloneIsNotEnough()
    {
        var f = new LayerTreeFilter(Text: "props", Type: "VFX");
        Assert.False(f.KeepFileLayer("props", false, With(E("1", "PropMesh", "a"))));
        Assert.True(f.KeepFileLayer("props", false, With(E("1", "VFX", "props_smoke"))));
        Assert.True(new LayerTreeFilter(Type: "vfx").KeepFileLayer("x", false, With(E("1", "VFX"))));
    }

    [Fact]
    public void KeepChild_KeepsParentsOfMatches()
    {
        var folder = E("f", TerryEntityTypes.Layer, "folder");
        var group = E("g", TerryEntityTypes.Group, "grp", parents: ["f"]);
        var inGroup = E("m", "PropMesh", "wanted_tree", group: "g");
        var other = E("o", "PropMesh", "rock", parents: ["f"]);
        var all = new List<TerryEntityData> { folder, group, inGroup, other };
        var f = new LayerTreeFilter(Text: "wanted");
        string L(TerryEntityData e) => e.Name ?? e.Id;
        Assert.True(f.KeepChild(folder, "folder", all, L));   // grandchild matches through the group
        Assert.True(f.KeepChild(group, "grp", all, L));
        Assert.True(f.KeepChild(inGroup, "wanted_tree", all, L));
        Assert.False(f.KeepChild(other, "rock", all, L));
    }

    [Fact]
    public void KeepChild_NestedLayerVisibility()
    {
        var folder = E("f", TerryEntityTypes.Layer, "folder");
        var f = new LayerTreeFilter(Visibility: LayerVisibilityFilter.Hidden);
        Assert.False(f.KeepChild(folder, "folder", [folder], e => e.Id, id => false));
        Assert.True(f.KeepChild(folder, "folder", [folder], e => e.Id, id => true));
    }
}
