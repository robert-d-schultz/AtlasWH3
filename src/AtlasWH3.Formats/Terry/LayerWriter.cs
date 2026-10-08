using System.Globalization;
using System.Security;
using System.Text;
using AtlasWH3.Formats.Props;

namespace AtlasWH3.Formats.Terry;

/// <summary>
/// Writes one region of global_props.bin as a Terry .layer, in the component layout Terry itself saves campaign
/// layers with. Tagged objects are put under one nested Layer entity per distinct tag set (ECLayerExportTags, linked
/// through the Logical association), the way CA's own assembly kit layers do it, so BOB exports them with their
/// building_level / settlement_level / night tags again.
/// </summary>
public static class LayerWriter
{
    public static string Write(RegionObjects o)
    {
        var ids = new IdSource(o.Region);
        var tagged = new SortedDictionary<string, List<string>>(Comparer<string>.Create(MetaTags.NaturalCompare));
        var sb = new StringBuilder(o.Count * 900);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<layer version=\"35\">\n  <entities>\n");

        string Open(string tags)
        {
            var id = ids.Next();
            if (tags.Length > 0)
            {
                if (!tagged.TryGetValue(tags, out var list)) tagged[tags] = list = new List<string>();
                list.Add(id);
            }
            sb.Append("    <entity id=\"").Append(id).Append("\">\n");
            return id;
        }
        void Close() => sb.Append("    </entity>\n");
        void Line(string xml) => sb.Append("      ").Append(xml).Append('\n');

        foreach (var p in o.Props)
        {
            Open(p.Tags);
            foreach (var line in PropLines(p)) Line(line);
            Close();
        }

        foreach (var v in o.Vfx)
        {
            Open(v.Tags);
            Line($"<ECVFX vfx=\"{X(v.Name)}\" autoplay=\"true\" scale=\"1\" instance_name=\"\"/>");
            Line(DefaultCampaignProperties(v.Seasons));
            Line(DlcMask);
            Line(Transform(v.Transform));
            Line(TerrainClamp);
            Close();
        }

        foreach (var l in o.LightProbes)
        {
            Open("");
            Line("<ECLightProbe primary=\"false\"/>");
            Line(Transform(l.Transform));
            Line($"<ECSphere radius=\"{F(l.Radius)}\"/>");
            Close();
        }

        foreach (var l in o.PointLights)
        {
            Open(l.Tags);
            Line($"<ECPointLight colour=\"{(int)(l.R * 255.0)} {(int)(l.G * 255.0)} {(int)(l.B * 255.0)} 255\" colour_scale=\"{F(l.ColourScale)}\" radius=\"{F(l.Radius)}\" animation_type=\"{X(l.AnimationType)}\" animation_speed_scale=\"{F(l.AnimationScale1)} {F(l.AnimationScale2)}\" colour_min=\"{F(l.ColourMin)}\" random_offset=\"{F(l.RandomOffset)}\" falloff_type=\"{X(l.FalloffType)}\" for_light_probes_only=\"{B(l.LightProbesOnly)}\"/>");
            Line(DefaultCampaignProperties(l.Seasons));
            Line(Transform(l.Transform));
            Close();
        }

        foreach (var m in o.PolyMeshes)
        {
            Open("");
            Line($"<ECPolygonMesh material=\"{X(m.Material)}\" affects_mesh_optimization=\"false\" ground_type=\"\"/>");
            Line(DefaultCampaignProperties());
            Line(Transform(m.Transform));
            sb.Append("      <ECPolyline>\n        <polyline closed=\"true\">\n");
            foreach (var (x, _, z) in m.Vertices)
                sb.Append("          <point x=\"").Append(F(x)).Append("\" y=\"").Append(F(z)).Append("\"/>\n");
            sb.Append("        </polyline>\n      </ECPolyline>\n");
            Close();
        }

