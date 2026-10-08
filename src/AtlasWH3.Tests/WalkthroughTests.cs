using AtlasWH3.Core;
using Xunit;

namespace AtlasWH3.Tests;

public class WalkthroughTests
{
    [Fact]
    public void ToursSeen_persists_and_resets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atlaswh3_tours_" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "settings.json");
            var s = new AppSettings();
            Assert.False(s.TourSeen("start"));
            Assert.True(s.MarkTourSeen("start"));
            Assert.False(s.MarkTourSeen("START"));      // case-insensitive, no duplicates
            s.MarkTourSeen("scene");
            s.SaveTo(file);

            var back = AppSettings.Load(file);
            Assert.True(back.TourSeen("start"));
            Assert.True(back.TourSeen("scene"));
            Assert.False(back.TourSeen("build"));

            back.ResetTours();
            back.SaveTo(file);
            Assert.Empty(AppSettings.Load(file).ToursSeen);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Old_settings_file_without_tours_loads_with_none_seen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atlaswh3_tours_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{ "gameFolder": "C:\\Game", "developerMode": true }""");
            var s = AppSettings.Load(file);
            Assert.True(s.DeveloperMode);
            Assert.Empty(s.ToursSeen);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Hidden_steps_are_skipped_and_counted_out()
    {
        var p = new TourProgress([true, false, true, true, false]);
        Assert.Equal([0, 2, 3], p.Steps);
        Assert.Equal("Step 1 of 3", p.Label);
        Assert.True(p.IsFirst);
        Assert.False(p.Back());
        Assert.True(p.Next());
        Assert.Equal(2, p.Current);
        Assert.Equal("Step 2 of 3", p.Label);
        Assert.True(p.Next());
        Assert.True(p.IsLast);
        Assert.False(p.Next());                         // Done
        Assert.Equal(3, p.Current);
        Assert.True(p.Back());
        Assert.Equal(2, p.Current);
    }

    [Fact]
    public void A_tour_with_nothing_to_show_is_empty()
    {
        Assert.True(new TourProgress([false, false]).IsEmpty);
        Assert.True(new TourProgress([]).IsEmpty);
    }
}
