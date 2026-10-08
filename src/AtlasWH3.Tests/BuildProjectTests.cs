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
        Assert.Equal(Path.Combine(_dir, "out", "my_map.pack"), project.Resolve(project.Build.Pack.Output, paths));
        Assert.Equal(@"K:\kit\x my_map G:\data", project.Expand("{ak}\\x {map} {game}", paths));
    }

    [Fact]
    public void DefaultProjectRoundTrips()
    {
        var file = Path.Combine(_dir, "d.atlaswh3");
        BuildProject.CreateDefault("m").Save(file);
        var loaded = BuildProject.Load(file);
        Assert.Equal("m", loaded.Map);
        Assert.Equal(PackMode.New, loaded.Build.Pack.Mode);
        Assert.Equal(3, loaded.Build.Pack.Contents.Count);
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
