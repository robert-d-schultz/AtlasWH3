using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// Editing session on a campaign map's ground-texture source: the assembly kit's <c>&lt;map&gt;.blend.&lt;layer&gt;.tif</c>
/// (8-bit palette TIF, lf resolution; pixel = texture group, texture_arrays.xml order), which the global_map build step
/// turns into global_blend.dds channel 0. Strokes go through <see cref="BlendBrush"/> with per-point pressure; undo/redo
/// are in memory (<see cref="UndoStack"/>), saves are journaled (<see cref="FileJournal"/>, output\blend_edits\&lt;map&gt;)
/// and refuse to overwrite a file changed on disk since it was loaded. Thread-safe (one lock).
/// </summary>
public sealed class BlendEditSession
{
    public sealed record StrokePoint(double X, double Y, double Pressure);

    private readonly object _lock = new();
    private readonly TerrainData _terrain;   // only BlendGroup is used (BlendBrush paints through TerrainData)
    private readonly UndoStack _undo = new();
    private string _diskHash;
    private int _version;

    public string BlendPath { get; }
    public TiffMap.Palette Palette { get; }
    public TextureArrays Textures { get; }
    public FileJournal Journal { get; }
    public Raster<byte> Blend => _terrain.BlendGroup;
    public int Width => Blend.Width;
    public int Height => Blend.Height;
    /// <summary>Unsaved strokes (undo depth since load / last save).</summary>
    public int Unsaved { get; private set; }
    /// <summary>Bumped on every change, so clients can tell their tiles are stale.</summary>
    public int Version => _version;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    public BlendEditSession(ProjectPaths paths, string blendPath, string textureArraysXml)
    {
        BlendPath = Path.GetFullPath(blendPath);
        var (indices, palette) = TiffMap.ReadPalette8(BlendPath);
        Palette = palette;
        Textures = TextureArrays.Load(textureArraysXml);
        _terrain = new TerrainData(new Raster<ushort>(1, 1), new Raster<ushort>(1, 1), indices, new Raster<byte>(1, 1),
                                   Textures, WorldCoords.Vanilla3K);
        _diskHash = FileJournal.Hash(File.ReadAllBytes(BlendPath));
        var dir = FileJournal.EditDir(paths, "blend_edits");
        if (!Path.GetFileName(Path.GetDirectoryName(BlendPath)!).Equals(paths.MapName, StringComparison.OrdinalIgnoreCase))
            dir = Path.Combine(dir, "custom_" + FileJournal.Hash(System.Text.Encoding.UTF8.GetBytes(BlendPath.ToLowerInvariant()))[..8].ToLowerInvariant());
        Journal = new FileJournal(dir);
    }

    /// <summary>The kit's blend TIF: the .terry's BlendCampaign layer, else the only <c>&lt;map&gt;.blend.*.tif</c>.</summary>
    public static string FindKitBlend(ProjectPaths paths)
    {
        var files = Directory.EnumerateFiles(paths.AkTerrainDir, $"{paths.MapName}.blend.*.tif").ToList();
        return files.Count switch
        {
            1 => files[0],
            0 => throw new FileNotFoundException($"no {paths.MapName}.blend.*.tif in {paths.AkTerrainDir}"),
            _ => throw new InvalidOperationException($"several blend TIFs in {paths.AkTerrainDir}; pass the file explicitly"),
        };
    }

    /// <summary>Editor colour of a texture group (the TIF palette, 8-bit).</summary>
    public uint PaletteRgb(int index) =>
        (uint)((Palette.R[index] >> 8) << 16 | (Palette.G[index] >> 8) << 8 | Palette.B[index] >> 8);

