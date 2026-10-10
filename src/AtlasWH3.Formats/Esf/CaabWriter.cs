using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Esf;

/// <summary>
/// Writes a CAAB / CBAB ESF file with child records the way CA's writer does: WH3's hlp_data.esf
/// (CAI_HIGH_LEVEL_PATHFINDER) and spd_data.esf (CAI_SIMPLE_PATH_DIRECTORY). Every hlp/spd file of the vanilla, IEE
/// and Old World packs comes back byte for byte through <see cref="EsfTree"/> and <see cref="Node"/>.
///
/// Layout: u32 magic (0xABCA CAAB, or 0xABCB CBAB: the game's current files; the same bytes when there are no strings),
/// u32 0, u32 timestamp, u32 offset of the name table; the root record (long header: 0x80, u16 name index, u8 version;
/// CA uleb128 body size); the name table (u16 count, u16-length ASCII names in first-use order), then u32 0 (UTF-16
/// strings) and u32 0 (ASCII strings).
///
/// A child record has the short header 0x80 | 0x40 (nested) | version &lt;&lt; 1 | name index bit 8, then the low byte of
/// the name index. A plain record's size counts its body; a nested record writes its size, then the group count, then
/// each group's size and body, and the size counts from after the group count. Arrays: type byte 0x40 | element type,
/// uleb128 byte length, the bytes.
///
/// CA's uleb128 is most significant group first. A size or length takes its shortest form unless the size field and
/// the end of what it measures fall in different 1 MiB blocks of the file; then it takes the 5-byte form, and so does a
/// nested record's group count (all 126 sizes of 1,000 and more in the 30 reference files; a size threshold fails:
/// 303,967 padded, 458,488 not).
///
/// Values: u32 in its smallest form (0x14 0, 0x15 1, 0x16 one byte, 0x17 two bytes, 0x18 three bytes big endian,
/// 0x08 four bytes), 0x07 u16, 0x06 u8, 0x12 / 0x13 bool.
/// </summary>
public sealed class CaabWriter
{
    public const uint MagicCaab = 0xABCA;
    public const uint MagicCbab = 0xABCB;
    public const int Block = 1 << 20;
    private const int Reserve = 5;

    private readonly List<string> _names = [];
    private readonly Stack<(int SizePos, bool Nested, int Groups)> _records = new();
    private int _groupPos = -1;
    private byte[] _buf;
    private int _len;

    /// <summary>Starts a file whose root is the plain record <paramref name="rootName"/>.</summary>
    public CaabWriter(string rootName, byte rootVersion, int capacity = 1 << 20)
    {
        _buf = new byte[Math.Max(64, capacity)];
        _len = 16;
        _names.Add(rootName);
        var s = Take(4);
        s[0] = 0x80;
        s[3] = rootVersion;
        Open(false);
    }

    private Span<byte> Take(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, (int)Math.Min(Array.MaxLength, Math.Max(2L * _buf.Length, _len + n)));
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public void U32(uint v)
    {
        if (v == 0) { Take(1)[0] = 0x14; return; }
        if (v == 1) { Take(1)[0] = 0x15; return; }
        if (v <= 0xFF) { var s = Take(2); s[0] = 0x16; s[1] = (byte)v; return; }
        if (v <= 0xFFFF) { var s = Take(3); s[0] = 0x17; BinaryPrimitives.WriteUInt16LittleEndian(s[1..], (ushort)v); return; }
        if (v <= 0xFFFFFF) { var s = Take(4); s[0] = 0x18; s[1] = (byte)(v >> 16); s[2] = (byte)(v >> 8); s[3] = (byte)v; return; }
        var t = Take(5); t[0] = 0x08; BinaryPrimitives.WriteUInt32LittleEndian(t[1..], v);
    }

    public void U16(ushort v) { var s = Take(3); s[0] = 0x07; BinaryPrimitives.WriteUInt16LittleEndian(s[1..], v); }
    public void U8(byte v) { var s = Take(2); s[0] = 0x06; s[1] = v; }
    public void Bool(bool v) => Take(1)[0] = v ? (byte)0x12 : (byte)0x13;

    /// <summary>A primitive exactly as read (type byte + payload).</summary>
    public void Raw(byte type, ReadOnlySpan<byte> payload)
    {
        Take(1)[0] = type;
        payload.CopyTo(Take(payload.Length));
    }

    /// <summary>An array: <paramref name="type"/> is the element type (0x08 u32 → 0x48, 0x06 u8 → 0x46, …).</summary>
    public void TypedArray(byte type, ReadOnlySpan<byte> data)
    {
        Take(1)[0] = (byte)(0x40 | type);
        var pos = _len;
        var n = UlebLength(pos, data.Length, 0);
        WriteUleb(Take(n), data.Length);
        data.CopyTo(Take(data.Length));
    }

