using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// environment_collection.xml (BOB "Terry file") for the map and, when there is one, its devastated project's folder,
/// which gets the same file (BOB compiled the devastated project's own). It is the map's project compiled
/// (WH3_visual_map_decompiler's EnvironmentVolumeWriter reads it back): <see cref="GlobalLighting"/>; a SPHERE or
/// CYLINDER per ECEnvironmentVolume entity of the exported layers (ECDoubleSphere / ECDoubleCylinder radii, the entity
/// position; a cylinder's height is dropped), in layer and entity order; and a DEVASTATION per name=path pair of the .terry's devastation_light_environments. Numbers are %f, lines CRLF, an empty
/// section self-closed.
/// </summary>
public sealed class EnvironmentStep : ICampaignBuildStep
{
    /// <summary>The global_lighting of every map: vanilla combi's, which suits any campaign map (the user, 2026-10-11).
    /// The .terry's is not used: it is what Terry shows, so authors keep a battle environment there (IEE's names a file
    /// only the kit's working_data\Weather has).</summary>
    public const string GlobalLighting = "weather/campaign/combi/default.environment";

    public string Name => "environment";
    public string ReplacesBobAction => "Terry file (environment_collection.xml)";
    public IReadOnlyList<string> DependsOn => [];

    public IReadOnlyList<string> CheckInputs(CampaignBuildContext ctx) =>
        File.Exists(ctx.TerryFile) ? [] : [$"missing {ctx.TerryFile}"];

    public StepResult Run(CampaignBuildContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var written = new List<string>();
        var notes = new List<string>();
        var project = TerryProject.Load(ctx.TerryFile);
        var (text, spheres, cylinders) = Build(project);
        void Write(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "environment_collection.xml");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            written.Add(path);
        }
        Write(ctx.TerrainOutDir);
        notes.Add($"{project.MapName}: {spheres} spheres, {cylinders} cylinders");
        // the devastated folder's file is the map's own, byte for byte (the user, 2026-10-11): an area keeps the map's
        // lighting volumes when it devastates. BOB compiled it from the devastated project, whose volumes lag the map's.
        if (DevastationPiecesStep.DevastatedMapName(ctx) is { } devastated)
        {
            Write(Path.Combine(ctx.TargetRoot, "terrain", "campaigns", devastated));
            notes.Add($"{devastated}: a copy of {project.MapName}'s");
        }
        return new StepResult(Name, written, notes, sw.Elapsed);
    }

    /// <summary>The file's text and its volume counts.</summary>
    public static (string Text, int Spheres, int Cylinders) Build(TerryProject project)
    {
        var spheres = new List<string>();
        var cylinders = new List<string>();
        foreach (var layer in project.Layers().Where(l => l.IsFile && l.Export && l.FilePath is { } f && File.Exists(f)))
        {
            var root = XDocument.Load(layer.FilePath!).Root!;
            var entities = root.Element("entities")?.Elements("entity").ToList() ?? [];
            var hidden = Wh3GlobalPropsBuilder.HiddenMembers(root, entities);
            foreach (var e in entities)
            {
                if (e.Element("ECEnvironmentVolume") is not { } volume || hidden.Contains((string?)e.Attribute("id") ?? "")) continue;
                var p = Floats((string?)e.Element("ECTransform")?.Attribute("position") ?? "0 0 0");
                var lighting = Escape((string?)volume.Attribute("lighting_file") ?? "");
                if (e.Element("ECDoubleSphere") is { } s)
                    spheres.Add($"\t\t<SPHERE serialise_version='1' lighting='{lighting}' x='{F(p[0])}' y='{F(p[1])}' z='{F(p[2])}' inner_radius='{F(R(s, "inner_radius"))}' outer_radius='{F(R(s, "outer_radius"))}'/>");
                else if (e.Element("ECDoubleCylinder") is { } c)
                    cylinders.Add($"\t\t<CYLINDER serialise_version='1' lighting='{lighting}' x='{F(p[0])}' y='{F(p[1])}' z='{F(p[2])}' inner_radius='{F(R(c, "inner_radius"))}' outer_radius='{F(R(c, "outer_radius"))}'/>");
            }
        }
        var devastations = (project.Setting("devastation_light_environments") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .Select(kv => $"\t\t<DEVASTATION serialise_version='1' name='{Escape(kv[0].Trim())}' lighting='{Escape(kv[1].Trim())}'/>")
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"<ENVIRONMENT_COLLECTION serialise_version='4' global_lighting='{GlobalLighting}'>\r\n");
        Section(sb, "ENVIRONMENT_SPHERES", spheres);
        Section(sb, "ENVIRONMENT_CYLINDERS", cylinders);
        Section(sb, "ENVIRONMENT_DEVASTATIONS", devastations);
        sb.Append("</ENVIRONMENT_COLLECTION>\r\n");
        return (sb.ToString(), spheres.Count, cylinders.Count);
    }

    private static void Section(StringBuilder sb, string name, List<string> lines)
    {
        if (lines.Count == 0) { sb.Append($"\t<{name}/>\r\n"); return; }
        sb.Append($"\t<{name}>\r\n");
        foreach (var l in lines) sb.Append(l).Append("\r\n");
        sb.Append($"\t</{name}>\r\n");
    }

    private static float R(XElement e, string name) =>
        float.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>printf's %f of a float.</summary>
    private static string F(float v) => ((double)v).ToString("F6", CultureInfo.InvariantCulture);

    private static float[] Floats(string s) =>
        s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("'", "&apos;").Replace("<", "&lt;");
}
