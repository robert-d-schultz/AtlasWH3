using AtlasWH3.Formats;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.Core.Campaign.Trees;

/// <summary>
/// The campaign hex grid BOB places trees on (from map_data.esf: column count and world width). Flat-topped hexes:
/// size s = 2/3 · width / (columns − 1), columns 1.5·s apart, rows √3·s apart, odd columns shifted half a row north.
/// Every value is float32 in the order BOB computes it (QTU FUN_1800e2130).
/// </summary>
public readonly record struct HexGrid(int Columns, int Rows, float WorldWidth)
{
    public float HexSize => 0.6666667f / (Columns - 1f) * WorldWidth;
    public float HalfRow => HexSize * 0.8660254f;
    public float RowStep => HalfRow + HalfRow;
    public float ColumnStep => HexSize * 1.5f;
    /// <summary>World z extent: half a row past the last row (vanilla 702.5 · 0.7712259 = 541.7862).</summary>
    public float WorldHeight => (Rows + 0.5f) * RowStep;

    /// <summary>The grid behind an AK CampaignTree map: 2 pixels per hex across, 2 per row (plus one) down.</summary>
    public static HexGrid ForTreeMap(int mapWidth, int mapHeight, float worldWidth) =>
        new(mapWidth / 2, mapHeight / 2, worldWidth);

    /// <summary>Tree-map pixel BOB samples for a hex (QTU FUN_1800e2410), y = 0 the top row.</summary>
    public static (int X, int Y) SamplePixel(int col, int row, int mapHeight) =>
        (2 * col, mapHeight - 2 * row - 1 - (col & 1));

    /// <summary>The hex whose tree can sit at a world point (trees are jittered by at most ±0.2).</summary>
    public (int Col, int Row) HexAt(float x, float z)
    {
        var col = (int)MathF.Round(x / ColumnStep);
        var row = (int)MathF.Round((z - ((col & 1) != 0 ? HalfRow : 0f)) / RowStep);
        return (col, row);
    }
}

/// <summary>
/// BOB's campaign tree placement (QTU::CampaignTreeGenerator + generate_campaign_tree_list_for, decompiled from
/// qttoolutility.modder.x64.dll), reproducing trees.campaign_tree_list byte for byte:
///  - one tree per hex whose sampled tree-map colour matches a campaign_tree_ids colour_hex
///  - std::minstd_rand seeded with ((row &lt;&lt; 16) | col) % (2^31 − 1) (0 → 1) picks, in order: the tree id among
///    the ids of that colour (ordinal by id; uniform_int, no draw for a single id), the z jitter, the x jitter
///    (generate_canonical&lt;float&gt; · 0.4 − 0.2 each) and the rotation index (uniform_int 0..5)
///  - y = terrain height at (x, z) (supplied by the caller)
///  - types in CA hash-map order of first appearance (<see cref="CaHash.HashMapOrder"/>), instances in row-major
///    hex order, header bounds (0, 0, width, height)
/// </summary>
public static class CampaignTreeGenerator
{
    public const int NoTree = -1;

    /// <summary>Delegate giving the tree height for a hex and its jittered world position.</summary>
    public delegate float HeightSource(int col, int row, float x, float z);

    public static CampaignTreeList Generate(int[] hexColours, HexGrid grid, TreeDatabase db, HeightSource height)
    {
        if (hexColours.Length != grid.Columns * grid.Rows)
            throw new ArgumentException($"{hexColours.Length} hex colours for a {grid.Columns}x{grid.Rows} grid.");
        var groups = db.ColourGroups();
        float half = grid.HalfRow, dz = grid.RowStep, dx = grid.ColumnStep;

        var order = new List<string>();
        var instances = new Dictionary<string, List<TreeInstance>>(StringComparer.Ordinal);
        for (var row = 0; row < grid.Rows; row++)
        for (var col = 0; col < grid.Columns; col++)
        {
            var colour = hexColours[row * grid.Columns + col];
            if (colour == NoTree || !groups.TryGetValue((uint)colour & 0xFFFFFF, out var ids)) continue;
            var rng = new MinstdRand(col, row);
            var id = ids[rng.UniformInt(0, ids.Length - 1)];
            var zj = rng.Canonical() * 0.4f;
            var xj = rng.Canonical() * 0.4f;
            var rowZ = row * dz;
            if ((col & 1) != 0) rowZ += half;
            var z = 0f + rowZ + zj - 0.2f;
            var x = xj - 0.2f + col * dx + 0f;
            var rotation = (byte)rng.UniformInt(0, 5);
            if (!instances.TryGetValue(id, out var list))
            {
                instances[id] = list = [];
                order.Add(id);
            }
            list.Add(new TreeInstance { X = x, Y = height(col, row, x, z), Z = z, Flag = 1, Variant = rotation });
        }

        var result = new CampaignTreeList { WorldWidth = grid.WorldWidth, WorldHeight = grid.WorldHeight };
        foreach (var id in CaHash.HashMapOrder(order))
        {
            var type = new TreeType { Name = id };
            type.Instances.AddRange(instances[id]);
            result.Types.Add(type);
        }
        return result;
    }

