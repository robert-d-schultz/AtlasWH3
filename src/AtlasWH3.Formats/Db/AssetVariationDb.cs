using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Db;

/// <summary>
/// A warscape_asset_variation_db\*.assetdb file (FASTBIN0 v3, little-endian; strings are u16 length + UTF-8). The format
/// is campaign_tools' docs/assetdb.md:
///   u16 version 3, u32 meta_data_checksum, u32 meta count { u16 1, str name, u32 n, str values },
///   u32 entry count { u16 1, str namespace, str key, u16 1, u32 variation count
///     { u16 6, str filename, u16 1, u64 flag0, u64 flag1, str display_name, u8 r, g, b, u32 uid } }.
/// The game and BOB read every *.assetdb under warscape_asset_variation_db\ and merge them: a key that already exists
/// gets the new variations appended. Flags are not decoded here (their bits depend on each file's meta data).
/// </summary>
public sealed class AssetVariationDb
{
    public const string Folder = "warscape_asset_variation_db";

    public sealed record Variation(string FileName, UInt128 Flags, string DisplayName, byte R, byte G, byte B, uint Uid);
    public sealed record Entry(string Namespace, string Key, IReadOnlyList<Variation> Variations);

    public uint Checksum { get; private init; }
    public IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Meta { get; private init; } = [];
    public IReadOnlyList<Entry> Entries { get; private init; } = [];

    public static AssetVariationDb Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 14 || !b[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException(".assetdb: not FASTBIN0.");
        var o = 8;
        var version = U16(b, ref o);
        if (version != 3) throw new InvalidDataException($".assetdb: unsupported version {version}.");
        var checksum = U32(b, ref o);
        var meta = new List<(string, IReadOnlyList<string>)>();
        for (var n = U32(b, ref o); n > 0; n--)
        {
            U16(b, ref o);
            var name = Str(b, ref o);
            var values = new string[U32(b, ref o)];
            for (var i = 0; i < values.Length; i++) values[i] = Str(b, ref o);
            meta.Add((name, values));
        }
        var entries = new List<Entry>();
        for (var n = U32(b, ref o); n > 0; n--)
        {
            U16(b, ref o);
            var ns = Str(b, ref o);
            var key = Str(b, ref o);
            U16(b, ref o);
            var variations = new Variation[U32(b, ref o)];
            for (var i = 0; i < variations.Length; i++)
            {
                U16(b, ref o);
                var file = Str(b, ref o);
                U16(b, ref o);
                var lo = BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
                var hi = BinaryPrimitives.ReadUInt64LittleEndian(b[(o + 8)..]);
                o += 16;
                var display = Str(b, ref o);
                var (r, g, bl) = (b[o], b[o + 1], b[o + 2]);
                o += 3;
                variations[i] = new Variation(file, new UInt128(hi, lo), display, r, g, bl, U32(b, ref o));
            }
            entries.Add(new Entry(ns, key, variations));
        }
        if (o != b.Length) throw new InvalidDataException($".assetdb: {b.Length - o} bytes left over after the last entry.");
        return new AssetVariationDb { Checksum = checksum, Meta = meta, Entries = entries };
    }

    private static ushort U16(ReadOnlySpan<byte> b, ref int o) { var v = BinaryPrimitives.ReadUInt16LittleEndian(b[o..]); o += 2; return v; }
    private static uint U32(ReadOnlySpan<byte> b, ref int o) { var v = BinaryPrimitives.ReadUInt32LittleEndian(b[o..]); o += 4; return v; }

    private static string Str(ReadOnlySpan<byte> b, ref int o)
    {
        var n = U16(b, ref o);
        var s = Encoding.UTF8.GetString(b.Slice(o, n));
        o += n;
        return s;
    }
}
