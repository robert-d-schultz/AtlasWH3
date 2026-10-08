namespace AtlasWH3.Formats.Esf;

/// <summary>
/// hlp_data.esf: the campaign AI's high-level pathfinding graph (record CAI_TRANSITION_DATA).
/// One node per region slot (land regions, then sea regions); each node holds the region's areas (connected pieces),
/// and each area its transitions (the hex pairs where a path leaves the area) with a cost matrix between them.
/// Stream: u32 node count; per node: u16 id, u32 area count, per area: u16 area id, u16 centre x, u16 centre y,
/// u32 a, u32 b, u32 transition count, per transition: u16 x, u16 y (inside), u16 x, u16 y (outside), u32 cost,
/// u16 target area, u8 index, bool, bool; then count·(count−1) u32 costs between transitions.
/// </summary>
public sealed class HlpData
{
    public uint Timestamp { get; set; }
    public List<HlpNode> Nodes { get; } = [];

    public sealed class HlpNode
    {
        public ushort Id { get; set; }
        public List<HlpArea> Areas { get; } = [];
    }

    public sealed class HlpArea
    {
        public ushort AreaId { get; set; }
        public ushort CentreX { get; set; }
        public ushort CentreY { get; set; }
        public uint A { get; set; }
        public uint B { get; set; }
        public List<HlpTransition> Transitions { get; } = [];
        /// <summary>count·(count−1) values, row-major without the diagonal.</summary>
        public List<uint> Costs { get; } = [];
    }

    public sealed record HlpTransition(ushort X, ushort Y, ushort OtherX, ushort OtherY, uint Cost, ushort TargetArea,
                                       byte Index, bool Flag1, bool Flag2);

    public const string RecordName = "CAI_TRANSITION_DATA";

    public static HlpData Read(string path) => Read(File.ReadAllBytes(path));

    public static HlpData Read(byte[] file)
    {
        var r = new CaabFlatReader(file);
        if (r.RecordName != RecordName) throw new InvalidDataException($"root record {r.RecordName}, expected {RecordName}");
        var d = new HlpData { Timestamp = r.Timestamp };
        var n = r.U32();
        for (var i = 0; i < n; i++)
        {
            var node = new HlpNode { Id = r.U16() };
            var k = r.U32();
            for (var j = 0; j < k; j++)
            {
                var a = new HlpArea { AreaId = r.U16(), CentreX = r.U16(), CentreY = r.U16(), A = r.U32(), B = r.U32() };
                var m = r.U32();
                for (var t = 0; t < m; t++)
                    a.Transitions.Add(new HlpTransition(r.U16(), r.U16(), r.U16(), r.U16(), r.U32(), r.U16(), r.U8(), r.Bool(), r.Bool()));
                for (var t = 0; t < m * (m - 1); t++) a.Costs.Add(r.U32());
                node.Areas.Add(a);
            }
            d.Nodes.Add(node);
        }
        if (!r.AtEnd) throw new InvalidDataException($"hlp_data: {r.End - r.Position} bytes left over");
        return d;
    }

    public byte[] ToBytes()
    {
        var w = new CaabFlatWriter();
        w.U32((uint)Nodes.Count);
        foreach (var node in Nodes)
        {
            w.U16(node.Id);
            w.U32((uint)node.Areas.Count);
            foreach (var a in node.Areas)
            {
                w.U16(a.AreaId); w.U16(a.CentreX); w.U16(a.CentreY); w.U32(a.A); w.U32(a.B);
                w.U32((uint)a.Transitions.Count);
                foreach (var t in a.Transitions)
                {
                    w.U16(t.X); w.U16(t.Y); w.U16(t.OtherX); w.U16(t.OtherY); w.U32(t.Cost); w.U16(t.TargetArea);
                    w.U8(t.Index); w.Bool(t.Flag1); w.Bool(t.Flag2);
                }
                foreach (var c in a.Costs) w.U32(c);
            }
        }
        return w.ToFile(RecordName, Timestamp);
    }
}

/// <summary>
/// spd_data.esf: the campaign AI's landmark distance table (record CAI_SIMPLE_PATH_DIRECTORY).
/// Stream: u16 x0, u16 y0, u16 x1, u16 y1 (inclusive hex box), u32 cell count; per cell (row-major, x fastest) u32 16 and
/// 16 u32 values: for each of the 8 landmarks, two path costs (0xFFFFFFFF = no path); then u32 landmark count and
/// the landmarks as u16 x, u16 y.
/// </summary>
public sealed class SpdData
{
    public const string RecordName = "CAI_SIMPLE_PATH_DIRECTORY";
    public const uint NoPath = 0xFFFFFFFF;

    public uint Timestamp { get; set; }
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    /// <summary>Values per cell (16).</summary>
    public int Stride { get; set; } = 16;
    /// <summary>Width·Height·Stride values.</summary>
    public uint[] Values { get; set; } = [];
    public List<(ushort X, ushort Y)> Landmarks { get; } = [];

    public static SpdData Read(string path) => Read(File.ReadAllBytes(path));

    public static SpdData Read(byte[] file)
    {
        var r = new CaabFlatReader(file);
        if (r.RecordName != RecordName) throw new InvalidDataException($"root record {r.RecordName}, expected {RecordName}");
        var d = new SpdData { Timestamp = r.Timestamp, X0 = r.U16(), Y0 = r.U16(), X1 = r.U16(), Y1 = r.U16() };
        var cells = (int)r.U32();
        if (cells != d.Width * d.Height) throw new InvalidDataException($"spd_data: {cells} cells for a {d.Width}x{d.Height} box");
        d.Values = new uint[cells * 16];
        for (var c = 0; c < cells; c++)
        {
            var k = r.U32();
            if (k != 16) throw new InvalidDataException($"spd_data: cell {c} has {k} values");
            for (var j = 0; j < 16; j++) d.Values[c * 16 + j] = r.U32();
        }
        var n = r.U32();
        for (var i = 0; i < n; i++) d.Landmarks.Add((r.U16(), r.U16()));
        if (!r.AtEnd) throw new InvalidDataException($"spd_data: {r.End - r.Position} bytes left over");
        return d;
    }

    public byte[] ToBytes()
    {
        var cells = Width * Height;
        var w = new CaabFlatWriter(cells * 50 + 1024);
        w.U16((ushort)X0); w.U16((ushort)Y0); w.U16((ushort)X1); w.U16((ushort)Y1);
        w.U32((uint)cells);
        for (var c = 0; c < cells; c++)
        {
            w.U32((uint)Stride);
            for (var j = 0; j < Stride; j++) w.U32(Values[c * Stride + j]);
        }
        w.U32((uint)Landmarks.Count);
        foreach (var (x, y) in Landmarks) { w.U16(x); w.U16(y); }
        return w.ToFile(RecordName, Timestamp);
    }
}
