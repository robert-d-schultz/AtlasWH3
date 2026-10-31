using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

public class PackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atlaswh3_packs_" + Guid.NewGuid().ToString("N"));

    public PackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var pack in Directory.EnumerateFiles(_dir, "*.pack")) PackFile.Forget(pack);
        Directory.Delete(_dir, true);
    }

    private string Pack(string name, params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_dir, name);
        PackWriter.Write(path, files.Select(f => (f.Path, System.Text.Encoding.UTF8.GetBytes(f.Text))));
        return path;
    }

    /// <summary>The game's own packs, compressed (zstd, some tile meshes LZ4): RPFM's server decompresses them.</summary>
    [Fact]
    public void Wh3_CampaignPacksDecode()
    {
        var data = TestKits.Wh3GameData;
        if (!File.Exists(Path.Combine(data, "tiles_campaign.pack"))) return;
        var tiles = PackFile.Open(Path.Combine(data, "tiles_campaign.pack"));
        foreach (var e in tiles.Entries.Values.Where(e => e.Path.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase)).Take(50))
            Assert.Equal("RMV2"u8.ToArray(), tiles.TryRead(e.Path)![..4]);
        Assert.NotNull(tiles.TryRead(@"terrain\tiles\campaign\_tile_database\_settings.bin"));
    }

    /// <summary>Paths are found with either slash and any case (the server's own lookups are exact), and a vanilla
    /// file names the CA pack it comes from.</summary>
    [Fact]
    public void Wh3_VanillaLookups()
    {
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        var vanilla = PackFile.Vanilla(TestKits.Wh3GameData);
        const string path = "terrain/tiles/campaign/_tile_database/tiles/cliff_gen_long_curve1.bin";
        var bytes = vanilla.TryRead(path);
        Assert.NotNull(bytes);
        Assert.Equal(bytes, vanilla.TryRead(path.ToUpperInvariant().Replace('/', '\\')));
        Assert.Equal("tiles_campaign.pack", vanilla.Origin(path));
        Assert.Same(vanilla, PackFile.Vanilla(TestKits.Wh3GameData));
    }

    [Fact]
    public void OpenReusesThePackUntilTheFileChanges()
    {
        var path = Pack("a.pack", ("Terrain/A.bin", "one"));
        var first = PackFile.Open(path);
        Assert.Same(first, PackFile.Open(path));
        Assert.Equal("Terrain/A.bin", first.Entries[@"terrain\a.bin"].Path);
        Assert.Equal((3u, "a.pack"), (first.PackType, first.Origin("terrain/a.bin")));

        File.SetLastWriteTimeUtc(Pack("a.pack", ("terrain/a.bin", "two!")), DateTime.UtcNow.AddMinutes(1));
        var second = PackFile.Open(path);
        Assert.NotSame(first, second);
        Assert.Equal("two!", System.Text.Encoding.UTF8.GetString(second.TryRead("TERRAIN/A.BIN")!));
    }

    /// <summary>Mod packs come before the vanilla packs.</summary>
    [Fact]
    public void Wh3_ModPacksOverrideVanilla()
    {
        if (!Directory.Exists(TestKits.Wh3GameData)) return;
        const string path = "terrain/tiles/campaign/_tile_database/_settings.bin";
        var set = new PackSet([PackFile.Open(Pack("mod.pack", (path, "mod"))), PackFile.Vanilla(TestKits.Wh3GameData)]);
        Assert.Equal("mod", System.Text.Encoding.UTF8.GetString(set.TryRead(path)!));
        Assert.Equal("mod.pack", set.Origin(path));
        Assert.Equal("tiles_campaign.pack", set.Origin("terrain/tiles/campaign/_tile_database/tiles/cliff_gen_long_curve1.bin"));
    }
}
