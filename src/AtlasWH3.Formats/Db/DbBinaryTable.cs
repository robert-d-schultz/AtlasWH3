using System.Text;

namespace AtlasWH3.Formats.Db;

/// <summary>
/// Reads a binary DB table from a pack (db/&lt;table&gt;/&lt;file&gt;): optional GUID (FD FE FC FF, u16 length, UTF-16),
/// optional version (FC FD FE FF, i32), u8, u32 row count, then the rows in schema column order. Only the few tables
/// AtlasWH3 needs have schemas here (from RPFM's schema_3k.ron), so it works without RPFM installed.
/// </summary>
public static class DbBinaryTable
{
    public enum FieldType { Boolean, I32, F32, StringU8, OptionalStringU8 }
    public sealed record Column(string Name, FieldType Type);

    /// <summary>(table, version) → columns in binary order.</summary>
    public static readonly IReadOnlyDictionary<(string Table, int Version), Column[]> Schemas = new Dictionary<(string, int), Column[]>
    {
        [("campaign_tree_ids_tables", 3)] = [new("colour_b", FieldType.I32), new("colour_g", FieldType.I32), new("colour_r", FieldType.I32),
                                             new("tree_id", FieldType.StringU8), new("can_be_removed", FieldType.Boolean)],
        [("campaign_tree_variants_tables", 4)] = [new("tree_id", FieldType.StringU8), new("tree_rigid", FieldType.StringU8),
                                                  new("tree_audio", FieldType.OptionalStringU8), new("season", FieldType.OptionalStringU8)],
        [("seasons_tables", 3)] = [new("season", FieldType.StringU8), new("longname", FieldType.StringU8), new("shortname", FieldType.StringU8),
                                   new("onscreen_name", FieldType.StringU8), new("index", FieldType.I32), new("default", FieldType.Boolean),
                                   new("battle_default", FieldType.Boolean), new("convert_rain_to_snow", FieldType.Boolean),
                                   new("convert_snow_to_rain", FieldType.Boolean)],
    };

    public sealed record Table(string Name, int Version, Column[] Columns, List<object?[]> Rows)
    {
        public int Index(string column) => Array.FindIndex(Columns, c => c.Name == column);
    }

    public static Table Read(string table, byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var version = 0;
        if (data.Length >= 4 && r.ReadUInt32() == 0xFFFCFEFD)
        {
            var len = r.ReadUInt16();
            r.ReadBytes(len * 2);
        }
        else r.BaseStream.Position = 0;
        var at = r.BaseStream.Position;
        if (r.ReadUInt32() == 0xFFFEFDFC) version = r.ReadInt32();
        else r.BaseStream.Position = at;
        r.ReadByte();
        var count = r.ReadUInt32();
        if (!Schemas.TryGetValue((table, version), out var columns))
            throw new NotSupportedException($"no schema for {table} version {version}");
        var rows = new List<object?[]>((int)count);
        for (var i = 0; i < count; i++)
        {
            var row = new object?[columns.Length];
            for (var c = 0; c < columns.Length; c++)
                row[c] = columns[c].Type switch
                {
                    FieldType.Boolean => r.ReadByte() != 0,
                    FieldType.I32 => r.ReadInt32(),
                    FieldType.F32 => r.ReadSingle(),
                    FieldType.StringU8 => StringU8(r),
                    _ => r.ReadByte() != 0 ? StringU8(r) : "",
                };
            rows.Add(row);
        }
        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($"{table}: {data.Length - r.BaseStream.Position} bytes left after {count} rows (schema mismatch?)");
        return new Table(table, version, columns, rows);
    }

    private static string StringU8(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
}