    /// <summary>
    /// Reverses a tree list onto the hex grid: the colour of each hex's tree id and its height. Throws if an instance
    /// does not sit on BOB's position for its hex (i.e. the list was not made by this generator on this grid).
    /// </summary>
    public static (int[] Colours, float[] Heights) Decode(CampaignTreeList list, HexGrid grid, TreeDatabase db)
    {
        var colours = Enumerable.Repeat(NoTree, grid.Columns * grid.Rows).ToArray();
        var heights = new float[colours.Length];
        foreach (var type in list.Types)
        {
            if (!db.Ids.TryGetValue(type.Name, out var id))
                throw new InvalidDataException($"Tree id '{type.Name}' is not in campaign_tree_ids.");
            foreach (var t in type.Instances)
            {
                var (col, row) = grid.HexAt(t.X, t.Z);
                if ((uint)col >= grid.Columns || (uint)row >= grid.Rows)
                    throw new InvalidDataException($"{type.Name} at ({t.X}, {t.Z}) is outside the {grid.Columns}x{grid.Rows} hex grid.");
                var i = row * grid.Columns + col;
                if (colours[i] != NoTree)
                    throw new InvalidDataException($"Two trees on hex ({col}, {row}).");
                colours[i] = (int)(id.ColourRgb & 0xFFFFFF);
                heights[i] = t.Y;
            }
        }
        return (colours, heights);
    }

    /// <summary>Hex colours from an AK CampaignTree map (palette TIF): the pixel BOB samples per hex.</summary>
    public static int[] ReadTreeMap(Raster<byte> map, TiffMap.Palette palette, HexGrid grid, byte noTreeIndex)
    {
        var colours = new int[grid.Columns * grid.Rows];
        for (var row = 0; row < grid.Rows; row++)
        for (var col = 0; col < grid.Columns; col++)
        {
            var (x, y) = HexGrid.SamplePixel(col, row, map.Height);
            var index = map.Contains(x, y) ? map[x, y] : noTreeIndex;
            colours[row * grid.Columns + col] = index == noTreeIndex || index >= palette.R.Length
                ? NoTree
                : (palette.R[index] >> 8) << 16 | (palette.G[index] >> 8) << 8 | palette.B[index] >> 8;
        }
        return colours;
    }

    /// <summary>
    /// An AK CampaignTree map painting each hex's 2x2 pixel footprint (the sampled pixel and its neighbours), with a
    /// palette of the DB tree colours (sorted) and <paramref name="noTreeIndex"/> for empty hexes.
    /// </summary>
    public static (Raster<byte> Map, TiffMap.Palette Palette) WriteTreeMap(int[] hexColours, HexGrid grid, int mapWidth,
                                                                         int mapHeight, TreeDatabase db, byte noTreeIndex)
    {
        var colours = db.Ids.Values.Select(t => t.ColourRgb & 0xFFFFFF).Distinct().Order().ToList();
        if (colours.Count > noTreeIndex)
            throw new InvalidDataException($"{colours.Count} tree colours do not fit below the no-tree index {noTreeIndex}.");
        var r = new ushort[256];
        var g = new ushort[256];
        var b = new ushort[256];
        var indexOf = new Dictionary<int, byte>();
        for (var i = 0; i < colours.Count; i++)
        {
            r[i] = (ushort)((colours[i] >> 16 & 0xFF) * 257);
            g[i] = (ushort)((colours[i] >> 8 & 0xFF) * 257);
            b[i] = (ushort)((colours[i] & 0xFF) * 257);
            indexOf[(int)colours[i]] = (byte)i;
        }

        var map = new Raster<byte>(mapWidth, mapHeight);
        Array.Fill(map.Data, noTreeIndex);
        for (var row = 0; row < grid.Rows; row++)
        for (var col = 0; col < grid.Columns; col++)
        {
            var colour = hexColours[row * grid.Columns + col];
            if (colour == NoTree) continue;
            var value = indexOf.TryGetValue(colour, out var index)
                ? index
                : throw new InvalidDataException($"Hex ({col}, {row}) colour {colour:X6} is not a campaign_tree_ids colour.");
            var (x, y) = HexGrid.SamplePixel(col, row, mapHeight);
            for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
                if (map.Contains(x + dx, y - dy)) map[x + dx, y - dy] = value;
        }
        return (map, new TiffMap.Palette(r, g, b));
    }
}

/// <summary>
/// MSVC std::minstd_rand with the distributions BOB uses: uniform_int_distribution&lt;int&gt; (30-bit draws with
/// rejection, as decompiled) and generate_canonical&lt;float, 24&gt;.
/// </summary>
public struct MinstdRand
{
    private const uint Modulus = 0x7FFFFFFF;
    private uint _state;

    public MinstdRand(int col, int row)
    {
        _state = (uint)(row << 16 | col) % Modulus;
        if (_state == 0) _state = 1;
    }

    public uint Next() => _state = (uint)((ulong)_state * 48271 % Modulus);

    public int UniformInt(int lo, int hi)
    {
        var range = (uint)(hi - lo);
        if (range == 0) return lo;
        var n = range + 1;
        while (true)
        {
            uint value = 0, mask = 0;
            do
            {
                uint v;
                do v = Next() - 1; while (v > 0x3FFFFFFF);
                value = value << 30 | v;
                mask = mask << 30 | 0x3FFFFFFF;
            } while (mask < range);
            if (mask / n > value / n || mask % n == range) return (int)(value % n) + lo;
        }
    }

    public float Canonical() => ((float)Next() - 1f) / 2147483648f;
}
