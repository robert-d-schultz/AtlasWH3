using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Camera;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Ground height (world y) at a campaign world point, for seating props.
/// <para>
/// <see cref="Scene"/> is BOB's scene height (<see cref="CameraHeightField"/>, byte-identical camera height map): the
/// global mesh, the height patches of rivers, of the tiles' own props (mountain tiles carry their height there) and
/// of the map's props, with the tile terrain as fallback. Vanilla's trees stand on it (median 0.24 under it), not
/// on the bare terrain (median 1.9 above lf + tile hf: they sit on mountain props). A prop's own patch is ignored
/// when seating it (<see cref="For"/>).
/// </para>
/// <para>
/// <see cref="Built"/> is the terrain alone: the compiled lf_height_map plus each tile's hf, sampled exactly as BOB's
/// Campaign Trees (<see cref="TileHfHeight.TreeHeight"/>, bit-exact on vanilla's tree list). Both need a build
/// (tile_list.bin, lf_height_map.compressed_map, global meshes …): they show the last build, not unbuilt kit edits.
/// <see cref="FromFunc"/> wraps any other sampler, such as the kit's lf TIF.
/// </para>
/// </summary>
public sealed class GroundHeight
{
    private readonly Func<double, double, double> _at;
    private Func<ClampProp, double>? _for;

    /// <summary>Where the heights come from, for the UI ("lf + tile hf (…/tile_list.bin)").</summary>
    public string Description { get; }

    /// <summary>True when this is BOB's lf + tile hf height.</summary>
    public bool IsBuilt { get; }

    /// <summary>Built ground: median |built − reference| over the check grid (NaN without a reference).</summary>
    public double ReferenceDifference { get; private init; } = double.NaN;

    private GroundHeight(string description, bool built, Func<double, double, double> at)
    {
        Description = description;
        IsBuilt = built;
        _at = at;
    }

    public double At(double x, double z) => _at(x, z);

    /// <summary>The ground under a prop, ignoring that prop's own height patch (scene ground).</summary>
    public double For(ClampProp p) => _for?.Invoke(p) ?? _at(p.X, p.Z);

    public static GroundHeight FromFunc(string description, Func<double, double, double> at) => new(description, false, at);

    /// <summary>A compiled terrain whose median height differs from the project's own lf by more than this belongs to
    /// another map or an old build (main190 and vanilla share the 3k_dlc07_main_map name).</summary>
    public const double MaxReferenceDifference = 0.25;

