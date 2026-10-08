using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Battle;

/// <summary>One area of a compiled battle_locations_map.bin list.</summary>
public sealed record BlmArea(CellBox Box, (int X, int Y) Centre, int DeploymentCount, int DeploymentValue,
                             float AttackX, float AttackY, string Redirection, string DefendingFaction, string Name,
                             string RedirectionCatchment, byte[] ValidFlags);

/// <summary>
/// Reader for compiled battle_locations_map.bin (FASTBIN0 v2): meta item names, the catchment lists and the per-cell
/// meta flag grid (row 0 = north). Layout in BattleMaps docs/catchment-format.md.
/// </summary>
public sealed class BattleLocationsMapFile
{
    public IReadOnlyList<string> MetaItems { get; init; } = [];
    public IReadOnlyList<(string Key, IReadOnlyList<BlmArea> Areas)> Lists { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    public int[] MetaGrid { get; init; } = [];

    public static BattleLocationsMapFile Read(string path)
    {
        var b = File.ReadAllBytes(path).AsSpan();
        if (!b[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException($"{path} is not FASTBIN0.");
        var o = 8;
        ushort U16(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]); o += 2; return v; }
        int I32(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4; return v; }
        float F32(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadSingleLittleEndian(s[o..]); o += 4; return v; }
        string Str(ReadOnlySpan<byte> s) { var n = U16(s); var v = Encoding.Latin1.GetString(s.Slice(o, n)); o += n; return v; }

        U16(b); U16(b);
        var items = new List<string>();
        for (var i = I32(b); i > 0; i--) items.Add(Str(b));
        var lists = new List<(string, IReadOnlyList<BlmArea>)>();
        for (var l = I32(b); l > 0; l--)
        {
            var key = Str(b);
            var areas = new List<BlmArea>();
            for (var n = I32(b); n > 0; n--)
            {
                U16(b);
                var box = new CellBox(I32(b), I32(b), I32(b), I32(b));
                var centre = (I32(b), I32(b));
                var dep = (I32(b), I32(b));
                var (ax, ay) = (F32(b), F32(b));
                var redirect = Str(b);
                var defending = Str(b);
                var name = Str(b);
                var redirectCatchment = Str(b);
                U16(b);
                var flags = b.Slice(o, 4).ToArray();
                o += 4;
                areas.Add(new BlmArea(box, centre, dep.Item1, dep.Item2, ax, ay, redirect, defending, name, redirectCatchment, flags));
            }
            lists.Add((key, areas));
        }
        var w = I32(b);
        var h = I32(b);
        var count = I32(b);
        var grid = new int[count];
        for (var i = 0; i < count; i++) grid[i] = I32(b);
        return new BattleLocationsMapFile { MetaItems = items, Lists = lists, Width = w, Height = h, MetaGrid = grid };
    }
}

/// <summary>A tile instance in a compiled tile_map.tiles: anchor cell, tile folder, origin (min corner, y-up) and rotation.</summary>
public sealed record TileMapInstance(int AnchorCell, string Location, int OriginX, int OriginY, int Rotation);

/// <summary>
/// Reader for compiled campaign-battle tile_map.index/.tiles (plane 0 only; planes 1-2 are unused in 3K).
/// Cell = 20 bytes: i32 w0, u32 w1, u32 w2, f32, f32; grid rows are y-up (row 0 = south).
/// w0 &gt; 0: anchor, 1-BASED index into the path table (0 = no tile); w0 &lt; 0: member of the instance anchored at
/// cell −w0−1. Every cell carries its instance's origin (w1 &gt;&gt; 16, w2 &amp; 0xFFFF) and rotation (w1 &amp; 0xF0).
/// </summary>
public sealed class BattleTileMapFile
{
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    /// <summary>Plane-0 words per cell: w0, w1, w2.</summary>
    public int[] W0 { get; init; } = [];
    public uint[] W1 { get; init; } = [];
    public uint[] W2 { get; init; } = [];

    public static BattleTileMapFile Read(string dir) =>
        Read(File.ReadAllBytes(Path.Combine(dir, "tile_map.index")), File.ReadAllBytes(Path.Combine(dir, "tile_map.tiles")));

    /// <summary>From the bytes of tile_map.index and tile_map.tiles (e.g. read from a pack).</summary>
    public static BattleTileMapFile Read(byte[] idx, byte[] tiles)
    {
        var w = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(4));
        var h = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(8));
        var data = tiles.AsSpan();
        var n = w * h;
        var w0 = new int[n];
        var w1 = new uint[n];
        var w2 = new uint[n];
        for (var i = 0; i < n; i++)
        {
            var c = data.Slice(i * 20, 12);
            w0[i] = BinaryPrimitives.ReadInt32LittleEndian(c);
            w1[i] = BinaryPrimitives.ReadUInt32LittleEndian(c[4..]);
            w2[i] = BinaryPrimitives.ReadUInt32LittleEndian(c[8..]);
        }
        var o = 3 * n * 20;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[o..]);
        o += 4;
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(data[o..]);
            paths.Add(Encoding.Latin1.GetString(data.Slice(o + 2, len)));
            o += 2 + len;
        }
        return new BattleTileMapFile { Width = w, Height = h, Paths = paths, W0 = w0, W1 = w1, W2 = w2 };
    }

    /// <summary>Tile folder of the instance owning a grid cell (y-up), or null for no tile.</summary>
    public string? OwnerLocation(int cell)
    {
        var v = W0[cell];
        if (v < 0)
        {
            var a = -v - 1;
            if (a < 0 || a >= W0.Length) return null;
            v = W0[a];
        }
        return v >= 1 && v <= Paths.Count ? Paths[v - 1] : null;
    }

    public IEnumerable<TileMapInstance> Instances()
    {
        for (var i = 0; i < W0.Length; i++)
            if (W0[i] >= 1 && W0[i] <= Paths.Count)
                yield return new TileMapInstance(i, Paths[W0[i] - 1], (int)(W1[i] >> 16), (int)(W2[i] & 0xFFFF),
                    (W1[i] & 0xF0) switch { 0x20 => 90, 0x40 => 180, 0x80 => 270, _ => 0 });
    }
}
