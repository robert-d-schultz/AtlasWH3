namespace AtlasWH3.Formats.Models;

/// <summary>models\river_N.wsmodel: the XML wrapper pointing at the river mesh and its water material (CRLF in CA's
/// shipped vanilla files, LF from BOB; no final newline).</summary>
public static class WsModel
{
    public const string RiverMaterial = "materials/campaign_navigable_yellow_river_plane_default.xml.material";

    public static string River(string mapName, int index, string material = RiverMaterial, string newline = "\r\n") =>
        "<model version=\"1\">" + newline +
        $"  <geometry>terrain/campaigns/{mapName}/models/river_{index}.wsmodel.rigid_model_v2</geometry>" + newline +
        "  <materials>" + newline +
        $"    <material lod_index=\"0\" part_index=\"0\">{material}</material>" + newline +
        "  </materials>" + newline +
        "</model>";
}
