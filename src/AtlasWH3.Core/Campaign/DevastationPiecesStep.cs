using System.Diagnostics;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// terrain\campaigns\&lt;map&gt;\pieces\event_&lt;colour&gt;\ plus the map's event_tiles and event_trees (BOB "Devastation
/// pieces", bob_terrain ACTION_PROCESS_TERRAIN_DEVASTATION_PIECES), from this build's compiled files (else the kit's
/// working_data). Read off bob_terrain (0x180011f00 tiles, 0x180013460 trees, 0x18000e430 area lookup) and measured on
/// every piece of the IEE and Old World packs and a fresh BOB run:
///  - a piece per area on the EventAreaMask map (its tight box), but for black, the "_empty" area; the folder is the
///    area's palette colour (<c>pieces/event_%06x/</c>). The pieces folder is emptied first: BOB never cleans it;
///  - the nine textures as raw crops with texture_info and mask (<see cref="EventPieces"/>): byte-identical;
///  - tile_list: the tile_list.bin records whose path contains "road" and whose footprint centre, rounded half up, is
///    in the area, in record order; event_tiles is every road path with a record on the mask. BOB numbers that list in
///    the order of a hash map keyed by the tile's address (not reproducible); this step uses the path table's order;
///  - tree_list: the trees whose position, rounded to the nearest event-mask pixel (x · W / world_width, z / 1.15476
///    the same way, float), is in the area, by type then instance; event_trees is every type of the list. BOB
///    regenerates the tree list from the kit's database for this (types it does not know are dropped, its heights are
///    the logic map's); the step takes the trees step's list, which is the same list with the mods' types;
///  - objects / bmd_objects_sound (+ _&lt;bmd_export_type&gt;, .bin + .culture): the project's layer objects whose
///    position, mapped as the trees', is in the area, flattened by the global_props builder
///    (<see cref="Wh3GlobalPropsBuilder.BuildPieces"/>; rivers and light probes stay map-wide). BOB's record order
///    changes from run to run (two runs of the same IEE sources: 268 files differ in order alone), so parity is content;
///  - rivers: the river_lava rivers whose position is in the area (<see cref="Wh3GlobalPropsBuilder.BuildPieceRivers"/>);
///  - event_vfx (the main map's folder): <see cref="EventVfx"/>.
/// The devastated project (&lt;map&gt;_devastate_1 in raw_data, <see cref="CampaignBuildContext.DevastatedMap"/>) is an input,
/// not a second campaign: its heightmaps, tile list, trees, global map and masks are built into the cache (no fake
/// campaign_maps folder; map_data.esf is the main map's; full_height_map only where the pieces cut it), and its pieces,
/// event_tiles, event_trees and event_area_mask.dds go into terrain\campaigns\&lt;map&gt;_devastate_1, which is all that
/// folder ships besides environment_collection.xml (<see cref="EnvironmentStep"/>).
/// lf_normal.dds (NVTT) is not native yet: it is cut from working_data's.
/// </summary>
public sealed class DevastationPiecesStep : ICampaignBuildStep
{
    public string Name => "devastation_pieces";
    public string ReplacesBobAction => "Devastation pieces";
    public IReadOnlyList<string> DependsOn => ["heightmaps", "tile_list", "trees", "global_map", "masks"];

