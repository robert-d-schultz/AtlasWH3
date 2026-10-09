using System.IO;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Scene;

/// <summary>
/// The open Terry project as the scene editor sees it: every file layer's entities, an id index, and the per-user
/// visibility/lock state resolved through the layer hierarchy. All edits go through <see cref="EntityEditor"/> (the same
/// op batches, validation and undo journal as the entity-* CLI and the terry MCP tools); after each batch only the
/// touched layers are re-read.
/// </summary>
public sealed class SceneModel
{
    public sealed record Item(ProjectLayer Layer, TerryEntityData Entity);

    private readonly ProjectPaths _paths;
    private Dictionary<string, List<TerryEntityData>> _byLayer = [];
    private Dictionary<string, Item> _byId = [];

    public EntityEditor Editor { get; private set; }
    public IReadOnlyList<ProjectLayer> Layers { get; private set; } = [];
    public IReadOnlySet<string> Invisible { get; private set; } = new HashSet<string>();
    public IReadOnlySet<string> Frozen { get; private set; } = new HashSet<string>();
    public string? ActiveLayer { get; private set; }

    /// <summary>Campaign height raster for the background, with the world size it covers (null for battle projects).</summary>
    public (Raster<ushort> Height, double WorldW, double WorldH)? Terrain { get; private set; }

    /// <summary>Campaign ground: per lf pixel the texture group (the project's BlendCampaign TIF, texture_arrays.xml
    /// order), and the groups' base colour textures. Null when the project has none.</summary>
    public (Raster<byte> Groups, TextureArrays Arrays)? TerrainBlend { get; private set; }

    /// <summary>Campaign sea surface (LowFrequencyHeightSea TIF, half the lf resolution, same height units).</summary>
    public Raster<ushort>? SeaHeight { get; private set; }

    /// <summary>One tree of the campaign forests: generated from the CampaignTree map exactly as the build does.</summary>
    public readonly record struct Tree(string TreeId, float X, float Y, float Z, float YawDegrees);

    /// <summary>Forests: one tree per hex from the CampaignTree map (BOB's placement, see CampaignTreeGenerator), with
    /// heights from the lf terrain. Empty when the project or the tree DB tables are missing.</summary>
    public IReadOnlyList<Tree> Trees { get; private set; } = [];
    public string? TreesNote { get; private set; }
    private AtlasWH3.Formats.Trees.TreeDatabase? _treeDb;

    /// <summary>One placed campaign tile from tile_list.bin: tile-map rectangle (y = 0 the south row; w/h already
    /// swapped for the 90°/270° orientations), its tile set and name, and whether it is a base tile. TileW/TileH are
    /// the database size before the turn; Low/High the record's normalized lf height range (0..1 of the 16-bit raster).</summary>
    public sealed record TilePlacement(int X, int Y, int W, int H, string TileSet, string Name, string Path, byte Orientation, bool Base, string Climate,
        int TileW = 1, int TileH = 1, float Low = 0, float High = 0, IReadOnlyList<string>? Layers = null)
    {
        public int Degrees => (Orientation & 0xF0) switch { 0x20 => 90, 0x40 => 180, 0x80 => 270, _ => 0 };
    }

    /// <summary>The compiled tile layout (what BOB or the native build placed), empty when there is none.</summary>
    public IReadOnlyList<TilePlacement> Tiles { get; private set; } = [];
    public string? TilesNote { get; private set; }
    /// <summary>Tile-map grid size (cells across and down) and the world size of one cell.</summary>
    public (int W, int H, double CellX, double CellZ) TileGrid { get; private set; }
    private int[]? _tileIndex;   // per cell: index of the top placement (features over base tiles), -1 none

    public enum TileOverlay { Off, Features, All }

    /// <summary>Which placed tiles the views draw.</summary>
    public TileOverlay TilesShown { get; set; } = TileOverlay.Off;

    public bool ShowsTile(TilePlacement t) => TilesShown == TileOverlay.All || (TilesShown == TileOverlay.Features && !t.Base);

