using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core.Campaign.GlobalMesh;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>
/// Validated editing of a campaign tile_map.png (default: the kit's raw_data\terrain\campaigns\&lt;map&gt;\tile_map.png;
/// <see cref="TileMapSource"/> can read it from another file or a pack and save to a chosen folder).
/// A batch of <see cref="TileMapOps"/> ops is applied in memory, the changed hexes (+ ring) are checked with
/// <see cref="TileMapValidator.CheckMap"/>, and only issues the edit introduced count: any new error blocks the write
/// unless forced. Written batches are journaled (<see cref="FileJournal"/>, output\tile_edits\&lt;map&gt;) with their ops
/// in ops.jsonl, so they can be undone, rolled back to a checkpoint, or replayed onto a regenerated tile map.
/// BOB's Terrain / Tilemap must run again before tile_list.bin reflects the edits.
/// </summary>
public sealed class TileMapEditor
{
    public sealed record OpsRecord(int Seq, DateTime Time, string Label, JsonArray Ops, List<int[]>? Hexes = null);

    private readonly ProjectPaths _paths;
    private CampaignTileDatabase? _db;

    public TileMapEditor(ProjectPaths paths, string? tileMapPath = null, CampaignTileDatabase? db = null)
    {
        _paths = paths;
        _db = db;
        Source = tileMapPath is null ? paths.TileMap : new TileMapSource { Kind = TileMapSourceKind.File, Path = tileMapPath };
        TileMapPath = Path.GetFullPath(tileMapPath ?? Source.EditPath(paths));
        Journal = new FileJournal(JournalDirFor(paths, TileMapPath));
    }

    /// <summary>output\tile_edits\&lt;map&gt;, plus custom_&lt;hash&gt; for any tile map other than the kit's: one journal per
    /// save target.</summary>
    public static string JournalDirFor(ProjectPaths paths, string tileMapPath)
    {
        var dir = FileJournal.EditDir(paths, "tile_edits");
        var full = Path.GetFullPath(tileMapPath);
        if (!full.Equals(Path.GetFullPath(Path.Combine(paths.AkTerrainDir, "tile_map.png")), StringComparison.OrdinalIgnoreCase))
            dir = Path.Combine(dir, "custom_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant());
        return dir;
    }

    /// <summary>Where the tile map is read from (<see cref="ProjectPaths.TileMap"/>, or a file passed in).</summary>
    public TileMapSource Source { get; }

    /// <summary>True when the save target needs seeding from the source: it is missing, or it was not seeded from this
    /// source and differs from it. Always false when the source is the save target.</summary>
    public bool NeedsSeed(out string reason)
    {
        reason = "";
        if (!Source.SeparateTarget(_paths)) return false;
        if (!File.Exists(TileMapPath)) { reason = $"{TileMapPath} does not exist yet"; return true; }
        if (Source.SeededFromThis(Journal.Dir, _paths)) return false;
        var same = FileJournal.Hash(Source.Read(_paths)) == FileJournal.Hash(File.ReadAllBytes(TileMapPath));
        if (same) { Source.WriteSeedMarker(Journal.Dir, _paths, File.ReadAllBytes(TileMapPath)); return false; }
        reason = $"{TileMapPath} differs from {Source.Describe(_paths)}";
        return true;
    }

    /// <summary>Copies the source (e.g. a pack entry) to the save target as one journaled batch (undoable) and records
    /// the seed. The pack is only read.</summary>
    public int SeedFromSource()
    {
        var bytes = Source.Read(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(TileMapPath)!);
        var seq = Journal.Commit([(TileMapPath, p => File.WriteAllBytes(p, bytes))], "load " + Source.Describe(_paths));
        try { Load(); }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Journal.Undo(1, force: true);
            throw;
        }
        Source.WriteSeedMarker(Journal.Dir, _paths, bytes);
        return seq;
    }

    public string TileMapPath { get; }
    public FileJournal Journal { get; }
    public CampaignTileDatabase Database => _db ??= TileMapValidator.LoadDatabase(_paths);
    private string OpsPath => Path.Combine(Journal.Dir, "ops.jsonl");

    public HexTileMap Load()
    {
        if (!File.Exists(TileMapPath)) throw new FileNotFoundException("tile map not found", TileMapPath);
        var map = HexTileMap.Read(TileMapPath);
        if (!map.HasHexLayout) throw new InvalidDataException($"{TileMapPath} is {map.PixelWidth}x{map.PixelHeight}, not a 2W x (2H+1) hex layout");
        return map;
    }

    // ---------------------------------------------------------------- coordinates

    /// <summary>World (x, z) of a hex centre: tile-map pixel × the campaign tile size, z = 0 south.</summary>
    public static (double X, double Z) HexToWorld(int col, int row) =>
        ((2 * col + 1) * GlobalMeshStep.TileSize, (2 * row + (col & 1) + 1) * GlobalMeshStep.TileSize);

