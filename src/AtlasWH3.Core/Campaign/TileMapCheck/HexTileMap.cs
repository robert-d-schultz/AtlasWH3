using AtlasWH3.Formats.Maps;

namespace AtlasWH3.Core.Campaign.TileMapCheck;

/// <summary>
/// A campaign tile map (tile_map.png) as BOB sees it, plus its hex view. The image is the hex grid at 2×2 px per hex,
/// <c>2W × (2H+1)</c>, odd columns 1 px higher: hex (col, row), row 0 = south, covers x = 2·col + {0,1},
/// y = 2H − (2·row + (col &amp; 1) + {0,1}) (image rows, top = 0). The pixels no hex covers (top row of even columns,
/// bottom row of odd columns) are filler. Colours are packed 0xRRGGBB.
/// </summary>
public sealed class HexTileMap
{
    /// <summary>Flat-top hex neighbours, [column parity][direction] → (dcol, drow); directions 0 up, 1 up-right,
    /// 2 down-right, 3 down, 4 down-left, 5 up-left (research/guandu/hexgrid.py).</summary>
    private static readonly (int Dc, int Dr)[][] Dirs =
    [
        [(0, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0)],
        [(0, 1), (1, 1), (1, 0), (0, -1), (-1, 0), (-1, 1)],
    ];

    public int PixelWidth { get; }
    public int PixelHeight { get; }
    /// <summary>Pixels, row-major from the top image row, 0xRRGGBB.</summary>
    public uint[] Pixels { get; }
    /// <summary>Hex grid size; 0 when the image is not 2W × (2H+1).</summary>
    public int Width { get; }
    public int Height { get; }
    public bool HasHexLayout => Width > 0 && Height > 0;

    public HexTileMap(int pixelWidth, int pixelHeight, uint[] pixels)
    {
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Pixels = pixels;
        if (pixelWidth > 0 && pixelWidth % 2 == 0 && pixelHeight % 2 == 1)
        {
            Width = pixelWidth / 2;
            Height = (pixelHeight - 1) / 2;
        }
    }

    /// <summary>Reads a PNG (PngMap gives 0xAABBGGRR) into 0xRRGGBB pixels.</summary>
    public static HexTileMap Read(string path)
    {
        var raster = PngMap.Read(path);
        var px = new uint[raster.Data.Length];
        for (var i = 0; i < px.Length; i++)
        {
            var v = raster.Data[i];
            px[i] = (v & 0xff) << 16 | (v & 0xff00) | (v >> 16 & 0xff);
        }
        return new HexTileMap(raster.Width, raster.Height, px);
    }

    /// <summary>An empty (black) tile map for a W × H hex grid.</summary>
    public static HexTileMap Create(int width, int height) =>
        new(2 * width, 2 * height + 1, new uint[2 * width * (2 * height + 1)]);

    /// <summary>A tile map painted from hex colours indexed [row * width + col]; filler pixels stay black.</summary>
    public static HexTileMap FromHexColours(int width, int height, uint[] colours)
    {
        var map = Create(width, height);
        for (var r = 0; r < height; r++)
            for (var c = 0; c < width; c++)
                map.SetHex(c, r, colours[r * width + c]);
        return map;
    }

    /// <summary>Paints all 4 pixels of a hex (0xRRGGBB).</summary>
    public void SetHex(int col, int row, uint rgb)
    {
        for (var d = 0; d < 4; d++)
        {
            var (x, y) = HexPixel(col, row, d & 1, d >> 1);
            Pixels[y * PixelWidth + x] = rgb & 0xffffff;
        }
    }

    /// <summary>Writes an opaque RGB PNG (what BOB and CAIME read).</summary>
    public void Write(string path)
    {
        var raster = new Raster<uint>(PixelWidth, PixelHeight);
        for (var i = 0; i < Pixels.Length; i++)
        {
            var v = Pixels[i];
            raster.Data[i] = 0xff000000 | (v & 0xff) << 16 | (v & 0xff00) | (v >> 16 & 0xff);
        }
        PngMap.Write(path, raster);
    }

    public uint Pixel(int x, int y) => Pixels[y * PixelWidth + x];

    /// <summary>Image pixel (x, y) of corner (dx, dy) ∈ {0,1}² of hex (col, row).</summary>
    public (int X, int Y) HexPixel(int col, int row, int dx = 0, int dy = 0) =>
        (2 * col + dx, 2 * Height - (2 * row + (col & 1) + dy));

    public int Index(int col, int row) => row * Width + col;

    /// <summary>Colour of each hex (its first pixel), indexed [row * Width + col].</summary>
    public uint[] HexColours()
    {
        var result = new uint[Width * Height];
        for (var r = 0; r < Height; r++)
            for (var c = 0; c < Width; c++)
            {
                var (x, y) = HexPixel(c, r);
                result[Index(c, r)] = Pixel(x, y);
            }
        return result;
    }

    /// <summary>Whether the 4 pixels of a hex agree.</summary>
    public bool HexUniform(int col, int row)
    {
        var (x, y) = HexPixel(col, row);
        var v = Pixel(x, y);
        return Pixel(x + 1, y) == v && Pixel(x, y - 1) == v && Pixel(x + 1, y - 1) == v;
    }

    /// <summary>Whether image pixel (x, y) lies in no hex.</summary>
    public bool IsFiller(int x, int y) => (x / 2 & 1) == 0 ? y == 0 : y == PixelHeight - 1;

    public bool Neighbour(int col, int row, int dir, out int ncol, out int nrow)
    {
        var (dc, dr) = Dirs[col & 1][dir];
        ncol = col + dc;
        nrow = row + dr;
        return ncol >= 0 && ncol < Width && nrow >= 0 && nrow < Height;
    }
}