    /// <summary>Overlay colour of a tile set (0xRRGGBB): roads brown, rivers blue, coast sand, mountains grey...</summary>
    public static uint TileColour(string set)
    {
        var s = set.ToLowerInvariant();
        return s switch
        {
            _ when s.StartsWith("roads_imperial") => 0xE0A030,
            _ when s.StartsWith("roads_paved") => 0xD08840,
            _ when s.StartsWith("roads_tracks") => 0xB08050,
            _ when s.StartsWith("roads") => 0xC07028,
            _ when s.StartsWith("river_crossing") => 0xB040E0,
            _ when s.StartsWith("river_mouth") => 0x40B0FF,
            _ when s.StartsWith("river_start") => 0x60D0FF,
            _ when s.StartsWith("river") => 0x2070FF,
            _ when s.StartsWith("canal") => 0x20D0D0,
            _ when s.StartsWith("lakes") => 0x5090E0,
            _ when s.StartsWith("sea_coast") || s.StartsWith("beach") => 0xE8D080,
            _ when s.StartsWith("generic_sea") || s == "sea" => 0x203860,
            _ when s.StartsWith("mountains") => 0xA0A0A8,
            _ when s.StartsWith("blockout_cliff") => 0xE03030,
            _ when s.StartsWith("terra") || s.Contains("farm") => 0x90D040,
            _ when s.StartsWith("mines") || s.StartsWith("salt") => 0xF0E040,
            _ when s.StartsWith("generic") => 0x50A050,
            _ => 0x808080 | (uint)(set.GetHashCode() & 0x7F7F7F),
        };
    }

    /// <summary>The placed tile covering a world point (a feature tile over the base tile there), or null.</summary>
    public TilePlacement? TileAt(double x, double z)
    {
        var (w, h, cx, cz) = TileGrid;
        if (_tileIndex is null || w == 0) return null;
        int col = (int)(x / cx), row = (int)(z / cz);
        if (col < 0 || row < 0 || col >= w || row >= h) return null;
        var i = _tileIndex[row * w + col];
        return i < 0 ? null : Tiles[i];
    }

    /// <summary>
    /// Reads the map's compiled tile_list.bin (the --pack mod packs, kit working_data, the vanilla folder, then the game
    /// packs) and the campaign
    /// tile database (sizes, tile sets) into placements. tile_list.bin is a build output: after editing tile_map.png
    /// it shows the last build, not the edit.
    /// </summary>
    private void LoadTiles()
    {
        try
        {
            var map = Project.MapName;
            var relative = $"terrain/campaigns/{map}/tile_list.bin";
            AtlasWH3.Formats.Maps.TileList? list = null;
            string? source = null;
            if (FromModPacks(relative) is var (modBytes, modPack)) { list = AtlasWH3.Formats.Maps.TileList.Read(modBytes); source = modPack; }
            if (list is null)
                foreach (var candidate in new[]
                         {
                             Path.Combine(_paths.AkWorkingDir, relative.Replace('/', Path.DirectorySeparatorChar)),
                             Path.Combine(_paths.VanillaRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                         })
                    if (File.Exists(candidate)) { list = AtlasWH3.Formats.Maps.TileList.Read(candidate); source = candidate; break; }
            if (list is null && Models.Source.TryRead(relative) is { } packed) { list = AtlasWH3.Formats.Maps.TileList.Read(packed); source = Models.Source.Locate(relative); }
            if (list is null) { TilesNote = "no tile_list.bin (build the map: Build > tile_list)"; return; }

            var db = AtlasWH3.Core.Campaign.TileMapCheck.TileMapValidator.LoadDatabase(_paths);
            static string Key(string p) => p.Replace('\\', '/').Trim('/').ToLowerInvariant();
            var byFolder = new Dictionary<string, AtlasWH3.Formats.Maps.CampaignTile>();
            var layersByFolder = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var t in db.Tiles)
                foreach (var v in t.Variations)
                {
                    byFolder.TryAdd(Key(v.Location), t);
                    layersByFolder.TryAdd(Key(v.Location), v.TextureLayers);
                }
            int gw = list.Ints[1], gh = list.Ints[2];
            if (gw <= 0 || gh <= 0 || Terrain is not var (_, worldW, worldH)) { TilesNote = "tile_list.bin has no grid size"; return; }
            var tiles = new List<TilePlacement>(list.Records.Count);
            var unknown = 0;
            foreach (var r in list.Records)
            {
                var path = list.Paths[(int)r.Path];
                byFolder.TryGetValue(Key(path), out var tile);
                if (tile is null) unknown++;
                var parts = Key(path).Split('/');
                var set = tile?.TileSet ?? (parts.Length > 3 ? parts[3] : "?");
                var name = tile?.Name ?? parts[^1];
                int w = tile?.Width ?? 1, h = tile?.Height ?? 1;
                if ((r.Orientation & 0xA0) != 0) (w, h) = (h, w); // 0x20 / 0x80: quarter turns
                tiles.Add(new TilePlacement(r.X, r.Y, w, h, set, name, path, r.Orientation,
                    AtlasWH3.Formats.Maps.TileList.IsBaseTile(path), r.Climate < list.Climates.Count ? list.Climates[r.Climate] : "",
                    tile?.Width ?? 1, tile?.Height ?? 1, r.LowHeight, r.HighHeight, layersByFolder.GetValueOrDefault(Key(path))));
            }
            var index = new int[gw * gh];
            Array.Fill(index, -1);
            foreach (var pass in new[] { true, false }) // base tiles first, features over them
                for (var i = 0; i < tiles.Count; i++)
                {
                    var t = tiles[i];
                    if (t.Base != pass) continue;
                    for (var y = t.Y; y < t.Y + t.H && y < gh; y++)
                        for (var x = t.X; x < t.X + t.W && x < gw; x++)
                            if (x >= 0 && y >= 0) index[y * gw + x] = i;
                }
            Tiles = tiles;
            _tileIndex = index;
            TileGrid = (gw, gh, worldW / gw, worldH / gh);
            TilesNote = $"{tiles.Count} tiles ({tiles.Count(t => !t.Base)} features) from {source}" + (unknown > 0 ? $"; {unknown} not in the tile database" : "");
        }
        catch (Exception ex) { TilesNote = "tiles not read: " + ex.Message; }
    }

