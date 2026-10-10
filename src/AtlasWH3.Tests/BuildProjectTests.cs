using AtlasWH3.Core;
using AtlasWH3.Core.Build;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Packs;
using Xunit;

namespace AtlasWH3.Tests;

public class BuildProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atlaswh3_tests_" + Guid.NewGuid().ToString("N"));

    public BuildProjectTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void LoadsCommentedJsonAndExpandsTokens()
    {
        var file = Path.Combine(_dir, "test.atlaswh3");
        File.WriteAllText(file, """
            {
              // comment
              "map": "my_map",
              "assemblyKit": "K:\\kit",
              "build": { "output": "{project}\\out", "pack": { "output": "{out}\\{map}.pack" }, },
            }
            """);
        var project = BuildProject.Load(file);
        var paths = project.ToPaths(new ProjectPaths { GameDataDir = @"G:\data" });
        Assert.Equal("my_map", paths.MapName);
        Assert.Equal(@"K:\kit", paths.AssemblyKitRoot);
        Assert.Equal(Path.Combine(_dir, "out"), project.OutputDir(paths));
        Assert.Equal(Path.Combine(_dir, "out", "my_map.pack"), project.PackOutput(paths));   // an older project's "pack"
        Assert.Equal(@"K:\kit\x my_map G:\data", project.Expand("{ak}\\x {map} {game}", paths));
    }

    [Fact]
    public void DefaultProjectRoundTrips()
    {
        var file = Path.Combine(_dir, "d.atlaswh3");
        BuildProject.CreateDefault("m").Save(file);
        var loaded = BuildProject.Load(file);
        Assert.Equal("m", loaded.Map);
        var pack = Assert.Single(loaded.Build.Packs);
        Assert.Equal(PackMode.New, pack.Mode);
        Assert.Equal(PackContent.CompiledSource, Assert.Single(pack.Contents).Source);
        Assert.False(loaded.Build.Install.Enabled);
    }

    [Fact]
    public void DefaultProjectWithModPackMergesIntoACopyAndInstalls()
    {
        var project = BuildProject.CreateDefault("cr_oldworld_map_1", modPacks: [@"G:\data\!cr_oldworld_campaign.pack"]);
        var pack = Assert.Single(project.Build.Packs);
        Assert.Equal(PackMode.Merge, pack.Mode);
        Assert.Equal(@"G:\data\!cr_oldworld_campaign.pack", pack.Base);
        Assert.Equal(@"{project}\!cr_oldworld_campaign.pack", pack.Output);
        Assert.True(project.Build.Install.Enabled);
        var paths = project.ToPaths(new ProjectPaths());
        Assert.Contains("terrain/campaigns/cr_oldworld_map_devastate_1/pieces/", pack.ReplaceDirs.Select(d => project.Expand(d, paths)));
        project.Build.DevastatedMap = "other_devastate";
        Assert.Equal("other_devastate", project.Expand("{devastated}", paths));
    }

    [Fact]
    public void PackTakesTheCompiledFilesSplitsThemAndInstalls()
    {
        var output = Path.Combine(_dir, "out");
        var paths = new ProjectPaths
        {
            MapName = "m", CacheRoot = Path.Combine(_dir, "cache"), OutputRoot = Path.Combine(_dir, "app"), GameDataDir = Path.Combine(_dir, "data"),
        };
        Directory.CreateDirectory(paths.GameDataDir);
        Write(output, "terrain/campaigns/m/a.bin", "a");
        Write(output, "terrain/campaigns/m/pieces/event_1/mask", "piece");
        Write(output, "terrain/campaigns/m_devastate_1/event_tiles", "tiles");
        Write(output, "terrain/campaigns/m_devastate_1/global_props_devastation_chaos.bin", "BOB's by-product");
        Write(output, "campaign_maps/m/m_lookup.bmp", "CAIME's input");
        var manifestFile = BuildManifest.FileFor(paths, "m", output);
        var manifest = BuildManifest.Load(manifestFile, output);
        manifest.Set("heightmaps", [Path.Combine(output, @"terrain\campaigns\m\a.bin"), Path.Combine(paths.CacheRoot, "elsewhere.bin")]);
        manifest.Set("devastation_pieces", [Path.Combine(output, @"terrain\campaigns\m\pieces\event_1\mask"),
                                            Path.Combine(output, @"terrain\campaigns\m_devastate_1\event_tiles")]);
        manifest.Save(manifestFile);

        var project = new BuildProject { Map = "m" };
        project.Build.Output = output;
        project.Build.Packs =
        [
            new PackSettings { Output = Path.Combine(_dir, "main.pack"), Contents = [new PackContent { Source = PackContent.CompiledSource }],
                               Exclude = ["terrain/campaigns/{map}/pieces/", "terrain/campaigns/{devastated}/"] },
            new PackSettings { Output = Path.Combine(_dir, "devastate.pack"),
                               Contents = [new PackContent { Source = PackContent.CompiledSource, Path = "terrain/campaigns/{map}/pieces" },
                                           new PackContent { Source = PackContent.CompiledSource, Path = "terrain/campaigns/{devastated}" }] },
        ];
        project.Build.Install.Enabled = true;
        var report = new BuildRunner(project, paths).Run(new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Pack, BuildSegment.Install } });

        Assert.True(report.Succeeded, string.Join("; ", report.Items.SelectMany(i => i.Problems)));
        Assert.Equal([@"terrain\campaigns\m\a.bin"], PackFile.Open(Path.Combine(paths.GameDataDir, "main.pack")).Entries.Keys);
        Assert.Equal([@"terrain\campaigns\m\pieces\event_1\mask", @"terrain\campaigns\m_devastate_1\event_tiles"],
                     PackFile.Open(Path.Combine(paths.GameDataDir, "devastate.pack")).Entries.Keys.OrderBy(k => k.Length));
    }

    [Fact]
    public void ManifestIsPerOutputFolder()
    {
        var paths = new ProjectPaths { CacheRoot = Path.Combine(_dir, "cache") };
        var a = Path.Combine(_dir, "a");
        var file = BuildManifest.FileFor(paths, "m", a);
        var m = BuildManifest.Load(file, a);
        m.Set("lookup", [Path.Combine(a, "campaign_maps", "m", "m_lookup.tga")]);
        m.Save(file);
        Assert.Equal(["campaign_maps/m/m_lookup.tga"], BuildManifest.Load(file, a + "\\").Files);
        Assert.NotEqual(file, BuildManifest.FileFor(paths, "m", Path.Combine(_dir, "b")));
        Assert.Empty(BuildManifest.Load(file, Path.Combine(_dir, "b")).Steps);
    }

    [Fact]
    public void MergeKeepsAReplaceFolderTheBuildPutNothingIn()
    {
        var src = Path.Combine(_dir, "src");
        Write(src, "terrain/m/pieces/event_1/mask", "piece");
        Write(src, "terrain/m/a.bin", "old a");
        var basePack = Path.Combine(_dir, "base.pack");
        PackBuilder.New(basePack, PackBuilder.FromFolder(src));
        var update = Path.Combine(_dir, "update");
        Write(update, "a.bin", "new a");
        var s = PackBuilder.Merge(basePack, Path.Combine(_dir, "out.pack"), PackBuilder.Collect([(update, "terrain/m")], m => Assert.Fail(m)),
                                  ["terrain/m/pieces/"]);
        Assert.Equal((1, 1, 0), (s.Kept, s.Replaced, s.Dropped));
        Assert.NotNull(PackFile.Open(Path.Combine(_dir, "out.pack")).TryRead("terrain/m/pieces/event_1/mask"));
    }

    [Fact]
    public void MergeCopiesCompressedEntriesAsTheyAre()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("db rows ", 500)));
        using var zstd = new ZstdSharp.Compressor();
        var stored = BitConverter.GetBytes(data.Length).Concat(zstd.Wrap(data).ToArray()).ToArray();
        var basePack = Path.Combine(_dir, "base.pack");
        PackWriter.Write(basePack, [new PackWriter.Source("db/t/data", stored.Length, s => s.Write(stored), Compressed: true)]);
        var update = Path.Combine(_dir, "update");
        Write(update, "x.bin", "x");
        PackBuilder.Merge(basePack, basePack, PackBuilder.Collect([(update, "terrain")], m => Assert.Fail(m)));
        var pack = PackFile.Open(basePack);
        Assert.True(pack.Entries[@"db\t\data"].IsCompressed);
        Assert.Equal(data, pack.TryRead("db/t/data"));
    }

    [Fact]
    public void MergeReplacesAddsAndDropsStaleFiles()
    {
        var src = Path.Combine(_dir, "src");
        Write(src, "terrain/a.bin", "old a");
        Write(src, "terrain/stale.bin", "stale");
        Write(src, "db/keep.bin", "keep");
        var basePack = Path.Combine(_dir, "base.pack");
        PackBuilder.New(basePack, PackBuilder.FromFolder(src));

        var update = Path.Combine(_dir, "update");
        Write(update, "a.bin", "new a");
        Write(update, "b.bin", "new b");
        var files = PackBuilder.Collect([(update, "terrain")], m => Assert.Fail(m));
        var s = PackBuilder.Merge(basePack, basePack, files, ["terrain/"]);   // in place

        Assert.True(s.Verified);
        Assert.Equal((1, 1, 1, 1), (s.Kept, s.Replaced, s.Added, s.Dropped));
        var pack = PackFile.Open(basePack);
        Assert.Equal("new a", System.Text.Encoding.UTF8.GetString(pack.TryRead("terrain/a.bin")!));
        Assert.Null(pack.TryRead("terrain/stale.bin"));
        Assert.NotNull(pack.TryRead("db/keep.bin"));
    }

    [Fact]
    public void CollectReportsMissingSources()
    {
        var missing = new List<string>();
        var files = PackBuilder.Collect([(Path.Combine(_dir, "nope"), "x")], missing.Add);
        Assert.Empty(files);
        Assert.Single(missing);
    }

    private static void Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
