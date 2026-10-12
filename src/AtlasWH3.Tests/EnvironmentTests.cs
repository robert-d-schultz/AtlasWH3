using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>environment_collection.xml (the environment step) and the pieces step's event_vfx list.</summary>
public class EnvironmentTests
{
    /// <summary>IEE's devastated project against BOB's output in the kit (2026-09-30): byte-identical (1 sphere, 65
    /// cylinders, no devastation lighting) but the global_lighting, which is vanilla combi's for every map, where BOB
    /// writes the .terry's (what Terry shows).</summary>
    [Fact]
    public void Build_IeeDevastated_MatchesBob()
    {
        const string map = "cr_combi_expanded_map_devastate_1";
        var terry = Path.Combine(TestKits.Kit(map), "raw_data", "terrain", "campaigns", map, map + ".terry");
        var bob = Path.Combine(TestKits.Kit(map), "working_data", "terrain", "campaigns", map, "environment_collection.xml");
        if (!File.Exists(terry) || !File.Exists(bob)) return;
        var project = TerryProject.Load(terry);
        var (text, _, spheres, cylinders) = EnvironmentStep.Build(project);
        Assert.Equal((1, 65), (spheres, cylinders));
        Assert.StartsWith($"<ENVIRONMENT_COLLECTION serialise_version='4' global_lighting='{EnvironmentStep.GlobalLighting}'>\r\n", text);
        Assert.Equal(File.ReadAllText(bob).Replace($"global_lighting='{project.Setting("global_lighting")}'",
            $"global_lighting='{EnvironmentStep.GlobalLighting}'"), text);
    }

    /// <summary>The devastated folder's file is the map's but for an empty ENVIRONMENT_DEVASTATIONS, as vanilla's
    /// wh3_main_combi_map_devastate_1; the map's lists every name=path pair of the .terry, IEE's devastation_beast too.</summary>
    [Fact]
    public void Build_IeeMap_DevastatedTextEmptiesOnlyTheDevastations()
    {
        const string map = "cr_combi_expanded_map_1";
        var terry = Path.Combine(TestKits.Kit(map), "raw_data", "terrain", "campaigns", map, map + ".terry");
        if (!File.Exists(terry)) return;
        var (text, devastated, _, _) = EnvironmentStep.Build(TerryProject.Load(terry));
        var start = text.IndexOf("\t<ENVIRONMENT_DEVASTATIONS>\r\n", StringComparison.Ordinal);
        const string end = "\t</ENVIRONMENT_DEVASTATIONS>\r\n";
        var stop = text.IndexOf(end, StringComparison.Ordinal) + end.Length;
        Assert.True(start >= 0 && stop > start);
        Assert.Contains("name='devastation_beast' lighting='Weather/et_beast_waagh.environment'", text[start..stop]);
        Assert.Equal(text[..start] + "\t<ENVIRONMENT_DEVASTATIONS/>\r\n" + text[stop..], devastated);
    }

    /// <summary>Per type (the untyped files last), the effects no map-wide object uses, each once.</summary>
    [Fact]
    public void EventVfx_GroupsByTypeAndLeavesOutMapWideEffects()
    {
        var pieces = new Dictionary<string, SortedSet<string>>
        {
            [""] = new(StringComparer.Ordinal) { "fire", "smoke", "z_extra" },
            ["devastation_skaven"] = new(StringComparer.Ordinal) { "warpstone", "smoke" },
            ["devastation_chaos"] = new(StringComparer.Ordinal) { "embers", "fire" },
        };
        var lines = DevastationPiecesStep.EventVfx(pieces, new HashSet<string>(StringComparer.Ordinal) { "fire" });
        Assert.Equal(["embers", "smoke", "warpstone", "z_extra"], lines);
    }
}
