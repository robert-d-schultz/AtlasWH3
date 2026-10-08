using System.Text;

namespace AtlasWH3.Formats.Trees;

// campaign_maps\<map>\display\trees\trees.campaign_tree_list (version 5), little-endian:
//   u32 version, u32 unknownA, u32 unknownB, f32 worldWidth, f32 worldHeight, u32 typeCount
//   per type:     u16 nameLength, ascii name, u32 instanceCount, then that many instances
//   per instance: f32 x, f32 y (height), f32 z, u8 flag (always 1), u8 variant (0-5),
//                 u32 seasonCount, seasonCount x u32 season id (0xFFFFFFFF = seasonless)
// Verified against the vanilla file: parses to the exact end of file (205,767 instances, 50 types).

public struct TreeInstance
{
    public float X, Y, Z;
    public byte Flag;
    public byte Variant;
    public uint[] Seasons;
}

public sealed class TreeType
{
    public required string Name { get; set; }
    public List<TreeInstance> Instances { get; } = new();
    public override string ToString() => $"{Name} ({Instances.Count})";
}

public sealed class CampaignTreeList
{
    public const uint NoSeason = 0xFFFFFFFF;

    public uint Version { get; set; } = 5;
    public uint UnknownA { get; set; }
    public uint UnknownB { get; set; }
    public float WorldWidth { get; set; }
    public float WorldHeight { get; set; }
    public List<TreeType> Types { get; } = new();

    public int TotalInstances => Types.Sum(t => t.Instances.Count);

    public static CampaignTreeList Load(string path) => Read(File.ReadAllBytes(path));

    public static CampaignTreeList Read(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data), Encoding.ASCII);
        var list = new CampaignTreeList
        {
            Version = reader.ReadUInt32(),
            UnknownA = reader.ReadUInt32(),
            UnknownB = reader.ReadUInt32(),
            WorldWidth = reader.ReadSingle(),
            WorldHeight = reader.ReadSingle(),
        };
        if (list.Version != 5)
            throw new InvalidDataException($"Unsupported campaign_tree_list version {list.Version}.");

        var typeCount = reader.ReadUInt32();
        for (var t = 0; t < typeCount; t++)
        {
            var nameLength = reader.ReadUInt16();
            var type = new TreeType { Name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength)) };
            var count = reader.ReadUInt32();
            type.Instances.Capacity = (int)count;
            for (var i = 0; i < count; i++)
            {
                var inst = new TreeInstance
                {
                    X = reader.ReadSingle(),
                    Y = reader.ReadSingle(),
                    Z = reader.ReadSingle(),
                    Flag = reader.ReadByte(),
                    Variant = reader.ReadByte(),
                };
                var seasonCount = reader.ReadUInt32();
                inst.Seasons = new uint[seasonCount];
                for (var s = 0; s < seasonCount; s++)
                    inst.Seasons[s] = reader.ReadUInt32();
                type.Instances.Add(inst);
            }
            list.Types.Add(type);
        }

        if (reader.BaseStream.Position != data.Length)
            throw new InvalidDataException(
                $"campaign_tree_list has {data.Length - reader.BaseStream.Position} trailing bytes.");
        return list;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(UnknownA);
            writer.Write(UnknownB);
            writer.Write(WorldWidth);
            writer.Write(WorldHeight);
            writer.Write((uint)Types.Count);
            foreach (var type in Types)
            {
                var name = Encoding.ASCII.GetBytes(type.Name);
                writer.Write((ushort)name.Length);
                writer.Write(name);
                writer.Write((uint)type.Instances.Count);
                foreach (var inst in type.Instances)
                {
                    writer.Write(inst.X);
                    writer.Write(inst.Y);
                    writer.Write(inst.Z);
                    writer.Write(inst.Flag);
                    writer.Write(inst.Variant);
                    writer.Write((uint)inst.Seasons.Length);
                    foreach (var s in inst.Seasons)
                        writer.Write(s);
                }
            }
        }
        return ms.ToArray();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, ToBytes());
    }
}
