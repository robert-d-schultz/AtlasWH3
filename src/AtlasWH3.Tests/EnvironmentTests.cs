using AtlasWH3.Core.Campaign;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>environment_collection.xml (the environment step) and the pieces step's event_vfx list.</summary>
public class EnvironmentTests
{
    /// <summary>IEE's devastated project against BOB's output in the kit (2026-09-30): byte-identical (1 sphere, 65
    /// cylinders, no devastation lighting).</summary>
    [Fact]
    public void Build_IeeDevastated_MatchesBob()
    {
        const string map = "cr_combi_expanded_map_devastate_1";
        var terry = Path.Combine(TestKits.Wh3Kit, "raw_data", "terrain", "campaigns", map, map + ".terry");
        var bob = Path.Combine(TestKits.Wh3Kit, "working_data", "terrain", "campaigns", map, "environment_collection.xml");
        if (!File.Exists(terry) || !File.Exists(bob)) return;
        var (text, spheres, cylinders) = EnvironmentStep.Build(TerryProject.Load(terry));
        Assert.Equal((1, 65), (spheres, cylinders));
        Assert.Equal(File.ReadAllText(bob), text);
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