    /// <summary>Tree z is scaled by this before the event mask lookup (hex rows; as the tree heights' logic map lookup).</summary>
    public const float TreeZScale = 1.15476f;

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        if (!File.Exists(ctx.TerryFile)) return [$"missing {ctx.TerryFile}"];
        var project = TerryProject.Load(ctx.TerryFile);
        if (project.Find("EventAreaMask") is not { } map) return ["no EventAreaMask map in the .terry"];
        try { TerrainComposite.Inputs(project, map); }
        catch (FileNotFoundException e) { return [e.Message]; }
        return [];
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var written = new List<string>();
        CampaignTileDatabase? db = null;
        CampaignTileDatabase TileDb() => db ??= TileMapValidator.LoadDatabase(ctx.Paths);
        PackSet? packs = null;
        PackSet Packs() => packs ??= GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks);

        string? Compiled(string root, string relative) =>
            new[] { Path.Combine(root, relative), Path.Combine(ctx.Paths.AkWorkingDir, relative) }.FirstOrDefault(File.Exists);
        string? Terrain(string root, string map, string relative) => Compiled(root, Path.Combine("terrain", "campaigns", map, relative));

        var main = TerryProject.Load(ctx.TerryFile);
        var mainObjects = Objects(ctx, main, ctx.MapName, Packs, notes);
        Write(main, rel => Terrain(ctx.TargetRoot, ctx.MapName, rel),
              Compiled(ctx.TargetRoot, TreeExporter.PackPath(ctx.MapName)), ctx.TerrainOutDir, TileDb, ctx, written, notes,
              mainObjects);

        if (DevastatedMapName(ctx) is not { } devastated)
            notes.Add($"no devastated project ({TerryFileOf(ctx, DevastatedName(ctx.MapName))}): only the main map's pieces");
        else
        {
            // the devastated project's rasters, tile list and trees, built where they do not ship (BOB writes them into
            // the fake campaign's working_data); full_height_map.dds only where the pieces cut it
            var project = TerryProject.Load(TerryFileOf(ctx, devastated));
            var build = Path.Combine(ctx.Paths.CacheRoot, "devastated", devastated);
            var sub = new CampaignBuildContext(ctx.Paths with { MapName = devastated, TileMap = TileMapSource.Kit }, build,
                                               m => ctx.Log($"{devastated}: {m}"))
            {
                Cancel = ctx.Cancel, AcceptedTileMapIssues = ctx.AcceptedTileMapIssues, PatchMask = ctx.PatchMask,
                CampaignMapName = ctx.MapName, HeightMapBlocks = HeightMapBlocks(project),
            };
            ctx.Log($"building {devastated} (cache {build})...");
            foreach (var o in new CampaignBuildPipeline().Run(sub, ["heightmaps", "tile_list", "trees", "global_map", "masks"]))
                if (o.Status != "ok") notes.Add($"{devastated} {o.Step}: {o.Status}: {string.Join("; ", o.Problems)}");

            // lf_normal.dds is BOB's (NVTT, not native): the devastated map's in working_data, else the main map's (the
            // same in vanilla's pieces)
            string? Source(string rel) => Terrain(build, devastated, rel)
                ?? (rel == "lf_normal.dds" ? Terrain(ctx.TargetRoot, ctx.MapName, rel) : null);
            var outDir = Path.Combine(ctx.TargetRoot, "terrain", "campaigns", devastated);
            var devastatedObjects = Objects(ctx, project, devastated, Packs, notes);
            var pieceAt = Write(project, Source, Compiled(build, TreeExporter.PackPath(devastated)), outDir, TileDb, ctx, written, notes,
                                devastatedObjects);
            if (mainObjects is not null && devastatedObjects is not null && pieceAt is not null)
            {
                var lines = EventVfx(devastatedObjects.PieceVfx(pieceAt), mainObjects.MapWideVfx());
                var path = ctx.OutFile("event_vfx");
                File.WriteAllBytes(path, EventPieces.Lines(lines));
                written.Add(path);
                notes.Add($"event_vfx: {lines.Count} effects the devastated pieces bring that no map-wide object uses");
            }
            if (Terrain(build, devastated, "event_area_mask.dds") is { } mask)
            {
                var path = Path.Combine(outDir, "event_area_mask.dds");
                File.Copy(mask, path, true);
                written.Add(path);
            }
        }
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>A project's layer objects, for the pieces' objects / bmd_objects_sound files (the global_props builder;
    /// null, with a note, when the game data is missing).</summary>
    private static Wh3GlobalPropsBuilder? Objects(CampaignBuildContext ctx, TerryProject project, string mapName, Func<PackSet> packs,
                                                  List<string> notes)
    {
        if (!Directory.Exists(ctx.Paths.GameDataDir))
        {
            notes.Add($"{mapName}: no game data folder {ctx.Paths.GameDataDir}: no objects in the pieces");
            return null;
        }
        var builder = new Wh3GlobalPropsBuilder(packs(), PrefabLibrary.ForKit(ctx.Paths.AssemblyKitRoot, "campaign"),
            Wh3GlobalPropsBuilder.ReadCultures(ctx.Paths.AssemblyKitRoot), mapName, (0, 0, 0, 0), (_, _) => "");
        var layers = project.Layers().Where(l => l.IsFile && l.Export && l.FilePath is { } f && File.Exists(f)).ToList();
        ctx.Log($"{mapName}: objects of {layers.Count} exported layers...");
        foreach (var layer in layers)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            builder.AddLayer(layer.FilePath!);
        }
        notes.AddRange(builder.Notes.Distinct().Select(n => $"{mapName} objects: {n}"));
        return builder;
    }

    /// <summary>
    /// event_vfx, the effects the game preloads at campaign start so an area's swap does not stall on them (it loads each
    /// name and drops the result; a missing file skips that). Nothing in the kit writes it and CA's list is hand-kept
    /// (WH3_visual_map_decompiler docs/event-area-pieces.md; IEE ships vanilla's copy). Here: per bmd_export_type
    /// (ordinal, the untyped files last, as vanilla's chaos / nagash / skaven groups), the effects in the devastated
    /// pieces that no global_props.bin object uses and no earlier group listed, ordinal.
    /// </summary>
    public static List<string> EventVfx(IReadOnlyDictionary<string, SortedSet<string>> pieceVfx, IReadOnlySet<string> mapWide)
    {
        var seen = new HashSet<string>(mapWide, StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var type in pieceVfx.Keys.OrderBy(t => t.Length == 0).ThenBy(t => t, StringComparer.Ordinal))
            foreach (var name in pieceVfx[type])
                if (name.Length > 0 && seen.Add(name)) lines.Add(name);
        return lines;
    }

    /// <summary>The devastated project: <see cref="CampaignBuildContext.DevastatedMap"/>, else
    /// &lt;map without _1&gt;_devastate_1 when its .terry exists.</summary>
    public static string? DevastatedMapName(CampaignBuildContext ctx)
    {
        if (ctx.DevastatedMap is { } set) return set.Length == 0 ? null : set;
        var name = DevastatedName(ctx.MapName);
        return File.Exists(TerryFileOf(ctx, name)) ? name : null;
    }

    private static string DevastatedName(string map) => (map.EndsWith("_1", StringComparison.Ordinal) ? map[..^2] : map) + "_devastate_1";

    public static string TerryFileOf(CampaignBuildContext ctx, string map) =>
        Path.Combine(ctx.Paths.AssemblyKitRoot, "raw_data", "terrain", "campaigns", map, map + ".terry");

    /// <summary>The full_height_map.dds blocks a project's pieces cut, as linear block indices (a crop past the right
    /// edge runs on into the next row, as <see cref="EventPieces.Crop"/> reads it).</summary>
    private static Func<int, int, bool> HeightMapBlocks(TerryProject project)
    {
        var (w, h) = project.Find("Height")?.Size ?? throw new InvalidDataException("no Height map in the .terry");
        var (mask, palette) = TerrainComposite.Indexed(project, "EventAreaMask");
        var layout = new EventPieces.TextureLayout(w, h, 4, 16, 1, 148);
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        var marked = new bool[bw * bh];
        foreach (var (_, box) in Areas(mask.Data, mask.Width, palette))
        {
            var top = EventPieces.Layout("full_height_map.dds", true, layout, mask.Width, mask.Height, box).Mips[0];
            for (var r = 0; r < top.BlocksH; r++)
            {
                var start = (top.SrcY / 4 + r) * bw + top.SrcX / 4;
                for (var c = start; c < start + top.BlocksW && c < marked.Length; c++) marked[c] = true;
            }
        }
        return (x, y) => marked[y * bw + x];
    }

    /// <summary>The areas that get a piece: every index on the event mask but black (the "_empty" area), with its box.</summary>
    private static IEnumerable<(byte Index, PieceBox Box)> Areas(byte[] mask, int width, TiffMap.Palette? palette)
    {
        if (palette is null) throw new InvalidDataException("the EventAreaMask map has no palette");
        return EventPieces.Boxes(mask, width).Where(b => Colour(palette, b.Key) != 0).Select(b => (b.Key, b.Value));
    }

    /// <summary>Writes one map folder's pieces, event_tiles and event_trees.</summary>
    /// <param name="compiled">A compiled file of the map by its path under the map folder, or null when there is none.</param>
    /// <param name="treeList">trees.campaign_tree_list, or null.</param>
    /// <param name="objects">The project's layer objects for the objects / bmd_objects_sound files, or null.</param>
    /// <returns>The area lookup the objects were placed with (world x, z to a piece; -1 for none), or null without
    /// objects.</returns>
    public static Func<float, float, int>? Write(TerryProject project, Func<string, string?> compiled, string? treeList, string mapOutDir,
                             Func<CampaignTileDatabase> tileDb, CampaignBuildContext ctx, List<string> written, List<string> notes,
                             Wh3GlobalPropsBuilder? objects = null)
    {
        ctx.Log("event areas...");
        var (mask, palette) = TerrainComposite.Indexed(project, "EventAreaMask");
        int maskW = mask.Width, maskH = mask.Height;
        var pieces = Areas(mask.Data, maskW, palette)
            .Select(a => (a.Index, a.Box, Folder: Path.Combine(mapOutDir, "pieces", $"event_{Colour(palette!, a.Index):x6}")))
            .OrderBy(p => p.Folder, StringComparer.Ordinal).ToList();
        var slot = new Dictionary<byte, int>();
        for (var i = 0; i < pieces.Count; i++) slot[pieces[i].Index] = i;
        notes.Add($"{pieces.Count} event areas on the {maskW}x{maskH} event mask");

        var piecesDir = Path.Combine(mapOutDir, "pieces");
        if (Directory.Exists(piecesDir))
            foreach (var old in Directory.GetDirectories(piecesDir, "event_*")) Directory.Delete(old, true);
        foreach (var p in pieces)
        {
            Directory.CreateDirectory(p.Folder);
            File.WriteAllBytes(Path.Combine(p.Folder, "mask"), EventPieces.Mask(mask.Data, maskW, p.Box, p.Index));
        }

        // textures, one map texture at a time (Old World's are a few hundred MB each)
        var layouts = pieces.Select(_ => new List<PieceTexture>()).ToList();
        foreach (var (name, mapPath, flipped) in EventPieces.Textures)
        {
            if (compiled(mapPath) is not { } source) { notes.Add($"no {mapPath} (this build or working_data): not in the pieces"); continue; }
            ctx.Log($"{name}...");
            var bytes = File.ReadAllBytes(source);
            var layout = EventPieces.TextureLayout.Of(bytes);
            if (!source.StartsWith(ctx.TargetRoot, StringComparison.OrdinalIgnoreCase)) notes.Add($"{name} cut from {source}");
            Parallel.For(0, pieces.Count, new ParallelOptions { CancellationToken = ctx.Cancel }, i =>
            {
                var texture = EventPieces.Layout(name, flipped, layout, maskW, maskH, pieces[i].Box);
                File.WriteAllBytes(Path.Combine(pieces[i].Folder, name), EventPieces.Crop(bytes, layout, texture));
                lock (layouts[i]) layouts[i].Add(texture);
            });
        }
        var order = EventPieces.Textures.Select(t => t.Name).ToList();
        for (var i = 0; i < pieces.Count; i++)
            File.WriteAllText(Path.Combine(pieces[i].Folder, "texture_info"),
                EventPieces.TextureInfo(maskW, maskH, pieces[i].Box, layouts[i].OrderBy(t => order.IndexOf(t.Name))));

        // roads
        if (compiled("tile_list.bin") is { } tileListPath)
        {
            ctx.Log("road tiles...");
            var (tiles, eventTiles) = Roads(TileList.Read(tileListPath), tileDb(), mask.Data, maskW, maskH, slot, pieces.Count, notes);
            for (var i = 0; i < pieces.Count; i++)
                if (tiles[i].Count > 0) File.WriteAllBytes(Path.Combine(pieces[i].Folder, "tile_list"), EventPieces.TileList(tiles[i]));
            var path = Path.Combine(mapOutDir, "event_tiles");
            File.WriteAllBytes(path, EventPieces.Lines(eventTiles));
            written.Add(path);
            notes.Add($"{tiles.Sum(t => t.Count)} road tiles in {tiles.Count(t => t.Count > 0)} pieces, {eventTiles.Count} road tiles in event_tiles");
        }
        else notes.Add("no tile_list.bin (this build or working_data): no tile_list in the pieces, no event_tiles");

        // trees
        if (treeList is not null)
        {
            ctx.Log("trees...");
            var list = CampaignTreeList.Load(treeList);
            var trees = Trees(list, project.WorldWidth ?? list.WorldWidth, mask.Data, maskW, maskH, slot, pieces.Count);
            for (var i = 0; i < pieces.Count; i++)
                if (trees[i].Count > 0) File.WriteAllBytes(Path.Combine(pieces[i].Folder, "tree_list"), EventPieces.TreeList(trees[i]));
            var path = Path.Combine(mapOutDir, "event_trees");
            File.WriteAllBytes(path, EventPieces.Lines(list.Types.Select(t => t.Name)));
            written.Add(path);
            notes.Add($"{trees.Sum(t => t.Count)} trees in {trees.Count(t => t.Count > 0)} pieces");
        }
        else notes.Add("no trees.campaign_tree_list (this build or working_data): no tree_list in the pieces, no event_trees");

        // objects and sounds
        Func<float, float, int>? pieceAt = null;
        if (objects is not null)
        {
            ctx.Log("objects...");
            var worldWidth = project.WorldWidth ?? throw new InvalidDataException("the .terry has no world_width");
            var sx = maskW / worldWidth;
            var sz = maskW / (worldWidth * TreeZScale);
            int PieceAt(float x, float z)
            {
                var px = RoundAway(x * sx);
                var row = maskH - RoundAway(z * sz) - 1;
                if (px < 0 || px >= maskW || row < 0 || row >= maskH) return -1;
                return slot.TryGetValue(mask.Data[row * maskW + px], out var s) ? s : -1;
            }
            pieceAt = PieceAt;
            var files = objects.BuildPieces(PieceAt, pieces.Count);
            for (var i = 0; i < pieces.Count; i++)
                foreach (var f in files[i])
                {
                    File.WriteAllBytes(Path.Combine(pieces[i].Folder, f.Stem + ".bin"), f.Bin);
                    File.WriteAllBytes(Path.Combine(pieces[i].Folder, f.Stem + ".culture"), f.Culture);
                }
            notes.Add($"{files.Sum(f => f.Count)} objects / sound files in {pieces.Count} pieces");
            var rivers = objects.BuildPieceRivers(PieceAt, pieces.Count);
            for (var i = 0; i < pieces.Count; i++)
                if (rivers[i] is { } bytes) File.WriteAllBytes(Path.Combine(pieces[i].Folder, "rivers"), bytes);
            notes.Add($"{rivers.Sum(r => (r?.Length ?? 0) / 16)} river_lava rivers in {rivers.Count(r => r is not null)} pieces' rivers files");
        }
        written.AddRange(pieces.Select(p => p.Folder));
        return pieceAt;
    }

    private static uint Colour(TiffMap.Palette palette, byte index) =>
        (uint)((palette.R[index] >> 8) << 16 | (palette.G[index] >> 8) << 8 | palette.B[index] >> 8);

    /// <summary>
    /// Road tiles per piece and the event_tiles list. Per record (bob_terrain 0x1800120f3): w × h the tile's footprint
    /// (swapped when rotated 0x20 / 0x80); the event-mask pixel ((x + w · 0.5) · sx + 0.5, (y + h · 0.5) · sy + 0.5),
    /// truncated, rows counted from the south; a path containing "road" (strstr) on a pixel that is not 255 goes into
    /// event_tiles, and into its area's piece when the area has one.
    /// </summary>
    public static (List<PieceTile>[] Pieces, List<string> EventTiles) Roads(TileList list, CampaignTileDatabase db, byte[] mask,
        int maskW, int maskH, IReadOnlyDictionary<byte, int> slot, int pieceCount, List<string> notes)
    {
        var sizes = new Dictionary<string, (int W, int H)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in db.Tiles)
            foreach (var v in t.Variations) sizes.TryAdd(v.Location.TrimEnd('\\'), (t.Width, t.Height));
        int tileW = list.Ints[1], tileH = list.Ints[2];
        float sx = (float)maskW / tileW, sy = (float)maskH / tileH;

        var pieces = Enumerable.Range(0, pieceCount).Select(_ => new List<(int Path, PieceTile Tile)>()).ToArray();
        var used = new SortedSet<int>();
        var unknown = new HashSet<string>();
        foreach (var r in list.Records)
        {
            var path = list.Paths[(int)r.Path];
            if (!path.Contains("road", StringComparison.Ordinal)) continue;
            if (!sizes.TryGetValue(path.TrimEnd('\\'), out var size)) { unknown.Add(path); continue; }
            var (w, h) = (r.Orientation & 0xF0) is 0x20 or 0x80 ? (size.H, size.W) : size;
            var cx = (int)((w * 0.5f + r.X) * sx + 0.5f);
            var cy = (int)((h * 0.5f + r.Y) * sy + 0.5f);
            var row = maskH - cy - 1;
            if (cx < 0 || cx >= maskW || row < 0 || row >= maskH) continue;
            var area = mask[row * maskW + cx];
            if (area == 255) continue;
            used.Add((int)r.Path);
            if (slot.TryGetValue(area, out var s)) pieces[s].Add(((int)r.Path, new PieceTile(r.X, r.Y, 0)));
        }
        if (unknown.Count > 0) notes.Add($"{unknown.Count} road tiles of tile_list.bin are not in the tile database, left out: {string.Join(", ", unknown.Take(5))}");
        var line = new Dictionary<int, uint>();
        foreach (var p in used) line[p] = (uint)line.Count;
        return (pieces.Select(l => l.Select(t => t.Tile with { Index = line[t.Path] }).ToList()).ToArray(),
                used.Select(p => list.Paths[p]).ToList());
    }

    /// <summary>
    /// Trees per piece (bob_terrain 0x180013460 and its lookup 0x18000e430): the event-mask pixel (x · sx, z · sz), each
    /// rounded half away from zero in float, rows counted from the south, with sx = W / world_width and sz =
    /// W / (world_width · 1.15476); trees of an area with a piece go into it, type by type, in the list's order.
    /// </summary>
    public static List<PieceTree>[] Trees(CampaignTreeList list, float worldWidth, byte[] mask, int maskW, int maskH,
                                          IReadOnlyDictionary<byte, int> slot, int pieceCount)
    {
        // in float: Old World has a tree at z · sz = 1068.5 exactly, which the double quotient puts at 1068.4999
        var sx = maskW / worldWidth;
        var sz = maskW / (worldWidth * TreeZScale);
        var pieces = Enumerable.Range(0, pieceCount).Select(_ => new List<PieceTree>()).ToArray();
        for (var t = 0; t < list.Types.Count; t++)
            foreach (var i in list.Types[t].Instances)
            {
                var px = RoundAway(i.X * sx);
                var row = maskH - RoundAway(i.Z * sz) - 1;
                if (px < 0 || px >= maskW || row < 0 || row >= maskH) continue;
                if (slot.TryGetValue(mask[row * maskW + px], out var s))
                    pieces[s].Add(new PieceTree(i.X, i.Y, i.Z, i.Flag, i.Variant, (ushort)t));
            }
        return pieces;
    }

    private static int RoundAway(float v) => (int)(v > 0 ? v + 0.5f : v - 0.5f);
}
