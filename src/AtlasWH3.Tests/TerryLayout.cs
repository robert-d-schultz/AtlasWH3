using System.Xml.Linq;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Tests;

/// <summary>Round-trip checks for Terry XML. Terry's own files (its declaration, two-space indent, <c>&lt;x/&gt;</c>, no
/// comments) must come back byte for byte. Files other tools wrote (IEE's decompiler-written layers: tabs and a province
/// comment; some CA prefabs: <c>encoding="utf-8"</c>, <c>&lt;x /&gt;</c>, no declaration) come back in Terry's layout
/// with the same elements and attributes, and without their comments.</summary>
internal static class TerryLayout
{
    public static bool IsTerrys(string text) =>
        text.StartsWith(TerryXml.Declaration(TerryXml.NewlineOf(text)), StringComparison.Ordinal)
        && !text.Contains("<!--", StringComparison.Ordinal) && !text.Contains("\n\t", StringComparison.Ordinal)
        && !text.Contains("\" />", StringComparison.Ordinal);

    /// <summary>The document's elements and attributes, without comments or whitespace.</summary>
    public static XElement Content(string text)
    {
        var root = XDocument.Parse(text).Root!;
        root.DescendantNodes().OfType<XComment>().ToList().ForEach(c => c.Remove());
        return root;
    }

    /// <summary>Null when <paramref name="back"/> is a faithful rewrite of <paramref name="text"/>, else why not.</summary>
    public static string? Check(string text, string back) =>
        IsTerrys(text) ? (back == text ? null : "not byte-identical " + FirstDifference(text, back))
        : XNode.DeepEquals(Content(text), Content(back)) ? null : "content changed";

    private static string FirstDifference(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Around(string s) => s.Substring(Math.Max(0, i - 60), Math.Min(s.Length, i + 60) - Math.Max(0, i - 60)).ReplaceLineEndings("⏎");
        return $"at {i}: file «{Around(a)}» written «{Around(b)}»";
    }
}
