using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AtlasWH3.Formats.Dds;

namespace AtlasWH3.Formats.Maps;

/// <summary>One mip of a piece texture: its rectangle in the map texture's mip (pixels, the file's own row order) and
/// where it sits in the piece file. A texture_info line: <c>srcX srcY srcW srcH bytesPerBlock blocksW blocksH offset</c>.</summary>
public sealed record PieceMip(int SrcX, int SrcY, int SrcW, int SrcH, int BlockBytes, int BlocksW, int BlocksH, int Offset)
{
    public int Bytes => BlocksW * BlocksH * BlockBytes;
}

/// <summary>A piece texture's texture_info entry. <paramref name="Flipped"/>: the map texture stores its rows from the
/// south (full_height_map, tile_mask), so the rectangle's y counts from the bottom.</summary>
public sealed record PieceTexture(string Name, bool Flipped, IReadOnlyList<PieceMip> Mips);

/// <summary>An event area's bounding box in event_area_mask pixels (rows from the north, as the DDS stores them).</summary>
public readonly record struct PieceBox(int X, int Y, int Width, int Height);

/// <summary>A road tile in a piece's tile_list: tile-map point (rows from the south) and its line in event_tiles.</summary>
public readonly record struct PieceTile(ushort X, ushort Y, uint Index);

/// <summary>A tree in a piece's tree_list: a trees.campaign_tree_list instance with its type's line in event_trees.</summary>
public readonly record struct PieceTree(float X, float Y, float Z, byte Flag, byte Variant, ushort Index);

/// <summary>
/// The files of an event-area piece (<c>terrain\campaigns\&lt;map&gt;\pieces\event_&lt;colour&gt;\</c>), as BOB's Devastation
/// pieces action writes them (bob_terrain ACTION_PROCESS_TERRAIN_DEVASTATION_PIECES; measured 2026-10-09 on every piece of
/// the IEE and Old World packs, byte-identical). The decompiler's docs/event-area-pieces.md describes how the game uses them.
/// <list type="bullet">
/// <item>A piece texture is a raw crop of the map texture, every mip copied block for block from the same mip
/// (<see cref="Layout"/>, <see cref="Crop"/>); texture_info says where each mip came from.</item>
/// <item>mask: one bit per event-mask pixel of the box, set where the pixel is the area's, LSB first, padded to 4 bytes.</item>
/// <item>tile_list: u32 1, then (u16 x, u16 y, u32 event_tiles line) per road tile; tree_list: 16-byte trees.</item>
/// </list>
/// </summary>
public static class EventPieces
{
    /// <summary>The piece textures in texture_info order: file name, path under the map folder, rows from the south.</summary>
    public static readonly IReadOnlyList<(string Name, string MapPath, bool Flipped)> Textures =
    [
        ("full_height_map.dds", "full_height_map.dds", true),
        ("lf_sea_colour.dds", "lf_sea_colour.dds", false),
        ("lf_normal.dds", "lf_normal.dds", false),
        ("corruption_mask.dds", "corruption_mask.dds", false),
        ("tile_mask.dds", "tile_mask.dds", true),
        ("snow_mask.dds", "snow_mask.dds", false),
        ("shroud_heights.dds", "shroud_heights.dds", false),
        ("colour_overlay.dds", "colour_overlay.dds", false),
        ("global_blend.dds", @"global_map\global_blend.dds", false),
    ];

    /// <summary>How a map texture's data is laid out: size, 4×4 blocks or pixels, bytes per block (pixel), mips.</summary>
    public sealed record TextureLayout(int Width, int Height, int BlockSize, int BlockBytes, int MipCount, int DataOffset)
    {
        public int BlocksWide(int mip) => (Math.Max(1, Width >> mip) + BlockSize - 1) / BlockSize;
        public int BlocksHigh(int mip) => (Math.Max(1, Height >> mip) + BlockSize - 1) / BlockSize;

        /// <summary>Byte offset of a mip's data in the file.</summary>
        public long MipOffset(int mip)
        {
            long o = DataOffset;
            for (var m = 0; m < mip; m++) o += (long)BlocksWide(m) * BlocksHigh(m) * BlockBytes;
            return o;
        }

        public static TextureLayout Of(ReadOnlySpan<byte> dds)
        {
            var h = DdsHeader.Read(dds);
            var (block, bytes) = h.IsDx10
                ? h.DxgiFormat switch
                {
                    DdsHeader.DxgiBc1Unorm or DdsHeader.DxgiBc4Unorm => (4, 8),
                    DdsHeader.DxgiBc6hSf16 => (4, 16),
                    DdsHeader.DxgiR8Unorm or DdsHeader.DxgiR8Uint => (1, 1),
                    DdsHeader.DxgiR32Float => (1, 4),
                    _ => throw new InvalidDataException($"DXGI format {h.DxgiFormat} is not one a piece texture uses"),
                }
                : h.FourCC switch
                {
                    "DXT1" => (4, 8),
                    "DXT3" or "DXT5" => (4, 16),
                    "" => (1, h.RgbBitCount / 8),
                    _ => throw new InvalidDataException($"DDS format {h.FourCC} is not one a piece texture uses"),
                };
            return new TextureLayout(h.Width, h.Height, block, bytes, h.MipCount, h.DataOffset);
        }
    }

