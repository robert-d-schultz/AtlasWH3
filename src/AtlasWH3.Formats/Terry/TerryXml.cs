using System.Text;
using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// Terry's XML layout, shared by .terry, .terry.user and .layer files: UTF-8 declaration, two-space indent, one
/// element per line, and no escaping beyond what XML requires. Two things vary between Terry versions and are kept
/// per file: the line ending (most files LF, some CRLF) and how an empty element is closed: <c>&lt;x/&gt;</c>,
/// <c>&lt;x&gt;&lt;/x&gt;</c>, or <c>&lt;x&gt;</c> and <c>&lt;/x&gt;</c> on two lines. Documents are parsed with
/// whitespace preserved so the three forms stay distinguishable (self-closed, no nodes, whitespace-only text); the
/// writer ignores whitespace between elements. Writing an unedited document reproduces Terry's bytes.
/// </summary>
public static class TerryXml
{
    public static string Declaration(string newline = "\n") => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + newline;

    /// <summary>The line ending a Terry file uses.</summary>
    public static string NewlineOf(string text)
    {
        var i = text.IndexOf('\n');
        return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
    }

    public static string ToText(XElement root, string newline = "\n")
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append(Declaration(newline));
        Write(sb, root, 0, newline);
        return sb.ToString();
    }

    public static void Save(XElement root, string path, string newline = "\n") =>
        File.WriteAllText(path, ToText(root, newline), new UTF8Encoding(false));

    /// <summary>Parses Terry XML, keeping whitespace so empty-element forms survive (see the class remarks).</summary>
    public static XDocument Parse(string text) => XDocument.Parse(text, LoadOptions.PreserveWhitespace);

    public static XDocument Load(string path) => Parse(File.ReadAllText(path));

    public static void Write(StringBuilder sb, XElement e, int depth, string newline = "\n")
    {
        sb.Append(' ', depth * 2).Append('<').Append(e.Name.LocalName);
        foreach (var a in e.Attributes())
            sb.Append(' ').Append(a.Name.LocalName).Append("=\"").Append(Escape(a.Value)).Append('"');
        if (!e.HasElements)
        {
            var value = e.Value;
            if (e.IsEmpty) sb.Append("/>").Append(newline);
            else if (value.Length == 0) sb.Append("></").Append(e.Name.LocalName).Append('>').Append(newline);
            else if (string.IsNullOrWhiteSpace(value))
                sb.Append('>').Append(newline).Append(' ', depth * 2).Append("</").Append(e.Name.LocalName).Append('>').Append(newline);
            // Text content is rare in Terry files (config files' <default_name>); keep it on one line.
            else sb.Append('>').Append(EscapeText(value)).Append("</").Append(e.Name.LocalName).Append('>').Append(newline);
            return;
        }
        sb.Append('>').Append(newline);
        foreach (var child in e.Elements()) Write(sb, child, depth + 1, newline);
        sb.Append(' ', depth * 2).Append("</").Append(e.Name.LocalName).Append('>').Append(newline);
    }

    public static string Escape(string s) =>
        s.IndexOfAny(['&', '<', '>', '"']) < 0 ? s
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscapeText(string s) =>
        s.IndexOfAny(['&', '<', '>']) < 0 ? s : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
