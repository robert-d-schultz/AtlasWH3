using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// A project's per-user state (&lt;project&gt;.terry.user): hidden ("invisible") and locked ("frozen") entities, and
/// the active layer. Everything else (settings, cameras, filters) is preserved. A missing file is created with just
/// the &lt;scene&gt; block when saved.
///
/// The id lists are stored in one attribute; none of the kit's files has a non-empty list, so the separator is not
/// confirmed. Reading accepts commas, semicolons and spaces; writing uses commas.
/// </summary>
public sealed class TerryUserFile
{
    private readonly XDocument _doc;
    private readonly string _newline;

    public string Path { get; }

    private TerryUserFile(string path, XDocument doc, string newline)
    {
        Path = path;
        _doc = doc;
        _newline = newline;
    }

    public static string PathFor(string terryPath) => terryPath + ".user";

    public static TerryUserFile Load(string terryPath)
    {
        var path = PathFor(terryPath);
        if (!File.Exists(path))
            return new TerryUserFile(path, new XDocument(new XElement("project", new XAttribute("version", "14"))), "\n");
        var text = File.ReadAllText(path);
        return new TerryUserFile(path, TerryXml.Parse(text), TerryXml.NewlineOf(text));
    }

    private XElement Scene
    {
        get
        {
            var root = _doc.Root!;
            var scene = root.Element("scene");
            if (scene is null)
            {
                scene = new XElement("scene", new XAttribute("active_layer", ""),
                    new XElement("invisible_entities", new XAttribute("value", "")),
                    new XElement("frozen_entities", new XAttribute("value", "")));
                // Terry writes <scene> after <settings>.
                if (root.Element("settings") is { } settings) settings.AddAfterSelf(scene);
                else root.Add(scene);
            }
            return scene;
        }
    }

    public string? ActiveLayer
    {
        get => (string?)_doc.Root!.Element("scene")?.Attribute("active_layer") is { Length: > 0 } a ? a : null;
        set => Scene.SetAttributeValue("active_layer", value ?? "");
    }

    public IReadOnlySet<string> Invisible => Ids("invisible_entities");
    public IReadOnlySet<string> Frozen => Ids("frozen_entities");

    public void SetVisible(string id, bool visible) => Toggle("invisible_entities", id, add: !visible);
    public void SetFrozen(string id, bool frozen) => Toggle("frozen_entities", id, add: frozen);

    private HashSet<string> Ids(string element) =>
        ((string?)_doc.Root!.Element("scene")?.Element(element)?.Attribute("value") ?? "")
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    private void Toggle(string element, string id, bool add)
    {
        var current = ((string?)Scene.Element(element)?.Attribute("value") ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (add == current.Contains(id)) return;
        if (add) current.Add(id);
        else current.Remove(id);
        var e = Scene.Element(element);
        if (e is null) Scene.Add(e = new XElement(element));
        e.SetAttributeValue("value", string.Join(",", current));
    }

    public string ToText() => TerryXml.ToText(_doc.Root!, _newline);

    public void Save(string? path = null) => TerryXml.Save(_doc.Root!, path ?? Path, _newline);
}