        foreach (var s in o.Sounds)
        {
            Open("");
            Line($"<ECSoundMarker key=\"{X(s.Name)}\"/>");
            Line(Transform(s.Transform));
            // SST_LINE_LIST has no Terry campaign equivalent that survives a load/save (the script's ECPolyline3D is dropped).
            if (s.ShapeType == "SST_MULTI_POINT")
            {
                sb.Append("      <ECPointCloud>\n        <point_cloud>\n");
                var (x0, y0, z0) = s.Coords[0];
                foreach (var (x, y, z) in s.Coords)
                    sb.Append("          <point x=\"").Append(F((double)x - x0)).Append("\" y=\"").Append(F((double)y - y0))
                      .Append("\" z=\"").Append(F((double)z - z0)).Append("\"/>\n");
                sb.Append("        </point_cloud>\n      </ECPointCloud>\n");
            }
            else if (s.ShapeType == "SST_SPHERE")
                Line($"<ECSphere radius=\"{F(s.Radius)}\"/>");
            Line(DefaultCampaignProperties());
            Line(DlcMask);
            Close();
        }

        foreach (var c in o.CompositeScenes)
        {
            Open(c.Tags);
            Line($"<ECCompositeScene path=\"{X(c.Path)}\" autoplay=\"true\"/>");
            Line(Transform(c.Transform));
            Line(DefaultCampaignProperties(c.Seasons));
            Line(TerrainClamp);
            Close();
        }

        var layerIds = new List<(string Id, List<string> Members)>();
        foreach (var (tags, members) in tagged)
        {
            var id = ids.Next();
            layerIds.Add((id, members));
            sb.Append("    <entity id=\"").Append(id).Append("\" name=\"").Append(X(tags)).Append("\">\n");
            foreach (var line in TagLayerLines(tags)) Line(line);
            Close();
        }

