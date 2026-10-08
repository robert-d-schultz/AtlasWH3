using AtlasWH3.Formats.Maps;
using BitMiracle.LibTiff.Classic;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// A terrain map's layers composited the way Terry exports them for BOB (qttoolutility TerrainMapLayer::composite and
/// measurements on the user's projects): only visible layers with an opacity above 0 count, bottom first.
///  - heights (Height, HeightSea, HeightShroud) add: out += opacity · layer;
///  - palette maps (BlendCampaign, CampaignTree, EventAreaMask) replace wherever the layer is not 255 (255 = empty);
///  - colour overlays (ColorOverlay, ColorOverlaySea) hard-light each layer onto the ones below where its alpha is
///    above 0; the bottom layer comes out as itself (WH3_visual_map_decompiler docs/colour-overlay-blending.md);
///  - masks (CorruptionMask, SnowMask, PatchVisibilityMask) union: out += opacity · layer · (1 − out), in 0..1.
/// Layers are read row by row, so a composite costs one output raster.
/// </summary>
public static class TerrainComposite
{
    public enum Rule { Add, Keyed, HardLight, Union }

    public static Rule RuleFor(string mapType) => mapType switch
    {
        "Height" or "HeightSea" or "HeightShroud" or "LowFrequencyHeight" or "LowFrequencyHeightSea" => Rule.Add,
        "BlendCampaign" or "CampaignTree" or "EventAreaMask" => Rule.Keyed,
        "ColorOverlay" or "ColorOverlaySea" => Rule.HardLight,
        "CorruptionMask" or "SnowMask" or "PatchVisibilityMask" => Rule.Union,
        _ => throw new NotSupportedException($"no compositing rule for terrain map type {mapType}"),
    };

    /// <summary>The TIFs Terry composites for <paramref name="map"/>, bottom first (a missing TIF is an error).</summary>
    public static IReadOnlyList<(TerryProject.TerrainLayer Layer, string Tif)> Inputs(TerryProject project, TerryProject.TerrainMap map)
    {
        var inputs = new List<(TerryProject.TerrainLayer, string)>();
        foreach (var layer in map.Layers.Where(l => l.Composited))
        {
            var tif = project.LayerTifPath(map, layer);
            if (!File.Exists(tif)) throw new FileNotFoundException($"{map.Type} layer '{layer.Name}' ({layer.Id}): {tif} is missing", tif);
            inputs.Add((layer, tif));
        }
        return inputs;
    }

    private static TerryProject.TerrainMap Map(TerryProject project, string type) =>
        project.Find(type) ?? throw new InvalidDataException($"{project.Path} has no {type} map");

    /// <summary>A float height map (Height, HeightSea, HeightShroud): the sum of its layers.</summary>
    public static Raster<float> Heights(TerryProject project, string type)
    {
        var map = Map(project, type);
        var (w, h) = map.Size;
        var result = new Raster<float>(w, h);
        foreach (var (layer, tif) in Inputs(project, map))
        {
            using var rows = new Rows(tif, w, h, 32, SampleFormat.IEEEFP);
            var line = new float[w];
            for (var y = 0; y < h; y++)
            {
                rows.Read(y, line.AsSpan());
                var o = result.Data.AsSpan(y * w, w);
                if (layer.Opacity == 1f) for (var x = 0; x < w; x++) o[x] += line[x];
                else for (var x = 0; x < w; x++) o[x] += layer.Opacity * line[x];
            }
        }
        return result;
    }

    /// <summary>A palette map (BlendCampaign, CampaignTree, EventAreaMask): upper layers replace where not 255.
    /// The palette is the first layer's that has one.</summary>
    public static (Raster<byte> Indices, TiffMap.Palette? Palette) Indexed(TerryProject project, string type)
    {
        var map = Map(project, type);
        var (w, h) = map.Size;
        var result = new Raster<byte>(w, h);
        Array.Fill(result.Data, (byte)255);
        TiffMap.Palette? palette = null;
        foreach (var (_, tif) in Inputs(project, map))
        {
            using var rows = new Rows(tif, w, h, 8, SampleFormat.UINT);
            palette ??= rows.Palette;
            var line = new byte[w];
            for (var y = 0; y < h; y++)
            {
                rows.Read(y, line.AsSpan());
                var o = result.Data.AsSpan(y * w, w);
                for (var x = 0; x < w; x++) if (line[x] != 255) o[x] = line[x];
            }
        }
        return (result, palette);
    }

