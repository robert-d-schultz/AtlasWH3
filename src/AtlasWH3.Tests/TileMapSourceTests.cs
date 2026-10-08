using AtlasWH3.Core;
using AtlasWH3.Core.Build;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Packs;
using Xunit;

namespace AtlasWH3.Tests;

public sealed class TileMapSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atlaswh3_tms_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ProjectPaths _paths;
    private readonly string _pack;
    private readonly byte[] _packed;

    public TileMapSourceTests()
    {
        _paths = new ProjectPaths { MapName = "test_map", AssemblyKitRoot = Path.Combine(_root, "kit"), OutputRoot = Path.Combine(_root, "out") };
        Directory.CreateDirectory(_root);
        var loose = Path.Combine(_root, "src.png");
        var map = HexTileMap.Create(4, 3);
        map.SetHex(1, 1, 0x96aa64);
        map.Write(loose);
        _packed = File.ReadAllBytes(loose);
        _pack = Path.Combine(_root, "my_map.pack");
        PackWriter.Write(_pack, [("terrain/campaigns/test_map/tile_map.png", loose)]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private TileMapSource PackSource(string save = "") => new() { Kind = TileMapSourceKind.Pack, Path = _pack, SaveFolder = save };

    [Fact]
    public void Kit_IsTheDefaultAndEditsInPlace()
    {
        var kit = Path.Combine(_paths.AkTerrainDir, "tile_map.png");
        Assert.Equal(Path.GetFullPath(kit), TileMapSource.Kit.EditPath(_paths), ignoreCase: true);
        Assert.False(TileMapSource.Kit.SeparateTarget(_paths));
    }

    [Fact]
    public void Pack_ReadsTheDefaultEntry_SavesToTheKitFolder()
    {
        var src = PackSource();
        Assert.Equal("terrain/campaigns/test_map/tile_map.png", src.PackEntry(_paths));
        Assert.Equal(_packed, src.Read(_paths));
        Assert.Equal(Path.GetFullPath(Path.Combine(_paths.AkTerrainDir, "tile_map.png")), src.EditPath(_paths), ignoreCase: true);
        Assert.True(src.SeparateTarget(_paths));
        Assert.Throws<FileNotFoundException>(() => (src with { InternalPath = "nope/tile_map.png" }).Read(_paths));
    }

    [Fact]
    public void File_FolderMeansItsTileMap_SaveFolderSeparates()
    {
        var dir = Path.Combine(_root, "maps");
        var f = new TileMapSource { Kind = TileMapSourceKind.File, Path = dir };
        Assert.Equal(Path.Combine(dir, "tile_map.png"), f.SourceFile(_paths), ignoreCase: true);
        Assert.False(f.SeparateTarget(_paths));
        Assert.True((f with { SaveFolder = Path.Combine(_root, "saved") }).SeparateTarget(_paths));
    }

    [Fact]
    public void Build_ExtractsThePackEntry_UntilTheEditorSeedsItsCopy()
    {
        var src = PackSource(Path.Combine(_root, "saved"));
        var p = _paths with { TileMap = src };
        var extract = Path.Combine(_root, "build", "_atlaswh3_inputs", "test_map");

        var input = src.BuildInput(p, extract, out var note);
        Assert.Equal(Path.Combine(extract, "tile_map.png"), input);
        Assert.Equal(_packed, File.ReadAllBytes(input));
        Assert.Contains("extracted", note);

        var editor = new TileMapEditor(p);
        Assert.Equal(src.EditPath(p), editor.TileMapPath);
        Assert.True(editor.NeedsSeed(out _));
        editor.SeedFromSource();
        Assert.Equal(_packed, File.ReadAllBytes(editor.TileMapPath));
        Assert.False(editor.NeedsSeed(out _));
        Assert.Equal(editor.TileMapPath, src.BuildInput(p, extract, out note));
        Assert.Contains("edited copy", note);
        Assert.Equal(FileJournal.Hash(_packed), FileJournal.Hash(PackFile.Open(_pack).TryRead("terrain/campaigns/test_map/tile_map.png")!));

        editor.Undo();   // the seed is one journal step
        Assert.False(File.Exists(editor.TileMapPath));
    }

    [Fact]
    public void Project_StoresTheSource_AndResolvesItsPaths()
    {
        var project = BuildProject.CreateDefault("test_map");
        project.TileMap = new TileMapSource { Kind = TileMapSourceKind.Pack, Path = "my_map.pack", SaveFolder = "{project}\\edits" };
        var file = Path.Combine(_root, "test" + BuildProject.Extension);
        project.Save(file);
        Assert.Contains("\"pack\"", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        var tm = BuildProject.Load(file).ToPaths(_paths).TileMap;
        Assert.Equal(TileMapSourceKind.Pack, tm.Kind);
        Assert.Equal(_pack, tm.Path, ignoreCase: true);
        Assert.Equal(Path.Combine(_root, "edits", "tile_map.png"), tm.EditPath(_paths), ignoreCase: true);
    }
}
