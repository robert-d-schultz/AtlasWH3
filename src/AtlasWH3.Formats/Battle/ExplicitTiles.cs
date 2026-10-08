using System.Globalization;
using System.Text;

namespace AtlasWH3.Formats.Battle;

/// <summary>
/// One line of explicit_tiles.txt: a fixed tile BOB places before scanning the tile map.
/// (X, Y) is the top-left of the tile's footprint in image coordinates (row 0 = north); Rotation is 0/90/180/270.
/// </summary>
public sealed record ExplicitTile(int X, int Y, string Location, int Rotation)
{
    /// <summary>Footprint size in cells after rotation.</summary>
    public (int W, int H) Size(BattleTile tile) => Rotation is 90 or 270 ? (tile.Height, tile.Width) : (tile.Width, tile.Height);
}

/// <summary>
/// explicit_tiles.txt reader/writer. BOB (EDITOR_TILE_MAP::load_explicit_tiles) splits on ',', '\r' and '\n' and
/// skips two bytes after the rotation, so lines must end in CRLF.
/// </summary>
public static class ExplicitTilesFile
{
    public static readonly int[] Rotations = [0, 90, 180, 270];

    public static List<ExplicitTile> Read(string path)
    {
        var result = new List<ExplicitTile>();
        var lineNo = 0;
        foreach (var raw in File.ReadAllText(path, Encoding.Latin1).Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length != 4
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rot)
                || !Rotations.Contains(rot))
                throw new InvalidDataException($"{Path.GetFileName(path)} line {lineNo}: expected x,y,location,0|90|180|270 but got '{line}'.");
            result.Add(new ExplicitTile(x, y, parts[2].Trim(), rot));
        }
        return result;
    }

    public static string Format(IEnumerable<ExplicitTile> tiles)
    {
        var sb = new StringBuilder();
        foreach (var t in tiles)
            sb.Append(CultureInfo.InvariantCulture, $"{t.X},{t.Y},{t.Location},{t.Rotation}\r\n");
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<ExplicitTile> tiles)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, Format(tiles), Encoding.Latin1);
        File.Move(temp, path, overwrite: true);
    }
}