    /// <summary>
    /// BOB's lf + tile hf ground for the map, or null (with the reason) when no usable compiled terrain exists. Looks
    /// for tile_list.bin and lf_height_map.compressed_map together in the native build output, the kit's
    /// working_data, the compiled vanilla folder, then the packs (<paramref name="source"/>: mod packs first, then the
    /// game's). With a <paramref name="reference"/> (the project's lf height, world extents <paramref name="worldW"/> ×
    /// <paramref name="worldH"/>) the first candidate that agrees with it is used, so a build of another map or an old
    /// build is skipped.
    /// </summary>
    public static GroundHeight? Built(ProjectPaths paths, string mapName, AssetSource? source, out string why,
                                      Func<double, double, double>? reference = null, double worldW = 0, double worldH = 0)
    {
        PackSet? packs = null;
        PackSet? Packs() => packs ??= Directory.Exists(paths.GameDataDir) ? PackSet.OpenVanilla(paths.GameDataDir) : null;
        Func<string, byte[]?> read = p => source?.TryRead(p) ?? Packs()?.TryRead(p);

        var rel = Path.Combine("terrain", "campaigns", mapName);
        var candidates = new List<(string From, Func<(byte[] Tl, byte[] Lf)?> Load)>();
        foreach (var d in new[] { Path.Combine(paths.OutputRoot, "compiled", mapName, rel), Path.Combine(paths.AkWorkingDir, rel), Path.Combine(paths.VanillaRoot, rel) })
        {
            string tl = Path.Combine(d, "tile_list.bin"), lm = Path.Combine(d, "lf_height_map.compressed_map");
            if (File.Exists(tl) && File.Exists(lm)) candidates.Add((d, () => (File.ReadAllBytes(tl), File.ReadAllBytes(lm))));
        }
        var prefix = $"terrain/campaigns/{mapName}/";
        candidates.Add(("packs", () => read(prefix + "tile_list.bin") is { } t && read(prefix + "lf_height_map.compressed_map") is { } l ? (t, l) : null));

        IReadOnlyDictionary<string, TileInfo>? db = null;
        var rejected = new List<string>();
        foreach (var (from, load) in candidates)
        {
            try
            {
                if (load() is not var (tileList, lf)) continue;
                if (db is null)
                {
                    var vanilla = source?.Vanilla ?? Packs();
                    if (vanilla is null) { why = "no game data folder for the tile database"; return null; }
                    var dbPrefix = PackFile.Normalize(TileDatabase.Folder);
                    db = TileDatabase.Load(vanilla.Packs.SelectMany(p => p.Entries.Keys).Where(k => k.StartsWith(dbPrefix, StringComparison.Ordinal))
                        .Distinct().Select(k => vanilla.TryRead(k)).OfType<byte[]>());
                }
                var tiles = new TileHfHeight(TileList.Read(tileList), db, read, CompressedMap.Decode(lf), TileHfHeight.TileSize3K) { BobCells = true };
                double At(double x, double z) => tiles.TreeHeight((float)x, (float)z);
                var diff = reference is null || worldW <= 0 || worldH <= 0 ? double.NaN : MedianDifference(At, reference, worldW, worldH);
                if (diff > MaxReferenceDifference)
                {
                    rejected.Add($"{Short(from)} (median {diff:0.##} off the project's lf: another map or an old build)");
                    continue;
                }
                why = rejected.Count > 0 ? "skipped " + string.Join("; ", rejected) : "";
                return new GroundHeight($"lf + tile hf ({Short(from)})", true, At) { ReferenceDifference = diff };
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
            {
                rejected.Add($"{Short(from)} unreadable ({ex.Message})");
            }
        }
        why = rejected.Count > 0
            ? "no compiled terrain matches this project: " + string.Join("; ", rejected)
            : "no compiled tile_list.bin + lf_height_map.compressed_map for " + mapName + " (build the map first)";
        return null;
    }

    /// <summary>The largest median difference from the project's lf for <see cref="Scene"/> (props' patches lift some
    /// grid points; vanilla 3k_dlc07 measures 0.03).</summary>
    public const double MaxSceneDifference = 0.5;

    /// <summary>
    /// BOB's scene height for the map (see the class summary), or null with the reason. Build output roots tried in
    /// order: the native build output, the kit's working_data, the compiled vanilla folder, then the game packs alone;
    /// with a <paramref name="reference"/> the first that agrees with the project's lf is used.
    /// </summary>
    public static GroundHeight? Scene(ProjectPaths paths, string mapName, out string why,
                                      Func<double, double, double>? reference = null, double worldW = 0, double worldH = 0)
    {
        var mapPaths = paths with { MapName = mapName };
        var rel = Path.Combine("terrain", "campaigns", mapName, "tile_list.bin");
        var roots = new[] { Path.Combine(paths.OutputRoot, "compiled", mapName), paths.AkWorkingDir, paths.VanillaRoot }
            .Where(r => File.Exists(Path.Combine(r, rel))).Append(Path.Combine(paths.OutputRoot, "compiled", "_packs_only_"));
        var rejected = new List<string>();
        foreach (var root in roots)
        {
            var from = Directory.Exists(root) ? root : "game packs";
            try
            {
                var field = CameraHeightmapStep.BuildField(new CampaignBuildContext(mapPaths, root), [], out _, out _);
                double At(double x, double z) => field.Height((float)x, (float)z);
                var diff = reference is null || worldW <= 0 || worldH <= 0 ? double.NaN : MedianDifference(At, reference, worldW, worldH);
                if (diff > MaxSceneDifference)
                {
                    rejected.Add($"{from} (median {diff:0.##} off the project's lf: another map or an old build)");
                    continue;
                }
                why = rejected.Count > 0 ? "skipped " + string.Join("; ", rejected) : "";
                return new GroundHeight($"scene height: global mesh + tile and prop height patches ({from})", true, At)
                {
                    ReferenceDifference = diff,
                    _for = p =>
                    {
                        var key = GroundClamp.Key(p.Model);
                        return field.Height((float)p.X, (float)p.Z, o => Math.Abs(o.M[3] - p.X) < 0.05 && Math.Abs(o.M[11] - p.Z) < 0.05
                                                                        && GroundClamp.Key(o.Source) == key);
                    },
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
            {
                rejected.Add($"{from}: {ex.Message}");
            }
        }
        why = "no usable build output: " + string.Join("; ", rejected);
        return null;
    }

    private static string Short(string from) => from == "packs" ? "game packs" : from;

    /// <summary>Median |a − b| over a 24 × 24 grid inside the map (borders left out).</summary>
    public static double MedianDifference(Func<double, double, double> a, Func<double, double, double> b, double worldW, double worldH)
    {
        var d = new List<double>(576);
        for (var i = 0; i < 24; i++)
            for (var j = 0; j < 24; j++)
            {
                double x = worldW * (0.05 + 0.9 * (i + 0.5) / 24), z = worldH * (0.05 + 0.9 * (j + 0.5) / 24);
                d.Add(Math.Abs(a(x, z) - b(x, z)));
            }
        d.Sort();
        return (d[287] + d[288]) / 2;
    }
}
