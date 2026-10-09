using AtlasWH3.Core.Campaign.Trees;
using AtlasWH3.Formats.Esf;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Packs;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Ground height (world y) at a campaign world point, for seating props.
/// <para>
/// <see cref="Scene"/> is the ground BOB stands WH3's trees on (<see cref="TreeHeightField"/>): the nearest
/// full_logic_map texel raised by the height patches of the project's layer props (mountains carry their height there).
/// A prop's own patch is ignored when seating it (<see cref="For"/>).
/// </para>
/// <para>
/// <see cref="Built"/> is the terrain alone: the full_logic_map texel. Both need a built full_logic_map: they show the
/// last build of the heights, not unbuilt kit edits. <see cref="FromFunc"/> wraps any other sampler, such as the kit's
/// height TIF.
/// </para>
/// </summary>
public sealed class GroundHeight
{
    private readonly Func<double, double, double> _at;
    private Func<ClampProp, double>? _for;

    /// <summary>Where the heights come from, for the UI ("full_logic_map (…)").</summary>
    public string Description { get; }

    /// <summary>True when the heights come from a build (not <see cref="FromFunc"/>).</summary>
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

    /// <summary>A built terrain whose median height differs from the project's own heights by more than this belongs to
    /// another map or an old build.</summary>
    public const double MaxReferenceDifference = 0.25;

    /// <summary>The same for <see cref="Scene"/> (props' patches lift some grid points).</summary>
    public const double MaxSceneDifference = 0.5;

    /// <summary>
    /// The bare built terrain for the map, or null (with the reason) when no usable full_logic_map exists. Looks in the
    /// native build output, the kit's working_data, then the packs (the map's mod packs first). With a
    /// <paramref name="reference"/> (the project's own height, world extents <paramref name="worldW"/> ×
    /// <paramref name="worldH"/>) the first candidate that agrees with it is used, so a build of another map or an old
    /// build is skipped.
    /// </summary>
    public static GroundHeight? Built(ProjectPaths paths, string mapName, out string why,
                                      Func<double, double, double>? reference = null, double worldW = 0, double worldH = 0) =>
        Load(paths, mapName, false, out why, reference, worldW, worldH);

    /// <summary>The scene ground (see the class summary), or null with the reason; candidates as <see cref="Built"/>.</summary>
    public static GroundHeight? Scene(ProjectPaths paths, string mapName, out string why,
                                      Func<double, double, double>? reference = null, double worldW = 0, double worldH = 0) =>
        Load(paths, mapName, true, out why, reference, worldW, worldH);

    private static GroundHeight? Load(ProjectPaths paths, string mapName, bool scene, out string why,
                                      Func<double, double, double>? reference, double worldW, double worldH)
    {
        var mapPaths = paths with { MapName = mapName };
        var terry = Path.Combine(mapPaths.AkTerrainDir, mapName + ".terry");
        if (!File.Exists(terry)) { why = $"no {terry}"; return null; }
        var project = TerryProject.Load(terry);
        var mapData = new[] { Path.Combine(paths.OutputRoot, "compiled", mapName, "campaign_maps", mapName, "map_data.esf"),
                              Path.Combine(mapPaths.AkWorkingCampaignMapDir, "map_data.esf") }.FirstOrDefault(File.Exists);
        float? width = project.WorldWidth ?? (mapData is null ? null : MapDataBounds.Read(mapData).Width);
        if (width is null) { why = "the .terry has no world_width and there is no map_data.esf"; return null; }

        var packs = Directory.Exists(paths.GameDataDir) ? GameSetup.OpenWithLinked(paths.GameDataDir, paths.ModPacks) : null;
        if (scene && packs is null) { why = "no game data folder for the props' height patches"; return null; }
        var patches = scene ? TreeHeightField.LoadPatches(project, packs!, []) : [];

        var rel = Path.Combine("terrain", "campaigns", mapName, "full_logic_map.compressed_map");
        var candidates = new[] { Path.Combine(paths.OutputRoot, "compiled", mapName, rel), Path.Combine(paths.AkWorkingDir, rel) }
            .Where(File.Exists).Select(f => (From: f, Load: (Func<byte[]?>)(() => File.ReadAllBytes(f)))).ToList();
        candidates.Add(("game packs", () => packs?.TryRead($"terrain/campaigns/{mapName}/full_logic_map.compressed_map")));

        var rejected = new List<string>();
        foreach (var (from, load) in candidates)
        {
            try
            {
                if (load() is not { } bytes) continue;
                var field = new TreeHeightField(CompressedMap.Decode(bytes), width.Value, patches);
                Func<double, double, double> at = scene ? (x, z) => field.Height((float)x, (float)z) : (x, z) => field.Terrain((float)x, (float)z);
                var diff = reference is null || worldW <= 0 || worldH <= 0 ? double.NaN : MedianDifference(at, reference, worldW, worldH);
                if (diff > (scene ? MaxSceneDifference : MaxReferenceDifference))
                {
                    rejected.Add($"{from} (median {diff:0.##} off the project's heights: another map or an old build)");
                    continue;
                }
                why = rejected.Count > 0 ? "skipped " + string.Join("; ", rejected) : "";
                var description = scene ? $"scene: full_logic_map + {patches.Count} prop height patches ({from})" : $"full_logic_map ({from})";
                return new GroundHeight(description, true, at)
                {
                    ReferenceDifference = diff,
                    _for = scene
                        ? p =>
                        {
                            var key = GroundClamp.Key(p.Model);
                            return field.Height((float)p.X, (float)p.Z,
                                o => Math.Abs(o.X - p.X) < 0.05 && Math.Abs(o.Z - p.Z) < 0.05 && GroundClamp.Key(o.Model) == key);
                        }
                        : null,
                };
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
            {
                rejected.Add($"{from} unreadable ({ex.Message})");
            }
        }
        why = rejected.Count > 0
            ? "no built full_logic_map matches this project: " + string.Join("; ", rejected)
            : $"no full_logic_map.compressed_map for {mapName} (build the heightmaps first)";
        return null;
    }

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
