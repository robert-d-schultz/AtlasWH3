using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AtlasWH3.Formats.Db;

/// <summary>
/// DB table layouts (table, version → fields in binary order). WH3 is still patched, so table versions change; the
/// layouts come from RPFM's schema file when it is installed (<see cref="RpfmSchemaPath"/>, read directly, no RPFM
/// process), and otherwise from a snapshot of the tables AtlasWH3 reads, embedded at build time
/// (<see cref="NeededTables"/>, regenerated with <c>AtlasWH3.Cli db-schema-snapshot</c>). A version neither knows is
/// reported, never guessed.
/// </summary>
public sealed class DbSchema
{
    public sealed record Field(string Name, DbBinaryTable.FieldType Type);
    public sealed record Definition(int Version, Field[] Fields);

    /// <summary>The tables AtlasWH3 reads; the embedded snapshot holds every version of these.</summary>
    public static readonly IReadOnlyList<string> NeededTables =
    [
        "campaigns_tables", "campaign_map_playable_areas_tables",
        "campaign_tree_ids_tables", "campaign_tree_types_tables", "campaign_tree_type_cultures_tables", "campaign_tree_variants_tables",
        "prefab_types_tables", "cultures_tables", "bmd_export_types_tables",
        "campaign_map_event_areas_tables", "campaign_map_event_area_types_tables", "campaign_map_event_area_province_region_junctions_tables",
        "region_to_province_junctions_tables", "battles_tables", "regions_tables",
    ];

    /// <summary>RPFM's WH3 schema: %AppData%\FrodoWazEre\rpfm\config\schemas\schema_wh3.ron.</summary>
    public static string RpfmSchemaPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FrodoWazEre", "rpfm", "config", "schemas", "schema_wh3.ron");

    private readonly Dictionary<string, List<Definition>> _tables;
    private readonly DbSchema? _fallback;

    /// <summary>Where the layouts come from: a .ron path or "embedded snapshot".</summary>
    public string Source { get; }

    /// <summary>Layout problems found while loading (an unsupported field type makes that version unusable).</summary>
    public IReadOnlyList<string> Warnings { get; }

    private DbSchema(string source, Dictionary<string, List<Definition>> tables, List<string> warnings, DbSchema? fallback = null)
    {
        Source = source;
        _tables = tables;
        Warnings = warnings;
        _fallback = fallback;
    }

    public IEnumerable<string> Tables => _tables.Keys;
    public IReadOnlyList<Definition> Versions(string table) => _tables.TryGetValue(table, out var v) ? v : [];

    /// <summary>The layout of <paramref name="table"/> version <paramref name="version"/>, from this schema or its
    /// fallback (the embedded snapshot behind RPFM's file); null when neither knows it.</summary>
    public Definition? Find(string table, int version) =>
        (_tables.TryGetValue(table, out var defs) ? defs.FirstOrDefault(d => d.Version == version) : null) ?? _fallback?.Find(table, version);

    private static readonly Lazy<DbSchema> DefaultSchema = new(() =>
    {
        var embedded = Embedded();
        try
        {
            if (File.Exists(RpfmSchemaPath)) return FromRon(File.ReadAllText(RpfmSchemaPath), RpfmSchemaPath, embedded);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException)
        {
            return new DbSchema(embedded.Source, embedded._tables, [$"{RpfmSchemaPath}: {e.Message}; using the embedded snapshot"]);
        }
        return embedded;
    });

    /// <summary>RPFM's schema when installed (backed by the snapshot), else the snapshot.</summary>
    public static DbSchema Default => DefaultSchema.Value;

    // ------------------------------------------------------------------ embedded snapshot (JSON)

    public static DbSchema Embedded()
    {
        using var stream = typeof(DbSchema).Assembly.GetManifestResourceStream("AtlasWH3.schema_wh3_snapshot.json")
                           ?? throw new InvalidOperationException("embedded schema snapshot missing");
        return FromJson(JsonNode.Parse(stream)!, "embedded snapshot");
    }

    private static DbSchema FromJson(JsonNode root, string source)
    {
        var tables = new Dictionary<string, List<Definition>>(StringComparer.Ordinal);
        foreach (var (table, versions) in root["tables"]!.AsObject())
            tables[table] = versions!.AsArray().Select(v => new Definition((int)v!["version"]!,
                v["fields"]!.AsArray().Select(f => new Field((string)f![0]!, Enum.Parse<DbBinaryTable.FieldType>((string)f[1]!))).ToArray())).ToList();
        return new DbSchema(source, tables, []);
    }

