using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Esf;

/// <summary>
/// Read-only tree view of a CAAB ESF file with child records (e.g. campaign_maps\&lt;map&gt;\map_data.esf).
/// Record header: type byte with bit 0x80 = record, 0x40 = nested blocks, 0x20 = long header (u16 name, u8 version;
/// the root always uses it); short header = version in bits 1-4 and name index (bit 0 &lt;&lt; 8 | next byte). Then CA
/// uleb128 size; nested-block records add a group count (the size counts from after it) and a size before each group.
/// </summary>
public sealed class EsfTree
{
    public IReadOnlyList<string> RecordNames { get; }
    public Dictionary<uint, string> Ascii { get; } = [];
    public Dictionary<uint, string> Utf16 { get; } = [];
    public EsfRecord Root { get; }

    private readonly byte[] _b;

    private EsfTree(byte[] b)
    {
        _b = b;
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != CaabFlat.Magic) throw new InvalidDataException("not a CAAB ESF");
        var p = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        var names = new List<string>();
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
        for (var i = 0; i < n; i++)
        {
            int l = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
            names.Add(Encoding.Latin1.GetString(b, p, l)); p += l;
        }
        RecordNames = names;
        var c16 = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        for (var i = 0; i < c16; i++)
        {
            int l = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
            var s = Encoding.Unicode.GetString(b, p, 2 * l); p += 2 * l;
            Utf16[BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p))] = s; p += 4;
        }
        var c8 = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        for (var i = 0; i < c8; i++)
        {
            int l = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2;
            var s = Encoding.Latin1.GetString(b, p, l); p += l;
            Ascii[BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p))] = s; p += 4;
        }
        var q = 16;
        Root = (EsfRecord)ReadNode(ref q, true);
    }

    public static EsfTree Read(string path) => new(File.ReadAllBytes(path));
    public static EsfTree Read(byte[] bytes) => new(bytes);

    private static int Uleb(byte[] b, ref int p)
    {
        var v = 0;
        byte c;
        do { c = b[p++]; v = v << 7 | (c & 0x7F); } while ((c & 0x80) != 0);
        return v;
    }

    private EsfNode ReadNode(ref int p, bool root = false)
    {
        var t = _b[p];
        if ((t & 0x80) != 0)
        {
            int name, version;
            if ((t & 0x20) != 0 || root)
            {
                name = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(p + 1));
                version = _b[p + 3];
                p += 4;
            }
            else
            {
                version = (t & 0x1E) >> 1;
                name = (t & 1) << 8 | _b[p + 1];
                p += 2;
            }
            var size = Uleb(_b, ref p);
            var rec = new EsfRecord(RecordNames[name], version, (t & 0x40) != 0);
            if (rec.Nested)
            {
                var count = Uleb(_b, ref p);
                var end = p + size;
                for (var g = 0; g < count; g++)
                {
                    var gs = Uleb(_b, ref p);
                    var ge = p + gs;
                    var list = new List<EsfNode>();
                    while (p < ge) list.Add(ReadNode(ref p));
                    rec.Groups.Add(list);
                }
                p = end;
            }
            else
            {
                var end = p + size;
                var list = new List<EsfNode>();
                while (p < end) list.Add(ReadNode(ref p));
                rec.Groups.Add(list);
            }
            return rec;
        }
        if (t >= 0x40)
        {
            p++;
            var n = Uleb(_b, ref p);
            var arr = new EsfArray(t, _b.AsSpan(p, n).ToArray());
            p += n;
            return arr;
        }
        var size1 = t switch
        {
            0x01 or 0x02 or 0x06 or 0x16 or 0x1a or 0x23 => 1,
            0x03 or 0x07 or 0x10 or 0x17 or 0x1b or 0x24 => 2,
            0x18 or 0x1c => 3,
            0x04 or 0x08 or 0x0a or 0x0e or 0x0f or 0x21 or 0x25 => 4,
            0x05 or 0x09 or 0x0b or 0x0c => 8,
            0x0d => 12,
            0x12 or 0x13 or 0x14 or 0x15 or 0x19 or 0x1d => 0,
            _ => throw new InvalidDataException($"ESF type {t:x2} at {p}"),
        };
        var v = new EsfValue(t, _b.AsSpan(p + 1, size1).ToArray());
        p += 1 + size1;
        return v;
    }
}

public abstract class EsfNode;

public sealed class EsfRecord(string name, int version, bool nested) : EsfNode
{
    public string Name { get; } = name;
    public int Version { get; } = version;
    public bool Nested { get; } = nested;
    public List<List<EsfNode>> Groups { get; } = [];
    /// <summary>Children of a non-nested record.</summary>
    public List<EsfNode> Children => Groups.Count > 0 ? Groups[0] : [];

    public IEnumerable<EsfRecord> Records(string name) => Groups.SelectMany(g => g).OfType<EsfRecord>().Where(r => r.Name == name);
    public EsfRecord? Record(string name) => Records(name).FirstOrDefault();

    /// <summary>Depth-first search for records named <paramref name="name"/>.</summary>
    public IEnumerable<EsfRecord> Descendants(string name)
    {
        foreach (var g in Groups)
        foreach (var c in g)
            if (c is EsfRecord r)
            {
                if (r.Name == name) yield return r;
                foreach (var d in r.Descendants(name)) yield return d;
            }
    }

    /// <summary>The primitive values among the children (records skipped), in order.</summary>
    public List<EsfValue> Values => Children.OfType<EsfValue>().ToList();
}

public sealed class EsfArray(byte type, byte[] data) : EsfNode
{
    public byte Type { get; } = type;
    public byte[] Data { get; } = data;

    public ushort[] U16s()
    {
        var r = new ushort[Data.Length / 2];
        for (var i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(2 * i));
        return r;
    }
}

public sealed class EsfValue(byte type, byte[] raw) : EsfNode
{
    public byte Type { get; } = type;
    public byte[] Raw { get; } = raw;

    public long Int => Type switch
    {
        0x12 => 1, 0x13 or 0x14 or 0x19 => 0, 0x15 => 1,
        0x01 or 0x06 or 0x16 => Raw[0],
        0x02 or 0x1a => (sbyte)Raw[0],
        0x03 or 0x1b => BinaryPrimitives.ReadInt16LittleEndian(Raw),
        0x07 or 0x17 or 0x10 => BinaryPrimitives.ReadUInt16LittleEndian(Raw),
        0x18 => Raw[0] << 16 | Raw[1] << 8 | Raw[2],
        0x1c => ((Raw[0] << 16 | Raw[1] << 8 | Raw[2]) << 8) >> 8,
        0x04 => BinaryPrimitives.ReadInt32LittleEndian(Raw),
        0x08 or 0x0e or 0x0f => BinaryPrimitives.ReadUInt32LittleEndian(Raw),
        0x05 => BinaryPrimitives.ReadInt64LittleEndian(Raw),
        0x09 => (long)BinaryPrimitives.ReadUInt64LittleEndian(Raw),
        _ => throw new InvalidOperationException($"ESF type {Type:x2} is not an integer"),
    };

    public (float X, float Y) Vec2 => (BinaryPrimitives.ReadSingleLittleEndian(Raw), BinaryPrimitives.ReadSingleLittleEndian(Raw.AsSpan(4)));
}
