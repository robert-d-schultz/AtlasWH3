using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Formats.Packs;

/// <summary>
/// db-dump &lt;table&gt; [--limit n]: a DB table's files in the game's packs (plus --pack mod packs) as JSON, decoded by
/// RPFM's server: each file's pack, version, columns and first rows.
/// </summary>
static class DbCommands
{
    public static readonly HashSet<string> Names = ["db-dump"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            var table = a.FirstOrDefault(x => !x.StartsWith("--")) ?? throw new ArgumentException("db-dump <table>");
            var i = Array.FindIndex(a, s => s.Equals("--limit", StringComparison.OrdinalIgnoreCase));
            var limit = i >= 0 && i + 1 < a.Length ? int.Parse(a[i + 1], CultureInfo.InvariantCulture) : 50;
            Console.WriteLine(Dump(paths, table, limit).ToJsonString(Indented));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(new JsonObject { ["error"] = ex.Message, ["type"] = ex.GetType().Name }.ToJsonString(Indented));
            return 1;
        }
    }

    private static JsonNode Dump(ProjectPaths paths, string table, int limit)
    {
        var packs = GameSetup.OpenWithMods(paths.GameDataDir, paths.ModPacks);
        var prefix = PackFile.Normalize($"db/{table}/");
        var result = new JsonArray();
        foreach (var file in packs.Packs.SelectMany(p => p.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))).Distinct())
        {
            var t = packs.FindOwner(file)!.ReadTable(file);
            result.Add(new JsonObject
            {
                ["file"] = file, ["pack"] = packs.Origin(file), ["version"] = t.Version, ["rows"] = t.Rows.Count,
                ["columns"] = new JsonArray(t.Columns.Select(c => (JsonNode)$"{c.Name}:{c.Type}").ToArray()),
                ["data"] = new JsonArray(t.Rows.Take(limit).Select(r => (JsonNode)new JsonArray(r.Select(v => JsonValue.Create(v?.ToString())).ToArray<JsonNode?>())).ToArray()),
            });
        }
        return result;
    }
}
