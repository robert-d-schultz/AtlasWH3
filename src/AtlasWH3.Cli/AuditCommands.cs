using System.Diagnostics;
using System.Text.Json;
using AtlasWH3.Core;
using AtlasWH3.Core.Audit;

/// <summary>
/// map-audit: campaign map defects over the project's layers, the lf / sea heights, map.hex and the models
/// (<see cref="MapAudit"/>). Writes findings.csv, summary.json and fix_&lt;check&gt;.json (entity-edit ops) to --out;
/// prints the summary. Read-only: fixes are applied separately with entity-edit.
/// </summary>
static class AuditCommands
{
    public static readonly HashSet<string> Names = ["map-audit"];

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var audit = new MapAudit(paths, Option(a, "--project"), m => Console.Error.WriteLine(m));
            var checks = Option(a, "--checks")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            audit.Run(checks);
            var outDir = Option(a, "--out") ?? Path.Combine(paths.OutputRoot, "audit", paths.MapName);
            audit.Write(outDir);
            var summary = audit.Summary();
            summary["out"] = outDir;
            summary["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
            Console.WriteLine(summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, type = ex.GetType().Name }));
            return 1;
        }
    }

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