        sb.Append("  </entities>\n  <associations>\n");
        if (layerIds.Count == 0)
            sb.Append("    <Logical/>\n");
        else
        {
            sb.Append("    <Logical>\n");
            foreach (var (id, members) in layerIds)
            {
                sb.Append("      <from id=\"").Append(id).Append("\">\n");
                foreach (var member in members)
                    sb.Append("        <to id=\"").Append(member).Append("\"/>\n");
                sb.Append("      </from>\n");
            }
            sb.Append("    </Logical>\n");
        }
        sb.Append("    <Transform/>\n  </associations>\n</layer>\n");
        return sb.ToString();
    }

    /// <summary>Component lines of a prop or decal entity, as Terry saves them.</summary>
    internal static IEnumerable<string> PropLines(PropRecord p)
    {
        if (p.IsDecal)
        {
            yield return $"<ECDecal model_path=\"{X(p.Path)}\" parallax_scale=\"0\" tiling=\"0\" tiling_affects_alpha=\"false\" normal_mode=\"DNM_DECAL_OVERRIDE\" apply_to_terrain=\"{B(p.ApplyToTerrain)}\" apply_to_objects=\"{B(p.ApplyToObjects)}\" render_above_snow=\"false\"/>";
            yield return CampaignProperties(p.VisibleInsideSnow, p.VisibleOutsideSnow, p.VisibleInsideDestruction,
                p.VisibleOutsideDestruction, p.VisibleInUnseenShroud, p.VisibleInSeenShroud, p.Seasons);
            yield return DlcMask;
            yield return RenderSettings;
        }
        else
        {
            yield return "<ECPropMesh/>";
            yield return $"<ECMesh model_path=\"{X(p.Path)}\" animation_path=\"\"/>";
            yield return RenderSettings;
            yield return $"<ECPropHeightPatch has_height_patch=\"{B(p.HasHeightPatch)}\" apply_height_patch=\"{B(p.ApplyHeightPatch)}\"/>";
            yield return CampaignProperties(p.VisibleInsideSnow, p.VisibleOutsideSnow, p.VisibleInsideDestruction,
                p.VisibleOutsideDestruction, p.VisibleInUnseenShroud, p.VisibleInSeenShroud, p.Seasons);
            yield return DlcMask;
        }
        yield return Transform(p.Transform);
        yield return TerrainClamp;
    }

    /// <summary>Component lines of a tag layer (a nested Layer entity whose members export with these tags).</summary>
    internal static IEnumerable<string> TagLayerLines(string tags) =>
    [
        "<ECLayerInternal/>",
        "<ECLayer/>",
        "<ECLayerExport export=\"true\" export_as_separate_file_if_not_meta_tagged=\"false\" buildings_have_linked_destruction=\"false\"/>",
        $"<ECLayerExportTags tags=\"{X(tags)}\"/>",
    ];

    private const string RenderSettings = "<ECMeshRenderSettings inherit_from_parent=\"false\" cast_shadow=\"true\" alpha=\"1\" tint_colour=\"255 255 255 255\" set_tint_colour_from_colour_overlay=\"false\" faction_colour=\"255 255 255 255\"/>";
    private const string DlcMask = "<ECDLCMask type=\"Exclude\" mask=\"\"/>";
    private const string TerrainClamp = "<ECTerrainClamp active=\"false\" clamp_to_sea_level=\"false\" terrain_oriented=\"false\"/>";
    private static string DefaultCampaignProperties(string seasons = "") =>
        CampaignProperties(true, true, true, true, false, true, seasons);

    private static string CampaignProperties(bool inSnow, bool outSnow, bool inDestruction, bool outDestruction,
                                             bool unseenShroud, bool seenShroud, string seasons) =>
        $"<ECCampaignProperties visible_inside_snow_region=\"{B(inSnow)}\" visible_outside_snow_region=\"{B(outSnow)}\" visible_inside_destruction_region=\"{B(inDestruction)}\" visible_outside_destruction_region=\"{B(outDestruction)}\" visible_in_unseen_shroud=\"{B(unseenShroud)}\" visible_in_seen_shroud=\"{B(seenShroud)}\" no_culling=\"false\" culture_mask=\"\" season_mask=\"{X(seasons)}\"/>";

    /// <summary>Transform values are rounded to 5 decimals like the Python script, then printed the way Terry does,
    /// so regenerated layers match the ones Terry re-saved from the script output.</summary>
    private static string Transform(PropTransform t) =>
        $"<ECTransform position=\"{R(t.X)} {R(t.Y)} {R(t.Z)}\" rotation=\"{R(t.RotX)} {R(t.RotY)} {R(t.RotZ)}\" scale=\"{R(t.ScaleX)} {R(t.ScaleY)} {R(t.ScaleZ)}\" pivot=\"0 0 0\"/>";

    private static string R(double v) => F(Math.Round(v, 5, MidpointRounding.ToEven));

    /// <summary>Terry prints floats with 9 significant digits (%.9g), rounding exact halves away from zero.</summary>
    public static string F(double v)
    {
        double d = (float)v;
        var e = Math.Abs(d).ToString("E16", CultureInfo.InvariantCulture); // exact for float values
        var digits = e[0] + e[2..18];
        if (digits[9] == '5' && digits.AsSpan(10).Trim('0').IsEmpty)
            d = d > 0 ? Math.BitIncrement(d) : Math.BitDecrement(d);
        return d.ToString("G9", CultureInfo.InvariantCulture).Replace("E", "e");
    }

    private static string B(bool b) => b ? "true" : "false";
    private static string X(string s) => SecurityElement.Escape(s);

    /// <summary>Deterministic 15-hex-digit entity ids ("1" + 14 digits, like Terry's), unique within the layer.</summary>
    private sealed class IdSource(string seed)
    {
        private readonly HashSet<string> _used = new();
        private int _n;

        public string Next()
        {
            while (true)
            {
                var hash = 14695981039346656037UL;
                foreach (var ch in $"{seed}/{_n++}")
                {
                    hash ^= ch;
                    hash *= 1099511628211UL;
                }
                var id = "1" + (hash & 0x00FF_FFFF_FFFF_FFFFUL).ToString("x14");
                if (_used.Add(id)) return id;
            }
        }
    }
}
