using System.Globalization;
using System.Text.Json.Nodes;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Formats.Db;

/// <summary>
/// DB tables from packs (db/&lt;table&gt;/&lt;file&gt;), decoded by RPFM's server with its schema
/// (<see cref="PackFile.ReadTable"/>). Columns are RPFM's processed ones: a colour stored as r, g, b fields is one
/// ColourRGB column (read it with <see cref="Table.Colour"/>).
/// </summary>
public static class DbTables
{
    /// <summary>RPFM's field types (the ones WH3's tables use); anything else is kept as its JSON text.</summary>
    public enum FieldType { Boolean, I16, I32, I64, F32, F64, ColourRGB, StringU8, StringU16, OptionalStringU8, OptionalStringU16, OptionalI32, Other }
    public sealed record Column(string Name, FieldType Type);

    /// <summary>A table file's rows. Values are bool, int, long, float, double, string, null (an unset OptionalI32), or
    /// uint 0x00RRGGBB for a ColourRGB.</summary>
    public sealed record Table(string Name, int Version, Column[] Columns, List<object?[]> Rows)
    {
        public int Index(string column) => Array.FindIndex(Columns, c => c.Name == column);
        public object? Get(object?[] row, string column) => Index(column) is var i and >= 0 ? row[i]
            : throw new KeyNotFoundException($"{Name} v{Version} has no column {column}");

        /// <summary>The row's colour (0x00RRGGBB): the table's one ColourRGB column, whatever RPFM names it.</summary>
        public uint Colour(object?[] row)
        {
            var colours = Columns.Select((c, i) => (c, i)).Where(c => c.c.Type == FieldType.ColourRGB).ToList();
            if (colours.Count != 1) throw new InvalidDataException($"{Name} v{Version} has {colours.Count} colour columns, not one");
            return (uint)row[colours[0].i]!;
        }
    }

    /// <summary>The rows of every file of <paramref name="table"/>, highest-priority pack first (a file name shadowed by a
    /// higher-priority pack is read from that pack only).</summary>
    public static IEnumerable<(Table Table, object?[] Row)> PackRows(PackSet packs, string table)
    {
        var prefix = PackFile.Normalize($"db/{table}/");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in packs.Packs)
        foreach (var file in pack.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (!seen.Add(file)) continue;
            var t = pack.ReadTable(file);
            foreach (var row in t.Rows) yield return (t, row);
        }
    }

    /// <summary>A table from RPFM's table.info and table.rows replies.</summary>
    internal static Table FromRpfm(JsonNode info, JsonNode rows)
    {
        var byName = info["columns"]!.AsArray().ToDictionary(c => (string)c!["name"]!,
            c => Enum.TryParse<FieldType>((string?)c!["field_type"], out var t) ? t : FieldType.Other);
        var columns = rows["columns"]!.AsArray().Select(c => new Column((string)c!, byName.GetValueOrDefault((string)c!, FieldType.Other))).ToArray();
        var list = rows["rows"]!.AsArray().Select(r =>
        {
            var values = r!["values"]!.AsArray();
            return columns.Select((c, i) => Value(c, values[i])).ToArray();
        }).ToList();
        return new Table((string)info["table_name"]!, (int)info["version"]!, columns, list);
    }

    private static object? Value(Column column, JsonNode? v) => v is null ? null : column.Type switch
    {
        FieldType.Boolean => v.GetValue<bool>(),
        FieldType.I16 or FieldType.I32 or FieldType.OptionalI32 => v.GetValue<int>(),
        FieldType.I64 => v.GetValue<long>(),
        FieldType.F32 => v.GetValue<float>(),
        FieldType.F64 => v.GetValue<double>(),
        FieldType.ColourRGB => uint.Parse(v.GetValue<string>().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        FieldType.StringU8 or FieldType.StringU16 or FieldType.OptionalStringU8 or FieldType.OptionalStringU16 => v.GetValue<string>(),
        _ => v.ToJsonString(),
    };
}
