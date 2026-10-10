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
    /// <summary>Bits of the region in a packed area key (region | area &lt;&lt; AreaShift): WH3 maps have over 512 regions
    /// (Old World about 1,465) and regions with over 32 areas (chaos maps), so 3K's u16 key (shift 9) is an int here.</summary>
    public const int AreaShift = 16;
    public const int RegionMask = (1 << AreaShift) - 1;
    public static int AreaKey(int region, int area) => region | area << AreaShift;
    public static RegionArea ToRegionArea(int key) => new((ushort)(key & RegionMask), (ushort)(key >> AreaShift));

    /// <summary>Packed area key per hex (<see cref="AreaKey"/>), row-major; from MASKED_REGIONS_DATA's run-length
    /// REGION_AREA_INDEX_OVERRIDE (one group per run: REGION_AREA_INDEX (region, area), u16 length).</summary>
    public int[] AreaMap { get; private init; } = [];
    /// <summary>World rectangle of the campaign map (THEATRES_AND_REGIONS_FOR_UI / CAMPAIGN_THEATRE: min, max).</summary>
    public (float X, float Y) WorldMin { get; private init; }
    public (float X, float Y) WorldMax { get; private init; }

    public Area AreaOf(int areaKey) => Regions[areaKey & RegionMask].Areas[areaKey >> AreaShift];

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
        /// <summary>The two REGION_AREA_INDEX lists of REGION_AREA_DATA, as packed area keys.</summary>
        public int[] Links1 { get; init; } = [];
        public int[] Links2 { get; init; } = [];
        public (int X, int Y) Centre { get; init; }
        public int HexCount { get; init; }
    }

    public static MapDataRegions Read(string mapDataEsf) => Read(EsfTree.Read(mapDataEsf));

    public static MapDataRegions Read(EsfTree esf)
    {
        var hexMap = esf.Root.Descendants("HEX_MAP_DATA").First().Values;
        int w = (int)hexMap[0].Int, h = (int)hexMap[1].Int;
        var masked = esf.Root.Descendants("MASKED_REGIONS_DATA").First();
        var runs = masked.Record("REGION_AREA_INDEX_OVERRIDE") ?? throw new InvalidDataException("map_data.esf has no REGION_AREA_INDEX_OVERRIDE");
        var areaMap = new int[w * h];
        var o = 0;
        foreach (var run in runs.Groups)
        {
            var key = Key(run.OfType<EsfRecord>().First());
            var n = (int)run.OfType<EsfValue>().First().Int;
            if (o + n > areaMap.Length) throw new InvalidDataException("REGION_AREA_INDEX_OVERRIDE runs exceed the hex map");
            Array.Fill(areaMap, key, o, n);
            o += n;
        }
        if (o != areaMap.Length) throw new InvalidDataException($"REGION_AREA_INDEX_OVERRIDE covers {o} of {areaMap.Length} hexes");
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
                    var links = c.OfType<EsfRecord>().Where(x => x.Name == "REGION_AREA_INDEX").ToList();
                    region.Areas.Add(new Area
                    {
                        Type = (int)v[0].Int,
                        Id = (int)v[3].Int,
                        Box = ((int)v[4].Int, (int)v[5].Int, (int)v[6].Int, (int)v[7].Int),
                        Links1 = links[0].Groups.Select(Key).ToArray(),
                        Links2 = links[1].Groups.Select(Key).ToArray(),
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

    /// <summary>A REGION_AREA_INDEX (u16 region, u16 area index), as a record or as one group of a nested list.</summary>
    private static int Key(EsfRecord r) => Key(r.Children);

    private static int Key(List<EsfNode> values)
    {
        var v = values.OfType<EsfValue>().ToList();
        int region = (int)v[0].Int, area = (int)v[1].Int;
        return AreaKey(region, area);
    }
}
