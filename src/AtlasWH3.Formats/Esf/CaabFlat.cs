using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Esf;

/// <summary>
/// The compact ESF encoding (magic CA AB 00 00, "CAAB") for files whose root record holds one flat stream of primitives
/// and no child records: the campaign AI's hlp_data.esf (CAI_TRANSITION_DATA) and spd_data.esf
/// (CAI_SIMPLE_PATH_DIRECTORY).
///
/// Layout: u32 magic 0xABCA, u32 0, u32 timestamp, u32 offset of the name table; the root record (0x80, u16 name
/// index, u8 version, CA uleb128 body size, body); the name table (u16 count, u16-length ASCII names), then u32 0
/// (UTF-16 strings) and u32 0 (UTF-8 strings).
///
/// Body values (type byte + payload, little endian except the 24-bit forms, which are big endian):
/// 0x06 u8, 0x07 u16, 0x08 u32, 0x12 true, 0x13 false, 0x14 u32 0, 0x15 u32 1, 0x16 u32 in one byte,
/// 0x17 u32 in two bytes, 0x18 u32 in three bytes (big endian).
/// </summary>
public static class CaabFlat
{
    public const uint Magic = 0xABCA;
}

/// <summary>Writes a <see cref="CaabFlat"/> file the way CA's writer does (smallest u32 form, u16 / u8 / bool as is).</summary>
public sealed class CaabFlatWriter
{
    private byte[] _buf;
    private int _len;

    public CaabFlatWriter(int capacity = 1 << 16) => _buf = new byte[Math.Max(16, capacity)];

    /// <summary>Bytes of body written so far.</summary>
    public int Length => _len;

    private Span<byte> Take(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
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

    /// <summary>The whole file: header, root record <paramref name="recordName"/> holding the body, name table.</summary>
    public byte[] ToFile(string recordName, uint timestamp, byte version = 0)
    {
        var size = CaUleb(_len);
        var nameBytes = Encoding.ASCII.GetBytes(recordName);
        var namesOffset = 16 + 4 + size.Length + _len;
        var file = new byte[namesOffset + 2 + 2 + nameBytes.Length + 8];
        var f = file.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(f, CaabFlat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(f[12..], (uint)namesOffset);
        f[16] = 0x80; // root record: u16 name index 0, u8 version
        f[19] = version;
        size.CopyTo(f[20..]);
        _buf.AsSpan(0, _len).CopyTo(f[(20 + size.Length)..]);
        var p = namesOffset;
        BinaryPrimitives.WriteUInt16LittleEndian(f[p..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(f[(p + 2)..], (ushort)nameBytes.Length);
        nameBytes.CopyTo(f[(p + 4)..]);
        return file;
    }

    /// <summary>CA's uleb128 (most significant group first). Bodies of 2 MB and more get the 5-byte form, as in CA's files
    /// (spd_data.esf: 80 8c 82 94 13); smaller ones the shortest form (hlp_data.esf: 87 8d 4d).</summary>
    public static byte[] CaUleb(int value)
    {
        var groups = new List<byte>();
        var v = (uint)value;
        do { groups.Add((byte)(v & 0x7F)); v >>= 7; } while (v != 0);
        if (value >= 1 << 21) while (groups.Count < 5) groups.Add(0);
        groups.Reverse();
        for (var i = 0; i < groups.Count - 1; i++) groups[i] |= 0x80;
        return groups.ToArray();
    }
}

/// <summary>Reads a <see cref="CaabFlat"/> file value by value.</summary>
public sealed class CaabFlatReader
{
    private readonly byte[] _b;
    private int _p;
    public int End { get; }
    public uint Timestamp { get; }
    public string RecordName { get; }
    public byte Version { get; }

    public CaabFlatReader(byte[] file)
    {
        _b = file;
        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != CaabFlat.Magic) throw new InvalidDataException("not a CAAB ESF");
        Timestamp = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        var namesOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        if (file[16] != 0x80) throw new InvalidDataException("root is not a record");
        var nameIndex = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(17));
        Version = file[19];
        _p = 20;
        var size = 0;
        byte c;
        do { c = file[_p++]; size = size << 7 | (c & 0x7F); } while ((c & 0x80) != 0);
        End = _p + size;
        if (End != namesOffset) throw new InvalidDataException($"root ends at {End}, name table at {namesOffset}");
        var n = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(namesOffset));
        var q = namesOffset + 2;
        var names = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var l = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(q));
            names.Add(Encoding.ASCII.GetString(file, q + 2, l));
            q += 2 + l;
        }
        RecordName = names[nameIndex];
    }

    public bool AtEnd => _p >= End;
    public int Position => _p;

    public uint U32()
    {
        var t = _b[_p++];
        switch (t)
        {
            case 0x14: return 0;
            case 0x15: return 1;
            case 0x16: return _b[_p++];
            case 0x17: { var v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(_p)); _p += 2; return v; }
            case 0x18: { var v = (uint)(_b[_p] << 16 | _b[_p + 1] << 8 | _b[_p + 2]); _p += 3; return v; }
            case 0x08: { var v = BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(_p)); _p += 4; return v; }
            default: throw new InvalidDataException($"expected u32 at {_p - 1}, type {t:x2}");
        }
    }

    public ushort U16()
    {
        if (_b[_p] != 0x07) throw new InvalidDataException($"expected u16 at {_p}, type {_b[_p]:x2}");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(_p + 1));
        _p += 3;
        return v;
    }

    public byte U8()
    {
        if (_b[_p] != 0x06) throw new InvalidDataException($"expected u8 at {_p}, type {_b[_p]:x2}");
        _p += 2;
        return _b[_p - 1];
    }

    public bool Bool()
    {
        var t = _b[_p++];
        return t switch
        {
            0x12 => true,
            0x13 => false,
            0x01 => _b[_p++] != 0,
            _ => throw new InvalidDataException($"expected bool at {_p - 1}, type {t:x2}"),
        };
    }
}
