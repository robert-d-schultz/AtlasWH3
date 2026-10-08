using AtlasWH3.Core.Bob;

namespace AtlasWH3.Tests;

public class BobRunnerTests
{
    private const string SilentLog = """
        8 action(s) were selected for execution.
        Configuration File: BOB/atlaswh3_run_configuration.xml, Started at: 08.10.2026 05:18:04

        === Terrain / Terry file  (d:/kit/raw_data/terrain/campaigns/m/m.terry) (STATUS: FinishedWithWarnings) ===
        Failed to find valid quadtree node for Prop entity, id: 1d5feb823164428. Check object position/bounds.
        === Terrain / Color Overlay (Sea) (d:/kit/raw_data/terrain/campaigns/m/m.terry) (STATUS: Finished) ===
        === Terrain / Devastation pieces: BMDs: 254/254 (d:/kit/raw_data/terrain/campaigns/m/m.terry) (STATUS: Finished) ===
        === Terrain / Campaign Trees (from d:/kit/raw_data/terrain/campaigns/m/m.terry) (STATUS: Failed) ===
        """;

    [Fact]
    public void ParsesSilentAndGuiHeaders()
    {
        Assert.Equal(8, BobRunner.SelectedCount(SilentLog));
        var s = BobRunner.Statuses(SilentLog);
        Assert.Equal(["Terry file", "Color Overlay (Sea)", "Devastation pieces: BMDs: 254/254", "Campaign Trees"], s.Select(x => x.Action));
        Assert.Equal(["FinishedWithWarnings", "Finished", "Finished", "Failed"], s.Select(x => x.Status));
        Assert.Equal("d:/kit/raw_data/terrain/campaigns/m/m.terry", s[3].Input);
    }

    [Fact]
    public void DefaultRunExpectsEightActionsPerMap()
    {
        var r = BobActions.DefaultRun(["a", "b"]);
        Assert.Equal(16, r.ExpectedSelected);
        Assert.Equal(["<raw>/terrain/campaigns/a/a.terry", "<raw>/terrain/campaigns/b/b.terry"], r.Consumers);
        Assert.DoesNotContain("Tilemap", r.Actions);
    }

    [Fact]
    public void ScratchKitRefusesForeignFolders()
    {
        var dir = Directory.CreateTempSubdirectory("atlaswh3_notscratch").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "user.txt"), "x");
            Assert.Throws<InvalidOperationException>(() => ScratchKit.Create(Path.Combine(dir, "kit"), dir, new ScratchKit.Options { Maps = ["m"] }));
            Assert.Throws<InvalidOperationException>(() => ScratchKit.ClearOutputs(dir, "m"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
