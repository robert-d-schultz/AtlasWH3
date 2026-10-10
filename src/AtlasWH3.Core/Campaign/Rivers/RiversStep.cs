using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Models;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign.Rivers;

/// <summary>models\river_&lt;entity id&gt;.wsmodel and .wsmodel.rigid_model_v2 for every river of the map's exported
/// layers (BOB "Terry file"), baked as BOB does (<see cref="Wh3River"/>). global_props places the same rivers.</summary>
public sealed class RiversStep : ICampaignBuildStep
{
    public string Name => "rivers";
    public string ReplacesBobAction => "Terry file (models\\river_<id>)";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) =>
        File.Exists(ctx.TerryFile) ? [] : [$"missing {ctx.TerryFile}"];

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var rivers = Read(ctx.TerryFile, notes);
        var models = ctx.OutFile("models");
        Directory.CreateDirectory(models);
        foreach (var old in Directory.EnumerateFiles(models, "river_*"))
            File.Delete(old);

        var built = new ConcurrentDictionary<string, RigidModelV2?>();
        Parallel.ForEach(rivers, new ParallelOptions { CancellationToken = ctx.Cancel }, r => built[r.Id] = Wh3River.Build(r.Spline));
        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var (id, spline) in rivers)
        {
            if (built[id] is not { } model) { skipped.Add(id); continue; }
            var mesh = Path.Combine(models, $"river_{id}.wsmodel.rigid_model_v2");
            var wsmodel = Path.Combine(models, $"river_{id}.wsmodel");
            model.Write(mesh);
            File.WriteAllText(wsmodel, WsModel.River(ctx.MapName, id, spline.Material));
            written.AddRange([mesh, wsmodel]);
        }
        notes.Insert(0, $"{written.Count / 2} rivers");
        if (skipped.Count > 0)
            notes.Add($"{skipped.Count} {(skipped.Count == 1 ? "river" : "rivers")} with no segment or over {Wh3River.MaxVertices - 1} vertices (BOB writes none): "
                      + string.Join(", ", skipped.Take(10)) + (skipped.Count > 10 ? $" (+{skipped.Count - 10} more)" : ""));
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>The rivers BOB bakes: top-level entities with ECRiver and ECRiverSpline in the exported layers, outside
    /// non-exported groups (the entities global_props places), by entity id.</summary>
    public static List<(string Id, Wh3River.Spline Spline)> Read(string terryPath, List<string>? notes = null)
    {
        var result = new List<(string, Wh3River.Spline)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int noSpline = 0, noRiver = 0;
        foreach (var layer in TerryProject.Load(terryPath).Layers())
        {
            if (!layer.IsFile || !layer.Export || layer.FilePath is not { } file || !File.Exists(file)) continue;
            if (!File.ReadLines(file).Any(l => l.Contains("<ECRiver", StringComparison.Ordinal))) continue;
            var root = XDocument.Load(file).Root!;
            var entities = root.Element("entities")?.Elements("entity").ToList() ?? [];
            var hidden = Wh3GlobalPropsBuilder.HiddenMembers(root, entities);
            foreach (var e in entities)
            {
                var id = (string?)e.Attribute("id") ?? "";
                if (e.Element("ECLayer") is not null || hidden.Contains(id)) continue;
                bool river = e.Element("ECRiver") is not null, spline = e.Element("ECRiverSpline") is not null;
                if (river && !spline) noSpline++;
                if (spline && !river) noRiver++;
                if (river && spline && seen.Add(id)) result.Add((id, Wh3River.Read(e)!));
            }
        }
        if (noSpline > 0) notes?.Add($"{noSpline} ECRiver {(noSpline == 1 ? "entity" : "entities")} without an ECRiverSpline: no model (global_props still places it)");
        if (noRiver > 0) notes?.Add($"{noRiver} ECRiverSpline {(noRiver == 1 ? "entity" : "entities")} without ECRiver: skipped (global_props places no river for it)");
        return result;
    }
}
