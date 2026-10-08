using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// height_patches\rivers.height_patch_collection: "FASTBIN0", u16 version 1, u32 count, then per patch
/// u16 1, u16 length, path ("terrain/campaigns/&lt;map&gt;//height_patches//&lt;name&gt;.compressed_map"),
/// f32 min x, min z, max x, max z (world units).
/// </summary>
public sealed class HeightPatchCollection
{
    public sealed record Patch(string Path, float MinX, float MinZ, float MaxX, float MaxZ);

    public List<Patch> Patches { get; } = [];

    public static string PatchPath(string mapName, string patchName) =>
        $"terrain/campaigns/{mapName}//height_patches//{patchName}.compressed_map";

    public static HeightPatchCollection Read(string path) => Read(File.ReadAllBytes(path));

    public static HeightPatchCollection Read(ReadOnlySpan<byte> b)
    {
        if (!b[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("height_patch_collection: not FASTBIN0.");
        var result = new HeightPatchCollection();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(b[10..]);
        var o = 14;
        for (var i = 0; i < count; i++)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 2)..]);
            var path = Encoding.ASCII.GetString(b.Slice(o + 4, len));
            o += 4 + len;
            var r = b.Slice(o, 16);
            result.Patches.Add(new Patch(path, BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[8..]), BinaryPrimitives.ReadSingleLittleEndian(r[12..])));
            o += 16;
        }
        return result;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("FASTBIN0"u8);
        w.Write((ushort)1);
        w.Write(Patches.Count);
        foreach (var p in Patches)
        {
            w.Write((ushort)1);
            w.Write((ushort)p.Path.Length);
            w.Write(Encoding.ASCII.GetBytes(p.Path));
            w.Write(p.MinX);
            w.Write(p.MinZ);
            w.Write(p.MaxX);
            w.Write(p.MaxZ);
        }
        w.Flush();
        return ms.ToArray();
    }

    public void Write(string path) => File.WriteAllBytes(path, ToBytes());
}
