using System.Text.Json.Nodes;
using AtlasWH3.Formats.Maps;
using static AtlasWH3.Core.Campaign.TileMapCheck.TileMapValidator;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>A hex's colour in a suggested fix.</summary>
public readonly record struct HexColour(int Col, int Row, uint Rgb);

/// <summary>A recommended repaint: <see cref="Ops"/> are ordinary <see cref="TileMapOps"/> ops (apply, journal, undo and
/// replay as any edit); <see cref="Preview"/> is the after-colours. Scores are weighted local finding counts (errors x10 +
/// warnings) before and after. <see cref="Verified"/>: BOB's tile matching, run on a window around the fix, finds no
/// new hole (and, for a hole, the hole gone); false = checked by the rules only.</summary>
public sealed record TileFix(string Summary, JsonArray Ops, IReadOnlyList<HexColour> Preview, int ScoreBefore, int ScoreAfter, bool Verified,
                             int HolesBefore = 0, int HolesAfter = 0);

/// <summary>One tile-map error at one hex (Col/Row -1: a whole-map problem), what to do about it, and the fix when one
/// can be computed.</summary>
public sealed record TileError(string Code, string Severity, int Col, int Row, string Message, string Advice, TileFix? Fix);

/// <summary>
/// Tile error mode: every rule finding of <see cref="TileMapValidator.CheckMap"/> (and, given a simulation, every hole)
/// as one error per hex, each with a recommended fix. Fixes generalise research/main190/tile_repair.py: try the
/// candidate recolourings for the error's code, score each with the validator on a small window around the hex, and keep
/// the best one that removes the error without adding a new error.
/// </summary>
public static class TileErrors
{
    public const string HoleCode = "match.no_tile";
    /// <summary>Hexes around a change that are re-checked (their findings make the score).</summary>
    private const int ScoreRing = 1;
    /// <summary>Window margin cut from the map for scoring (covers the score ring, its neighbours and their patterns).</summary>
    private const int Margin = 4;

    /// <summary>Codes not counted in scores (advice only; they never block a save).</summary>
    private static readonly HashSet<string> Unscored = ["palette.unused_set"];

    /// <summary>Text recommendation per code, for errors without a computed fix and for whole-map findings.</summary>
    public static string AdviceFor(string code) => code switch
    {
        "palette.black" => "Paint the hex with the surrounding land or sea set.",
        "palette.stray" or "palette.unknown" => "Repaint the hex with the nearest real tile-set colour.",
        "line.thick" => "Thin the line to one hex wide: paint the extra hexes as the land beside them.",
        "coast.missing_ring" => "Put a sea_coast (or cliff) hex between land and sea.",
        "coast.cliff_end" => "A cliff touching sea_coast must be blockout_cliff_ends; a cliff end must touch sea_coast.",
        "river.ends" => "Rivers start with river_start (1 river neighbour) and end with river_mouth (1 river neighbour and the sea).",
        "river.crossing" => "Keep the river straight through the crossing and the road crossing it on opposite sides.",
        "palette.unused_set" => "Use a tile set CA's map uses (its tiles are tested).",
        "pattern.unseen" => "Reshape the coast / river here to a layout CA's map uses (often one hex of sea, coast or river).",
        HoleCode => "Simplify the shapes here: BOB has no tile for this spot (smooth the coast, straighten the river or road).",
        "layout.mesh_columns" => "Pad the map width to a multiple of 4 hexes, or accept layout.mesh_columns in the build profile.",
        "layout.size" => "The image must be 2W x (2H+1) pixels for a W x H hex grid.",
        "layout.filler" => "Make the filler pixels black (top row of even columns, bottom row of odd columns).",
        "climate.size" or "climate.missing" => "Give the map a climate_map.png the size of the tile map (and climate_map_g.png, 4x).",
        "inputs.sizes" => "Resize the .terry layers / lf maps to the tile map's size (8W x (8H+4), sea 4W x (4H+2)).",
        "inputs.extents" => "Set the map's extents in campaign_map_playable_areas / campaign_maps to the new size.",
        "inputs.stale_lf" => "Run the rasters build step (lf_height_map is older than the height map).",
        "inputs.rules_bob" => "Restore raw_data/terrain/campaigns/rules.bob (save_final_tile_map, generate_global_mesh).",
        _ => "",
    };

