using AtlasWH3.Formats.Esf;

namespace AtlasWH3.Core.Campaign.AiPathfinding;

/// <summary>
/// The parts of campaign_maps\&lt;map&gt;\map_data.esf the campaign AI's offline analysis reads: per region slot
/// (REGIONS_BLOCK order: land regions, then sea regions) its areas and its settlement.
/// </summary>
public sealed class MapDataRegions
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public List<Region> Regions { get; } = [];
    /// <summary>REGION_AREA_INDEX per hex (region | area &lt;&lt; 9), row-major; from MASKED_REGIONS_DATA's run-length array.</summary>
    public ushort[] AreaMap { get; private init; } = [];
    /// <summary>World rectangle of the campaign map (THEATRES_AND_REGIONS_FOR_UI / CAMPAIGN_THEATRE: min, max).</summary>
    public (float X, float Y) WorldMin { get; private init; }
    public (float X, float Y) WorldMax { get; private init; }

    public Area AreaOf(int areaIndex) => Regions[areaIndex & 0x1FF].Areas[areaIndex >> 9];

    public sealed class Region
    {
        public int Index { get; init; }
        public string Key { get; init; } = "";
        public bool IsSea { get; init; }
        public List<Area> Areas { get; } = [];
        /// <summary>Settlement position, or null.</summary>
        public (int X, int Y)? Settlement { get; set; }
        public (int X, int Y)? Port { get; set; }
        public List<(int X, int Y)> PrimarySlot { get; } = [];
        public List<(int X, int Y)> PortSlot { get; } = [];
    }

    /// <summary>REGION_AREA_DATA: one connected piece of a region.</summary>
    public sealed class Area
    {
        /// <summary>0 = main land area, 1 = land behind a beach, 3/4 = sea, 6/7 = other (the AI skips 1, 6, 7).</summary>
        public int Type { get; init; }
        public int Id { get; init; }
        public (int X0, int Y0, int X1, int Y1) Box { get; init; }
        public ushort[] Links1 { get; init; } = [];
        public ushort[] Links2 { get; init; } = [];
        public (int X, int Y) Centre { get; init; }
        public int HexCount { get; init; }
    }

    public static MapDataRegions Read(string mapDataEsf) => Read(EsfTree.Read(mapDataEsf));

    public static MapDataRegions Read(EsfTree esf)
    {
        var hexMap = esf.Root.Descendants("HEX_MAP_DATA").First().Values;
        int w = (int)hexMap[0].Int, h = (int)hexMap[1].Int;
        var masked = esf.Root.Descendants("MASKED_REGIONS_DATA").First();
        var runs = masked.Children.OfType<EsfArray>().Last().U16s();
        var areaMap = new ushort[w * h];
        var o = 0;
        for (var k = 0; k + 1 < runs.Length; k += 2)
        {
            var n = runs[k + 1];
            if (o + n > areaMap.Length) throw new InvalidDataException("MASKED_REGIONS_DATA runs exceed the hex map");
            Array.Fill(areaMap, runs[k], o, n);
            o += n;
        }
        if (o != areaMap.Length) throw new InvalidDataException($"MASKED_REGIONS_DATA covers {o} of {areaMap.Length} hexes");
        var theatre = esf.Root.Descendants("CAMPAIGN_THEATRE").First().Values;
        var result = new MapDataRegions
        {
            Width = w, Height = h, AreaMap = areaMap,
            WorldMin = theatre[0].Vec2, WorldMax = theatre[1].Vec2,
        };
        var block = esf.Root.Descendants("REGIONS_BLOCK").First();
        var i = 0;
        foreach (var group in block.Groups)
        {
            var rd = group.OfType<EsfRecord>().First(r => r.Name == "REGION_DATA");
            var vals = rd.Values;
            var keyIndex = (uint)vals[0].Int;
            var region = new Region
            {
                Index = i++,
                Key = esf.Ascii.GetValueOrDefault(keyIndex, keyIndex.ToString()),
                IsSea = vals[1].Int != 0,
            };
            var areas = rd.Record("REGION_AREAS");
            if (areas is not null)
                foreach (var g in areas.Groups)
                {
                    var ad = g.OfType<EsfRecord>().First();
                    var c = ad.Children;
                    var v = c.OfType<EsfValue>().ToList();
                    var arrays = c.OfType<EsfArray>().ToList();
                    region.Areas.Add(new Area
                    {
                        Type = (int)v[0].Int,
                        Id = (int)v[3].Int,
                        Box = ((int)v[4].Int, (int)v[5].Int, (int)v[6].Int, (int)v[7].Int),
                        Links1 = arrays[0].U16s(),
                        Links2 = arrays[1].U16s(),
                        Centre = ((int)v[8].Int, (int)v[9].Int),
                        HexCount = (int)v[10].Int,
                    });
                }
            var si = rd.Record("SETTLEMENT_INFO");
            if (si is not null)
            {
                var sv = si.Values;
                if (sv.Count >= 2 && sv[0].Int != 0xFFFF) region.Settlement = ((int)sv[0].Int, (int)sv[1].Int);
                if (sv.Count >= 6 && sv[4].Int != 0xFFFF) region.Port = ((int)sv[4].Int, (int)sv[5].Int);
                foreach (var (name, list) in new[] { ("PRIMARY_SLOT_AREA_BLOCK", region.PrimarySlot), ("PORT_SLOT_AREA_BLOCK", region.PortSlot) })
                    if (si.Record(name) is { } slots)
                        foreach (var g in slots.Groups)
                        {
                            var gv = g.OfType<EsfValue>().ToList();
                            list.Add(((int)gv[0].Int, (int)gv[1].Int));
                        }
            }
            result.Regions.Add(region);
        }
        return result;
    }
}
