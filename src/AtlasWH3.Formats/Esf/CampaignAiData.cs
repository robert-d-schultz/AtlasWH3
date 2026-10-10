namespace AtlasWH3.Formats.Esf;

/// <summary>A region area: REGION_AREA_INDEX (u16 region, u16 area index within the region).</summary>
public readonly record struct RegionArea(ushort Region, ushort Area)
{
    public const string RecordName = "REGION_AREA_INDEX";
    public override string ToString() => $"({Region},{Area})";
}

/// <summary>
/// hlp_data.esf: the campaign AI's high-level pathfinding data (root CAI_HIGH_LEVEL_PATHFINDER v1).
///  - TRANSITION_DATA (nested, one group): 3K's CAI_TRANSITION_DATA stream with REGION_AREA_INDEX records in place of
///    3K's packed u16 area ids. u32 node count; per node (one per region): u16 id, u32 area count; per area:
///    REGION_AREA_INDEX, u16 centre x, u16 centre y, u32 a, u32 b, u32 transition count; per transition: u16 x, u16 y
///    (inside), u16 x, u16 y (outside), u32 cost, REGION_AREA_INDEX target, u8 index, bool, bool; then count·(count−1)
///    u32 costs between the area's transitions.
///  - a u32 array of 1024 × 1024: region-to-region costs, row-major [from · 1024 + to], upper triangle only
///    (from &lt; to), <see cref="NoRegionCost"/> elsewhere and between regions without a path;
///  - a u8 array of 1024 × 1024: region-to-region hop counts, symmetric, 0 on the diagonal and without a path;
///  - OTHER_CONSTANTS (nested, one group): u32 the largest region-to-region cost.
/// </summary>
public sealed class HlpData
{
    public const string RecordName = "CAI_HIGH_LEVEL_PATHFINDER";
    public const int RegionTableSize = 1024;
    public const uint NoRegionCost = 0xFFFFFFFE;

    public uint Magic { get; set; } = CaabWriter.MagicCbab;
    public uint Timestamp { get; set; }
    public List<HlpNode> Nodes { get; } = [];
    /// <summary><see cref="RegionTableSize"/>² values.</summary>
    public uint[] RegionCosts { get; set; } = NewRegionCosts();
    /// <summary><see cref="RegionTableSize"/>² values.</summary>
    public byte[] RegionHops { get; set; } = new byte[RegionTableSize * RegionTableSize];
    public uint MaxRegionCost { get; set; }

    public static uint[] NewRegionCosts()
    {
        var a = new uint[RegionTableSize * RegionTableSize];
        Array.Fill(a, NoRegionCost);
        return a;
    }

    public sealed class HlpNode
    {
        public ushort Id { get; set; }
        public List<HlpArea> Areas { get; } = [];
    }

    public sealed class HlpArea
    {
        public RegionArea Area { get; set; }
        public ushort CentreX { get; set; }
        public ushort CentreY { get; set; }
        public uint A { get; set; }
        public uint B { get; set; }
        public List<HlpTransition> Transitions { get; } = [];
        /// <summary>count·(count−1) values, row-major without the diagonal.</summary>
        public List<uint> Costs { get; } = [];
    }

    public sealed record HlpTransition(ushort X, ushort Y, ushort OtherX, ushort OtherY, uint Cost, RegionArea Target,
                                       byte Index, bool Flag1, bool Flag2);

    public static HlpData Read(string path) => Read(File.ReadAllBytes(path));