    /// <summary>The snapshot JSON for <paramref name="tables"/>: every version this schema has of each.</summary>
    public string ToSnapshotJson(IEnumerable<string> tables)
    {
        var obj = new JsonObject();
        foreach (var t in tables)
        {
            if (!_tables.TryGetValue(t, out var defs)) throw new KeyNotFoundException($"{Source} has no table {t}");
            obj[t] = new JsonArray(defs.Select(d => (JsonNode)new JsonObject
            {
                ["version"] = d.Version,
                ["fields"] = new JsonArray(d.Fields.Select(f => (JsonNode)new JsonArray(f.Name, f.Type.ToString())).ToArray()),
            }).ToArray());
        }
        var root = new JsonObject { ["source"] = "RPFM schema_wh3.ron (MIT, https://github.com/Frodo45127/rpfm)", ["tables"] = obj };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    // ------------------------------------------------------------------ RPFM .ron

    /// <summary>Parses RPFM's schema file: <c>( version: 5, definitions: { "table": [ ( version: N, fields: [ ( name:
    /// "x", field_type: StringU8, … ), … ], … ), … ], … }, … )</c>.</summary>
    public static DbSchema FromRon(string ron, string source = "schema.ron", DbSchema? fallback = null)
    {
        var root = new RonReader(ron).ReadValue() as Dictionary<string, object?>
                   ?? throw new InvalidDataException("not an RPFM schema (no top-level struct)");
        if (root.GetValueOrDefault("definitions") is not Dictionary<string, object?> definitions)
            throw new InvalidDataException("not an RPFM schema (no definitions)");
        var tables = new Dictionary<string, List<Definition>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var (table, value) in definitions)
        {
            var defs = new List<Definition>();
            foreach (var d in value as List<object?> ?? [])
            {
                if (d is not Dictionary<string, object?> def) continue;
                var version = int.Parse((string)def["version"]!, CultureInfo.InvariantCulture);
                var fields = new List<Field>();
                var ok = true;
                foreach (var f in def.GetValueOrDefault("fields") as List<object?> ?? [])
                {
                    var field = (Dictionary<string, object?>)f!;
                    var type = field["field_type"] as string;
                    if (type is null || !Enum.TryParse<DbBinaryTable.FieldType>(type, out var t))
                    {
                        ok = false;
                        warnings.Add($"{table} v{version}: field {field["name"]} has unsupported type {type ?? "(sequence)"}");
                        break;
                    }
                    fields.Add(new Field((string)field["name"]!, t));
                }
                if (ok) defs.Add(new Definition(version, [.. fields]));
            }
            tables[table] = defs;
        }
        return new DbSchema(source, tables, warnings, fallback);
    }

    /// <summary>A small RON reader for RPFM schema files: structs and maps become dictionaries, lists become lists,
    /// strings stay strings, numbers / identifiers / <c>None</c> become their text, <c>Some(x)</c> becomes x, and a
    /// call like <c>SequenceU32((…))</c> becomes a dictionary with its name under "$".</summary>
    private sealed class RonReader(string s)
    {
        private int _i;

        public object? ReadValue()
        {
            Skip();
            var c = s[_i];
            if (c == '"') return ReadString();
            if (c == '[') return ReadList();
            if (c == '{') return ReadMap();
            if (c == '(') return ReadStruct();
            var word = ReadWord();
            Skip();
            if (_i < s.Length && s[_i] == '(')
            {
                if (word == "Some")
                {
                    _i++;
                    var inner = ReadValue();
                    Expect(')');
                    return inner;
                }
                var args = ReadStruct();
                args["$"] = word;
                return args;
            }
            return word;
        }

        private List<object?> ReadList()
        {
            Expect('[');
            var list = new List<object?>();
            while (true)
            {
                Skip();
                if (s[_i] == ']') { _i++; return list; }
                list.Add(ReadValue());
                Comma();
            }
        }

        private Dictionary<string, object?> ReadMap()
        {
            Expect('{');
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                Skip();
                if (s[_i] == '}') { _i++; return map; }
                var key = ReadValue()?.ToString() ?? "";
                Expect(':');
                map[key] = ReadValue();
                Comma();
            }
        }

        /// <summary>( name: value, … ), or a tuple ( value, … ) with keys "0", "1", ….</summary>
        private Dictionary<string, object?> ReadStruct()
        {
            Expect('(');
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            var n = 0;
            while (true)
            {
                Skip();
                if (s[_i] == ')') { _i++; return map; }
                var at = _i;
                if (char.IsLetter(s[_i]) || s[_i] == '_')
                {
                    var word = ReadWord();
                    Skip();
                    if (s[_i] == ':') { _i++; map[word] = ReadValue(); Comma(); continue; }
                    _i = at;
                }
                map[(n++).ToString(CultureInfo.InvariantCulture)] = ReadValue();
                Comma();
            }
        }

        private string ReadString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (s[_i] != '"')
            {
                if (s[_i] == '\\')
                {
                    _i++;
                    sb.Append(s[_i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var x => x });
                }
                else sb.Append(s[_i]);
                _i++;
            }
            _i++;
            return sb.ToString();
        }

        private string ReadWord()
        {
            var start = _i;
            while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] is '_' or '-' or '.' or '+')) _i++;
            if (_i == start) throw new InvalidDataException($"unexpected '{s[_i]}' at offset {_i}");
            return s[start.._i];
        }

        private void Skip()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i])) _i++;
                else if (s[_i] == '/' && _i + 1 < s.Length && s[_i + 1] == '/') { while (_i < s.Length && s[_i] != '\n') _i++; }
                else break;
            }
        }

        private void Comma()
        {
            Skip();
            if (_i < s.Length && s[_i] == ',') _i++;
        }

        private void Expect(char c)
        {
            Skip();
            if (_i >= s.Length || s[_i] != c) throw new InvalidDataException($"expected '{c}' at offset {_i}");
            _i++;
        }
    }
}
