using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Esf;

/// <summary>
/// Reads a CAAB / CBAB ESF file value by value, entering child records as it goes (the layout is in
/// <see cref="CaabWriter"/>). For files too big for <see cref="EsfTree"/>'s node objects: spd_data.esf is up to 250 MB.
/// </summary>
public sealed class CaabReader
{
    private readonly byte[] _b;
    private int _p;
    public uint Magic { get; }
    public uint Timestamp { get; }
    public IReadOnlyList<string> Names { get; }
    public string RootName { get; }
    public byte RootVersion { get; }
    /// <summary>End of the root record's body.</summary>
    public int End { get; }

    public CaabReader(byte[] file)
    {
        _b = file;
        Magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (Magic is not (CaabWriter.MagicCaab or CaabWriter.MagicCbab)) throw new InvalidDataException("not a CAAB / CBAB ESF");
        Timestamp = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        var namesOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        var n = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(namesOffset));
        var q = namesOffset + 2;
        var names = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var l = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(q));
            names.Add(Encoding.ASCII.GetString(file, q + 2, l));
            q += 2 + l;
        }
        Names = names;
        if ((file[16] & 0x80) == 0) throw new InvalidDataException("root is not a record");
        RootName = names[BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(17))];
        RootVersion = file[19];
        _p = 20;
        var size = Uleb();
        End = _p + size;
        if (End != namesOffset) throw new InvalidDataException($"root ends at {End}, name table at {namesOffset}");
    }

    public static CaabReader Open(string path) => new(File.ReadAllBytes(path));

    public int Position => _p;
    public bool AtEnd => _p >= End;
    public byte PeekType => _b[_p];

    private int Uleb()
    {
        var v = 0;
        byte c;
        do { c = _b[_p++]; v = v << 7 | (c & 0x7F); } while ((c & 0x80) != 0);
        return v;
    }

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

    /// <summary>Enters a child record named <paramref name="name"/>; returns the end of its body and, for a nested
    /// record, its group count (−1 for a plain one).</summary>
    public (int End, int Groups) BeginRecord(string name)
    {
        var t = _b[_p];
        if ((t & 0x80) == 0) throw new InvalidDataException($"expected record {name} at {_p}, type {t:x2}");
        int index;
        if ((t & 0x20) != 0) { index = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(_p + 1)); _p += 4; }
        else { index = (t & 1) << 8 | _b[_p + 1]; _p += 2; }
        if (Names[index] != name) throw new InvalidDataException($"expected record {name} at {_p}, found {Names[index]}");
        var size = Uleb();
        if ((t & 0x40) == 0) return (_p + size, -1);
        var groups = Uleb();
        return (_p + size, groups);
    }

    /// <summary>Enters the next group of a nested record; returns the group's end.</summary>
    public int BeginGroup()
    {
        var size = Uleb();
        return _p + size;
    }

    /// <summary>An array of element type <paramref name="type"/> (0x08 u32, 0x06 u8, …): its bytes.</summary>
    public ReadOnlySpan<byte> TypedArray(byte type)
    {
        if (_b[_p] != (0x40 | type)) throw new InvalidDataException($"expected array {0x40 | type:x2} at {_p}, type {_b[_p]:x2}");
        _p++;
        var n = Uleb();
        var s = _b.AsSpan(_p, n);
        _p += n;
        return s;
    }

    public void Expect(int position, string what)
    {
        if (_p != position) throw new InvalidDataException($"{what}: at {_p}, expected {position}");
    }
}