    public static HlpData Read(byte[] file)
    {
        var r = new CaabReader(file);
        if (r.RootName != RecordName) throw new InvalidDataException($"root record {r.RootName}, expected {RecordName}");
        var d = new HlpData { Magic = r.Magic, Timestamp = r.Timestamp };
        var (tdEnd, _) = r.BeginRecord("TRANSITION_DATA");
        var groupEnd = r.BeginGroup();
        var n = r.U32();
        for (var i = 0; i < n; i++)
        {
            var node = new HlpNode { Id = r.U16() };
            var k = r.U32();
            for (var j = 0; j < k; j++)
            {
                var a = new HlpArea { Area = ReadArea(r), CentreX = r.U16(), CentreY = r.U16(), A = r.U32(), B = r.U32() };
                var m = r.U32();
                for (var t = 0; t < m; t++)
                    a.Transitions.Add(new HlpTransition(r.U16(), r.U16(), r.U16(), r.U16(), r.U32(), ReadArea(r), r.U8(), r.Bool(), r.Bool()));
                for (var t = 0; t < m * (m - 1); t++) a.Costs.Add(r.U32());
                node.Areas.Add(a);
            }
            d.Nodes.Add(node);
        }
        r.Expect(groupEnd, "TRANSITION_DATA");
        r.Expect(tdEnd, "TRANSITION_DATA");
        var costs = r.TypedArray(0x08);
        d.RegionCosts = new uint[costs.Length / 4];
        for (var i = 0; i < d.RegionCosts.Length; i++) d.RegionCosts[i] = BitConverter.ToUInt32(costs.Slice(4 * i, 4));
        d.RegionHops = r.TypedArray(0x06).ToArray();
        var (ocEnd, _) = r.BeginRecord("OTHER_CONSTANTS");
        r.BeginGroup();
        d.MaxRegionCost = r.U32();
        r.Expect(ocEnd, "OTHER_CONSTANTS");
        r.Expect(r.End, "hlp_data");
        return d;
    }

    private static RegionArea ReadArea(CaabReader r)
    {
        var (end, _) = r.BeginRecord(RegionArea.RecordName);
        var a = new RegionArea(r.U16(), r.U16());
        r.Expect(end, RegionArea.RecordName);
        return a;
    }

    private static void WriteArea(CaabWriter w, RegionArea a) =>
        w.Record(RegionArea.RecordName, 0, x => { x.U16(a.Region); x.U16(a.Area); });

    public byte[] ToBytes()
    {
        var w = new CaabWriter(RecordName, 1, (int)(RegionCosts.Length * 5L + (1 << 20)));
        w.NestedRecord("TRANSITION_DATA", 0, [x =>
        {
            x.U32((uint)Nodes.Count);
            foreach (var node in Nodes)
            {
                x.U16(node.Id);
                x.U32((uint)node.Areas.Count);
                foreach (var a in node.Areas)
                {
                    WriteArea(x, a.Area);
                    x.U16(a.CentreX); x.U16(a.CentreY); x.U32(a.A); x.U32(a.B);
                    x.U32((uint)a.Transitions.Count);
                    foreach (var t in a.Transitions)
                    {
                        x.U16(t.X); x.U16(t.Y); x.U16(t.OtherX); x.U16(t.OtherY); x.U32(t.Cost);
                        WriteArea(x, t.Target);
                        x.U8(t.Index); x.Bool(t.Flag1); x.Bool(t.Flag2);
                    }
                    foreach (var c in a.Costs) x.U32(c);
                }
            }
        }]);
        w.U32Array(RegionCosts);
        w.U8Array(RegionHops);
        w.NestedRecord("OTHER_CONSTANTS", 0, [x => x.U32(MaxRegionCost)]);
        return w.ToFile(Timestamp, Magic);
    }
}

/// <summary>
/// spd_data.esf: the campaign AI's landmark distance table (root CAI_SIMPLE_PATH_DIRECTORY v1).
/// Stream: u16 x0, u16 y0, u16 x1, u16 y1 (inclusive hex box), u32 cell count, u32 landmark set count, u32 area count;
/// per cell (row-major, x fastest): 16 u32 costs (0..7: the 8 landmarks of the cell's set, 8..15: the 8 landmarks of
/// the cell's area; <see cref="NoPath"/> = none), u32 set index (<see cref="NoSet"/> = none), u16 region, u16 area
/// (0xFFFF = none). Then REFERENCE_POINTS (nested, two groups): the sets' landmarks (8 per set, u16 x, u16 y); per
/// area u16 region, u16 area and its 8 landmarks (0xFFFF, 0xFFFF where it has none).
/// </summary>
public sealed class SpdData
{
    public const string RecordName = "CAI_SIMPLE_PATH_DIRECTORY";
    public const uint NoPath = 0xFFFFFFFF;
    public const uint NoSet = 0xFFFFFFFF;
    public const ushort None = 0xFFFF;
    public const int Stride = 16;