    /// <summary>The hex whose 2×2 pixel block holds world (x, z).</summary>
    public static (int Col, int Row) WorldToHex(double x, double z)
    {
        var col = (int)Math.Floor(x / GlobalMeshStep.TileSize / 2);
        var py = z / GlobalMeshStep.TileSize - (col & 1);
        return (col, (int)Math.Floor(py / 2));
    }

    // ---------------------------------------------------------------- edit

    public sealed record EditResult(bool Written, int Seq, JsonArray OpResults, IReadOnlyList<(int Col, int Row)> Changed,
                                    IReadOnlyList<TileMapFinding> NewIssues, int ExistingIssues, HexTileMap Map)
    {
        public int NewErrors => NewIssues.Count(f => f.Severity == TileMapFinding.Error);
        public int Blocking => NewIssues.Count(f => Blocks(f, false));
    }

    /// <summary>Whether a new issue stops the write. The validator's severities are calibrated for whole maps (CA's own
    /// map has thick lines and unseen patterns, so those are warnings); an edit that newly causes one is still a likely
    /// hole, so new rule warnings block too unless <paramref name="allowWarnings"/>. Unused tile sets never block.</summary>
    public static bool Blocks(TileMapFinding f, bool allowWarnings) =>
        f.Severity == TileMapFinding.Error || !allowWarnings && f.Severity == TileMapFinding.Warning && f.Code != "palette.unused_set";

    /// <summary>Applies the ops. Writes when the edit adds no blocking issue (see <see cref="Blocks"/>; or
    /// <paramref name="force"/>), unless <paramref name="dryRun"/>. Nothing is written if any op throws.</summary>
    public EditResult Edit(JsonArray ops, string? label = null, bool dryRun = false, bool force = false, HexTileMap? map = null,
                           bool allowWarnings = false)
    {
        map ??= Load();
        var before = Clone(map);
        var tileOps = new TileMapOps(map, Database);
        var results = new JsonArray();
        foreach (var node in ops)
        {
            var op = node as JsonObject ?? throw new ArgumentException("each op must be a JSON object");
            try { results.Add(tileOps.Apply(op)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"op {results.Count} ({op["op"]}): {ex.Message}", ex);
            }
        }
        var changed = tileOps.Changed.OrderBy(h => h.Row).ThenBy(h => h.Col).ToList();
        var (issues, existing) = changed.Count == 0 ? ([], 0) : NewIssues(before, map, changed);
        var blocked = issues.Any(f => Blocks(f, allowWarnings)) && !force;
        var seq = 0;
        if (!dryRun && !blocked && changed.Count > 0)
            seq = Commit(map, label ?? Summary(ops), ops, changed);
        return new EditResult(seq > 0, seq, results, changed, issues, existing, map);
    }

    /// <summary>Saves an already edited map (the GUI path): checks it against the file on disk, then commits.</summary>
    public EditResult Save(HexTileMap edited, IReadOnlyCollection<(int Col, int Row)> changed, JsonArray ops, string label,
                           bool force = false, bool allowWarnings = false)
    {
        var onDisk = Load();
        var (issues, existing) = changed.Count == 0 ? ([], 0) : NewIssues(onDisk, edited, changed);
        var blocked = issues.Any(f => Blocks(f, allowWarnings)) && !force;
        var seq = !blocked && changed.Count > 0 ? Commit(edited, label, ops, changed) : 0;
        return new EditResult(seq > 0, seq, ops, changed.ToList(), issues, existing, edited);
    }

    /// <summary>Findings in the changed area after the edit that were not there before (matched on code + hex);
    /// also returns how many pre-existing issue hexes the area still has.</summary>
    public (List<TileMapFinding> New, int Existing) NewIssues(HexTileMap before, HexTileMap after, IReadOnlyCollection<(int Col, int Row)> changed)
    {
        var old = TileMapValidator.CheckMap(before, Database, changed, TileMapValidator.DefaultVanillaTileMap(_paths));
        var now = TileMapValidator.CheckMap(after, Database, changed, TileMapValidator.DefaultVanillaTileMap(_paths));
        var oldKeys = old.SelectMany(f => f.AllHexes.Select(h => (f.Code, h[0], h[1]))).ToHashSet();
        var oldCodes = old.Where(f => f.AllHexes.Count == 0).Select(f => (f.Code, f.Message)).ToHashSet();
        var result = new List<TileMapFinding>();
        var existing = 0;
        foreach (var f in now)
        {
            if (f.AllHexes.Count == 0)
            {
                if (!oldCodes.Contains((f.Code, f.Message))) result.Add(f);
                continue;
            }
            var fresh = f.AllHexes.Where(h => !oldKeys.Contains((f.Code, h[0], h[1]))).ToList();
            existing += f.AllHexes.Count - fresh.Count;
            if (fresh.Count > 0)
                result.Add(f with { Count = fresh.Count, Hexes = fresh.Take(TileMapValidator.MaxListedHexes).ToList(), AllHexes = fresh });
        }
        return (result, existing);
    }