    /// <summary>A region's main settlement: the centre of its slot-0 hexes in map.hex.</summary>
    public sealed record RegionCity(string Region, double X, double Z);

    /// <summary>
    /// The region mask: an RGBA image over the whole world (row 0 = north), one colour per land region at 35% with
    /// darker region borders and sea left clear, plus the cities. From the kit's map.hex and the map's
    /// campaign_map_playable_areas bounds, through the same hex lookup the native build uses.
    /// </summary>
    public sealed record RegionMap(byte[] Rgba, int Width, int Height, IReadOnlyList<RegionCity> Cities,
        AtlasWH3.Core.Campaign.Props.HexRegionLookup Lookup);

    /// <summary>Show the region mask and cities in the views.</summary>
    public bool ShowRegions { get; set; }
    public string? RegionsNote { get; private set; }
    private RegionMap? _regions;
    private bool _regionsTried;
    private readonly object _regionsLock = new();

    /// <summary>The region mask, built on first use (null when the kit has no map.hex for this map).</summary>
    public RegionMap? Regions
    {
        get
        {
            lock (_regionsLock)
            {
                if (_regionsTried) return _regions;
                _regionsTried = true;
                try { _regions = BuildRegions(); }
                catch (Exception ex) { RegionsNote = "regions not read: " + ex.Message; }
                return _regions;
            }
        }
    }

    /// <summary>The region mask if it has been built (never builds it).</summary>
    public RegionMap? RegionsIfBuilt => _regions;

    /// <summary>Region key at a world point (null without a region mask).</summary>
    public string? RegionAt(double x, double z) => _regions?.Lookup.RegionAt(x, z);

    /// <summary>Region key without the game prefix (3k_main_ / 3k_dlcNN_): "luoyang_capital".</summary>
    public static string RegionLabel(string region) =>
        System.Text.RegularExpressions.Regex.Replace(region, @"^3k_(main|dlc\d+)_", "");

    public static uint RegionColour(int index)
    {
        // Golden-ratio hues so neighbouring indices differ; bright, medium saturation.
        var h = index * 0.618034 % 1.0 * 6;
        double s = 0.55, v = 0.95, c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        var (r, g, b) = (int)h switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return (uint)((r + m) * 255) << 16 | (uint)((g + m) * 255) << 8 | (uint)((b + m) * 255);
    }

