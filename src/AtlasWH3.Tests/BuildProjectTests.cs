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
              "assemblyKit": "X:\\old_kit",
              "build": { "output": "{project}\\out", "pack": { "output": "{out}\\{map}.pack" }, },
            }
            """);
        var project = BuildProject.Load(file);
        var paths = project.ToPaths(new ProjectPaths { GameDataDir = @"G:\data", AssemblyKitRoot = @"K:\kit" });
        Assert.Equal("my_map", paths.MapName);
        Assert.Equal(@"K:\kit", paths.AssemblyKitRoot);   // an older project's "assemblyKit" is ignored: Settings' kit
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
        Assert.Equal(@"{project}\{map}.pack", loaded.Build.PackFile);
        Assert.True(loaded.Build.WritePack);
        Assert.True(loaded.Build.BackupPack);
    }

    [Fact]
    public void DefaultProjectWritesIntoTheModPack()
    {
        var project = BuildProject.CreateDefault("cr_oldworld_map_1", modPacks: [@"G:\data\!cr_oldworld_campaign.pack"]);
        Assert.Equal(@"G:\data\!cr_oldworld_campaign.pack", project.Build.PackFile);
        Assert.Empty(project.ModPacks);   // the pack is read first anyway
    }

    [Fact]
    public void AnOlderProjectsPacksAndInstallBecomeItsPackFile()
    {
        var file = Path.Combine(_dir, "old.atlaswh3");
        File.WriteAllText(file, """
            {
              "map": "m",
              "modPacks": ["{game}\\!mod.pack", "{game}\\other.pack"],
              "build": {
                "customSteps": [{ "name": "after", "runAt": "afterInstall", "command": "x" }],
                "packs": [{ "mode": "merge", "base": "{game}\\!mod.pack", "output": "{project}\\!mod.pack",
                            "contents": [{ "source": "{compiled}" }], "exclude": [] }],
                "install": { "enabled": true, "backup": false }
              }
            }
            """);
        var project = BuildProject.Load(file);
        Assert.Equal(@"{game}\!mod.pack", project.Build.PackFile);   // where Install put it
        Assert.False(project.Build.BackupPack);
        Assert.True(project.Build.WritePack);
        Assert.Equal([@"{game}\other.pack"], project.ModPacks);
        Assert.Equal(CustomStepStage.AfterPack, Assert.Single(project.Build.CustomSteps).RunAt);

        File.WriteAllText(file, """
            { "map": "m", "build": { "packs": [{ "output": "{project}\\main.pack" }, { "output": "{project}\\devastate.pack" }],
                                     "install": { "enabled": true } } }
            """);
        project = BuildProject.Load(file);
        Assert.Equal(@"{game}\main.pack", project.Build.PackFile);   // Old World's two packs
        Assert.Equal(@"{game}\devastate.pack", project.Build.PiecesPackFile);

        File.WriteAllText(file, """{ "map": "m", "build": { "packs": [{ "enabled": false, "output": "{project}\\m.pack" }] } }""");
        project = BuildProject.Load(file);
        Assert.Equal(@"{project}\m.pack", project.Build.PackFile);   // not installed: its output
        Assert.False(project.Build.WritePack);
        project.Save();
        Assert.DoesNotContain("\"packs\"", File.ReadAllText(file));
    }

    [Fact]
    public void PackWritesTheCompiledFilesIntoThePackInPlace_AfterABackup()
    {
        var output = Path.Combine(_dir, "out");
        var paths = new ProjectPaths
        {
            MapName = "m", CacheRoot = Path.Combine(_dir, "cache"), OutputRoot = Path.Combine(_dir, "app"), GameDataDir = Path.Combine(_dir, "data"),
        };
        Write(output, "terrain/campaigns/m/a.bin", "a");
        Write(output, "terrain/campaigns/m/pieces/event_1/mask", "piece");
        Write(output, "terrain/campaigns/m_devastate_1/event_tiles", "tiles");
        Write(output, "terrain/campaigns/m_devastate_1/global_props_devastation_chaos.bin", "BOB's by-product");
        var manifestFile = BuildManifest.FileFor(paths, "m", output);
        var manifest = BuildManifest.Load(manifestFile, output);
        manifest.Set("heightmaps", [Path.Combine(output, @"terrain\campaigns\m\a.bin"), Path.Combine(paths.CacheRoot, "elsewhere.bin")]);
        manifest.Set("devastation_pieces", [Path.Combine(output, @"terrain\campaigns\m\pieces\event_1\mask"),
                                            Path.Combine(output, @"terrain\campaigns\m_devastate_1\event_tiles")]);
        manifest.Save(manifestFile);

        var src = Path.Combine(_dir, "src");
        Write(src, "db/keep", "the mod's DB");
        Write(src, "terrain/campaigns/m/a.bin", "old a");
        var target = Path.Combine(paths.GameDataDir, "!mod.pack");
        PackBuilder.New(target, PackBuilder.FromFolder(src));

        var project = new BuildProject { Map = "m" };
        project.Build.Output = output;
        project.Build.PackFile = target;
        var report = new BuildRunner(project, paths).Run(new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Pack } });

        Assert.True(report.Succeeded, string.Join("; ", report.Items.SelectMany(i => i.Problems)));
        var pack = PackFile.Open(target);
        Assert.Equal([@"db\keep", @"terrain\campaigns\m\a.bin", @"terrain\campaigns\m\pieces\event_1\mask", @"terrain\campaigns\m_devastate_1\event_tiles"],
                     pack.Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("a", System.Text.Encoding.UTF8.GetString(pack.TryRead("terrain/campaigns/m/a.bin")!));
        var backup = PackFile.Open(Path.Combine(paths.OutputRoot, "pack_backups", "!mod.pack"));
        Assert.Equal("old a", System.Text.Encoding.UTF8.GetString(backup.TryRead("terrain/campaigns/m/a.bin")!));
        Assert.False(File.Exists(target + ".tmp"));

        // no pack there yet: a new one with the build's files only
        project.Build.PackFile = Path.Combine(_dir, "new.pack");
        report = new BuildRunner(project, paths).Run(new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Pack } });
        Assert.True(report.Succeeded, string.Join("; ", report.Items.SelectMany(i => i.Problems)));
        Assert.Equal(3, PackFile.Open(project.Build.PackFile).Entries.Count);
    }

    [Fact]
    public void ThePiecesPackTakesBothFoldersPieces_TheMainPackTheRest()
    {
        var output = Path.Combine(_dir, "out");
        var paths = new ProjectPaths { MapName = "m", CacheRoot = Path.Combine(_dir, "cache"), OutputRoot = Path.Combine(_dir, "app") };
        Write(output, "terrain/campaigns/m/a.bin", "a");
        Write(output, "terrain/campaigns/m/event_tiles", "the map's event tiles: main pack");
        Write(output, "terrain/campaigns/m/pieces/event_1/mask", "piece");
        Write(output, "terrain/campaigns/m_devastate_1/event_tiles", "tiles");
        Write(output, "terrain/campaigns/m_devastate_1/pieces/event_1/mask", "devastated piece");
        Write(output, "campaign_maps/m/x.bin", "x");
        var manifestFile = BuildManifest.FileFor(paths, "m", output);
        var manifest = BuildManifest.Load(manifestFile, output);
        manifest.Set("heightmaps", [Path.Combine(output, @"terrain\campaigns\m\a.bin"), Path.Combine(output, @"campaign_maps\m\x.bin")]);
        manifest.Set("devastation_pieces", [Path.Combine(output, @"terrain\campaigns\m\event_tiles"), Path.Combine(output, @"terrain\campaigns\m\pieces"),
                                            Path.Combine(output, @"terrain\campaigns\m_devastate_1")],
                     [Path.Combine(output, @"terrain\campaigns\m\pieces\event_"), Path.Combine(output, @"terrain\campaigns\m_devastate_1\pieces\event_")]);
        manifest.Save(manifestFile);

        // the main pack still has an old piece: it is not the main pack's to drop
        var src = Path.Combine(_dir, "src");
        Write(src, "terrain/campaigns/m/pieces/event_9/mask", "left alone");
        Write(src, "db/keep", "keep");
        var main = Path.Combine(_dir, "main.pack");
        PackBuilder.New(main, PackBuilder.FromFolder(src));
        var src2 = Path.Combine(_dir, "src2");
        Write(src2, "terrain/campaigns/m_devastate_1/pieces/event_9/mask", "a deleted event area");
        var piecesPack = Path.Combine(_dir, "devastate.pack");
        PackBuilder.New(piecesPack, PackBuilder.FromFolder(src2));

        var project = new BuildProject { Map = "m" };
        project.Build.Output = output;
        project.Build.PackFile = main;
        project.Build.PiecesPackFile = piecesPack;
        var report = new BuildRunner(project, paths).Run(new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Pack } });

        Assert.True(report.Succeeded, string.Join("; ", report.Items.SelectMany(i => i.Problems)));
        Assert.Equal([@"campaign_maps\m\x.bin", @"db\keep", @"terrain\campaigns\m\a.bin", @"terrain\campaigns\m\event_tiles", @"terrain\campaigns\m\pieces\event_9\mask"],
                     PackFile.Open(main).Entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([@"terrain\campaigns\m\pieces\event_1\mask", @"terrain\campaigns\m_devastate_1\event_tiles", @"terrain\campaigns\m_devastate_1\pieces\event_1\mask"],
                     PackFile.Open(piecesPack).Entries.Keys.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(paths.OutputRoot, "pack_backups", "devastate.pack")));
    }

    [Fact]
    public void PackDropsTheStepsStaleFilesEvenWhenTheyWroteNone()
    {
        var output = Path.Combine(_dir, "out");
        var paths = new ProjectPaths { MapName = "m", CacheRoot = Path.Combine(_dir, "cache"), OutputRoot = Path.Combine(_dir, "app") };
        Write(output, "terrain/campaigns/m/a.bin", "a");
        var manifestFile = BuildManifest.FileFor(paths, "m", output);
        var manifest = BuildManifest.Load(manifestFile, output);
        manifest.Set("heightmaps", [Path.Combine(output, @"terrain\campaigns\m\a.bin")]);
        manifest.Set("rivers", [], [Path.Combine(output, @"terrain\campaigns\m\models\river_")]);   // every river deleted
        manifest.Set("devastation_pieces", [], [Path.Combine(output, @"terrain\campaigns\m\pieces\event_")]);
        manifest.Save(manifestFile);

        var src = Path.Combine(_dir, "src");
        Write(src, "terrain/campaigns/m/models/river_123.wsmodel", "deleted river");
        Write(src, "terrain/campaigns/m/models/river_123.wsmodel.rigid_model_v2", "deleted river");
        Write(src, "terrain/campaigns/m/models/hand_made.wsmodel", "not the build's");
        Write(src, "terrain/campaigns/m/pieces/event_ff0000/mask", "a deleted event area");
        Write(src, "db/keep", "keep");
        var target = Path.Combine(_dir, "mod.pack");
        PackBuilder.New(target, PackBuilder.FromFolder(src));

        var project = new BuildProject { Map = "m" };
        project.Build.Output = output;
        project.Build.PackFile = target;
        project.Build.BackupPack = false;
        var report = new BuildRunner(project, paths).Run(new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Pack } });

        Assert.True(report.Succeeded, string.Join("; ", report.Items.SelectMany(i => i.Problems)));
        Assert.Equal([@"db\keep", @"terrain\campaigns\m\a.bin", @"terrain\campaigns\m\models\hand_made.wsmodel"],
                     PackFile.Open(target).Entries.Keys.Order());
        Assert.False(Directory.Exists(Path.Combine(paths.OutputRoot, "pack_backups")));
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
    public void OutputDefaultsToAtlasWH3sFolder_AndTheOldWorkingDataDefaultIsDropped()
    {
        var paths = new ProjectPaths { MapName = "m", OutputRoot = Path.Combine(_dir, "output") };
        var project = BuildProject.CreateDefault("m");
        Assert.Equal(Path.Combine(_dir, "output", "compiled", "m"), project.OutputDir(paths));

        var file = Path.Combine(_dir, "old.atlaswh3");
        project.Build.Output = @"{ak}\working_data";   // what new projects used to write
        project.Save(file);
        Assert.Equal("", BuildProject.Load(file).Build.Output);
        project.Build.Output = "{project}\\out";        // a folder of its own is kept
        project.Save(file);
        Assert.Equal("{project}\\out", BuildProject.Load(file).Build.Output);
    }

    /// <summary>A project in its own folder over an empty kit with map m (its .terry only), output beside it.</summary>
    private (BuildProject Project, ProjectPaths Paths) CompileProject()
    {
        var kit = Path.Combine(_dir, "kit");
        Write(Path.Combine(kit, "raw_data", "terrain", "campaigns", "m"), "m.terry", "");
        var paths = new ProjectPaths
        {
            MapName = "m", AssemblyKitRoot = kit, GameDataDir = Path.Combine(_dir, "game"),
            OutputRoot = Path.Combine(_dir, "output"), CacheRoot = Path.Combine(_dir, "cache"),
        };
        var project = BuildProject.CreateDefault("m");
        project.Build.Output = "{project}\\out";
        Directory.CreateDirectory(Path.Combine(_dir, "project"));
        project.Save(Path.Combine(_dir, "project", "m.atlaswh3"));
        return (project, paths);
    }

    [Fact]
    public void MergeKeepsTheBasePacksTypeAndDependencies()
    {
        var basePack = Path.Combine(_dir, "base.pack");
        PackWriter.Write(basePack, [new PackWriter.Source("db/t/data", 1, s => s.WriteByte(7))],
                         new PackWriter.Header(4, ["!a_dependency.pack", "other.pack"]));   // a movie pack
        var update = Path.Combine(_dir, "update");
        Write(update, "x.bin", "x");
        var out1 = Path.Combine(_dir, "out.pack");
        Assert.True(PackBuilder.Merge(basePack, out1, PackBuilder.Collect([(update, "terrain")], m => Assert.Fail(m))).Verified);
        var pack = PackFile.Open(out1);
        Assert.Equal(4u, pack.PackType);
        Assert.Equal(["!a_dependency.pack", "other.pack"], pack.Dependencies);
        Assert.Equal([(byte)7], pack.TryRead("db/t/data")!);
        Assert.Equal("x", System.Text.Encoding.UTF8.GetString(pack.TryRead("terrain/x.bin")!));
        // a new pack is a mod pack without dependencies
        var fresh = PackFile.Open(PackBuilder.New(Path.Combine(_dir, "new.pack"), PackBuilder.FromFolder(update)).Pack);
        Assert.Equal((PackWriter.ModPack, 0), (fresh.PackType, fresh.Dependencies.Count));
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
