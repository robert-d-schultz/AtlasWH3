namespace AtlasWH3.Formats.Models;

/// <summary>models\river_&lt;entity id&gt;.wsmodel: the XML wrapper pointing at the river mesh and its water material, as
/// WH3's BOB writes it (LF, no final newline).</summary>
public static class WsModel
{
    public static string River(string mapName, string entityId, string material, string newline = "\n") =>
        "<model version=\"1\">" + newline +
        $"  <geometry>terrain/campaigns/{mapName}/models/river_{entityId}.wsmodel.rigid_model_v2</geometry>" + newline +
        "  <materials>" + newline +
        $"    <material lod_index=\"0\" part_index=\"0\">{material}</material>" + newline +
        "  </materials>" + newline +
        "</model>";
}