    private RegionMap? BuildRegions()
    {
        if (Terrain is not var (_, worldW, worldH)) { RegionsNote = "no terrain"; return null; }
        var lookup = AtlasWH3.Core.Campaign.Props.HexRegionLookup.ForMap(_paths with { MapName = Project.MapName }, out var why);
        if (lookup is null) { RegionsNote = why; return null; }
        var hex = lookup.Hex;
        int w = Math.Min(4096, hex.Width * 3), h = Math.Max(1, (int)Math.Round(w * worldH / worldW));
        var index = new int[w * h];
        Parallel.For(0, h, y =>
        {
            var z = worldH - (y + 0.5) / h * worldH;
            for (var x = 0; x < w; x++) index[y * w + x] = lookup.RegionIndexAt((x + 0.5) / w * worldW, z);
        });
        var land = hex.LandRegions.Count;
        var rgba = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = index[y * w + x];
                if (i < 0 || i >= land) continue; // sea and no-region stay clear
                var border = (x > 0 && index[y * w + x - 1] != i) || (x < w - 1 && index[y * w + x + 1] != i)
                          || (y > 0 && index[(y - 1) * w + x] != i) || (y < h - 1 && index[(y + 1) * w + x] != i);
                var c = RegionColour(i);
                var k = border ? 0.35 : 1.0;
                var o = (y * w + x) * 4;
                rgba[o] = (byte)((c >> 16 & 0xFF) * k);
                rgba[o + 1] = (byte)((c >> 8 & 0xFF) * k);
                rgba[o + 2] = (byte)((c & 0xFF) * k);
                rgba[o + 3] = (byte)(border ? 230 : 90);
            }
        });
        var sums = new Dictionary<int, (double X, double Z, int N)>();
        for (var r = 0; r < hex.Height; r++)
            for (var c = 0; c < hex.Width; c++)
                if (hex.SlotAt(c, r) == 0 && hex.RegionIndexAt(c, r) is var i and >= 0)
                {
                    var (cx, cz) = lookup.HexCentre(c, r);
                    var t = sums.GetValueOrDefault(i);
                    sums[i] = (t.X + cx, t.Z + cz, t.N + 1);
                }
        var cities = sums.Select(kv => new RegionCity(hex.RegionName(kv.Key) ?? "?", kv.Value.X / kv.Value.N, kv.Value.Z / kv.Value.N))
            .OrderBy(c => c.Region, StringComparer.Ordinal).ToList();
        RegionsNote = $"{land} land regions, {cities.Count} cities (map.hex {hex.Width}x{hex.Height})";
        return new RegionMap(rgba, w, h, cities, lookup);
    }

    /// <summary>The tree's BASE model.</summary>
    public string? TreeModel(string treeId) => _treeDb?.Model(treeId)?.Replace('\\', '/');

    /// <summary>Bumped on every change, so views know to redraw.</summary>
    public int Version { get; private set; }
    public event Action? Changed;

    public SceneModel(ProjectPaths paths, string? terryPath)
    {
        _paths = paths;
        Editor = new EntityEditor(paths, terryPath);
    }

    public string TerryPath => Editor.TerryPath;
    public TerryProject Project => Editor.Project;
    public EntityConfiguration Config => Editor.Config;
    public ComponentSchema Schema => Editor.Schema;

    public IEnumerable<Item> All => _byId.Values;

    public PrefabLibrary Prefabs => Editor.Prefabs;

    private AtlasWH3.Core.Assets.ModelLibrary? _models;
    /// <summary>Game models and textures (vanilla packs), for thumbnails, footprints and later the 3D view.</summary>
    public AtlasWH3.Core.Assets.ModelLibrary Models => _models ??= AtlasWH3.Core.Assets.ModelLibrary.ForGame(_paths, modPacks: _paths.ModPacks);

    private Dictionary<string, float[]> _modelBounds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>LOD 0 bounds per model path used in the scene (filled in the background by <see cref="LoadModelBoundsAsync"/>).</summary>
    public IReadOnlyDictionary<string, float[]> ModelBounds => _modelBounds;
    public event Action? ModelBoundsReady;

    public static string? ModelPathOf(TerryEntityData e) =>
        e.Component("ECMesh")?["model_path"] is { Length: > 0 } m ? m : e.Component("ECDecal")?["model_path"] is { Length: > 0 } d ? d : null;

    /// <summary>Reads the bounds of every model the scene uses, off the UI thread; raises <see cref="ModelBoundsReady"/>.</summary>
    public Task LoadModelBoundsAsync() => Task.Run(() =>
    {
        // Models used directly, and by the prefabs the scene's instances point at.
        var keys = All.Select(i => i.Entity.Component("ECPrefab")?["key"]).Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.OrdinalIgnoreCase);
        var paths = All.Select(i => ModelPathOf(i.Entity))
            .Concat(keys.SelectMany(k => PartsOf(k!).Select(p => p.ModelPath)))
            .Concat(Trees.Select(t => t.TreeId).Distinct().Select(TreeModel))
            .Concat(All.Select(i => i.Entity.Component("ECCompositeScene")?["path"]).Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(p => CompositeModels(p!)))
            .Where(p => p is not null && !_modelBounds.ContainsKey(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0 || !Directory.Exists(_paths.GameDataDir)) return;
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(paths, p => { if (Models.Bounds(p!) is { } b) found[p!] = b; });
        var merged = new Dictionary<string, float[]>(_modelBounds, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in found) merged[k] = v;
        _modelBounds = merged;
        ModelBoundsReady?.Invoke();
    });

    /// <summary>A prefab's content in prefab space, nested prefabs expanded: entity positions and outline polylines.</summary>
    public sealed record PrefabContent((double X, double Z)[] Points, (double X, double Z)[][] Lines, (double X0, double Z0, double X1, double Z1)? Bounds);

    private readonly Dictionary<string, PrefabContent?> _prefabContent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One entity inside a prefab (nested prefabs expanded), in prefab space.</summary>
    public sealed record PrefabPart(string Type, string? ModelPath, TerryTransform Local, TerryEntityData Entity);

    private readonly Dictionary<string, IReadOnlyList<PrefabPart>> _prefabParts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<string>> _compositeModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _compositeScales = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scale a composite scene gives its model. The .csc stream stores tagged floats (tag 0x0A + f32); the root
    /// element's first pair is (1, scale): 0.1 for the fauna scenes (whose models are real-world size, e.g. a 3.5-unit
    /// cow), 1 for the living-campaign figures (modelled at campaign scale). Scenes without that pair (the horses)
    /// use 0.1 when their model is fauna, else 1.
    /// </summary>
    public float CompositeScale(string cscPath)
    {
        lock (_compositeScales)
        {
            if (_compositeScales.TryGetValue(cscPath, out var cached)) return cached;
            var scale = 0f;
            try
            {
                if (Models.Source.TryRead(cscPath) is { } b)
                    for (var i = 0; i + 10 <= b.Length && i < 160 && scale == 0; i++)
                        if (b[i] == 0x0A && b[i + 5] == 0x0A && BitConverter.ToSingle(b, i + 1) == 1f)
                        {
                            var v = BitConverter.ToSingle(b, i + 6);
                            if (v is > 0.001f and <= 10f) scale = v;
                        }
            }
            catch (Exception) { }
            if (scale == 0 || (scale == 1 && CompositeModels(cscPath).Any(m => m.Contains("/fauna/", StringComparison.OrdinalIgnoreCase))))
                scale = CompositeModels(cscPath).Any(m => m.Contains("/fauna/", StringComparison.OrdinalIgnoreCase)) ? 0.1f : 1f;
            return _compositeScales[cscPath] = scale;
        }
    }

    /// <summary>
    /// The models a composite scene (.csc) uses. The .csc is a binary CA format; its model paths are stored as plain
    /// strings, found here by their extension and walked back over path characters. The scene's own element
    /// transforms and animation are not decoded (single-model scenes, like the living-campaign figures and animals,
    /// are exact at the entity's transform).
    /// </summary>
    public IReadOnlyList<string> CompositeModels(string cscPath)
    {
        lock (_compositeModels)
        {
            if (_compositeModels.TryGetValue(cscPath, out var cached)) return cached;
            var found = new List<string>();
            try
            {
                if (Models.Source.TryRead(cscPath) is { } bytes)
                {
                    var text = System.Text.Encoding.Latin1.GetString(bytes);
                    foreach (var ext in new[] { ".wsmodel", ".rigid_model_v2" })
                        for (var i = text.IndexOf(ext, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(ext, i + 1, StringComparison.OrdinalIgnoreCase))
                        {
                            var start = i;
                            while (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-' or '.' or '/' or '\\')) start--;
                            var path = text[start..(i + ext.Length)].Replace('\\', '/');
                            if (path.Contains('/') && !found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
                        }
                }
            }
            catch (Exception) { }
            return _compositeModels[cscPath] = found;
        }
    }

    /// <summary>What a prefab instance with this key draws: every placed entity inside it, nested prefabs expanded
    /// (overrides of the instance itself are not applied here; they change fields, not what is drawn).</summary>
    public IReadOnlyList<PrefabPart> PartsOf(string key)
    {
        lock (_prefabParts)
        {
            if (_prefabParts.TryGetValue(key, out var cached)) return cached;
            List<PrefabPart> parts = [];
            try
            {
                var probe = new System.Xml.Linq.XElement("entity", new System.Xml.Linq.XElement("ECPrefab", new System.Xml.Linq.XAttribute("key", key)));
                foreach (var x in PrefabExpander.Expand(probe, Prefabs))
                {
                    var data = LayerDocument.Describe(x.Entity, Config);
                    if (data.Transform is not var (p, r, sc)) continue;
                    parts.Add(new PrefabPart(data.Type, ModelPathOf(data), new TerryTransform(p, r, sc), data));
                }
            }
            catch (Exception) { }
            return _prefabParts[key] = parts;
        }
    }

    public PrefabContent? ContentOf(string key)
    {
        lock (_prefabContent)
        {
            if (_prefabContent.TryGetValue(key, out var cached)) return cached;
            PrefabContent? content = null;
            try
            {
                var probe = new System.Xml.Linq.XElement("entity", new System.Xml.Linq.XElement("ECPrefab", new System.Xml.Linq.XAttribute("key", key)));
                var items = PrefabExpander.Expand(probe, Prefabs);
                if (items.Count > 0)
                {
                    var data = items.Select(i => LayerDocument.Describe(i.Entity, Config)).ToList();
                    var points = data.Where(d => d.Transform is not null).Select(d => (d.Transform!.Value.Position[0], d.Transform!.Value.Position[2])).ToArray();
                    var lines = data.SelectMany(d => d.Outlines.Select(o =>
                    {
                        var pts = o.Points.Select(pt => d.ToWorld(pt.X, pt.Z));
                        return (o.Closed && o.Points.Count > 2 ? pts.Append(d.ToWorld(o.Points[0].X, o.Points[0].Z)) : pts).ToArray();
                    })).ToArray();
                    var all = points.Concat(lines.SelectMany(l => l)).ToList();
                    content = new PrefabContent(points, lines,
                        all.Count == 0 ? null : (all.Min(p => p.Item1), all.Min(p => p.Item2), all.Max(p => p.Item1), all.Max(p => p.Item2)));
                }
            }
            catch (Exception) { content = null; }
            return _prefabContent[key] = content;
        }
    }
    public Item? Find(string id) => _byId.GetValueOrDefault(id);
    public IReadOnlyList<TerryEntityData> EntitiesOf(string layerId) => _byLayer.GetValueOrDefault(layerId) ?? [];

    /// <summary>(Re)reads the project, every layer and the user state. Safe to call off the UI thread.</summary>
    public void Load()
    {
        lock (_prefabContent) _prefabContent.Clear();
        lock (_prefabParts) _prefabParts.Clear();
        lock (_compositeModels) _compositeModels.Clear();
        lock (_compositeScales) _compositeScales.Clear();
        Editor = new EntityEditor(_paths, Editor.TerryPath);
        Layers = Editor.Layers();
        _byLayer = Editor.ReadAll().ToDictionary(x => x.Layer.Id, x => x.Entities);
        Reindex();
        ReadUser();
        if (Terrain is null && Project.Database == "campaign" && Project.Find("LowFrequencyHeight") is { } map
            && File.Exists(Project.LayerTifPath(map)))
        {
            var raster = TiffMap.ReadGray16(Project.LayerTifPath(map));
            Terrain = (raster, raster.Width * AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeX, raster.Height * AtlasWH3.Core.Campaign.Terrain.Lf3K.PixelSizeZ);
        }
        if (TerrainBlend is null && Terrain is not null && Project.Find("BlendCampaign") is { } blend && File.Exists(Project.LayerTifPath(blend))
            && LoadTextureArrays() is { } arrays)
            TerrainBlend = (TiffMap.ReadPalette8(Project.LayerTifPath(blend)).Indices, arrays);
        if (SeaHeight is null && Terrain is not null && Project.Find("LowFrequencyHeightSea") is { } sea && File.Exists(Project.LayerTifPath(sea)))
            SeaHeight = TiffMap.ReadGray16(Project.LayerTifPath(sea));
        if (Terrain is not null) LoadTrees();
        if (Terrain is not null && Tiles.Count == 0) LoadTiles();
    }

    /// <summary>Generates the forests from the project's CampaignTree map (tree ids by colour from campaign_tree_ids).</summary>
    private void LoadTrees()
    {
        try
        {
            if (Project.Find("CampaignTree") is not { } treeMap || !File.Exists(Project.LayerTifPath(treeMap)))
            {
                TreesNote = "no CampaignTree map";
                return;
            }
            if (!File.Exists(_paths.TreeIdsTsv) || !File.Exists(_paths.TreeVariantsTsv))
            {
                TreesNote = $"tree DB tables not found ({_paths.TreeIdsTsv})";
                return;
            }
            _treeDb = AtlasWH3.Formats.Trees.TreeDatabase.Load(_paths.TreeIdsTsv, _paths.TreeVariantsTsv);
            var (map, palette) = TiffMap.ReadPalette8(Project.LayerTifPath(treeMap));
            var (height, worldW, _) = Terrain!.Value;
            var grid = AtlasWH3.Core.Campaign.Trees.HexGrid.ForTreeMap(map.Width, map.Height, (float)worldW);
            TreeMap = (map, palette, grid);
            var colours = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.ReadTreeMap(map, palette, grid, AtlasWH3.Core.Exporters.AkExporter.NoTreeIndex);
            var list = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.Generate(colours, grid, _treeDb, (_, _, x, z) => (float)GroundY(x, z));
            _treeByHex = new Tree?[grid.Columns * grid.Rows];
            foreach (var type in list.Types)
                foreach (var t in type.Instances)
                {
                    var (col, row) = grid.HexAt(t.X, t.Z);
                    if ((uint)col < grid.Columns && (uint)row < grid.Rows)
                        _treeByHex[row * grid.Columns + col] = new Tree(type.Name, t.X, t.Y, t.Z, t.Variant * 60f);
                }
            Trees = [.. _treeByHex.OfType<Tree>()];
            TreesNote = $"{Trees.Count} trees of {list.Types.Count} types";
        }
        catch (Exception ex) { TreesNote = "trees not generated: " + ex.Message; }
    }

    /// <summary>The CampaignTree map the forests come from (the tree editor paints this raster in place).</summary>
    public (Raster<byte> Map, TiffMap.Palette Palette, AtlasWH3.Core.Campaign.Trees.HexGrid Grid)? TreeMap { get; private set; }
    public AtlasWH3.Formats.Trees.TreeDatabase? TreeDb => _treeDb;
    private Tree?[] _treeByHex = [];

    /// <summary>Re-generates the trees of some hexes (row · columns + col) from the current tree map and ground, as the
    /// build would place them; the 3D view picks them up on its next Refresh.</summary>
    public void RegenerateTrees(IEnumerable<int> hexes)
    {
        if (TreeMap is not var (map, palette, grid) || _treeDb is null || _treeByHex.Length != grid.Columns * grid.Rows) return;
        var touched = hexes.Where(i => (uint)i < (uint)_treeByHex.Length).ToHashSet();
        if (touched.Count == 0) return;
        var all = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.ReadTreeMap(map, palette, grid, AtlasWH3.Core.Exporters.AkExporter.NoTreeIndex);
        var colours = new int[all.Length];
        Array.Fill(colours, AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.NoTree);
        foreach (var i in touched)
        {
            colours[i] = all[i];
            _treeByHex[i] = null;
        }
        var list = AtlasWH3.Core.Campaign.Trees.CampaignTreeGenerator.Generate(colours, grid, _treeDb, (_, _, x, z) => (float)GroundY(x, z));
        foreach (var type in list.Types)
            foreach (var t in type.Instances)
            {
                var (col, row) = grid.HexAt(t.X, t.Z);
                if ((uint)col < grid.Columns && (uint)row < grid.Rows)
                    _treeByHex[row * grid.Columns + col] = new Tree(type.Name, t.X, t.Y, t.Z, t.Variant * 60f);
            }
        Trees = [.. _treeByHex.OfType<Tree>()];
        TreesNote = $"{Trees.Count} trees";
    }

    /// <summary>Terrain height (world y) at a world point, bilinear over the lf raster.</summary>
    public double GroundY(double x, double z)
    {
        if (Terrain is not var (h, worldW, worldH)) return 0;
        var col = Math.Clamp(x / worldW * h.Width - 0.5, 0, h.Width - 1.001);
        var row = Math.Clamp((1 - z / worldH) * h.Height - 0.5, 0, h.Height - 1.001);
        int c0 = (int)col, r0 = (int)row;
        double fx = col - c0, fz = row - r0;
        double v = h[c0, r0] * (1 - fx) * (1 - fz) + h[c0 + 1, r0] * fx * (1 - fz) + h[c0, r0 + 1] * (1 - fx) * fz + h[c0 + 1, r0 + 1] * fx * fz;
        return v * AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightStep + AtlasWH3.Core.Campaign.Terrain.Lf3K.HeightOffset;
    }

    /// <summary>A map file from the <c>--pack</c> mod packs (first that has it), with the pack's path.</summary>
    private (byte[] Bytes, string Pack)? FromModPacks(string relative)
    {
        foreach (var pack in Models.Source.ExtraPacks)
            if (pack.TryRead(relative) is { } bytes) return (bytes, $"{pack.SourcePath} → {relative}");
        return null;
    }

    /// <summary>texture_arrays.xml for the project's map: the <c>--pack</c> mod packs, the kit's working_data, the
    /// vanilla folder, else the game packs.</summary>
    private TextureArrays? LoadTextureArrays()
    {
        var map = Project.MapName;
        var relative = $"terrain/campaigns/{map}/global_map/texture_arrays.xml";
        if (FromModPacks(relative) is var (modBytes, _))
        {
            var modTmp = Path.Combine(Path.GetTempPath(), $"texture_arrays_{map}_mod.xml");
            File.WriteAllBytes(modTmp, modBytes);
            return TextureArrays.Load(modTmp);
        }
        foreach (var candidate in new[]
                 {
                     Path.Combine(_paths.AkWorkingDir, relative.Replace('/', Path.DirectorySeparatorChar)),
                     Path.Combine(_paths.VanillaRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                     (_paths with { MapName = map }).TextureArraysXml,
                 })
            if (File.Exists(candidate)) return TextureArrays.Load(candidate);
        try
        {
            if (Models.Source.TryRead(relative) is { } bytes)
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"texture_arrays_{map}.xml");
                File.WriteAllBytes(tmp, bytes);
                return TextureArrays.Load(tmp);
            }
        }
        catch (Exception) { }
        return null;
    }

    private void Reindex()
    {
        var byId = new Dictionary<string, Item>();
        foreach (var layer in Layers)
            foreach (var e in _byLayer.GetValueOrDefault(layer.Id) ?? [])
                byId.TryAdd(e.Id, new Item(layer, e));
        _byId = byId;
    }

    private void ReadUser()
    {
        var user = TerryUserFile.Load(TerryPath);
        Invisible = user.Invisible;
        Frozen = user.Frozen;
        ActiveLayer = user.ActiveLayer;
    }

    /// <summary>True when the entity, its file layer or any layer above it is hidden (or, with frozen, locked).</summary>
    public bool IsHidden(Item item) => Inherited(item, Invisible);

    /// <summary>Draw layers hidden in the .terry.user too (view only; the saved visibility is untouched). Hidden
    /// layers still export to the game, so this is the game's view of the map.</summary>
    public bool ShowHidden { get; set; }

    /// <summary>Whether the views draw the entity: not hidden, or hidden layers shown.</summary>
    public bool IsDrawn(Item item) => ShowHidden || !IsHidden(item);
    public bool IsFrozen(Item item) => Inherited(item, Frozen);

    private bool Inherited(Item item, IReadOnlySet<string> set)
    {
        if (set.Count == 0) return false;
        if (set.Contains(item.Layer.Id) || set.Contains(item.Entity.Id)) return true;
        var seen = new HashSet<string>();
        var stack = new Stack<string>(item.Entity.Parents);
        if (item.Entity.Group is { } g) stack.Push(g);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            if (set.Contains(id)) return true;
            if (_byId.TryGetValue(id, out var parent))
            {
                foreach (var p in parent.Entity.Parents) stack.Push(p);
                if (parent.Entity.Group is { } pg) stack.Push(pg);
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- edits

    /// <summary>
    /// Applies an op batch (see <see cref="EntityEditor.Apply"/>) and refreshes the touched layers. Returns the
    /// results; throws (with nothing written) if any op fails.
    /// </summary>
    public JsonArray Apply(JsonArray ops, string? label = null)
    {
        var results = Editor.Apply(ops, label);
        var last = Editor.History().LastOrDefault();
        Refresh(last?.Files.Select(f => f.Path) ?? []);
        return results;
    }

    public List<FileJournal.HistoryEntry> Undo(int steps = 1)
    {
        var undone = Editor.Undo(steps);
        Editor = new EntityEditor(_paths, Editor.TerryPath);
        Refresh(undone.SelectMany(h => h.Files).Select(f => f.Path));
        return undone;
    }

    public void Checkpoint(string label) => Editor.Checkpoint(label);

    public List<FileJournal.HistoryEntry> Rollback(string label)
    {
        var undone = Editor.Rollback(label);
        Editor = new EntityEditor(_paths, Editor.TerryPath);
        Refresh(undone.SelectMany(h => h.Files).Select(f => f.Path));
        return undone;
    }

    /// <summary>Re-reads the given files: the .terry (layer list), .terry.user (state) or layer files.</summary>
    private void Refresh(IEnumerable<string> files)
    {
        var set = files.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Contains(Path.GetFullPath(TerryPath)))
        {
            Layers = Editor.Layers();
            foreach (var gone in _byLayer.Keys.Where(k => Layers.All(l => l.Id != k)).ToList()) _byLayer.Remove(gone);
        }
        foreach (var layer in Layers)
        {
            if (layer.FilePath is not { } path) continue;
            var full = Path.GetFullPath(path);
            if (!set.Contains(full) && _byLayer.ContainsKey(layer.Id)) continue;
            _byLayer[layer.Id] = File.Exists(full) ? Editor.Open(layer).ReadEntities() : [];
        }
        Reindex();
        ReadUser(); // cheap; also picks up state changed by other tools
        Version++;
        Changed?.Invoke();
    }
}
