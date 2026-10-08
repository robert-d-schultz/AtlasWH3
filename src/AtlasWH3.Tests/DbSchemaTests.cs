using AtlasWH3.Formats.Db;
using AtlasWH3.Formats.Packs;

namespace AtlasWH3.Tests;

public class DbSchemaTests
{
    private const string Ron = """
        (
            version: 5,
            definitions: {
                "things_tables": [
                    (
                        version: 2,
                        fields: [
                            ( name: "key", field_type: StringU8, is_key: true, default_value: None, description: "a (tricky) \"name\" [x]", enum_values: {}, ),
                            ( name: "n", field_type: I32, is_key: false, default_value: Some("0"), ),
                            ( name: "colour", field_type: ColourRGB, ),
                            ( name: "note", field_type: OptionalStringU8, ),
                        ],
                        localised_fields: [],
                    ),
                    ( version: 1, fields: [ ( name: "key", field_type: StringU8, ), ], ),
                ],
                "nested_tables": [
                    ( version: 0, fields: [ ( name: "seq", field_type: SequenceU32(( version: 0, fields: [] )), ), ], ),
                ],
            },
        )
        """;

    private static byte[] TableFile(int version, params byte[][] rows)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0xFFFCFEFD); w.Write((ushort)1); w.Write((ushort)'x');      // GUID
        w.Write(0xFFFEFDFC); w.Write(version);                               // version
        w.Write((byte)1); w.Write(rows.Length);
        foreach (var r in rows) w.Write(r);
        return ms.ToArray();
    }

    private static byte[] Row(string key, int n, int colour, string? note)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)key.Length); w.Write(System.Text.Encoding.UTF8.GetBytes(key));
        w.Write(n); w.Write(colour);
        w.Write((byte)(note is null ? 0 : 1));
        if (note is not null) { w.Write((ushort)note.Length); w.Write(System.Text.Encoding.UTF8.GetBytes(note)); }
        return ms.ToArray();
    }

    [Fact]
    public void Ron_ParsesRpfmLayouts()
    {
        var schema = DbSchema.FromRon(Ron);
        var v2 = schema.Find("things_tables", 2)!;
        Assert.Equal(["key", "n", "colour", "note"], v2.Fields.Select(f => f.Name));
        Assert.Equal(DbBinaryTable.FieldType.ColourRGB, v2.Fields[2].Type);
        Assert.Single(schema.Find("things_tables", 1)!.Fields);
        // a sequence field is not supported: that version is left out, with a warning
        Assert.Null(schema.Find("nested_tables", 0));
        Assert.Contains(schema.Warnings, w => w.Contains("nested_tables"));
    }

    [Fact]
    public void Read_DecodesWithTheSchemaVersion()
    {
        var schema = DbSchema.FromRon(Ron);
        var t = DbBinaryTable.Read("things_tables", TableFile(2, Row("a", 5, 0x112233, null), Row("b", -1, 0, "hi")), schema);
        Assert.Equal(2, t.Version);
        Assert.Equal(["a", 5, 0x112233, ""], t.Rows[0]);
        Assert.Equal("hi", t.Get(t.Rows[1], "note"));
    }

    [Fact]
    public void Read_ReportsAnUnknownVersion()
    {
        var schema = DbSchema.FromRon(Ron);
        var e = Assert.Throws<NotSupportedException>(() => DbBinaryTable.Read("things_tables", TableFile(7), schema));
        Assert.Contains("version 7", e.Message);
        Assert.Contains("known versions: 2, 1", e.Message);
    }

    [Fact]
    public void Ron_FallsBackToTheSnapshot()
    {
        var schema = DbSchema.FromRon(Ron, "x.ron", DbSchema.Embedded());
        Assert.NotNull(schema.Find("things_tables", 2));
        Assert.NotNull(schema.Find("campaign_tree_ids_tables", 2));
    }

    [Fact]
    public void Snapshot_HoldsEveryNeededTable()
    {
        var snapshot = DbSchema.Embedded();
        Assert.All(DbSchema.NeededTables, t => Assert.NotEmpty(snapshot.Versions(t)));
        // round trip through its own JSON
        Assert.Equal(snapshot.ToSnapshotJson(DbSchema.NeededTables).Length,
            DbSchema.Embedded().ToSnapshotJson(DbSchema.NeededTables).Length);
    }

    /// <summary>The installed RPFM schema parses, and it agrees with the snapshot on every version both have.</summary>
    [Fact]
    public void RpfmSchema_AgreesWithTheSnapshot()
    {
        if (!File.Exists(DbSchema.RpfmSchemaPath)) return;
        var ron = DbSchema.FromRon(File.ReadAllText(DbSchema.RpfmSchemaPath));
        var snapshot = DbSchema.Embedded();
        foreach (var table in DbSchema.NeededTables)
            foreach (var def in snapshot.Versions(table))
                if (ron.Find(table, def.Version) is { } r)
                    Assert.Equal(def.Fields, r.Fields);
    }

    [Fact]
    public void Wh3_NeededTablesDecode()
    {
        var data = TestKits.Wh3GameData;
        if (!File.Exists(Path.Combine(data, "db.pack"))) return;
        var db = PackFile.Open(Path.Combine(data, "db.pack"));
        foreach (var table in DbSchema.NeededTables)
        {
            var file = db.Entries.Keys.First(k => k.StartsWith($@"db\{table}\", StringComparison.Ordinal));
            Assert.NotEmpty(DbBinaryTable.Read(table, db.TryRead(file)!, DbSchema.Embedded()).Rows);
        }
        var ids = DbBinaryTable.Read("campaign_tree_ids_tables", db.TryRead(@"db\campaign_tree_ids_tables\data__")!);
        Assert.Contains(ids.Rows, r => (string)ids.Get(r, "tree_id")! == "grass_01");
    }
}
