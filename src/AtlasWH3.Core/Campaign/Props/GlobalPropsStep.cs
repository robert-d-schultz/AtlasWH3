using System.Diagnostics;
using System.Xml.Linq;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>global_props.bin and global_props_sound.bin from the map's layers (BOB "Terry file"), BMD v27
/// (<see cref="Wh3GlobalPropsBuilder"/>).</summary>
public sealed class GlobalPropsStep : ICampaignBuildStep
{
    public string Name => "global_props";
    public string ReplacesBobAction => "Terry file (global_props.bin, global_props_sound.bin)";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx)
    {
        var missing = new List<string>();
        if (!File.Exists(ctx.TerryFile)) missing.Add($"missing {ctx.TerryFile}");
        if (!Directory.Exists(ctx.Paths.GameDataDir)) missing.Add($"missing game data folder {ctx.Paths.GameDataDir} (models and materials)");
        if (HexRegionLookup.ForMap(ctx.Paths, out var why) is null) missing.Add($"no map.hex region lookup: {why}");
        return missing;
    }

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var lookup = HexRegionLookup.ForMap(ctx.Paths, out var why) ?? throw new InvalidOperationException(why);
        var packs = GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks);
        var project = TerryProject.Load(ctx.TerryFile);
        // the quadtree covers the map bounds of map_data.esf grown by the playable-areas row's quadtree_margin on every
        // side (CA's vanilla combi file and the user's GUI BOB runs: IEE -10..1078.11 x -10..758.57; 2026-10-10)
        var bounds = HexRegionLookup.EsfHeaderBounds(Path.Combine(ctx.Paths.AkWorkingCampaignMapDir, "map_data.esf")) is { } b
            ? (b.MinX, b.MinY, b.MaxX, b.MaxY) : lookup.QuadRoot;
        var (margin, marginFrom) = QuadtreeMargin(ctx);
        var root = (bounds.Item1 - margin, bounds.Item2 - margin, bounds.Item3 + margin, bounds.Item4 + margin);
        var builder = new Wh3GlobalPropsBuilder(packs, PrefabLibrary.ForKit(ctx.Paths.AssemblyKitRoot, "campaign"),
            Wh3GlobalPropsBuilder.ReadCultures(ctx.Paths.AssemblyKitRoot), ctx.MapName, root,
            (x, z) => lookup.RegionAt(x, z) ?? NoRegion)
        {
            Trace = Environment.GetEnvironmentVariable("ATLASWH3_GP_TRACE") is { Length: > 0 } trace ? TraceTo(trace) : null,
        };
        var layers = project.Layers().Where(l => l.IsFile && l.Export && l.FilePath is { } f && File.Exists(f)).ToList();
        ctx.Log($"{layers.Count} exported layers...");
        foreach (var layer in layers)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            builder.AddLayer(layer.FilePath!);
        }
        ctx.Log($"{builder.ObjectCount:N0} objects, {builder.SoundCount:N0} sound emitters; writing...");
        var props = builder.BuildProps();
        var sound = builder.BuildSound();
        var propsPath = ctx.OutFile("global_props.bin");
        var soundPath = ctx.OutFile("global_props_sound.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(propsPath)!);
        File.WriteAllBytes(propsPath, GlobalProps.Pack(props));
        File.WriteAllBytes(soundPath, GlobalProps.Pack(sound));
        var notes = new List<string>
        {
            $"{layers.Count} layers -> {builder.ObjectCount:N0} objects in {props.Count:N0} bodies, {builder.SoundCount:N0} sound emitters in {sound.Count:N0} bodies",
        };
        if (builder.MissingModels is { Count: > 0 } missing)
            notes.Add($"{missing.Count} {(missing.Count == 1 ? "model" : "models")} not in the game or mod packs (or unreadable), boxed as 2 x 2 x 2: "
                      + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? $" (+{missing.Count - 10} more)" : ""));
        notes.Add($"quadtree root {root.Item1:G9}..{root.Item3:G9} x {root.Item2:G9}..{root.Item4:G9} (quadtree_margin {margin:G9}, {marginFrom})");
        notes.AddRange(builder.Notes.Distinct());
        return new StepResult(Name, [propsPath, soundPath], notes, sw.Elapsed);
    }

    private static Action<string> TraceTo(string path)
    {
        var w = new StreamWriter(path) { AutoFlush = true };
        return line => { lock (w) w.WriteLine(line); };
    }

    /// <summary>quadtree_margin when the map has no campaign_map_playable_areas row: every vanilla row's value.</summary>
    public const float DefaultQuadtreeMargin = 10f;

    /// <summary>The map's campaign_map_playable_areas quadtree_margin, from the map's mod packs and the vanilla db packs
    /// (a higher-priority pack winning), the row the game reads. BOB in the GUI pads the bmd quadtree by it; headless
    /// runs in the scratch kit did not (not yet explained), so their cells are not the game's.</summary>
    public static (float Margin, string From) QuadtreeMargin(CampaignBuildContext ctx)
    {
        if (!Directory.Exists(ctx.Paths.GameDataDir)) return (DefaultQuadtreeMargin, "no game data folder: default");
        var packs = GameSetup.OpenWithLinked(ctx.Paths.GameDataDir, ctx.Paths.ModPacks, n => n.StartsWith("db", StringComparison.OrdinalIgnoreCase));
        foreach (var (t, row) in DbBinaryTable.PackRows(packs, "campaign_map_playable_areas_tables"))
            if (string.Equals((string?)t.Get(row, "mapname"), ctx.MapName, StringComparison.OrdinalIgnoreCase) &&
                t.Get(row, "quadtree_margin") is { } m)
                return (Convert.ToSingle(m, System.Globalization.CultureInfo.InvariantCulture), "campaign_map_playable_areas");
        return (DefaultQuadtreeMargin, "no campaign_map_playable_areas row: default");
    }

    /// <summary>Region of a point outside the map.hex grid.</summary>
    public const string NoRegion = "";

    /// <summary>(layer name, id) of every layer file the .terry exports (ECLayerFile/ECFileLayer with export="true").</summary>
    public static List<(string Name, string Id)> ExportedLayers(string terryPath) =>
        XDocument.Load(terryPath).Descendants("entity")
            .Where(e => (e.Element("ECLayerFile") is not null && (string?)e.Element("ECLayerExport")?.Attribute("export") == "true")
                        || (string?)e.Element("ECFileLayer")?.Attribute("export") == "true")
            .Select(e => ((string?)e.Attribute("name") ?? "", (string)e.Attribute("id")!))
            .Where(l => l.Item1.Length > 0)
            .ToList();
}