    public void U32Array(ReadOnlySpan<uint> values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), values[i]);
        TypedArray(0x08, bytes);
    }

    public void U8Array(ReadOnlySpan<byte> values) => TypedArray(0x06, values);

    private int NameIndex(string name)
    {
        var i = _names.IndexOf(name);
        if (i >= 0) return i;
        _names.Add(name);
        return _names.Count - 1;
    }

    private void Open(bool nested)
    {
        _records.Push((_len, nested, 0));
        Take(nested ? 2 * Reserve : Reserve);
    }

    /// <summary>Opens a child record; close it with <see cref="EndRecord"/>. In a nested record, wrap each group in
    /// <see cref="BeginGroup"/> / <see cref="EndGroup"/>.</summary>
    public void BeginRecord(string name, byte version, bool nested = false)
    {
        var index = NameIndex(name);
        if (index >= 512 || version >= 16) throw new InvalidDataException($"record {name} v{version} needs the long header");
        var s = Take(2);
        s[0] = (byte)(0x80 | (nested ? 0x40 : 0) | version << 1 | index >> 8);
        s[1] = (byte)index;
        Open(nested);
    }

    public void EndRecord()
    {
        if (_records.Count <= 1) throw new InvalidOperationException("no child record is open");
        Close();
    }

    public void BeginGroup()
    {
        var (pos, nested, groups) = _records.Pop();
        if (!nested || _groupPos >= 0) throw new InvalidOperationException("BeginGroup outside a nested record");
        _records.Push((pos, nested, groups + 1));
        _groupPos = _len;
        Take(Reserve);
    }

    public void EndGroup()
    {
        if (_groupPos < 0) throw new InvalidOperationException("no group is open");
        var pos = _groupPos;
        _groupPos = -1;
        var body = _len - pos - Reserve;
        var n = UlebLength(pos, body, 0);
        Compact(pos, Reserve, n, body);
        WriteUleb(_buf.AsSpan(pos, n), body);
    }

    /// <summary>A plain child record.</summary>
    public void Record(string name, byte version, Action<CaabWriter> body)
    {
        BeginRecord(name, version);
        body(this);
        EndRecord();
    }

    /// <summary>A nested child record, one block per group.</summary>
    public void NestedRecord(string name, byte version, IEnumerable<Action<CaabWriter>> groups)
    {
        BeginRecord(name, version, true);
        foreach (var g in groups)
        {
            BeginGroup();
            g(this);
            EndGroup();
        }
        EndRecord();
    }

    /// <summary>Closes the innermost record: its size (and group count) in place of the reserved bytes.</summary>
    private void Close()
    {
        var (pos, nested, groups) = _records.Pop();
        var reserved = nested ? 2 * Reserve : Reserve;
        var body = _len - pos - reserved;
        var count = nested ? CountLength(groups) : 0;
        var n = UlebLength(pos, body, count);
        if (nested && n == Reserve) count = Reserve; // a padded nested size pads its group count too
        Compact(pos, reserved, n + count, body);
        WriteUleb(_buf.AsSpan(pos, n), body);
        if (nested) WriteUleb(_buf.AsSpan(pos + n, count), groups);
    }

    /// <summary>The form of a size at <paramref name="pos"/> measuring <paramref name="body"/> bytes after
    /// <paramref name="extra"/> bytes (a nested record's group count): shortest, or 5 bytes across a 1 MiB block.</summary>
    private static int UlebLength(int pos, int body, int extra)
    {
        var n = CountLength(body);
        return pos / Block == (pos + n + extra + body) / Block ? n : Reserve;
    }

    private static int CountLength(int value)
    {
        var n = 1;
        while ((uint)value >= 1u << (7 * n) && n < Reserve) n++;
        return n;
    }

    private void Compact(int pos, int reserved, int used, int body)
    {
        if (used == reserved) return;
        _buf.AsSpan(pos + reserved, body).CopyTo(_buf.AsSpan(pos + used));
        _len -= reserved - used;
    }

    private static void WriteUleb(Span<byte> dest, int value)
    {
        var v = (uint)value;
        for (var i = dest.Length - 1; i >= 0; i--)
        {
            dest[i] = (byte)(v & 0x7F | (i < dest.Length - 1 ? 0x80 : 0));
            v >>= 7;
        }
    }

    /// <summary>Writes a node read by <see cref="EsfTree"/> back out (round trips).</summary>
    public void Node(EsfNode node)
    {
        switch (node)
        {
            case EsfValue v: Raw(v.Type, v.Raw); break;
            case EsfArray a: TypedArray((byte)(a.Type & 0x3F), a.Data); break;
            case EsfRecord r when r.Nested:
                BeginRecord(r.Name, (byte)r.Version, true);
                foreach (var g in r.Groups)
                {
                    BeginGroup();
                    foreach (var c in g) Node(c);
                    EndGroup();
                }
                EndRecord();
                break;
            case EsfRecord r:
                BeginRecord(r.Name, (byte)r.Version);
                foreach (var c in r.Children) Node(c);
                EndRecord();
                break;
        }
    }

    /// <summary>Closes the root and returns the whole file.</summary>
    public byte[] ToFile(uint timestamp, uint magic = MagicCbab)
    {
        if (_records.Count != 1) throw new InvalidOperationException($"{_records.Count - 1} child records still open");
        Close();
        var nameBytes = _names.Select(Encoding.ASCII.GetBytes).ToList();
        var namesOffset = _len;
        var tail = Take(2 + nameBytes.Sum(n => 2 + n.Length) + 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tail, (ushort)_names.Count);
        var p = 2;
        foreach (var n in nameBytes)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tail[p..], (ushort)n.Length);
            n.CopyTo(tail[(p + 2)..]);
            p += 2 + n.Length;
        }
        var f = _buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(f, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(f[12..], (uint)namesOffset);
        return _buf.AsSpan(0, _len).ToArray();
    }
}
