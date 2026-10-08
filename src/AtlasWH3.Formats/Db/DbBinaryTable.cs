using System.Text;

namespace AtlasWH3.Formats.Db;

/// <summary>
/// Reads a binary DB table from a pack (db/&lt;table&gt;/&lt;file&gt;): optional GUID (FD FE FC FF, u16 length, UTF-16),
/// optional version (FC FD FE FF, i32), u8, u32 row count, then the rows in schema column order. Layouts come from
/// <see cref="DbSchema"/> (RPFM's schema file, else the embedded snapshot); an unknown version is an error that says so.
/// </summary>
public static class DbBinaryTable
{
    /// <summary>RPFM's field types (the ones WH3's schema uses). ColourRGB is a u32 0x00RRGGBB.</summary>
    public enum FieldType { Boolean, I16, I32, I64, F32, F64, ColourRGB, StringU8, StringU16, OptionalStringU8, OptionalStringU16, OptionalI32 }
    public sealed record Column(string Name, FieldType Type);

    public sealed record Table(string Name, int Version, Column[] Columns, List<object?[]> Rows)
    {
        public int Index(string column) => Array.FindIndex(Columns, c => c.Name == column);
        public object? Get(object?[] row, string column) => Index(column) is var i and >= 0 ? row[i]
            : throw new KeyNotFoundException($"{Name} v{Version} has no column {column}");
    }

    /// <summary>The table file's version (0 when it has no version marker).</summary>
    public static int ReadVersion(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        SkipHeader(r, out var version);
        return version;
    }

    public static Table Read(string table, byte[] data, DbSchema? schema = null)
    {
        schema ??= DbSchema.Default;
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        SkipHeader(r, out var version);
        r.ReadByte();
        var count = r.ReadUInt32();
        var definition = schema.Find(table, version)
            ?? throw new NotSupportedException($"{table} version {version} is not in {schema.Source}" +
                                               (schema.Source.EndsWith(".ron") ? "" : " (install RPFM and update its schemas, or update AtlasWH3)") +
                                               $"; known versions: {string.Join(", ", schema.Versions(table).Select(d => d.Version))}");
        var columns = definition.Fields.Select(f => new Column(f.Name, f.Type)).ToArray();
        var rows = new List<object?[]>((int)count);
        for (var i = 0; i < count; i++)
        {
            var row = new object?[columns.Length];
            for (var c = 0; c < columns.Length; c++)
                row[c] = columns[c].Type switch
                {
                    FieldType.Boolean => r.ReadByte() != 0,
                    FieldType.I16 => (int)r.ReadInt16(),
                    FieldType.I32 => r.ReadInt32(),
                    FieldType.I64 => r.ReadInt64(),
                    FieldType.F32 => r.ReadSingle(),
                    FieldType.F64 => r.ReadDouble(),
                    FieldType.ColourRGB => r.ReadInt32(),
                    FieldType.StringU8 => StringU8(r),
                    FieldType.StringU16 => StringU16(r),
                    FieldType.OptionalStringU8 => r.ReadByte() != 0 ? StringU8(r) : "",
                    FieldType.OptionalStringU16 => r.ReadByte() != 0 ? StringU16(r) : "",
                    FieldType.OptionalI32 => r.ReadByte() != 0 ? r.ReadInt32() : null,
                    _ => throw new NotSupportedException(columns[c].Type.ToString()),
                };
            rows.Add(row);
        }
        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($"{table} v{version}: {data.Length - r.BaseStream.Position} bytes left after {count} rows (schema mismatch?)");
        return new Table(table, version, columns, rows);
    }

    private static void SkipHeader(BinaryReader r, out int version)
    {
        version = 0;
        var data = r.BaseStream;
        if (data.Length >= 4 && r.ReadUInt32() == 0xFFFCFEFD)
        {
            var len = r.ReadUInt16();
            r.ReadBytes(len * 2);
        }
        else data.Position = 0;
        var at = data.Position;
        if (data.Length - at >= 8 && r.ReadUInt32() == 0xFFFEFDFC) version = r.ReadInt32();
        else data.Position = at;
    }

    private static string StringU8(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
    private static string StringU16(BinaryReader r) => Encoding.Unicode.GetString(r.ReadBytes(r.ReadUInt16() * 2));
}
