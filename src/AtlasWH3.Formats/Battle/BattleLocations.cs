using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats.Battle;

/// <summary>One placeable battle location in a compiled battle_locations_map.bin (CATCHMENT_AREA, serialise version 5).
/// Box and centre are cells, row 0 = north.</summary>
public sealed class CatchmentArea
{
    public ushort Version { get; set; } = 5;
    public CellBox Box { get; set; }
    public (int X, int Y) Centre { get; set; }
    /// <summary>The two deployment ints; vanilla areas carry (2, −1).</summary>
    public (int A, int B) Deployment { get; set; } = (2, -1);
    public (float X, float Y) AttackDirection { get; set; }
    /// <summary>A battle-map folder under terrain\battles\ the battle loads instead of the composed terrain ("" = none).</summary>
    public string Redirection { get; set; } = "";
    public string DefendingFactionRestriction { get; set; } = "";
    public string Name { get; set; } = "";
    public string RedirectionCatchment { get; set; } = "";
    public ushort FlagsVersion { get; set; } = 1;
    /// <summary>Campaign approaches allowed here: north, south, east, west.</summary>
    public bool North { get; set; } = true;
    public bool South { get; set; } = true;
    public bool East { get; set; } = true;
    public bool West { get; set; } = true;

    public CatchmentArea Clone() => (CatchmentArea)MemberwiseClone();

    public override string ToString() => $"{Name} [{Box.MinX},{Box.MinY} .. {Box.MaxX},{Box.MaxY}]";
}

/// <summary>A named list of catchment areas: the battle type, e.g. settlement_standard (walled sieges).</summary>
public sealed class CatchmentList(string key)
{
    public string Key { get; } = key;
    public List<CatchmentArea> Areas { get; } = [];

    public CatchmentList Clone()
    {
        var copy = new CatchmentList(Key);
        copy.Areas.AddRange(Areas.Select(a => a.Clone()));
        return copy;
    }
}

/// <summary>
/// terrain\battles\&lt;terrain&gt;\battle_locations_map.bin (FASTBIN0 v2), read and written byte for byte: the meta item
/// names ("land", "sea"), the catchment lists (land_ambush, settlement_standard, encampments, settlement_unfortified,
/// gate_battle) and a per-cell meta index grid (row 0 = north). Strings are u16-length Latin-1. Format: BattleMaps
/// docs/catchment-format.md. <see cref="BattleLocationsMapFile"/> is the older read-only view.
/// </summary>
public sealed class BattleLocations
{
    public const string Ambush = "land_ambush", Standard = "settlement_standard", Encampments = "encampments",
        Unfortified = "settlement_unfortified", Gate = "gate_battle";

