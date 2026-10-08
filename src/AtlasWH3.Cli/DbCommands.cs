using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Packs;

/// <summary>
/// db-* commands: DB layouts (<see cref="DbSchema"/>) and tables from the game's db.pack (plus --pack mod packs).
///  - db-schema-snapshot [--ron &lt;schema_wh3.ron&gt;] [--out &lt;json&gt;]: regenerate the embedded layout snapshot
///  - db-check: decode every table AtlasWH3 reads, report versions and row counts
///  - db-dump &lt;table&gt; [--limit n]: rows as JSON
/// </summary>
static class DbCommands
{
    public static readonly HashSet<string> Names = ["db-schema-snapshot", "db-check", "db-dump"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            JsonNode result = command switch
            {
                "db-schema-snapshot" => Snapshot(Option(a, "--ron") ?? DbSchema.RpfmSchemaPath,
                    Option(a, "--out") ?? Path.Combine(RepoRoot(), "src", "AtlasWH3.Formats", "Db", "Data", "schema_wh3_snapshot.json")),
                "db-check" => Check(paths),
                "db-dump" => Dump(paths, a.FirstOrDefault(x => !x.StartsWith("--")) ?? throw new ArgumentException("db-dump <table>"),
                    int.Parse(Option(a, "--limit") ?? "50", CultureInfo.InvariantCulture)),
                _ => throw new ArgumentException(command),
            };
            Console.WriteLine(result.ToJsonString(Indented));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(new JsonObject { ["error"] = ex.Message, ["type"] = ex.GetType().Name }.ToJsonString(Indented));
            return 1;
        }
    }

    private static JsonNode Snapshot(string ron, string outPath)
    {
        var schema = DbSchema.FromRon(File.ReadAllText(ron), ron);
        File.WriteAllText(outPath, schema.ToSnapshotJson(DbSchema.NeededTables));
        return new JsonObject
        {
            ["ron"] = ron, ["out"] = outPath,
            ["tables"] = new JsonObject(DbSchema.NeededTables.Select(t => KeyValuePair.Create(t, (JsonNode?)new JsonArray(
                schema.Versions(t).Select(d => (JsonNode)d.Version).ToArray())))),
            ["warnings"] = new JsonArray(schema.Warnings.Take(20).Select(w => (JsonNode)w).ToArray()),
        };
    }

    private static PackSet Packs(ProjectPaths paths) =>
        GameSetup.OpenWithLinked(paths.GameDataDir, paths.ModPacks, n => n.StartsWith("db", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string File, PackFile Owner)> TableFiles(PackSet packs, string table)
    {
        var prefix = PackFile.Normalize($"db/{table}/");
        return packs.Packs.SelectMany(p => p.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))).Distinct()
            .Select(f => (f, packs.FindOwner(f)!));
    }

    private static JsonNode Check(ProjectPaths paths)
    {
        var schema = DbSchema.Default;
        var packs = Packs(paths);
        var tables = new JsonObject();
        var failed = 0;
        foreach (var table in DbSchema.NeededTables)
        {
            var files = new JsonArray();
            foreach (var (file, owner) in TableFiles(packs, table))
            {
                var bytes = owner.TryRead(file)!;
                try
                {
                    var t = DbBinaryTable.Read(table, bytes, schema);
                    files.Add(new JsonObject { ["file"] = file, ["pack"] = Path.GetFileName(owner.SourcePath), ["version"] = t.Version, ["rows"] = t.Rows.Count });
                }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException or EndOfStreamException)
                {
                    failed++;
                    files.Add(new JsonObject { ["file"] = file, ["pack"] = Path.GetFileName(owner.SourcePath), ["version"] = DbBinaryTable.ReadVersion(bytes), ["error"] = e.Message });
                }
            }
            tables[table] = files;
        }
        return new JsonObject { ["schema"] = schema.Source, ["failed"] = failed, ["tables"] = tables };
    }

    private static JsonNode Dump(ProjectPaths paths, string table, int limit)
    {
        var packs = Packs(paths);
        var result = new JsonArray();
        foreach (var (file, owner) in TableFiles(packs, table))
        {
            var t = DbBinaryTable.Read(table, owner.TryRead(file)!);
            result.Add(new JsonObject
            {
                ["file"] = file, ["pack"] = Path.GetFileName(owner.SourcePath), ["version"] = t.Version, ["rows"] = t.Rows.Count,
                ["columns"] = new JsonArray(t.Columns.Select(c => (JsonNode)$"{c.Name}:{c.Type}").ToArray()),
                ["data"] = new JsonArray(t.Rows.Take(limit).Select(r => (JsonNode)new JsonArray(r.Select(v => JsonValue.Create(v?.ToString())).ToArray<JsonNode?>())).ToArray()),
            });
        }
        return result;
    }

    private static string RepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "AtlasWH3.slnx"))) return dir;
        throw new DirectoryNotFoundException("run from the repository (or pass --out)");
    }

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