    /// <summary>
    /// A texture's texture_info entry for a box. The box is scaled to the texture (map size / mask size, per axis):
    /// [trunc(s·x), trunc(s·(x + w − 1)) + 1). It is widened to a grid, 64 pixels for a mipped block-compressed texture,
    /// 8 for a mipped uncompressed one, else one block; then flipped for a texture stored from the south. A mipped
    /// texture gets ⌊log2(min(W, H))⌋ + 1 levels of the W × H crop; at level m the rectangle starts at the level's own
    /// position (x0 &gt;&gt; m) rounded down to a block and covers W &gt;&gt; m pixels from there, rounded up to blocks.
    /// </summary>
    public static PieceTexture Layout(string name, bool flipped, TextureLayout map, int maskWidth, int maskHeight, PieceBox box)
    {
        var mipped = map.MipCount > 1;
        var block = map.BlockSize;
        var grid = mipped ? (block == 4 ? 64 : 8) : block;
        double sx = (double)map.Width / maskWidth, sy = (double)map.Height / maskHeight;
        var x0 = Down((int)(sx * box.X), grid);
        var y0 = Down((int)(sy * box.Y), grid);
        var x1 = Up((int)(sx * (box.X + box.Width - 1)) + 1, grid);
        var y1 = Up((int)(sy * (box.Y + box.Height - 1)) + 1, grid);
        if (flipped) (y0, y1) = (map.Height - y1, map.Height - y0);
        int w = x1 - x0, h = y1 - y0;
        var levels = mipped ? (int)Math.Log2(Math.Min(w, h)) + 1 : 1;

        var mips = new List<PieceMip>(levels);
        var offset = map.DataOffset;
        for (var m = 0; m < levels; m++)
        {
            int ax = x0 >> m, ay = y0 >> m;
            int mx = Down(ax, block), my = Down(ay, block);
            int mw = Up(ax - mx + (w >> m), block), mh = Up(ay - my + (h >> m), block);
            var mip = new PieceMip(mx, my, mw, mh, map.BlockBytes, mw / block, mh / block, offset);
            mips.Add(mip);
            offset += mip.Bytes;
        }
        return new PieceTexture(name, flipped, mips);
    }