    private int Commit(HexTileMap map, string label, JsonArray ops, IEnumerable<(int Col, int Row)> changed)
    {
        var seq = Journal.Commit([(TileMapPath, p => map.Write(p))], label);
        var record = new OpsRecord(seq, DateTime.Now, label, (JsonArray)ops.DeepClone(), changed.Select(h => new[] { h.Col, h.Row }).ToList());
        File.AppendAllText(OpsPath, JsonSerializer.Serialize(record) + "\n");
        return seq;
    }

    private static HexTileMap Clone(HexTileMap map) => new(map.PixelWidth, map.PixelHeight, (uint[])map.Pixels.Clone());

    private static string Summary(JsonArray ops) =>
        string.Join(", ", ops.OfType<JsonObject>().GroupBy(o => o["op"]?.ToString()).Select(g => $"{g.Key} x{g.Count()}"));

    // ---------------------------------------------------------------- history

    public List<OpsRecord> Ops()
    {
        if (!File.Exists(OpsPath)) return [];
        var top = Journal.Top;
        return File.ReadAllLines(OpsPath).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<OpsRecord>(l)!)
            .Where(r => r.Seq <= top).ToList();
    }

    public List<FileJournal.HistoryEntry> Undo(int steps = 1, bool force = false) => PruneOps(Journal.Undo(steps, force));

    public List<FileJournal.HistoryEntry> Rollback(string label, bool force = false) => PruneOps(Journal.Rollback(label, force));

    private List<FileJournal.HistoryEntry> PruneOps(List<FileJournal.HistoryEntry> undone)
    {
        var keep = Ops();
        Directory.CreateDirectory(Journal.Dir);
        File.WriteAllLines(OpsPath, keep.Select(r => JsonSerializer.Serialize(r)));
        return undone;
    }

    /// <summary>Re-applies recorded batches (seq range, default all) to the current tile map as one new batch, e.g.
    /// after caime_tilemap.py regenerated it. Validated like any edit.</summary>
    public EditResult Replay(int fromSeq = 1, int toSeq = int.MaxValue, bool dryRun = false, bool force = false, bool allowWarnings = false)
    {
        var records = Ops().Where(r => r.Seq >= fromSeq && r.Seq <= toSeq).ToList();
        if (records.Count == 0) throw new InvalidOperationException("no recorded tile edits in that range (see tiles-history)");
        var ops = new JsonArray(records.SelectMany(r => r.Ops.Select(o => o!.DeepClone())).ToArray());
        return Edit(ops, $"replay {records[0].Seq}-{records[^1].Seq}", dryRun, force, allowWarnings: allowWarnings);
    }

    // ---------------------------------------------------------------- BOB tile-matching simulation

    public sealed record SimulationResult(TileMatchSummary Summary, int NoTilePoints, IReadOnlyList<int[]> NoTileHexes,
                                          IReadOnlyList<int[]> InEdited, TimeSpan Elapsed);

    /// <summary>
    /// Runs <see cref="TileMatchSimulator"/> (the port of BOB's Tilemap; whole map, ~1-3 min) on the tile map, or on
    /// <paramref name="map"/> (unsaved edits), and splits the hexes that would get no tile into those in edited hexes
    /// (journaled after <paramref name="sinceSeq"/>, plus <paramref name="extraEdited"/>) and the rest. Climates come from
    /// the climate map next to the tile map, else the kit's.
    /// </summary>
    public SimulationResult Simulate(HexTileMap? map = null, IEnumerable<(int Col, int Row)>? extraEdited = null, int sinceSeq = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        map ??= Load();
        var result = new TileMatchSimulator(Database).Run(map, Climate(map));
        var hexes = result.NoTileHexes(map);
        var edited = EditedHexes(sinceSeq);
        if (extraEdited is not null) edited.UnionWith(extraEdited);
        var inEdited = hexes.Where(h => edited.Contains((h[0], h[1]))).ToList();
        return new SimulationResult(result.Summary, result.NoTile.Count, hexes, inEdited, sw.Elapsed);
    }

    /// <summary>Per-pixel climate indices for <paramref name="map"/> (the tile map's size): from the climate map next to
    /// the tile map, else the kit's. For <see cref="TileMatchSimulator"/> and hole-checked fixes.</summary>
    public byte[] Climate(HexTileMap map)
    {
        var own = Path.GetDirectoryName(TileMapPath)!;
        var climateDir = Directory.EnumerateFiles(own, "climate_map*.png").Any() ? own : _paths.AkTerrainDir;
        return TileMapValidator.ClimateIndices(map, climateDir, Database);
    }

    /// <summary>The hexes changed by journaled batches after <paramref name="sinceSeq"/> (to tell holes in edited
    /// hexes from old ones).</summary>
    public HashSet<(int Col, int Row)> EditedHexes(int sinceSeq = 0) =>
        Ops().Where(r => r.Seq > sinceSeq).SelectMany(r => r.Hexes ?? []).Select(h => (h[0], h[1])).ToHashSet();
}
