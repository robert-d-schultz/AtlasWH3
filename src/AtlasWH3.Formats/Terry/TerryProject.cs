using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>A layer declared in a project's scene. File layers live in their own &lt;project&gt;.&lt;id&gt;.layer.</summary>
public sealed record ProjectLayer(string Id, string Name, bool IsFile, bool Export, string? FilePath);

/// <summary>
/// Assembly-kit Terry project (.terry). The QTU::Terrain maps and the QTU::Scene layer list are interpreted; everything
/// else is preserved. Each terrain map's base layer is stored as "&lt;map&gt;.&lt;kind&gt;.&lt;layerId&gt;.tif".
/// Saved in Terry's own layout (<see cref="TerryXml"/>), so an unedited project writes back byte-identical.
/// </summary>
public sealed class TerryProject
{
    public sealed class TerrainMap
    {
        internal XElement Data { get; init; } = null!;
        public string Type => (string?)Data.Attribute("type") ?? "";
        public string BaseLayerId { get; init; } = "";

        public (int Width, int Height) Size
        {
            get
            {
                var parts = ((string?)Data.Attribute("size") ?? "0x0").Split('x');
                return (int.Parse(parts[0]), int.Parse(parts[1]));
            }
            set => Data.SetAttributeValue("size", $"{value.Width}x{value.Height}");
        }
    }

    /// <summary>File kind used in the TIF name for each Terry map type.</summary>
    public static readonly IReadOnlyDictionary<string, string> KindByType = new Dictionary<string, string>
    {
        ["BlendCampaign"] = "blend",
        ["CampaignTree"] = "tree",
        ["LowFrequencyHeight"] = "height",
        ["LowFrequencyHeightSea"] = "sea_height",
    };

    private readonly XDocument _doc;
    private readonly string _newline;

    public string Path { get; }
    public string MapName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;
    public IReadOnlyList<TerrainMap> Maps { get; }

    private TerryProject(string path, XDocument doc, string newline)
    {
        Path = path;
        _doc = doc;
        _newline = newline;
        Maps = doc.Descendants("pc")
            .Where(pc => (string?)pc.Attribute("type") == "QTU::TerrainMap")
            .Select(pc => new TerrainMap
            {
                Data = pc.Element("data")!,
                BaseLayerId = pc.Elements("pc")
                    .Where(l => (string?)l.Attribute("type") == "QTU::TerrainMapLayer")
                    .Select(l => (string?)l.Element("data")?.Attribute("id"))
                    .FirstOrDefault(id => id != null) ?? "",
            })
            .ToList();
    }

    public static TerryProject Load(string path)
    {
        var text = File.ReadAllText(path);
        return new TerryProject(path, TerryXml.Parse(text), TerryXml.NewlineOf(text));
    }

    public TerrainMap? Find(string type) => Maps.FirstOrDefault(m => m.Type == type);

    // ---------------------------------------------------------------- project kind

    private XElement? KindElement => _doc.Root!.Elements("pc").FirstOrDefault(pc =>
        ((string?)pc.Attribute("type"))?.Contains("Project", StringComparison.Ordinal) == true
        || (string?)pc.Attribute("type") == "QTU::TerryPrefab");

    /// <summary>Terry's project type as used by entity_configuration.xml's allow_if: tile_map, prefab or tile.</summary>
    public string ProjectType => (string?)KindElement?.Attribute("type") switch
    {
        "QTU::ProjectTileMap" => "tile_map",
        "QTU::ProjectPrefab" or "QTU::TerryPrefab" => "prefab",
        { } t when t.StartsWith("QTU::ProjectTile", StringComparison.Ordinal) => "tile",
        { } t => t[(t.LastIndexOf(':') + 1)..].Replace("Project", "").ToLowerInvariant(),
        null => "unknown",
    };

    /// <summary>The tile database the project belongs to: "campaign" or "battle".</summary>
    public string Database
    {
        get
        {
            var data = KindElement?.Element("data");
            if ((string?)data?.Attribute("database") is { Length: > 0 } db) return db;
            var setup = (string?)data?.Attribute("terrain_setup") ?? Path;
            return setup.Replace('\\', '/').Contains("campaigns/", StringComparison.OrdinalIgnoreCase) ? "campaign" : "battle";
        }
    }

    // ---------------------------------------------------------------- scene layers

    private XElement SceneData =>
        _doc.Root!.Elements("pc").FirstOrDefault(pc => (string?)pc.Attribute("type") == "QTU::Scene")?.Element("data")
        ?? throw new InvalidDataException($"{Path} has no QTU::Scene");

    /// <summary>Scene version, used as the version of new .layer files.</summary>
    public int SceneVersion => int.TryParse((string?)SceneData.Attribute("version"), out var v) ? v : 35;

    /// <summary>The top-level layers of the scene, in the order Terry lists them.</summary>
    public List<ProjectLayer> Layers() =>
        SceneData.Elements("entity").Select(ToLayer).ToList();