    /// <summary>
    /// The piece DDS: the map's header with the crop's size and mip count (and MIPMAPCOUNT set), then each mip's blocks.
    /// Rows are read as BOB reads them, linearly from the mip's start with no bounds check, so a crop running past the
    /// map's right edge takes the next row's first blocks, and past the bottom the next mip's; past the end of the file
    /// it is zero.
    /// </summary>
    public static byte[] Crop(ReadOnlySpan<byte> mapDds, TextureLayout map, PieceTexture texture)
    {
        var top = texture.Mips[0];
        var size = map.DataOffset + texture.Mips.Sum(m => m.Bytes);
        var piece = new byte[size];
        mapDds[..map.DataOffset].CopyTo(piece);
        var header = piece.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) | 0x20000);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], top.SrcH);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], top.SrcW);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], texture.Mips.Count);

        for (var m = 0; m < texture.Mips.Count; m++)
        {
            var mip = texture.Mips[m];
            var source = map.MipOffset(m);
            var rowBytes = mip.BlocksW * mip.BlockBytes;
            for (var r = 0; r < mip.BlocksH; r++)
            {
                var from = source + ((long)(mip.SrcY / map.BlockSize + r) * map.BlocksWide(m) + mip.SrcX / map.BlockSize) * map.BlockBytes;
                var to = mip.Offset + r * rowBytes;
                var n = (int)Math.Clamp(mapDds.Length - from, 0, rowBytes);
                if (n > 0) mapDds.Slice((int)from, n).CopyTo(piece.AsSpan(to, n));
            }
        }
        return piece;
    }

    /// <summary>texture_info: the mask line, then per texture its name, mip count and flip, and a line per mip. CRLF.</summary>
    public static string TextureInfo(int maskWidth, int maskHeight, PieceBox box, IEnumerable<PieceTexture> textures)
    {
        var sb = new StringBuilder();
        void Line(params object[] values) => sb.Append(string.Join(' ', values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).Append("\r\n");
        Line("mask", maskWidth, maskHeight, box.X, box.Y, box.Width, box.Height);
        foreach (var t in textures)
        {
            Line(t.Name, t.Mips.Count, t.Flipped ? 1 : 0);
            foreach (var m in t.Mips) Line(m.SrcX, m.SrcY, m.SrcW, m.SrcH, m.BlockBytes, m.BlocksW, m.BlocksH, m.Offset);
        }
        return sb.ToString();
    }

    /// <summary>The tight box of every index on an event mask (rows from the north; 255, no layer, excluded).</summary>
    public static Dictionary<byte, PieceBox> Boxes(ReadOnlySpan<byte> indices, int width)
    {
        var min = new (int X, int Y)[256];
        var max = new (int X, int Y)[256];
        Array.Fill(min, (int.MaxValue, int.MaxValue));
        Array.Fill(max, (-1, -1));
        for (var i = 0; i < indices.Length; i++)
        {
            var v = indices[i];
            if (v == 255) continue;
            int x = i % width, y = i / width;
            if (x < min[v].X) min[v].X = x;
            if (x > max[v].X) max[v].X = x;
            if (y < min[v].Y) min[v].Y = y;
            max[v].Y = y;
        }
        var boxes = new Dictionary<byte, PieceBox>();
        for (var v = 0; v < 255; v++)
            if (max[v].X >= 0) boxes[(byte)v] = new PieceBox(min[v].X, min[v].Y, max[v].X - min[v].X + 1, max[v].Y - min[v].Y + 1);
        return boxes;
    }

    /// <summary>mask: a bit per box pixel (row-major, rows from the north), set where the event mask is
    /// <paramref name="index"/>, LSB first, the byte count rounded up to 4.</summary>
    public static byte[] Mask(ReadOnlySpan<byte> indices, int width, PieceBox box, byte index)
    {
        var bits = box.Width * box.Height;
        var mask = new byte[(bits + 31) / 32 * 4];
        var i = 0;
        for (var y = 0; y < box.Height; y++)
        {
            var row = indices.Slice((box.Y + y) * width + box.X, box.Width);
            for (var x = 0; x < box.Width; x++, i++)
                if (row[x] == index) mask[i >> 3] |= (byte)(1 << (i & 7));
        }
        return mask;
    }

    /// <summary>tile_list: u32 1, then the road tiles.</summary>
    public static byte[] TileList(IReadOnlyList<PieceTile> tiles)
    {
        var b = new byte[4 + tiles.Count * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        for (var i = 0; i < tiles.Count; i++)
        {
            var o = 4 + i * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), tiles[i].X);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 2), tiles[i].Y);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), tiles[i].Index);
        }
        return b;
    }

    public static List<PieceTile> ReadTileList(ReadOnlySpan<byte> b)
    {
        var tiles = new List<PieceTile>((b.Length - 4) / 8);
        for (var o = 4; o + 8 <= b.Length; o += 8)
            tiles.Add(new PieceTile(BinaryPrimitives.ReadUInt16LittleEndian(b[o..]), BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 2)..]),
                                    BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 4)..])));
        return tiles;
    }

    /// <summary>tree_list: per tree f32 x, y, z, u8 flag, u8 variant, u16 event_trees line; no header.</summary>
    public static byte[] TreeList(IReadOnlyList<PieceTree> trees)
    {
        var b = new byte[trees.Count * 16];
        for (var i = 0; i < trees.Count; i++)
        {
            var s = b.AsSpan(i * 16);
            BinaryPrimitives.WriteSingleLittleEndian(s, trees[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(s[4..], trees[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], trees[i].Z);
            s[12] = trees[i].Flag;
            s[13] = trees[i].Variant;
            BinaryPrimitives.WriteUInt16LittleEndian(s[14..], trees[i].Index);
        }
        return b;
    }

    public static List<PieceTree> ReadTreeList(ReadOnlySpan<byte> b)
    {
        var trees = new List<PieceTree>(b.Length / 16);
        for (var o = 0; o + 16 <= b.Length; o += 16)
            trees.Add(new PieceTree(BinaryPrimitives.ReadSingleLittleEndian(b[o..]), BinaryPrimitives.ReadSingleLittleEndian(b[(o + 4)..]),
                                    BinaryPrimitives.ReadSingleLittleEndian(b[(o + 8)..]), b[o + 12], b[o + 13],
                                    BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 14)..])));
        return trees;
    }

    /// <summary>event_tiles / event_trees: one entry per line, CRLF after each.</summary>
    public static byte[] Lines(IEnumerable<string> lines) =>
        Encoding.Latin1.GetBytes(string.Concat(lines.Select(l => l + "\r\n")));

    private static int Down(int v, int g) => v / g * g;
    private static int Up(int v, int g) => (v + g - 1) / g * g;
}
