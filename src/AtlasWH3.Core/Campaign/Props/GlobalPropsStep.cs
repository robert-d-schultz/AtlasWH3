using System.Diagnostics;
using System.Xml.Linq;
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
        // the quadtree covers the map bounds of map_data.esf (Old World: 0..1367.396 x 0..1368.743, fitted to BOB's cells)
        var root = HexRegionLookup.EsfHeaderBounds(Path.Combine(ctx.Paths.AkWorkingCampaignMapDir, "map_data.esf")) is { } b
            ? (b.MinX, b.MinY, b.MaxX, b.MaxY) : lookup.QuadRoot;
        var builder = new Wh3GlobalPropsBuilder(packs, PrefabLibrary.ForKit(ctx.Paths.AssemblyKitRoot, "campaign"),
            Wh3GlobalPropsBuilder.ReadCultures(ctx.Paths.AssemblyKitRoot), ctx.MapName, root,
            (x, z) => lookup.RegionAt(x, z) ?? NoRegion)
        {
            // the build's own files first, then what BOB left in the kit's working_data (river models until step 3.10)
            LooseFile = rel => new[] { ctx.TargetRoot, Path.Combine(ctx.Paths.AssemblyKitRoot, "working_data") }
                .Select(root => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)))
                .Where(File.Exists).Select(File.ReadAllBytes).FirstOrDefault(),
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
        notes.AddRange(builder.Notes.Distinct());
        return new StepResult(Name, [propsPath, soundPath], notes, sw.Elapsed);
    }

    private static Action<string> TraceTo(string path)
    {
        var w = new StreamWriter(path) { AutoFlush = true };
        return line => { lock (w) w.WriteLine(line); };
    }

    /// <summary>Region of a point outside the map.hex grid.</summary>
    public const string NoRegion = "";

    /// <summary>Atlas3K's fallback region (3K's rivers step); kept until that step is ported.</summary>
    public const string NonPlayable = "3k_main_reg_non_playable";

    /// <summary>(layer name, id) of every layer file the .terry exports (ECLayerFile/ECFileLayer with export="true").</summary>
    public static List<(string Name, string Id)> ExportedLayers(string terryPath) =>
        XDocument.Load(terryPath).Descendants("entity")
            .Where(e => (e.Element("ECLayerFile") is not null && (string?)e.Element("ECLayerExport")?.Attribute("export") == "true")
                        || (string?)e.Element("ECFileLayer")?.Attribute("export") == "true")
            .Select(e => ((string?)e.Attribute("name") ?? "", (string)e.Attribute("id")!))
            .Where(l => l.Item1.Length > 0)
            .ToList();
}
