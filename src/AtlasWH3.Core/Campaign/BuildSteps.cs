using System.Diagnostics;
using AtlasWH3.Core.Exporters;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>campaign_maps\&lt;map&gt;\*lookup*.bmp → .tga, .dds and _minimap.tga (BOB "Texture / Convert lookup texture").</summary>
public sealed class LookupStep : ICampaignBuildStep
{
    public string Name => "lookup";
    public string ReplacesBobAction => "Texture / Convert lookup texture";
    public IReadOnlyList<string> DependsOn => [];

    /// <summary>The palette entries the game reads (the user's finding on Old World).</summary>
    public const int GamePaletteEntries = 1024;

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (Sources(ctx).Count == 0)
            missing.Add($"no *lookup*.bmp in {ctx.Paths.AkWorkingCampaignMapDir} or {ctx.Paths.AkDesignCampaignMapDir}");
        try
        {
            if (RegionList(ctx.LookupLastRegions).Count > 0 && !Directory.Exists(ctx.Paths.GameDataDir))
                missing.Add($"missing game data folder {ctx.Paths.GameDataDir} (regions_tables, for the lookup's region list)");
        }
        catch (IOException e) { missing.Add(e.Message); }
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var written = new List<string>();
        var notes = new List<string>();
        var last = RegionList(ctx.LookupLastRegions);
        Dictionary<string, uint>? regions = null;
        Dictionary<string, uint> Regions() => regions ??= RegionColours(ctx);
        var moving = new HashSet<uint>();
        if (last.Count > 0)
        {
            foreach (var key in last)
                if (Regions().TryGetValue(key, out var colour)) moving.Add(colour);
                else notes.Add($"lookup region list: {key} is not in regions_tables");
        }
        var found = new HashSet<uint>();
        Directory.CreateDirectory(ctx.CampaignMapOutDir);
        foreach (var bmp in Sources(ctx))
        {
            ctx.Log($"{Path.GetFileName(bmp)}...");
            var lookup = LookupTexture.FromBmp(bmp);
            var name = Path.GetFileName(bmp);
            if (lookup.Palette.Count > GamePaletteEntries) found.UnionWith(lookup.Palette.Where(moving.Contains));
            // only a lookup the game can't read whole (the small ones, e.g. elector_counts_small, hold region colours too);
            // black (CAIME's hexes without a region) goes with the listed regions
            if (lookup.Palette.Count > GamePaletteEntries && lookup.Palette.Any(moving.Contains))
            {
                var moved = lookup.Palette.Count(c => moving.Contains(c) || c == NoRegion);
                lookup = lookup.MoveToEnd(new HashSet<uint>(moving) { NoRegion });
                notes.Add($"{name}: {moved} of {lookup.Palette.Count} palette entries moved to the end (the region list, and black)");
            }
            if (lookup.Palette.Count > GamePaletteEntries)
            {
                // names only; without a list the step must not need the packs
                Dictionary<uint, string> keys;
                try { keys = Regions().GroupBy(r => r.Value).ToDictionary(g => g.Key, g => g.First().Key); }
                catch (Exception e) when (last.Count == 0 && e is IOException or InvalidDataException or NotSupportedException) { keys = []; }
                var unread = lookup.Palette.Skip(GamePaletteEntries).Where(c => !moving.Contains(c) && c != NoRegion).ToList();
                if (unread.Count > 0)
                    notes.Add($"{name}: {unread.Count} region colours past palette entry {GamePaletteEntries}, which the game doesn't read " +
                              $"(list regions to move past it in the build profile): " +
                              string.Join(", ", unread.Take(20).Select(c => keys.GetValueOrDefault(c, $"#{c:X6}"))) + (unread.Count > 20 ? ", …" : ""));
            }
            var stem = Path.Combine(ctx.CampaignMapOutDir, Path.GetFileNameWithoutExtension(bmp));
            File.WriteAllBytes(stem + ".tga", lookup.ToTga());
            File.WriteAllBytes(stem + ".dds", lookup.ToDds());
            File.WriteAllBytes(stem + "_minimap.tga", lookup.Minimap().ToTga());
            written.AddRange([stem + ".tga", stem + ".dds", stem + "_minimap.tga"]);
        }
        var absent = last.Where(k => regions!.TryGetValue(k, out var c) && !found.Contains(c)).ToList();
        if (absent.Count > 0) notes.Add($"lookup region list: not in any lookup: {string.Join(", ", absent)}");
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>CAIME paints hexes without a region black.</summary>
    private const uint NoRegion = 0x000000;

    /// <summary>The setting's entries: region keys, or text files of them (an entry with a '.', '/' or '\'; one key per
    /// line, # starts a comment).</summary>
    public static List<string> RegionList(IEnumerable<string> entries) =>
        entries.Select(e => e.Trim()).SelectMany(e => IsFile(e)
                ? File.Exists(e) ? File.ReadLines(e).Select(l => l.Split('#')[0].Trim())
                  : throw new FileNotFoundException($"lookup region list {e} not found")
                : [e])
            .Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public static bool IsFile(string entry) => entry.IndexOfAny(['.', '/', '\\']) >= 0;

    /// <summary>Region key → lookup colour (regions_tables r, g, b, which CAIME paints the lookup with), from the
    /// map's mod packs and the vanilla db packs, a higher-priority pack winning.</summary>
    private static Dictionary<string, uint> RegionColours(CampaignBuildContext ctx)
    {
        var packs = GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks,
            n => n.StartsWith("db", StringComparison.OrdinalIgnoreCase));
        var colours = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (t, row) in DbBinaryTable.PackRows(packs, "regions_tables"))
            colours.TryAdd((string)t.Get(row, "key")!, (uint)(Convert.ToInt32(t.Get(row, "r")) << 16 |
                                                            Convert.ToInt32(t.Get(row, "g")) << 8 | Convert.ToInt32(t.Get(row, "b"))));
        return colours;
    }

    /// <summary>Lookup bitmaps by file name; the working_data copy wins over EmpireDesignData.</summary>
    private static List<string> Sources(CampaignBuildContext ctx) =>
        new[] { ctx.Paths.AkWorkingCampaignMapDir, ctx.Paths.AkDesignCampaignMapDir }
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*lookup*.bmp"))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
}

/// <summary>A BOB action whose algorithm has not been reverse-engineered yet (Phase 2).</summary>
/// <param name="preflight">Optional check of the BOB action's own inputs, so <c>diagnose</c> reports them before BOB runs.</param>
public sealed class PendingStep(string name, string bobAction, string[] dependsOn, string whatIsMissing,
                                Func<CampaignBuildContext, IEnumerable<string>>? preflight = null) : ICampaignBuildStep
{
    public string Name => name;
    public string ReplacesBobAction => bobAction;
    public IReadOnlyList<string> DependsOn => dependsOn;
    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) =>
        [$"not native yet: {whatIsMissing}", .. preflight?.Invoke(ctx) ?? []];
    public StepResult Run(CampaignBuildContext ctx) => throw new NotSupportedException($"Step '{name}' is not native yet: {whatIsMissing}");
}
