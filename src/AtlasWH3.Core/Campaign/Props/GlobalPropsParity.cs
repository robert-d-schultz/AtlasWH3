using System.Collections;
using System.Reflection;
using AtlasWH3.Formats.Props;

namespace AtlasWH3.Core.Campaign.Props;

/// <summary>
/// Content parity of two WH3 global_props(_sound).bin files: the measure two BOB runs of the same input agree on. Two
/// Old World runs (2026-10-09) differ in record order inside 20,912 bodies, in entry order and in the numbering of the
/// one-prop buckets 640 + n (address-ordered in BOB), so files are compared as: every bucket body's records as a multiset,
/// the one-prop buckets as one multiset per (region, cell), every cell body's references as a multiset, and the root's.
/// </summary>
public static class GlobalPropsParity
{
    public sealed record Result(int Same, int Differ, IReadOnlyList<string> Examples)
    {
        public double Share => Same + Differ == 0 ? 1 : (double)Same / (Same + Differ);
    }

    public static Result Compare(IReadOnlyDictionary<string, byte[]> native, IReadOnlyDictionary<string, byte[]> bob, int examples = 5)
    {
        var n = Group(native);
        var b = Group(bob);
        int same = 0, differ = 0;
        var ex = new List<string>();
        foreach (var key in n.Keys.Union(b.Keys))
        {
            var nl = n.GetValueOrDefault(key) ?? [];
            var bl = b.GetValueOrDefault(key) ?? [];
            if (nl.SequenceEqual(bl)) { same++; continue; }
            differ++;
            if (ex.Count < examples)
            {
                var only = nl.Except(bl).FirstOrDefault() ?? "";
                ex.Add($"{key}: native {nl.Count} records, BOB {bl.Count}; first only-native: {only[..Math.Min(160, only.Length)]}");
            }
        }
        return new Result(same, differ, ex);
    }

    /// <summary>(region, cell, bucket) of an entry name; bucket -1 for a cell body, cell -1 for the root.</summary>
    public static (string Region, int Cell, int Bucket) Parts(string name)
    {
        var p = name[(name.LastIndexOf('/') + 1)..].Split('.');
        return p.Length switch
        {
            5 => (p[1], int.Parse(p[2]), int.Parse(p[3])),
            4 => (p[1], int.Parse(p[2]), -1),
            _ => ("", -1, -1),
        };
    }

    private static string Key(string name)
    {
        var (region, cell, bucket) = Parts(name);
        return bucket >= 640 ? $"{region}.{cell}.one" : name;
    }

    private static Dictionary<string, List<string>> Group(IReadOnlyDictionary<string, byte[]> entries)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, data) in entries)
        {
            var body = Bmd27Body.Parse(data);
            var key = Key(name);
            if (!d.TryGetValue(key, out var list)) d[key] = list = [];
            if (Parts(name).Bucket < 0)
                list.AddRange(body.Nested.Select(x => $"{Key(x.Name)}|{x.CultureMask}|{x.Region}"));
            else
                list.AddRange(Records(body).Select(r => RecordText(body, r)));
        }
        foreach (var l in d.Values) l.Sort(StringComparer.Ordinal);
        return d;
    }

    /// <summary>Every record of a body, in section order.</summary>
    public static IEnumerable<object> Records(Bmd27Body b) =>
        b.Props.Cast<object>().Concat(b.Vfx).Concat(b.Lights).Concat(b.Scenes).Concat(b.Probes).Concat(b.Holes).Concat(b.Polys)
            .Concat(b.Spots).Concat(b.Sounds);

    /// <summary>A record's fields as text, with a prop's path in place of its path index.</summary>
    public static string RecordText(Bmd27Body body, object record)
    {
        var parts = new List<string> { record.GetType().Name };
        foreach (var f in record.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            parts.Add(f.Name == nameof(Bmd27Prop.PathIndex) ? body.PropPaths[(int)(uint)f.GetValue(record)!] : Text(f.GetValue(record)));
        return string.Join("|", parts);
    }

    /// <summary>A field value as text; floats as their bits and value.</summary>
    public static string Text(object? v) => v switch
    {
        null => "null",
        float f => BitConverter.SingleToUInt32Bits(f).ToString("x8") + $"({f:R})",
        string s => s,
        byte[] bytes => Convert.ToHexString(bytes),
        IEnumerable e => "[" + string.Join(",", e.Cast<object>().Select(Text)) + "]",
        _ => v.ToString() ?? "",
    };

    public static Dictionary<string, byte[]> Load(string file)
    {
        var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, body) in GlobalProps.Load(file).Bodies()) d[name] = body;
        return d;
    }
}
