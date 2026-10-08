using System.Text.Json.Nodes;
using Microsoft.AspNetCore.ResponseCompression;
using SkiaSharp;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Core.Editing;

// AtlasWH3 web editor: the campaign tile map and the ground-texture blend in a browser (iPad + Apple Pencil).
// One process holds one tile-map session and one blend session; the page (wwwroot) sends strokes, the server applies
// and validates them with the same code as the CLI / MCP tools / WPF window, and saves through the shared journals.
//
//   AtlasWH3.Web --map <map> [--ak <kit>] [--tilemap <png>] [--blend <tif>] [--port 5180] [--bind 127.0.0.1]
//
// Default bind is localhost; reach it from other devices with `tailscale serve --bg 5180` (HTTPS on the tailnet),
// or --bind 0.0.0.0 on a trusted network.

var paths = ProjectPaths.FromArgs(args, out var rest);
string? Opt(string name)
{
    var i = Array.FindIndex(rest, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
}
var port = int.Parse(Opt("--port") ?? "5180");
var bind = Opt("--bind") ?? "127.0.0.1";
var tileMapPath = Opt("--tilemap");
var blendPath = Opt("--blend") ?? BlendEditSession.FindKitBlend(paths);
var textureArraysXml = new ProjectPaths().TextureArraysXml;   // the same texture_arrays.xml for every 3K map

Console.WriteLine($"loading tile map {tileMapPath ?? paths.AkTerrainDir + "\\tile_map.png"} ...");
var tiles = new TileEditSession(new TileMapEditor(paths, tileMapPath));
_ = tiles.Editor.Database;
Console.WriteLine($"loading blend {blendPath} ...");
var blend = new BlendEditSession(paths, blendPath, textureArraysXml);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.UseUrls($"http://{bind}:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]);
});
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
var app = builder.Build();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

static string Hex(uint rgb) => (rgb & 0xffffff).ToString("x6");

// ---------------------------------------------------------------- info

app.MapGet("/api/info", () =>
{
    var db = tiles.Editor.Database;
    var map = tiles.Map;
    var counts = map.HexColours().GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
    var area = TileMapValidator.Category(null);
    var blendCounts = blend.Counts();
    return Results.Json(new
    {
        map = paths.MapName,
        tileMap = tiles.Editor.TileMapPath,
        blendFile = blend.BlendPath,
        hexes = new[] { map.Width, map.Height },
        pixels = new[] { map.PixelWidth, map.PixelHeight },
        blendSize = new[] { blend.Width, blend.Height },
        sets = db.TileSets.Select(s => new
        {
            name = s.Name, rgb = Hex(s.Rgb), count = counts.GetValueOrDefault(s.Rgb),
            kind = TileMapValidator.Category(s.Name) == area ? "area" : "line",
        }),
        colourSets = TileMapValidator.PlacementColours(db).ToDictionary(k => Hex(k.Key), k => k.Value),
        textures = blend.Textures.Groups.Select(g => new
        {
            index = g.Index, name = g.Name, rgb = Hex(blend.PaletteRgb(g.Index)), natural = Hex(Thumbs.Average(paths, g.Index, g.Name)),
            count = blendCounts[g.Index],
        }),
    });
});

// ---------------------------------------------------------------- tile map

object TileState(TileEditSession.StrokeResult? r = null) => new
{
    changed = r?.Changed.Select(c => new object[] { c.Col, c.Row, Hex(c.Rgb) }) ?? [],
    issues = Issues(tiles.Issues, false),
    pending = tiles.Pending,
    pendingHexes = tiles.PendingHexes().Select(h => new[] { h.Col, h.Row }),
    strokes = tiles.Strokes,
    version = tiles.Version,
};

static object Issues(IEnumerable<TileMapFinding> findings, bool allowWarnings) => findings.Select(f => new
{
    code = f.Code, severity = f.Severity, blocking = TileMapEditor.Blocks(f, allowWarnings), count = f.Count, message = f.Message,
    hexes = f.AllHexes.Take(500),
});

app.MapGet("/api/tiles/png", () => Results.File(tiles.Png(), "image/png"));
app.MapGet("/api/tiles/state", () => Results.Json(TileState()));
app.MapPost("/api/tiles/op", async (HttpRequest req) =>
{
    var op = (await JsonNode.ParseAsync(req.Body))?.AsObject() ?? throw new ArgumentException("body must be a JSON op");
    try { return Results.Json(TileState(tiles.Apply(op))); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.Json(new { error = e.Message }, statusCode: 400);
    }
});
app.MapPost("/api/tiles/undo", () => Results.Json(TileState(tiles.Undo())));
app.MapPost("/api/tiles/reload", () => { tiles.Reload(); return Results.Json(TileState()); });
app.MapPost("/api/tiles/save", (bool? allowWarnings, bool? force) =>
{
    var r = tiles.Save(allowWarnings == true, force == true);
    return Results.Json(new
    {
        written = r.Written, seq = r.Seq, hexes = r.Changed.Count, blocking = r.Blocking,
        issues = Issues(r.NewIssues, allowWarnings == true), state = TileState(),
    });
});
app.MapGet("/api/tiles/history", () => Results.Json(tiles.Editor.Journal.History().Select(h => new { h.Seq, h.Time, h.Label }).Reverse()));

