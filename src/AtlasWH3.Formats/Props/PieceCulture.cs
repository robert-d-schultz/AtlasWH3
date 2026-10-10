using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Props;

/// <summary>
/// A devastation piece's .culture file: the culture masks of the objects in the .bin beside it (whose own masks are a
/// placeholder 1), one section per object kind, "u32 count, char[4] tag (NUL-padded), count × u64 mask", empty sections
/// left out (WH3_visual_map_decompiler docs/event-area-pieces.md).
/// </summary>
public static class PieceCulture
{
    /// <summary>The section tags, in file order: props, polygon meshes, VFX, point lights, spot lights, terrain holes,
    /// composite scenes, sound emitters.</summary>
    public static readonly string[] Tags = ["p", "m", "v", "lp", "ls", "ht", "sc", "ss"];

    public static List<(string Tag, List<ulong> Masks)> Read(ReadOnlySpan<byte> data)
    {
        var result = new List<(string, List<ulong>)>();
        var pos = 0;
        while (pos < data.Length)
        {
            if (pos + 8 > data.Length) throw new InvalidDataException($"truncated .culture section at {pos}");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            var tag = Encoding.ASCII.GetString(data.Slice(pos + 4, 4)).TrimEnd('\0');
            pos += 8;
            if (pos + 8L * count > data.Length) throw new InvalidDataException($".culture section '{tag}' runs past the end");
            var masks = new List<ulong>((int)count);
            for (var i = 0; i < count; i++, pos += 8) masks.Add(BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]));
            result.Add((tag, masks));
        }
        return result;
    }

    public static byte[] Write(IEnumerable<(string Tag, IReadOnlyList<ulong> Masks)> sections)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var (tag, masks) in sections)
        {
            if (masks.Count == 0) continue;
            if (tag.Length > 4) throw new ArgumentException($".culture tag '{tag}' is longer than 4 characters");
            w.Write((uint)masks.Count);
            var t = new byte[4];
            Encoding.ASCII.GetBytes(tag, t);
            w.Write(t);
            foreach (var m in masks) w.Write(m);
        }
        w.Flush();
        return ms.ToArray();
    }
}