    private ProjectLayer ToLayer(XElement e)
    {
        var id = (string)e.Attribute("id")!;
        var isFile = e.Element("ECLayerFile") is not null || e.Element("ECFileLayer") is not null;
        var exportAttr = (string?)e.Element("ECLayerExport")?.Attribute("export") ?? (string?)e.Element("ECFileLayer")?.Attribute("export");
        return new ProjectLayer(id, (string?)e.Attribute("name") ?? id, isFile, exportAttr != "false",
            isFile ? LayerFilePath(id) : null);
    }

    public ProjectLayer? FindLayer(string nameOrId) =>
        Layers().FirstOrDefault(l => l.Id == nameOrId)
        ?? Layers().FirstOrDefault(l => l.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Layer name to id for every scene entity stored as its own file (&lt;map&gt;.&lt;id&gt;.layer).</summary>
    public IReadOnlyDictionary<string, string> LayerFiles() =>
        _doc.Descendants("entity")
            .Where(e => e.Element("ECLayerFile") != null && e.Attribute("name") != null && e.Attribute("id") != null)
            .GroupBy(e => (string)e.Attribute("name")!)
            .ToDictionary(g => g.Key, g => (string)g.First().Attribute("id")!);

    /// <summary>Full path of the .layer file for a layer id.</summary>
    public string LayerFilePath(string id) => System.IO.Path.Combine(Directory, $"{MapName}.{id}.layer");

    /// <summary>
    /// Declares a new file layer in the scene (the caller writes the .layer, e.g. <see cref="LayerDocument.Empty"/>).
    /// Laid out like the project's existing layer entities. Returns the new layer.
    /// </summary>
    public ProjectLayer AddFileLayer(string name, bool export = true)
    {
        if (Layers().Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"a layer named '{name}' already exists");
        var id = NewId(name);
        var e = new XElement("entity", new XAttribute("id", id), new XAttribute("name", name));
        if (SceneData.Elements("entity").Any(x => x.Element("ECFileLayer") is not null) && !SceneData.Elements("entity").Any(x => x.Element("ECLayerFile") is not null))
            e.Add(new XElement("ECFileLayer", new XAttribute("export", export ? "true" : "false"), new XAttribute("bmd_export_type", "")));
        else
            e.Add(new XElement("ECLayerFile"), new XElement("ECLayer"),
                new XElement("ECLayerExport", new XAttribute("export", export ? "true" : "false"),
                    new XAttribute("export_as_separate_file_if_not_meta_tagged", "false"),
                    new XAttribute("buildings_have_linked_destruction", "false")));
        SceneData.Add(e);
        return ToLayer(e);
    }

    public void RenameLayer(string id, string name)
    {
        if (Layers().Any(l => l.Id != id && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"a layer named '{name}' already exists");
        RequireLayer(id).SetAttributeValue("name", name);
    }

    /// <summary>Sets whether the layer is exported by the build (ECLayerExport/ECFileLayer export).</summary>
    public void SetLayerExport(string id, bool export)
    {
        var e = RequireLayer(id);
        var c = e.Element("ECLayerExport") ?? e.Element("ECFileLayer") ?? throw new InvalidOperationException($"layer {id} has no export flag");
        c.SetAttributeValue("export", export ? "true" : "false");
    }

    /// <summary>Removes the layer from the scene (its .layer file is left for the caller to delete or keep).</summary>
    public void RemoveLayer(string id) => RequireLayer(id).Remove();

    private XElement RequireLayer(string id) =>
        SceneData.Elements("entity").FirstOrDefault(e => (string?)e.Attribute("id") == id)
        ?? throw new KeyNotFoundException($"no layer {id} in {System.IO.Path.GetFileName(Path)}");

    private string NewId(string seed)
    {
        var used = _doc.Descendants().Select(e => (string?)e.Attribute("id")).Where(i => i is not null).ToHashSet();
        for (var n = 0; ; n++)
        {
            var hash = 14695981039346656037UL;
            foreach (var ch in $"{MapName}/layer/{seed}/{DateTime.UtcNow.Ticks}/{n}")
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }
            var id = "1" + (hash & 0x00FF_FFFF_FFFF_FFFFUL).ToString("x14");
            if (!used.Contains(id) && !File.Exists(LayerFilePath(id))) return id;
        }
    }

    // ---------------------------------------------------------------- files

    /// <summary>Full path of the TIF holding a terrain map's base layer.</summary>
    public string LayerTifPath(TerrainMap map) =>
        System.IO.Path.Combine(Directory,
            $"{MapName}.{KindByType.GetValueOrDefault(map.Type, map.Type.ToLowerInvariant())}.{map.BaseLayerId}.tif");

    public string ToText() => TerryXml.ToText(_doc.Root!, _newline);

    public void Save(string? path = null) => TerryXml.Save(_doc.Root!, path ?? Path, _newline);
}
