using System.Text.Json;
using System.Text.Json.Serialization;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>Where a campaign tile_map.png is read from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TileMapSourceKind>))]
public enum TileMapSourceKind { Kit, File, Pack }

/// <summary>
/// Where the tile map editor and the build's tile_list step read tile_map.png, and where the editor saves it.
/// <list type="bullet">
/// <item><b>Kit</b> (default): the kit's raw_data\terrain\campaigns\&lt;map&gt;\tile_map.png, edited in place.</item>
/// <item><b>File</b>: <see cref="Path"/> is a tile_map.png or a folder holding one. Edited in place unless
/// <see cref="SaveFolder"/> is set.</item>
/// <item><b>Pack</b>: <see cref="Path"/> is a .pack and <see cref="InternalPath"/> the file inside it (default
/// terrain/campaigns/&lt;map&gt;/tile_map.png). Packs are never written: edits go to a loose tile_map.png in
/// <see cref="SaveFolder"/> (default the kit's map folder).</item>
/// </list>
/// When the save target is not the source, the editor seeds it from the source (journaled, so undoable) and records
/// that in a marker next to its journal. The build reads the save target when it was seeded from this source, else the
/// source itself (a pack entry is extracted to the build output first).
/// Paths may use <c>{map}</c>.
/// </summary>
public sealed record TileMapSource
{
    public const string DefaultInternalPath = "terrain/campaigns/{map}/tile_map.png";
    public const string SeedMarker = "source.json";

    public TileMapSourceKind Kind { get; init; } = TileMapSourceKind.Kit;
    /// <summary>File: a tile_map.png or its folder. Pack: the .pack file.</summary>
    public string Path { get; init; } = "";
    /// <summary>Pack only: the file inside the pack; empty = <see cref="DefaultInternalPath"/>.</summary>
    public string InternalPath { get; init; } = "";
    /// <summary>Folder the editor saves tile_map.png to; empty = the kit's map folder (Pack) or the source itself (File).</summary>
    public string SaveFolder { get; init; } = "";

    public static readonly TileMapSource Kit = new();

    [JsonIgnore] public bool IsKit => Kind == TileMapSourceKind.Kit;

    private static string Expand(string value, ProjectPaths paths) => value.Replace("{map}", paths.MapName, StringComparison.OrdinalIgnoreCase);

    private static string KitFile(ProjectPaths paths) => System.IO.Path.GetFullPath(System.IO.Path.Combine(paths.AkTerrainDir, "tile_map.png"));

    /// <summary>The pack entry (Pack only), with <c>{map}</c> expanded.</summary>
    public string PackEntry(ProjectPaths paths) => Expand(InternalPath.Length > 0 ? InternalPath : DefaultInternalPath, paths);

    /// <summary>The loose source file (Kit / File); null for a pack.</summary>
    public string? SourceFile(ProjectPaths paths)
    {
        switch (Kind)
        {
            case TileMapSourceKind.Kit: return KitFile(paths);
            case TileMapSourceKind.File:
                var p = System.IO.Path.GetFullPath(Expand(Path, paths));
                return Directory.Exists(p) || !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.Combine(p, "tile_map.png") : p;
            default: return null;
        }
    }

    /// <summary>The loose file the editor writes.</summary>
    public string EditPath(ProjectPaths paths)
    {
        if (SaveFolder.Length > 0 && !IsKit) return System.IO.Path.GetFullPath(System.IO.Path.Combine(Expand(SaveFolder, paths), "tile_map.png"));
        return SourceFile(paths) ?? KitFile(paths);
    }

    /// <summary>True when edits go to another file than the source (the editor seeds it from the source).</summary>
    public bool SeparateTarget(ProjectPaths paths) =>
        SourceFile(paths) is not { } src || !src.Equals(EditPath(paths), StringComparison.OrdinalIgnoreCase);

    /// <summary>The source's bytes: the loose file, or the pack entry.</summary>
    public byte[] Read(ProjectPaths paths)
    {
        if (SourceFile(paths) is { } file)
            return File.Exists(file) ? File.ReadAllBytes(file) : throw new FileNotFoundException("tile map not found", file);
        var pack = System.IO.Path.GetFullPath(Expand(Path, paths));
        if (!File.Exists(pack)) throw new FileNotFoundException("pack not found", pack);
        var entry = PackEntry(paths);
        return PackFile.Open(pack).TryRead(entry) ?? throw new FileNotFoundException($"{entry} is not in {pack}", pack);
    }

    /// <summary>One line for logs and the UI.</summary>
    public string Describe(ProjectPaths paths) => Kind switch
    {
        TileMapSourceKind.Kit => $"kit: {KitFile(paths)}",
        TileMapSourceKind.File => $"file: {SourceFile(paths)}",
        _ => $"pack: {System.IO.Path.GetFullPath(Expand(Path, paths))} > {PackEntry(paths)}",
    };

    /// <summary>The tokens and relative paths resolved by <paramref name="resolve"/> (a project's own path rules).</summary>
    public TileMapSource ResolvePaths(Func<string, string> resolve) => this with
    {
        Path = Path.Length > 0 ? resolve(Path) : "",
        SaveFolder = SaveFolder.Length > 0 ? resolve(SaveFolder) : "",
    };

    // ---------------------------------------------------------------- seed marker

    private sealed record Marker(string Source, string Hash);

    /// <summary>Records that the save target in <paramref name="journalDir"/> was seeded from this source.</summary>
    public void WriteSeedMarker(string journalDir, ProjectPaths paths, byte[] seeded)
    {
        Directory.CreateDirectory(journalDir);
        File.WriteAllText(System.IO.Path.Combine(journalDir, SeedMarker),
            JsonSerializer.Serialize(new Marker(Describe(paths), Editing.FileJournal.Hash(seeded))));
    }

    /// <summary>Whether the save target was seeded from this source (so it holds this source plus edits).</summary>
    public bool SeededFromThis(string journalDir, ProjectPaths paths)
    {
        var path = System.IO.Path.Combine(journalDir, SeedMarker);
        if (!File.Exists(path)) return false;
        try { return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path))?.Source == Describe(paths); }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// The tile_map.png file the build reads: the kit / file source; the editor's save target when it was seeded from
    /// this source; else the pack entry extracted to <paramref name="extractDir"/>. <paramref name="note"/> says which.
    /// </summary>
    public string BuildInput(ProjectPaths paths, string extractDir, out string note)
    {
        var target = EditPath(paths);
        if (!SeparateTarget(paths))
        {
            note = $"tile map: {Describe(paths)}";
            return target;
        }
        if (File.Exists(target) && SeededFromThis(TileMapEditor.JournalDirFor(paths, target), paths))
        {
            note = $"tile map: edited copy {target} (seeded from {Describe(paths)})";
            return target;
        }
        if (SourceFile(paths) is { } file)
        {
            note = $"tile map: {Describe(paths)}";
            return file;
        }
        var bytes = Read(paths);
        Directory.CreateDirectory(extractDir);
        var outPath = System.IO.Path.Combine(extractDir, "tile_map.png");
        File.WriteAllBytes(outPath, bytes);
        note = $"tile map: extracted {PackEntry(paths)} from {System.IO.Path.GetFileName(Expand(Path, paths))} to {outPath}";
        return outPath;
    }
}
