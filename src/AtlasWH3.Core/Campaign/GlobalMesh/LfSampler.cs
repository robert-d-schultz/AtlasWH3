using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.GlobalMesh;

/// <summary>
/// BOB's low-frequency terrain height at a world point (WARSCAPE::TERRAIN_RENDER_SETUP::get_height_worker with the
/// bilinear COMPRESSED_HEIGHT_MAP sampler), all in float32:
///   u = (x - minX) / (maxX - minX), v = 1 - (z - minZ) / (maxZ - minZ); fx = u·W, fy = v·H
///   top row = int((v - 1/H)·H), right column = int((u + 1/W)·W); lerp columns by frac(fx), rows by frac(fy)
///   value = u16 · (1/65535), mapped through the compressed map's lo/hi header
///   height = (l · 5500) · f - f · 1200, f = (1/128) · tile size
/// Reproduces vanilla land-mesh vertex heights closely (median within ~20 ulps; not yet bit-exact).
/// </summary>
public sealed class LfSampler
{
    private readonly ushort[] _data;
    private readonly int _w, _h;
    private readonly float _maxX, _maxZ, _rx, _ry, _lo, _range, _f;
    private const float Inv65535 = 1f / 65535f;

    public LfSampler(CompressedMap.Map map, float worldWidth, float worldHeight, float tileSize)
    {
        _data = map.Raster.Data;
        _w = map.Raster.Width;
        _h = map.Raster.Height;
        _maxX = worldWidth;
        _maxZ = worldHeight;
        _rx = 1f / _w;
        _ry = 1f / _h;
        _lo = map.Header[1];
        _range = map.Header[4] - map.Header[1];
        _f = 1f / 128f * tileSize;
    }

    private float Value(float col, float row)
    {
        var c = (int)Math.Clamp(col, 0f, _w - 1);
        var r = (int)Math.Clamp(row, 0f, _h - 1);
        var v = _data[r * _w + c] * Inv65535;
        return _lo == 0f && _range == 1f ? v : _lo + v * _range;
    }

    public float Height(float x, float z)
    {
        var u = x / _maxX;
        var v = 1f - z / _maxZ;
        var fx = u * _w;
        var fy = v * _h;
        var x0 = MathF.Floor(fx);
        var y0 = MathF.Floor(fy);
        var fyu = (v - _ry) * _h;
        var fxr = (u + _rx) * _w;
        var a = Value(fx, fyu);
        var b = Value(fxr, fyu);
        var top = (b - a) * (fx - x0) + a;
        var c = Value(fx, fy);
        var d = Value(fxr, fy);
        var bot = (d - c) * (fx - x0) + c;
        var l = (bot - top) * (fy - y0) + top;
        return l * 5500f * _f - _f * 1200f;
    }
}