TileMapEditor.SimulationResult? simResult = null;
string? simError = null;
Task? simTask = null;
app.MapPost("/api/tiles/simulate", () =>
{
    if (simTask is { IsCompleted: false }) return Results.Json(new { running = true });
    simError = null;
    simTask = Task.Run(() =>
    {
        try { simResult = tiles.Simulate(); }
        catch (Exception e) { simError = e.Message; }
    });
    return Results.Json(new { running = true });
});
app.MapGet("/api/tiles/simulate", () => Results.Json(new
{
    running = simTask is { IsCompleted: false },
    error = simError,
    result = simResult is null ? null : new
    {
        seconds = Math.Round(simResult.Elapsed.TotalSeconds), placed = simResult.Summary.Placed,
        noTile = simResult.NoTileHexes, inEdited = simResult.InEdited,
    },
}));

// ---------------------------------------------------------------- blend

object BlendState(PixelRect? dirty = null) => new
{
    version = blend.Version, unsaved = blend.Unsaved, canUndo = blend.CanUndo, canRedo = blend.CanRedo,
    dirty = dirty is { IsEmpty: false } d ? new[] { d.X0, d.Y0, d.X1, d.Y1 } : null,
};

app.MapGet("/api/blend/tile/{level:int}/{tx:int}/{ty:int}", (int level, int tx, int ty) =>
    Results.Bytes(blend.Tile(level, tx, ty), "application/octet-stream"));
app.MapGet("/api/blend/state", () => Results.Json(BlendState()));
app.MapPost("/api/blend/stroke", async (HttpRequest req) =>
{
    var o = (await JsonNode.ParseAsync(req.Body))?.AsObject() ?? throw new ArgumentException("body must be JSON");
    var points = o["points"]!.AsArray().Select(p =>
    {
        var a = p!.AsArray();
        return new BlendEditSession.StrokePoint((double)a[0]!, (double)a[1]!, a.Count > 2 ? (double)a[2]! : 1);
    }).ToList();
    byte? only = o["onlyReplace"] is JsonValue v && v.TryGetValue<int>(out var ov) && ov >= 0 ? (byte)ov : null;
    try
    {
        var dirty = blend.Stroke(points, (byte)(int)o["group"]!, (double?)o["radius"] ?? 20, (double?)o["softness"] ?? 0.5,
                                 (double?)o["strength"] ?? 1, only);
        return Results.Json(BlendState(dirty));
    }
    catch (ArgumentException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
});
app.MapPost("/api/blend/undo", () => { blend.Undo(); return Results.Json(BlendState()); });
app.MapPost("/api/blend/redo", () => { blend.Redo(); return Results.Json(BlendState()); });
app.MapPost("/api/blend/save", (bool? force) =>
{
    try
    {
        var n = blend.Unsaved;
        var seq = blend.Save($"web: {n} texture strokes", force == true);
        return Results.Json(new { seq, state = BlendState() });
    }
    catch (InvalidOperationException e) { return Results.Json(new { error = e.Message }, statusCode: 409); }
});
app.MapGet("/api/blend/thumb/{index:int}", (int index) =>
    index >= 0 && index < blend.Textures.Groups.Count && Thumbs.Png(paths, index, blend.Textures.Groups[index].Name) is { } png
        ? Results.File(png, "image/png") : Results.NotFound());
app.MapGet("/api/blend/history", () => Results.Json(blend.Journal.History().Select(h => new { h.Seq, h.Time, h.Label }).Reverse()));

Console.WriteLine($"AtlasWH3 web editor on http://{bind}:{port}/  (map {paths.MapName})");
if (bind == "127.0.0.1") Console.WriteLine($"iPad via Tailscale: run `tailscale serve --bg {port}` and open the https://<this-pc>.<tailnet>.ts.net/ address");
app.Run();

/// <summary>64 px texture thumbnails and average colours from the terrain texture cache (cache\terrain_textures).</summary>
static class Thumbs
{
    private static readonly Dictionary<int, byte[]?> PngCache = [];
    private static readonly Dictionary<int, uint> AvgCache = [];

    private static string? Source(ProjectPaths paths, int index, string name)
    {
        var f = Path.Combine(paths.TextureCacheDir, $"{index:00}_{name}.png");
        return File.Exists(f) ? f : null;
    }

    public static byte[]? Png(ProjectPaths paths, int index, string name)
    {
        lock (PngCache)
        {
            if (PngCache.TryGetValue(index, out var cached)) return cached;
            byte[]? result = null;
            if (Source(paths, index, name) is { } f)
            {
                using var bmp = SKBitmap.Decode(f);
                using var small = bmp.Resize(new SKImageInfo(64, 64), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var data = small.Encode(SKEncodedImageFormat.Png, 90);
                result = data.ToArray();
            }
            return PngCache[index] = result;
        }
    }

    public static uint Average(ProjectPaths paths, int index, string name)
    {
        lock (AvgCache)
        {
            if (AvgCache.TryGetValue(index, out var c)) return c;
            uint rgb = 0x808080;
            if (Source(paths, index, name) is { } f)
            {
                using var bmp = SKBitmap.Decode(f);
                long r = 0, g = 0, b = 0;
                var pixels = bmp.Pixels;
                foreach (var px in pixels) { r += px.Red; g += px.Green; b += px.Blue; }
                var n = Math.Max(1, pixels.Length);
                rgb = (uint)(r / n << 16 | g / n << 8 | b / n);
            }
            return AvgCache[index] = rgb;
        }
    }
}