    public uint Magic { get; set; } = CaabWriter.MagicCbab;
    public uint Timestamp { get; set; }
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    /// <summary>Width·Height·<see cref="Stride"/> values.</summary>
    public uint[] Values { get; set; } = [];
    /// <summary>Per cell: its landmark set.</summary>
    public uint[] Sets { get; set; } = [];
    /// <summary>Per cell: its region area (<see cref="None"/>, <see cref="None"/> for none).</summary>
    public RegionArea[] Areas { get; set; } = [];
    /// <summary>8 per set.</summary>
    public List<(ushort X, ushort Y)> SetLandmarks { get; } = [];
    public List<(RegionArea Area, (ushort X, ushort Y)[] Landmarks)> AreaLandmarks { get; } = [];

    public int SetCount => SetLandmarks.Count / 8;

    public static SpdData Read(string path) => Read(File.ReadAllBytes(path));

    public static SpdData Read(byte[] file)
    {
        var r = new CaabReader(file);
        if (r.RootName != RecordName) throw new InvalidDataException($"root record {r.RootName}, expected {RecordName}");
        var d = new SpdData { Magic = r.Magic, Timestamp = r.Timestamp, X0 = r.U16(), Y0 = r.U16(), X1 = r.U16(), Y1 = r.U16() };
        var cells = (int)r.U32();
        if (cells != d.Width * d.Height) throw new InvalidDataException($"spd_data: {cells} cells for a {d.Width}x{d.Height} box");
        var sets = (int)r.U32();
        var areas = (int)r.U32();
        d.Values = new uint[(long)cells * Stride];
        d.Sets = new uint[cells];
        d.Areas = new RegionArea[cells];
        for (var c = 0; c < cells; c++)
        {
            var o = (long)c * Stride;
            for (var j = 0; j < Stride; j++) d.Values[o + j] = r.U32();
            d.Sets[c] = r.U32();
            d.Areas[c] = new RegionArea(r.U16(), r.U16());
        }
        var (end, groups) = r.BeginRecord("REFERENCE_POINTS");
        if (groups != 2) throw new InvalidDataException($"REFERENCE_POINTS has {groups} groups");
        var g0 = r.BeginGroup();
        for (var i = 0; i < sets * 8; i++) d.SetLandmarks.Add((r.U16(), r.U16()));
        r.Expect(g0, "REFERENCE_POINTS sets");
        var g1 = r.BeginGroup();
        for (var i = 0; i < areas; i++)
        {
            var a = new RegionArea(r.U16(), r.U16());
            var lm = new (ushort, ushort)[8];
            for (var j = 0; j < 8; j++) lm[j] = (r.U16(), r.U16());
            d.AreaLandmarks.Add((a, lm));
        }
        r.Expect(g1, "REFERENCE_POINTS areas");
        r.Expect(end, "REFERENCE_POINTS");
        r.Expect(r.End, "spd_data");
        return d;
    }

    public byte[] ToBytes()
    {
        var cells = Width * Height;
        var w = new CaabWriter(RecordName, 1, (int)Math.Min(Array.MaxLength, cells * 70L + (1 << 20)));
        w.U16((ushort)X0); w.U16((ushort)Y0); w.U16((ushort)X1); w.U16((ushort)Y1);
        w.U32((uint)cells);
        w.U32((uint)SetCount);
        w.U32((uint)AreaLandmarks.Count);
        for (var c = 0; c < cells; c++)
        {
            var o = (long)c * Stride;
            for (var j = 0; j < Stride; j++) w.U32(Values[o + j]);
            w.U32(Sets[c]);
            w.U16(Areas[c].Region); w.U16(Areas[c].Area);
        }
        w.NestedRecord("REFERENCE_POINTS", 0,
        [
            x => { foreach (var (px, py) in SetLandmarks) { x.U16(px); x.U16(py); } },
            x =>
            {
                foreach (var (a, lm) in AreaLandmarks)
                {
                    x.U16(a.Region); x.U16(a.Area);
                    foreach (var (px, py) in lm) { x.U16(px); x.U16(py); }
                }
            },
        ]);
        return w.ToFile(Timestamp, Magic);
    }
}
