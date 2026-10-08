using System.Xml.Linq;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// Names the type of an entity as Terry shows it. Structural entities that entity_configuration.xml does not list get
/// built-in names; everything else is the configured type whose components match best.
/// </summary>
public static class TerryEntityTypes
{
    /// <summary>A layer stored in its own .layer file (declared in the .terry's scene).</summary>
    public const string LayerFile = "LayerFile";
    /// <summary>A layer nested inside a .layer that exports its members with meta tags.</summary>
    public const string TagLayer = "TagLayer";
    /// <summary>A plain folder layer nested inside a .layer.</summary>
    public const string Layer = "Layer";
    public const string Group = "Group";
    public const string Signature = "Signature";
    public const string Unknown = "Unknown";

    public static bool IsLayerType(string type) => type is LayerFile or TagLayer or Layer;

    public static string Classify(XElement entity, EntityConfiguration? config = null)
    {
        if (entity.Element("ECLayerFile") is not null || entity.Element("ECFileLayer") is not null) return LayerFile;
        if (entity.Element("ECLayer") is not null)
            return entity.Element("ECLayerExportTags") is not null ? TagLayer : Layer;
        if (entity.Element("ECGroup") is not null) return Group;
        var components = entity.Elements().Select(c => c.Name.LocalName).Where(n => n.StartsWith("EC", StringComparison.Ordinal)).ToList();
        if (components is ["ECSignature", ..] && components.All(c => c is "ECSignature" or "ECTransform")) return Signature;
        return (config ?? EntityConfiguration.Embedded).Classify(components)?.Type ?? Unknown;
    }
}
