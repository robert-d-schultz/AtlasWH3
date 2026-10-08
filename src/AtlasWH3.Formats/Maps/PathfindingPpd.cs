using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Maps;

/// <summary>
/// campaign_maps\&lt;map&gt;\pathfinding.ppd (written by CA's tools or CAIME): the campaign hex grid's movement data.
/// Hexes are flat-topped, stored row-major (x = column, y = row); per hex 8 bytes: one edge byte per direction
/// (bit 7 = navigable, bits 0-6 = index into <see cref="MoveCosts"/>), then the tile group index (12 bits, low byte
/// at 6, high nibble in byte 7's low nibble) and the hex type (byte 7's high nibble).
/// Neighbour directions (dx, dy) by column parity: even (0,1),(1,0),(1,−1),(0,−1),(−1,−1),(−1,0);
/// odd (0,1),(1,1),(1,0),(0,−1),(−1,0),(−1,1).
/// </summary>
public sealed class PathfindingPpd
{
    public static readonly (int Dx, int Dy)[][] Directions =
    [
        [(0, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0)],
        [(0, 1), (1, 1), (1, 0), (0, -1), (-1, 0), (-1, 1)],
    ];

    public ulong Magic { get; private set; }
    public uint Version { get; private set; }
    public List<string> LandRegions { get; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Width·Height·8 bytes.</summary>
    public byte[] Cells { get; private set; } = [];
    public ushort[] MoveCosts { get; private set; } = [];
    /// <summary>(high-level connectivity area, region index + 1; 0 = no region).</summary>
    public List<(ushort Hlci, ushort Region)> TileGroups { get; } = [];
    /// <summary>Per land region: its passable border hexes (x, y, edge mask).</summary>
    public List<List<(ushort X, ushort Y, byte Mask)>> RegionEdges { get; } = [];
    public List<(ushort AreaEnter, ushort AreaLeave, List<(ushort X, ushort Y, byte Mask)> Enter, List<(ushort X, ushort Y, byte Mask)> Leave)> Beaches { get; } = [];
    public List<(ushort A, ushort B)> HlciConnections { get; } = [];
    public List<(List<(ushort X, ushort Y)> SideA, List<(ushort X, ushort Y)> SideB)> Bridges { get; } = [];
    /// <summary>TileGroups² best-case costs.</summary>
    public uint[] HeuristicCache { get; private set; } = [];
    public List<(ushort X, ushort Y)> BorderHexes { get; } = [];
    public int[] CumulativeBorderHexes { get; private set; } = [];
    public List<(List<(ushort From, ushort To)> RegionPairs, List<(ushort X, ushort Y, byte Mask)> Hexes)> Roads { get; } = [];
    /// <summary>Null when the file has no restrictions section.</summary>
    public List<List<(ushort X, ushort Y, byte Mask)>>? Restrictions { get; private set; }
    public uint Crc { get; private set; }

    public int Index(int x, int y) => y * Width + x;
    public byte Edge(int x, int y, int dir) => Cells[(y * Width + x) * 8 + dir];
    public int TileGroup(int x, int y)
    {
        var o = (y * Width + x) * 8;
        return (Cells[o + 7] & 0xF) << 8 | Cells[o + 6];
    }
    public int HexType(int x, int y) => Cells[(y * Width + x) * 8 + 7] >> 4;

    /// <summary>The neighbour of (x, y) in direction <paramref name="dir"/>, or false off the map.</summary>
    public bool Neighbour(int x, int y, int dir, out int nx, out int ny)
    {
        var (dx, dy) = Directions[x & 1][dir];
        nx = x + dx;
        ny = y + dy;
        return (uint)nx < (uint)Width && (uint)ny < (uint)Height;
    }

    public static PathfindingPpd Read(string path) => Read(File.ReadAllBytes(path));

    public static PathfindingPpd Read(byte[] b)
    {
        var p = new PathfindingPpd();
        var o = 0;
        ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)); o += 2; return v; }
        int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)); o += 4; return v; }
        (ushort, ushort, byte) Hex5() { var r = (U16(), U16(), b[o]); o++; return r; }
        p.Magic = BinaryPrimitives.ReadUInt64LittleEndian(b); o = 8;
        p.Version = (uint)I32();
        var n = I32();
        for (var i = 0; i < n; i++) { var l = I32(); p.LandRegions.Add(Encoding.ASCII.GetString(b, o, l)); o += l; }
        p.Width = U16();
        p.Height = U16();
        p.Cells = b.AsSpan(o, p.Width * p.Height * 8).ToArray(); o += p.Cells.Length;
        n = I32();
        p.MoveCosts = new ushort[n];
        for (var i = 0; i < n; i++) p.MoveCosts[i] = U16();
        n = U16();
        for (var i = 0; i < n; i++) p.TileGroups.Add((U16(), U16()));
        for (var r = 0; r < p.LandRegions.Count; r++)
        {
            var k = U16();
            var list = new List<(ushort, ushort, byte)>(k);
            for (var i = 0; i < k; i++) list.Add(Hex5());
            p.RegionEdges.Add(list);
        }
        n = U16();
        for (var i = 0; i < n; i++)
        {
            var a = U16(); var l = U16();
            var ent = new List<(ushort, ushort, byte)>(); var k = U16(); for (var j = 0; j < k; j++) ent.Add(Hex5());
            var lev = new List<(ushort, ushort, byte)>(); k = U16(); for (var j = 0; j < k; j++) lev.Add(Hex5());
            p.Beaches.Add((a, l, ent, lev));
        }
        n = U16();
        for (var i = 0; i < n; i++) p.HlciConnections.Add((U16(), U16()));
        n = U16();
        for (var i = 0; i < n; i++)
        {
            var a = new List<(ushort, ushort)>(); var k = U16(); for (var j = 0; j < k; j++) a.Add((U16(), U16()));
            var c = new List<(ushort, ushort)>(); k = U16(); for (var j = 0; j < k; j++) c.Add((U16(), U16()));
            p.Bridges.Add((a, c));
        }
        var tg = p.TileGroups.Count;
        p.HeuristicCache = new uint[tg * tg];
        for (var i = 0; i < p.HeuristicCache.Length; i++) { p.HeuristicCache[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)); o += 4; }
        n = I32();
        for (var i = 0; i < n; i++) p.BorderHexes.Add((U16(), U16()));
        p.CumulativeBorderHexes = new int[tg];
        for (var i = 0; i < tg; i++) p.CumulativeBorderHexes[i] = I32();
        n = U16();
        for (var i = 0; i < n; i++)
        {
            var k = I32();
            var pairs = new List<(ushort, ushort)>(k); for (var j = 0; j < k; j++) pairs.Add((U16(), U16()));
            var m = U16();
            var hexes = new List<(ushort, ushort, byte)>(m); for (var j = 0; j < m; j++) hexes.Add(Hex5());
            p.Roads.Add((pairs, hexes));
        }
        if (b.Length - o > 4)
        {
            p.Restrictions = [];
            var nr = b[o++];
            for (var i = 0; i < nr; i++)
            {
                var k = I32();
                var list = new List<(ushort, ushort, byte)>(k); for (var j = 0; j < k; j++) list.Add(Hex5());
                p.Restrictions.Add(list);
            }
        }
        if (b.Length - o == 4) p.Crc = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        return p;
    }
}