    public ushort MapVersion { get; set; } = 2;
    public ushort MetaVersion { get; set; } = 1;
    public List<string> MetaItems { get; set; } = ["land", "sea"];
    public List<CatchmentList> Lists { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Meta item index per cell, row-major, row 0 = north.</summary>
    public int[] Cells { get; set; } = [];

    public CatchmentList? List(string key) => Lists.FirstOrDefault(l => l.Key == key);

    public int AreaCount => Lists.Sum(l => l.Areas.Count);

    /// <summary>The meta index of the battle-bearing landmass. The "land"/"sea" item names are CA-internal labels, not
    /// geography, so it is calibrated as the most common index under the areas' centres (as the blm tool does).</summary>
    public int LandIndex { get; private set; }

    /// <summary>Recomputes <see cref="LandIndex"/> from the areas' centres.</summary>
    public void CalibrateLand()
    {
        var counts = new Dictionary<int, int>();
        foreach (var a in Lists.SelectMany(l => l.Areas))
            if (a.Centre.X >= 0 && a.Centre.Y >= 0 && a.Centre.X < Width && a.Centre.Y < Height)
            {
                var v = Cells[a.Centre.Y * Width + a.Centre.X];
                counts[v] = counts.GetValueOrDefault(v) + 1;
            }
        LandIndex = counts.Count == 0 ? 0 : counts.MaxBy(kv => kv.Value).Key;
    }

    public bool IsLand(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Cells[y * Width + x] == LandIndex;

    /// <summary>Deep copy of the catchment lists (for undo snapshots).</summary>
    public List<CatchmentList> CloneLists() => Lists.Select(l => l.Clone()).ToList();

    public static BattleLocations Read(string path) => Read(File.ReadAllBytes(path));

    public static BattleLocations Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8 || !b[..8].SequenceEqual("FASTBIN0"u8)) throw new InvalidDataException("not a FASTBIN0 battle_locations_map");
        var o = 8;
        ushort U16(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]); o += 2; return v; }
        int I32(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4; return v; }
        float F32(ReadOnlySpan<byte> s) { var v = BinaryPrimitives.ReadSingleLittleEndian(s[o..]); o += 4; return v; }
        bool Bool(ReadOnlySpan<byte> s) => s[o++] != 0;
        string Str(ReadOnlySpan<byte> s) { var n = U16(s); var v = Encoding.Latin1.GetString(s.Slice(o, n)); o += n; return v; }

        var map = new BattleLocations { MapVersion = U16(b), MetaVersion = U16(b) };
        var metaCount = I32(b);
        map.MetaItems = new List<string>(metaCount);
        for (var i = 0; i < metaCount; i++) map.MetaItems.Add(Str(b));
        var listCount = I32(b);
        map.Lists = new List<CatchmentList>(listCount);
        for (var l = 0; l < listCount; l++)
        {
            var list = new CatchmentList(Str(b));
            for (var n = I32(b); n > 0; n--)
            {
                var a = new CatchmentArea { Version = U16(b) };
                a.Box = new CellBox(I32(b), I32(b), I32(b), I32(b));
                a.Centre = (I32(b), I32(b));
                a.Deployment = (I32(b), I32(b));
                a.AttackDirection = (F32(b), F32(b));
                a.Redirection = Str(b);
                a.DefendingFactionRestriction = Str(b);
                a.Name = Str(b);
                a.RedirectionCatchment = Str(b);
                a.FlagsVersion = U16(b);
                a.North = Bool(b);
                a.South = Bool(b);
                a.East = Bool(b);
                a.West = Bool(b);
                list.Areas.Add(a);
            }
            map.Lists.Add(list);
        }
        map.Width = I32(b);
        map.Height = I32(b);
        var count = I32(b);
        map.Cells = new int[count];
        for (var i = 0; i < count; i++) map.Cells[i] = I32(b);
        if (o != b.Length) throw new InvalidDataException($"trailing data: parser stopped at {o} of {b.Length} bytes");
        map.CalibrateLand();
        return map;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream(1 << 21);
        using var w = new BinaryWriter(ms, Encoding.Latin1);
        void Str(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            w.Write(checked((ushort)bytes.Length));
            w.Write(bytes);
        }
        w.Write("FASTBIN0"u8);
        w.Write(MapVersion);
        w.Write(MetaVersion);
        w.Write(MetaItems.Count);
        foreach (var item in MetaItems) Str(item);
        w.Write(Lists.Count);
        foreach (var list in Lists)
        {
            Str(list.Key);
            w.Write(list.Areas.Count);
            foreach (var a in list.Areas)
            {
                w.Write(a.Version);
                w.Write(a.Box.MinX); w.Write(a.Box.MinY); w.Write(a.Box.MaxX); w.Write(a.Box.MaxY);
                w.Write(a.Centre.X); w.Write(a.Centre.Y);
                w.Write(a.Deployment.A); w.Write(a.Deployment.B);
                w.Write(a.AttackDirection.X); w.Write(a.AttackDirection.Y);
                Str(a.Redirection);
                Str(a.DefendingFactionRestriction);
                Str(a.Name);
                Str(a.RedirectionCatchment);
                w.Write(a.FlagsVersion);
                w.Write(a.North); w.Write(a.South); w.Write(a.East); w.Write(a.West);
            }
        }
        w.Write(Width);
        w.Write(Height);
        w.Write(Cells.Length);
        foreach (var c in Cells) w.Write(c);
        w.Flush();
        return ms.ToArray();
    }
}
