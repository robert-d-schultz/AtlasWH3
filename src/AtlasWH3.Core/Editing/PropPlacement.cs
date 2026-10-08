using System.Text.Json.Nodes;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.Core.Editing;

/// <summary>
/// New props placed from the asset browser: the EntityEditor "create" op for a Terry campaign Prop (ECPropMesh,
/// ECMesh, ECMeshRenderSettings, ECPropHeightPatch, ECCampaignProperties, ECDLCMask, ECTransform, ECTerrainClamp, the
/// schema defaults as Terry writes them) with the model, position, yaw and uniform scale filled in.
/// </summary>
public static class PropPlacement
{
    /// <summary>Model files a Prop's ECMesh.model_path can point at.</summary>
    public static readonly string[] ModelExtensions = [".wsmodel", ".rigid_model_v2"];

    public static bool IsModel(string path) => ModelExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Model paths for the browser: campaign models (rigidmodels/campaign/…) unless <paramref name="all"/>; a .wsmodel
    /// hides the .rigid_model_v2 of the same name (Terry's campaign props use the .wsmodel, which carries the materials).
    /// </summary>
    public static List<string> BrowsableModels(IEnumerable<string> assets, bool all = false)
    {
        var list = assets.Where(IsModel)
            .Where(p => all || p.StartsWith("rigidmodels/campaign/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ws = list.Where(p => p.EndsWith(".wsmodel", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[..^".wsmodel".Length]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Where(p => !p.EndsWith(".rigid_model_v2", StringComparison.OrdinalIgnoreCase) || !ws.Contains(p[..^".rigid_model_v2".Length]))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Path as Terry writes it in ECMesh.model_path ("RigidModels/campaign/…", forward slashes).</summary>
    public static string TerryModelPath(string asset)
    {
        var p = asset.Replace('\\', '/').TrimStart('/');
        return p.StartsWith("rigidmodels/", StringComparison.OrdinalIgnoreCase) ? "RigidModels/" + p["rigidmodels/".Length..] : p;
    }

    /// <summary>The create op for one prop. <paramref name="yawDegrees"/> turns it about y; <paramref name="scale"/> is
    /// uniform. <paramref name="parent"/> is a folder / tag layer inside the file layer.</summary>
    public static JsonObject CreateOp(string model, string layer, double x, double y, double z, double yawDegrees = 0, double scale = 1,
                                      string? parent = null)
    {
        var s = LayerWriter.F(scale);
        var op = new JsonObject
        {
            ["op"] = "create", ["type"] = "Prop", ["layer"] = layer,
            ["position"] = new JsonArray(x, y, z),
            ["fields"] = new JsonObject
            {
                ["ECMesh.model_path"] = TerryModelPath(model),
                ["ECTransform.rotation"] = $"0 {LayerWriter.F(NormaliseYaw(yawDegrees))} 0",
                ["ECTransform.scale"] = $"{s} {s} {s}",
            },
        };
        if (parent is not null) op["parent"] = parent;
        return op;
    }

    /// <summary>Yaw in (−180, 180].</summary>
    public static double NormaliseYaw(double degrees)
    {
        var d = degrees % 360;
        if (d <= -180) d += 360;
        if (d > 180) d -= 360;
        return d;
    }
}
