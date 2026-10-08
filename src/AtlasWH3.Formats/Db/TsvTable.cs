namespace AtlasWH3.Formats.Db;

/// <summary>
/// RPFM-exported DB table TSV: line 1 = column names, line 2 = "#table_name;version;path" metadata,
/// then one row per line. Fields are tab separated.
/// </summary>
public sealed class TsvTable
{
    public IReadOnlyList<string> Columns { get; }
    public string? Metadata { get; }
    public IReadOnlyList<string[]> Rows { get; }

    private readonly Dictionary<string, int> _columnIndex;

    private TsvTable(IReadOnlyList<string> columns, string? metadata, IReadOnlyList<string[]> rows)
    {
        Columns = columns;
        Metadata = metadata;
        Rows = rows;
        _columnIndex = columns.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => t.i, StringComparer.OrdinalIgnoreCase);
    }

    public static TsvTable Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var columns = lines[0].Split('\t');
        string? metadata = null;
        var rows = new List<string[]>();
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith('#')) { metadata ??= line; continue; }
            if (line.Length == 0) continue;
            rows.Add(line.Split('\t'));
        }
        return new TsvTable(columns, metadata, rows);
    }

    public int IndexOf(string column) =>
        _columnIndex.TryGetValue(column, out var i) ? i : throw new KeyNotFoundException($"Column '{column}' not found.");

    public string Get(string[] row, string column)
    {
        var i = IndexOf(column);
        return i < row.Length ? row[i] : "";
    }
}