    /// <summary>Paints one stroke. Pen pressure (0-1; mouse/touch pass 1) scales the radius between 35 % and 100 %.
    /// Returns the dirty rectangle in blend pixels.</summary>
    public PixelRect Stroke(IReadOnlyList<StrokePoint> points, byte group, double radius, double softness, double strength, byte? onlyReplace)
    {
        if (points.Count == 0) return PixelRect.Empty;
        if (group >= Textures.Groups.Count) throw new ArgumentException($"texture group {group} out of range (0-{Textures.Groups.Count - 1})");
        lock (_lock)
        {
            var brush = new BlendBrush
            {
                Group = group, OnlyReplace = onlyReplace,
                Radius = Math.Clamp(radius, 0.5, 2000), Softness = Math.Clamp(softness, 0, 1), Strength = Math.Clamp(strength, 0.01, 1),
            };
            var dirty = PixelRect.Empty;
            brush.Begin(_terrain, points[0].X, points[0].Y);
            var prev = points[0];
            foreach (var p in points)   // the first point dabs once (distance 0)
            {
                // fill gaps between sparse points so fast pen moves stay continuous
                var dist = Math.Sqrt((p.X - prev.X) * (p.X - prev.X) + (p.Y - prev.Y) * (p.Y - prev.Y));
                var steps = Math.Max(1, (int)(dist / Math.Max(1, radius * 0.25)));
                for (var i = 1; i <= steps; i++)
                {
                    var t = (double)i / steps;
                    var pressure = prev.Pressure + (p.Pressure - prev.Pressure) * t;
                    brush.Radius = Math.Clamp(radius * (0.35 + 0.65 * Math.Clamp(pressure, 0, 1)), 0.5, 2000);
                    dirty = dirty.Union(brush.Dab(prev.X + (p.X - prev.X) * t, prev.Y + (p.Y - prev.Y) * t));
                }
                prev = p;
            }
            if (brush.End() is { } undo)
            {
                _undo.Push(undo);
                Unsaved++;
                _version++;
            }
            return dirty;
        }
    }

    public bool Undo()
    {
        lock (_lock)
        {
            if (_undo.Undo() is null) return false;
            Unsaved--;
            _version++;
            return true;
        }
    }

    public bool Redo()
    {
        lock (_lock)
        {
            if (_undo.Redo() is null) return false;
            Unsaved++;
            _version++;
            return true;
        }
    }

    /// <summary>
    /// Raw texture-group indices of one display tile: <paramref name="size"/>² samples covering
    /// <c>size·2^level</c> blend pixels from tile (tx, ty), nearest sampling; 255 outside the map.
    /// </summary>
    public byte[] Tile(int level, int tx, int ty, int size = 256)
    {
        var step = 1 << Math.Clamp(level, 0, 12);
        var result = new byte[size * size];
        var g = Blend;
        lock (_lock)
        {
            for (var y = 0; y < size; y++)
            {
                var sy = (ty * size + y) * step + step / 2;
                for (var x = 0; x < size; x++)
                {
                    var sx = (tx * size + x) * step + step / 2;
                    result[y * size + x] = sx < g.Width && sy < g.Height ? g.Data[sy * g.Width + sx] : (byte)255;
                }
            }
        }
        return result;
    }

    /// <summary>Texture group counts over the whole map.</summary>
    public int[] Counts()
    {
        var counts = new int[256];
        lock (_lock)
            foreach (var v in Blend.Data) counts[v]++;
        return counts;
    }

    public bool ChangedOnDisk() => File.Exists(BlendPath) && FileJournal.Hash(File.ReadAllBytes(BlendPath)) != _diskHash;

    /// <summary>Writes the TIF (palette kept, LZW) through the journal. Refuses when the file changed on disk since
    /// it was loaded, unless forced. Returns the journal seq.</summary>
    public int Save(string label, bool force = false)
    {
        lock (_lock)
        {
            if (!force && ChangedOnDisk())
                throw new InvalidOperationException($"{Path.GetFileName(BlendPath)} changed on disk since it was loaded (another tool); reload, or force");
            var seq = Journal.Commit([(BlendPath, p => TiffMap.WritePalette8(p, Blend, Palette, lzw: true))], label);
            _diskHash = FileJournal.Hash(File.ReadAllBytes(BlendPath));
            _undo.Clear();
            Unsaved = 0;
            _version++;
            return seq;
        }
    }
}
