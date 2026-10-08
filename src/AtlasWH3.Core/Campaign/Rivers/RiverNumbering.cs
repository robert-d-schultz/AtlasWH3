using System.Globalization;
using System.Xml.Linq;

namespace AtlasWH3.Core.Campaign.Rivers;

/// <summary>
/// BOB's river_N numbering (models\river_N.*, height patches and the global_props river records). BOB numbers a river by
/// how many river models it has already written, walking the river entities grouped by region: regions by their largest
/// river entity id, descending; inside a region, ascending entity id. Checked on main190 (all 24 rivers, two BOB runs,
/// 2026-10-05; research/bob_re/frida_props_finish.js). CA's shipped vanilla files use the entity names instead
/// (river_N = entity "river_N"), so a build that must match vanilla keeps <see cref="ByName"/>.
/// </summary>
public static class RiverNumbering
{
    public sealed record RiverEntity(string Name, ulong Id, double X, double Z);

    /// <summary>Every entity with an ECRiver or ECRiverSpline component in the layers, with its id and position.</summary>
    public static List<RiverEntity> Read(IEnumerable<string> layerPaths)
    {
        var result = new List<RiverEntity>();
        var seen = new HashSet<ulong>();
        foreach (var path in layerPaths)
        {
            if (!File.Exists(path)) continue;
            XDocument doc;
            try { doc = XDocument.Load(path); } catch (Exception) { continue; }
            foreach (var e in doc.Descendants("entity"))
            {
                if (e.Element("ECRiver") is null && e.Element("ECRiverSpline") is null) continue;
                if (!ulong.TryParse((string?)e.Attribute("id"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) continue;
                if (!seen.Add(id)) continue;
                var p = ((string?)e.Element("ECTransform")?.Attribute("position") ?? "0 0 0")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                result.Add(new RiverEntity((string?)e.Attribute("name") ?? "", id, p.Length > 0 ? p[0] : 0, p.Length > 2 ? p[2] : 0));
            }
        }
        return result;
    }

    /// <summary>Entity name -> BOB's river number. <paramref name="regionAt"/> gives the region at an entity's position
    /// (map.hex, as the global_props step); a null region counts as one shared "no region" group.</summary>
    public static Dictionary<string, int> Bob(IEnumerable<RiverEntity> rivers, Func<double, double, string?> regionAt)
    {
        var list = rivers.ToList();
        var byRegion = list.GroupBy(r => regionAt(r.X, r.Z) ?? "", StringComparer.Ordinal);
        var order = byRegion.OrderByDescending(g => g.Max(r => r.Id)).SelectMany(g => g.OrderBy(r => r.Id));
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var n = 0;
        foreach (var r in order) numbers[r.Name] = n++;
        return numbers;
    }

    /// <summary>Entity name -> the number in its name ("river_N"), as CA's shipped vanilla files.</summary>
    public static int ByName(string name, int fallback) =>
        int.TryParse(name.Split('_').Last(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
}