    /// <summary>
    /// Every error, one per hex: the rule findings (in <paramref name="region"/> and a ring around it, or the whole map),
    /// plus <paramref name="holeHexes"/> from a simulation (<see cref="TileMatchResult.NoTileHexes"/>). With
    /// <paramref name="suggest"/>, each error gets its fix (one small validator run per candidate).
    /// </summary>
    public static IReadOnlyList<TileError> Find(HexTileMap map, CampaignTileDatabase db, IReadOnlyList<int[]>? holeHexes = null,
                                                IEnumerable<(int Col, int Row)>? region = null, bool suggest = true,
                                                string? vanillaTileMap = null, CancellationToken cancel = default, byte[]? climate = null)
    {
        var ctx = new Context(map, db, vanillaTileMap, climate);
        var errors = new List<TileError>();
        foreach (var f in CheckMap(map, db, region, vanillaTileMap))
        {
            if (f.Severity == TileMapFinding.Info) continue;
            if (f.AllHexes.Count == 0 || f.AllHexes[0].Length < 2)
            {
                errors.Add(new TileError(f.Code, f.Severity, -1, -1, f.Message, AdviceFor(f.Code), null));
                continue;
            }
            foreach (var h in f.AllHexes)
                errors.Add(new TileError(f.Code, f.Severity, h[0], h[1], ctx.Describe(f.Code, h[0], h[1]), AdviceFor(f.Code), null));
        }
        foreach (var h in holeHexes ?? [])
            errors.Add(new TileError(HoleCode, TileMapFinding.Error, h[0], h[1], "BOB places no tile here: a see-through hole in game",
                                     AdviceFor(HoleCode), null));
        if (!suggest) return errors;
        var result = new List<TileError>(errors.Count);
        foreach (var e in errors)
        {
            cancel.ThrowIfCancellationRequested();
            result.Add(e.Col < 0 ? e : e with { Fix = ctx.Suggest(e) });
        }
        return result;
    }

    /// <summary>The fix for one error on <paramref name="map"/> as it is now (null: no candidate helps). With
    /// <paramref name="climate"/> (<see cref="TileMapValidator.ClimateIndices"/> of the map), candidates are checked
    /// for new holes with BOB's tile matching on a window around them (about 0.2-0.5 s each) and the fix is Verified.</summary>
    public static TileFix? Suggest(HexTileMap map, CampaignTileDatabase db, TileError error, string? vanillaTileMap = null, byte[]? climate = null) =>
        error.Col < 0 ? null : new Context(map, db, vanillaTileMap, climate).Suggest(error);

    public sealed record FixAllResult(JsonArray Ops, IReadOnlyList<HexColour> Changes, int Fixed, int Skipped, int Remaining);