    /// <summary>A mask (CorruptionMask, SnowMask, PatchVisibilityMask): the union of its layers, 0..255.</summary>
    public static Raster<byte> Mask(TerryProject project, string type)
    {
        var map = Map(project, type);
        var (w, h) = map.Size;
        var inputs = Inputs(project, map);
        var result = new Raster<byte>(w, h);
        if (inputs.Count == 0) return result;
        var acc = new float[w];
        var line = new byte[w];
        var readers = inputs.Select(i => new Rows(i.Tif, w, h, 8, SampleFormat.UINT)).ToList();
        try
        {
            for (var y = 0; y < h; y++)
            {
                Array.Clear(acc);
                for (var l = 0; l < readers.Count; l++)
                {
                    readers[l].Read(y, line.AsSpan());
                    var opacity = inputs[l].Layer.Opacity;
                    for (var x = 0; x < w; x++) acc[x] += opacity * (line[x] / 255f) * (1f - acc[x]);
                }
                var o = result.Data.AsSpan(y * w, w);
                for (var x = 0; x < w; x++) o[x] = (byte)Math.Clamp(MathF.Round(acc[x] * 255f), 0, 255);
            }
        }
        finally { foreach (var r in readers) r.Dispose(); }
        return result;
    }

    /// <summary>A colour overlay (ColorOverlay, ColorOverlaySea) as 0xAABBGGRR: hard light of each layer onto the ones
    /// below.</summary>
    public static Raster<uint> Overlay(TerryProject project, string type)
    {
        var map = Map(project, type);
        var (w, h) = map.Size;
        var result = new Raster<uint>(w, h);
        var first = true;
        foreach (var (layer, tif) in Inputs(project, map))
        {
            using var rows = new Rows(tif, w, h, 8, SampleFormat.UINT);
            var line = new uint[w];
            for (var y = 0; y < h; y++)
            {
                rows.ReadRgba(y, line);
                var o = result.Data.AsSpan(y * w, w);
                if (first) { line.CopyTo(o); continue; }
                for (var x = 0; x < w; x++)
                {
                    var top = line[x];
                    var a = (top >> 24) / 255f * layer.Opacity;
                    if (a <= 0) continue;
                    var bottom = o[x];
                    uint mixed = 0;
                    for (var c = 0; c < 24; c += 8)
                    {
                        int t = (int)(top >> c) & 0xFF, b = (int)(bottom >> c) & 0xFF;
                        var hl = HardLight(t, b);
                        var v = a >= 1f ? hl : (int)MathF.Round(b + (hl - b) * a);
                        mixed |= (uint)v << c;
                    }
                    o[x] = mixed | (Math.Max(top >> 24, bottom >> 24) << 24);
                }
            }
            first = false;
        }
        return result;
    }

    /// <summary>Hard light of one 0..255 channel: top &lt; 128 multiplies, else screens.</summary>
    public static int HardLight(int top, int bottom) =>
        top < 128 ? (2 * top * bottom + 127) / 255 : 255 - (2 * (255 - top) * (255 - bottom) + 127) / 255;

    /// <summary>Scanline reader of one layer TIF, checked against the map's size and the expected sample type.</summary>
    private sealed class Rows : IDisposable
    {
        private readonly Tiff _tif;
        private readonly byte[] _row;
        private readonly int _width, _samples;
        public TiffMap.Palette? Palette { get; }

        public Rows(string path, int width, int height, int bits, SampleFormat format)
        {
            TiffMap.Quiet();
            _tif = Tiff.Open(path, "r") ?? throw new IOException($"Cannot open {path}");
            var w = _tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var h = _tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            if (w != width || h != height)
                throw new InvalidDataException($"{Path.GetFileName(path)} is {w}x{h}, its terrain map is {width}x{height}");
            var b = _tif.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 1;
            var f = (SampleFormat)(_tif.GetField(TiffTag.SAMPLEFORMAT)?[0].ToInt() ?? 1);
            if (b != bits || f != format) throw new InvalidDataException($"{Path.GetFileName(path)} has {b}-bit {f} samples, expected {bits}-bit {format}");
            _samples = _tif.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 1;
            _width = w;
            _row = new byte[_tif.ScanlineSize()];
            if (_tif.GetField(TiffTag.COLORMAP) is { } map)
                Palette = new TiffMap.Palette(map[0].ToUShortArray(), map[1].ToUShortArray(), map[2].ToUShortArray());
        }

        public void Read<T>(int y, Span<T> line) where T : unmanaged
        {
            if (_samples != 1) throw new InvalidDataException($"{_tif.FileName()} has {_samples} samples per pixel, expected 1");
            _tif.ReadScanline(_row, y);
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(_row.AsSpan(0, line.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>())).CopyTo(line);
        }

        /// <summary>One row as 0xAABBGGRR (3-sample TIFs are opaque).</summary>
        public void ReadRgba(int y, uint[] line)
        {
            if (_samples is not (3 or 4)) throw new InvalidDataException($"{_tif.FileName()} has {_samples} samples per pixel, expected RGB(A)");
            _tif.ReadScanline(_row, y);
            for (int x = 0, i = 0; x < _width; x++, i += _samples)
                line[x] = _row[i] | (uint)_row[i + 1] << 8 | (uint)_row[i + 2] << 16 | (_samples == 4 ? (uint)_row[i + 3] << 24 : 0xFF000000u);
        }

        public void Dispose() => _tif.Dispose();
    }
}