    /// <summary>
    /// Applies the fixes of <paramref name="errors"/> one after another on a copy of <paramref name="map"/>, re-scoring
    /// each against the map as already fixed (a neighbour's fix may have cleared it, or changed what fits). Returns the
    /// combined ops (one paint op per colour) and the final colour of every changed hex; <paramref name="map"/> itself
    /// is not changed.
    /// </summary>
    public static FixAllResult FixAll(HexTileMap map, CampaignTileDatabase db, IEnumerable<TileError> errors, byte[]? climate,
                                      bool verifiedOnly = true, string? vanillaTileMap = null, CancellationToken cancel = default,
                                      IProgress<(int Done, int Total)>? progress = null)
    {
        var ctx = new Context(map, db, vanillaTileMap, climate);
        var list = errors.Where(e => e.Col >= 0).ToList();
        var done = 0;
        var final = new Dictionary<(int, int), uint>();
        int fixedCount = 0, skipped = 0;
        foreach (var e in list)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((done++, list.Count));
            if (e.Code != HoleCode && !ctx.StillFlagged(e)) continue;      // an earlier fix cleared it
            var fix = ctx.Suggest(e);
            if (fix is null || (verifiedOnly && !fix.Verified)) { skipped++; continue; }
            foreach (var h in fix.Preview)
            {
                ctx.Colours[map.Index(h.Col, h.Row)] = h.Rgb;
                final[(h.Col, h.Row)] = h.Rgb;
            }
            fixedCount++;
        }
        var original = map.HexColours();
        var changes = final.Select(kv => new HexColour(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .Where(h => h.Rgb != original[map.Index(h.Col, h.Row)] || !map.HexUniform(h.Col, h.Row)).ToList();
        var remaining = list.Count - fixedCount;
        return new FixAllResult(ctx.Ops(changes), changes, fixedCount, skipped, remaining);
    }

    // ------------------------------------------------------------------ engine

    private sealed class Context
    {
        public readonly uint[] Colours;
        private readonly HexTileMap _map;
        private readonly CampaignTileDatabase _db;
        private readonly string? _vanilla;
        private readonly Dictionary<uint, string> _setOf;
        private readonly int[] _cats;
        private readonly HashSet<string> _vanillaSets;

        private readonly byte[]? _climate;
        private TileMatchSimulator? _simulator;

        public Context(HexTileMap map, CampaignTileDatabase db, string? vanillaTileMap, byte[]? climate = null)
        {
            _map = map;
            _db = db;
            _vanilla = vanillaTileMap;
            _climate = climate is not null && climate.Length == map.Pixels.Length ? climate : null;
            _setOf = PlacementColours(db);
            Colours = map.HexColours();
            _cats = Categories(map, Colours, _setOf);
            _vanillaSets = LoadVanilla(vanillaTileMap ?? DefaultVanillaTileMap(new ProjectPaths()), db)?.Sets ?? db.TileSets.Select(s => s.Name).ToHashSet();
        }

        private int W => _map.Width;
        private int Cat(int c, int r) => Cat(Colours[_map.Index(c, r)]);
        private int Cat(uint rgb) => rgb == 0 ? Black : Category(_setOf.GetValueOrDefault(rgb));
        private uint SetRgb(string name) => _db.TileSet(name)?.Rgb ?? 0;
        private string? SetName(uint rgb) => _setOf.GetValueOrDefault(rgb);

        private IEnumerable<(int Col, int Row)> Around(int c, int r)
        {
            for (var d = 0; d < 6; d++)
                if (_map.Neighbour(c, r, d, out var nc, out var nr)) yield return (nc, nr);
        }

        /// <summary>The neighbours' most common area (land) colour, else generic (tile_repair land_of).</summary>
        private uint LandOf(int c, int r)
        {
            var best = Around(c, r).Select(h => Colours[_map.Index(h.Col, h.Row)])
                .Where(v => v != 0 && Cat(v) == Area && _setOf.ContainsKey(v))
                .GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault();
            return best?.Key ?? SetRgb("generic");
        }

        private uint NearestColour(uint colour, Func<string, bool>? setFilter = null)
        {
            var best = (Rgb: 0u, D: int.MaxValue);
            foreach (var (rgb, set) in _setOf)
            {
                if (setFilter != null && !setFilter(set)) continue;
                var d = Math.Max(Math.Abs((int)(rgb >> 16) - (int)(colour >> 16)),
                    Math.Max(Math.Abs((int)(rgb >> 8 & 0xff) - (int)(colour >> 8 & 0xff)), Math.Abs((int)(rgb & 0xff) - (int)(colour & 0xff))));
                if (d < best.D) best = (rgb, d);
            }
            return best.Rgb;
        }

        public string Describe(string code, int c, int r)
        {
            var rgb = Colours[_map.Index(c, r)];
            var set = SetName(rgb) ?? $"#{rgb:x6}";
            return code switch
            {
                "palette.black" => "black hex: no tile set, so no tile",
                "palette.stray" or "palette.unknown" => $"colour #{rgb:x6} is no tile-set colour",
                "line.thick" => $"{set} is more than one hex wide here",
                "coast.missing_ring" => "land touches the sea with no coast hex between",
                "coast.cliff_end" => Cat(c, r) == Cliff ? "cliff touches sea_coast: it should be a cliff end" : "cliff end touches no sea_coast",
                "river.ends" => Cat(c, r) switch
                {
                    Start => "river_start needs exactly one river neighbour",
                    Mouth => "river_mouth needs one river neighbour and the sea",
                    _ => "river hex with no river neighbour",
                },
                "river.crossing" => $"crossing layout {CrossingLayout(_map, _cats, c, r)} has no tile",
                "palette.unused_set" => $"{set} is never used on CA's map",
                "pattern.unseen" => $"{set}: a neighbourhood CA's map never uses (likely hole)",
                _ => code,
            };
        }

        /// <summary>Candidate recolourings: (label, hexes and their new colours).</summary>
        private IEnumerable<(string Label, HexColour[] Changes)> Candidates(TileError e)
        {
            int c = e.Col, r = e.Row;
            var rgb = Colours[_map.Index(c, r)];
            var cat = Cat(c, r);
            (string, HexColour[]) One(uint to, string? why = null) =>
                ($"paint {SetName(to) ?? $"#{to:x6}"}{(why is null ? "" : $" ({why})")}", [new HexColour(c, r, to)]);
            uint sea = SetRgb("generic_sea"), beach = SetRgb("sea_coast"), cliff = SetRgb("blockout_cliff"),
                cliffEnd = SetRgb("blockout_cliff_ends"), river = SetRgb("river"), mouth = SetRgb("river_mouth"), land = LandOf(c, r);

            switch (e.Code)
            {
                case "palette.black" or "palette.stray" or "palette.unknown":
                    if (rgb != 0) yield return One(NearestColour(rgb), "nearest colour");
                    yield return One(land, "surrounding land");
                    yield return One(sea);
                    break;
                case "line.thick":
                    yield return One(land, "thin the line");
                    break;
                case "coast.missing_ring":
                    yield return One(beach);
                    break;
                case "coast.cliff_end":
                    yield return One(cat == Cliff ? cliffEnd : cliff);
                    break;
                case "river.ends":
                    if (cat == Start) { yield return One(river); yield return One(land); }
                    else if (cat == Mouth) { yield return One(sea); yield return One(river); }
                    else yield return One(land, "remove the stray river hex");
                    break;
                case "river.crossing":
                    foreach (var cand in CrossingCandidates(c, r)) yield return cand;
                    break;
                case "palette.unused_set":
                    var similar = NearestColour(rgb, s => _vanillaSets.Contains(s) && Category(s) == cat);
                    if (similar != 0 && similar != rgb) yield return One(similar, "a set CA's map uses");
                    break;
                case "pattern.unseen":
                    foreach (var to in UnseenTable(cat, c, r))
                        yield return One(to);
                    // second tier: the odd neighbourhood is often fixed by changing a neighbour instead
                    foreach (var (nc, nr) in Around(c, r))
                        foreach (var to in UnseenTable(Cat(nc, nr), nc, nr))
                            yield return ($"paint {SetName(to)} at ({nc},{nr})", [new HexColour(nc, nr, to)]);
                    break;
                case HoleCode:
                    foreach (var to in cat switch
                    {
                        Crossing => new[] { river },
                        CliffEnd => [beach, cliff, sea],
                        Cliff => [beach, sea],
                        Beach => [sea, land],
                        Mouth => [sea, river],
                        River or Start or Road => [land],
                        _ => [],
                    })
                        yield return One(to);
                    // a hole on plain land usually comes from the line beside it
                    foreach (var (nc, nr) in Around(c, r))
                        if (Cat(nc, nr) is River or Start or Road or Mouth)
                            yield return ($"paint {SetName(LandOf(nc, nr))} at ({nc},{nr}) (remove the line hex beside the hole)",
                                          [new HexColour(nc, nr, LandOf(nc, nr))]);
                    break;
            }
        }

        /// <summary>tile_repair.py's recolourings for a coast / river hex whose neighbourhood CA's map never uses.</summary>
        private uint[] UnseenTable(int cat, int c, int r)
        {
            uint sea = SetRgb("generic_sea"), beach = SetRgb("sea_coast"), cliff = SetRgb("blockout_cliff"),
                cliffEnd = SetRgb("blockout_cliff_ends"), river = SetRgb("river"), mouth = SetRgb("river_mouth");
            return cat switch
            {
                CliffEnd => [beach, cliff],
                Cliff => [cliffEnd, beach, sea],
                Beach => [sea, LandOf(c, r)],
                Mouth => [sea, river],
                Crossing => [river],
                Start => [river, LandOf(c, r)],
                River => [mouth],
                _ => [],
            };
        }

        /// <summary>tile_repair.py fix_crossings: extend a one-sided road, shift a road neighbour, else plain river.</summary>
        private IEnumerable<(string, HexColour[])> CrossingCandidates(int c, int r)
        {
            var roadRgb = Around(c, r).Select(h => Colours[_map.Index(h.Col, h.Row)]).FirstOrDefault(v => Cat(v) == Road);
            if (roadRgb == 0) roadRgb = SetRgb("roads_tracks");
            var dirs = new (int Col, int Row)?[6];
            for (var d = 0; d < 6; d++) dirs[d] = _map.Neighbour(c, r, d, out var nc, out var nr) ? (nc, nr) : null;
            for (var d = 0; d < 6; d++)
            {
                if (dirs[d] is not { } n) continue;
                if (Cat(n.Col, n.Row) == Area)
                    yield return ($"extend the road to ({n.Col},{n.Row})", [new HexColour(n.Col, n.Row, roadRgb)]);
                if (Cat(n.Col, n.Row) != Road) continue;
                var onward = Around(n.Col, n.Row).Count(h => (h.Col, h.Row) != (c, r) && Cat(h.Col, h.Row) == Road);
                if (onward > 1) continue;     // a junction: leave it
                foreach (var side in new[] { (d + 1) % 6, (d + 5) % 6 })
                    if (dirs[side] is { } s && Cat(s.Col, s.Row) == Area)
                        yield return ($"move the road from ({n.Col},{n.Row}) to ({s.Col},{s.Row})",
                                      [new HexColour(s.Col, s.Row, Colours[_map.Index(n.Col, n.Row)]), new HexColour(n.Col, n.Row, LandOf(n.Col, n.Row))]);
            }
            yield return ("paint river (drop the crossing)", [new HexColour(c, r, SetRgb("river"))]);
        }

        public TileFix? Suggest(TileError e)
        {
            var before = Score(e, []);
            var hole = e.Code == HoleCode;
            var ranked = new List<(string Label, HexColour[] Changes, int Score)>();
            foreach (var (label, changes) in Candidates(e))
            {
                if (changes.Any(h => h.Rgb == 0 || h.Col < 0 || h.Row < 0 || h.Col >= W || h.Row >= _map.Height)) continue;
                if (changes.All(h => Colours[_map.Index(h.Col, h.Row)] == h.Rgb)) continue;
                var after = Score(e, changes);
                var ok = hole ? after.Score <= before.Score : after.Gone && after.Score < before.Score && after.Errors <= before.Errors;
                if (ok) ranked.Add((label, changes, after.Score));
            }
            if (ranked.Count == 0) return null;
            ranked = ranked.OrderBy(c => c.Score).ToList();      // stable: candidate order breaks ties
            if (_climate is null)
            {
                var b = ranked[0];
                return new TileFix(b.Label, Ops(b.Changes), b.Changes, before.Score, b.Score, Verified: false);
            }
            // BOB's tile matching on a window around the error: the rules can't see holes (a river_start painted as land
            // leaves a river with no start tile; main190 2026-10-05: 31 rule-only fixes opened 14 holes)
            var holesBefore = HolesNear(e.Col, e.Row, []);
            foreach (var c in ranked)
            {
                var holesAfter = HolesNear(e.Col, e.Row, c.Changes);
                if (hole ? holesAfter < holesBefore : holesAfter <= holesBefore)
                    return new TileFix(c.Label, Ops(c.Changes), c.Changes, before.Score, c.Score, Verified: true, holesBefore, holesAfter);
            }
            return null;   // every rule fix here would open a hole
        }

        /// <summary>Window radius (hexes) simulated around an error: covers the largest campaign tiles and their links.</summary>
        private const int SimRadius = 18;
        /// <summary>Holes counted within this distance of the error (the window's own edges give false holes).</summary>
        private const int HoleRadius = 6;

        /// <summary>Holes BOB's tile matching leaves within <see cref="HoleRadius"/> of (c, r) once
        /// <paramref name="changes"/> are painted, simulated on a window of the map (unsaved and FixAll changes included).</summary>
        private int HolesNear(int c, int r, HexColour[] changes)
        {
            int minC = Math.Max(0, c - SimRadius) & ~1, maxC = Math.Min(W - 1, c + SimRadius);
            int minR = Math.Max(0, r - SimRadius), maxR = Math.Min(_map.Height - 1, r + SimRadius);
            int ww = maxC - minC + 1, wh = maxR - minR + 1;
            var window = new uint[ww * wh];
            for (var row = 0; row < wh; row++)
                Array.Copy(Colours, (minR + row) * W + minC, window, row * ww, ww);
            foreach (var h in changes) window[(h.Row - minR) * ww + h.Col - minC] = h.Rgb;
            var sub = HexTileMap.FromHexColours(ww, wh, window);

            // the same pixels' climate: x = x' + 2 minC, y = y' + 2 (H - wh - minR)
            var climate = new byte[sub.Pixels.Length];
            int dy = 2 * (_map.Height - wh - minR), dx = 2 * minC;
            for (var y = 0; y < sub.PixelHeight; y++)
                Array.Copy(_climate!, (y + dy) * _map.PixelWidth + dx, climate, y * sub.PixelWidth, sub.PixelWidth);

            _simulator ??= new TileMatchSimulator(_db);
            var result = _simulator.Run(sub, climate);
            var centre = TileMapOps.Cube(c - minC, r - minR);
            return result.NoTileHexes(sub).Count(h =>
            {
                var p = TileMapOps.Cube(h[0], h[1]);
                return Math.Max(Math.Abs(p.X - centre.X), Math.Max(Math.Abs(p.Y - centre.Y), Math.Abs(p.Z - centre.Z))) <= HoleRadius;
            });
        }

        public bool StillFlagged(TileError e) => Score(e, []).Gone == false;

        /// <summary>Validator score on a window around the error and the changes: errors x10 + warnings; whether the
        /// error's own (code, hex) is still reported; the error count.</summary>
        private (int Score, bool Gone, int Errors) Score(TileError e, HexColour[] changes)
        {
            var hexes = changes.Select(h => (h.Col, h.Row)).Append((e.Col, e.Row)).ToList();
            int minC = Math.Max(0, hexes.Min(h => h.Col) - Margin) & ~1, maxC = Math.Min(W - 1, hexes.Max(h => h.Col) + Margin);
            int minR = Math.Max(0, hexes.Min(h => h.Row) - Margin), maxR = Math.Min(_map.Height - 1, hexes.Max(h => h.Row) + Margin);
            int ww = maxC - minC + 1, wh = maxR - minR + 1;
            var window = new uint[ww * wh];
            for (var r = 0; r < wh; r++)
                Array.Copy(Colours, (minR + r) * W + minC, window, r * ww, ww);
            foreach (var h in changes) window[(h.Row - minR) * ww + h.Col - minC] = h.Rgb;
            var sub = HexTileMap.FromHexColours(ww, wh, window);

            // the changed hexes, the error hex and a ring around them; CheckMap adds one more ring
            var region = new HashSet<(int, int)>();
            foreach (var (c, r) in hexes)
            {
                region.Add((c - minC, r - minR));
                if (ScoreRing > 0)
                    for (var d = 0; d < 6; d++)
                        if (sub.Neighbour(c - minC, r - minR, d, out var nc, out var nr)) region.Add((nc, nr));
            }
            int score = 0, errorCount = 0;
            var gone = true;
            foreach (var f in CheckMap(sub, _db, region, _vanilla))
            {
                if (f.Severity == TileMapFinding.Info) continue;
                if (f.Code == e.Code && f.AllHexes.Any(h => h.Length >= 2 && h[0] == e.Col - minC && h[1] == e.Row - minR)) gone = false;
                if (Unscored.Contains(f.Code)) continue;
                var n = Math.Max(1, f.AllHexes.Count);
                if (f.Severity == TileMapFinding.Error) { score += 10 * n; errorCount += n; }
                else score += n;
            }
            return (score, gone, errorCount);
        }

        /// <summary>One paint op per colour (set name when the colour is a tile set's, else #rrggbb).</summary>
        public JsonArray Ops(IEnumerable<HexColour> changes)
        {
            var ops = new JsonArray();
            foreach (var g in changes.GroupBy(h => h.Rgb))
            {
                var name = _db.TileSets.FirstOrDefault(s => s.Rgb == g.Key)?.Name ?? $"#{g.Key:x6}";
                var hexes = new JsonArray();
                foreach (var h in g) hexes.Add(new JsonArray(h.Col, h.Row));
                ops.Add(new JsonObject { ["op"] = "paint", ["set"] = name, ["hexes"] = hexes });
            }
            return ops;
        }
    }
}
