using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Assets;
using AtlasWH3.Core.Campaign.Lakes;
using AtlasWH3.Core.Campaign.Props;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

/// <summary>
/// lake-build: raised lakes as one flat water mesh each, with the shore shaped (<see cref="LakeBuilder"/>), on a campaign
/// terrain project (the kit's, or the main190 pipeline's output folder). Writes the lake models (.rigid_model_v2 +
/// .wsmodel, pack layout) to --assets; with --apply also writes the shaped LowFrequencyHeight TIF and the entity
/// changes (removes small_lake_1 discs and earlier 190e_lake props inside the lakes, adds one prop per lake) as two
/// journaled steps (undo with entity-undo). Without --apply it only reports and writes the assets.
/// </summary>
static class LakeCommands
{
    public static readonly HashSet<string> Names = ["lake-build", "sea-carve", "terrain-scale", "river-bed"];

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            var editor = new EntityEditor(paths, Option(a, "--project"));
            var project = editor.Project;
            var lfMap = project.Find("LowFrequencyHeight") ?? throw new InvalidOperationException("project has no LowFrequencyHeight map");
            var tif = project.LayerTifPath(lfMap);
            HexRegionLookup look;
            if (Option(a, "--hex") is { } hexPath)
            {
                var b = (Option(a, "--bounds") ?? throw new ArgumentException("--hex needs --bounds minx,miny,maxx"))
                    .Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                look = new HexRegionLookup(MapHexFile.Read(hexPath), b[0], b[1], b[2]);
            }
            else look = HexRegionLookup.ForMap(paths with { MapName = project.MapName }, out var why) ?? throw new InvalidOperationException(why);
            if (command == "sea-carve") return SeaCarve(editor, tif, look, a);
            if (command == "terrain-scale") return TerrainScale(paths, editor, tif, a);
            if (command == "river-bed") return RiverBed(editor, project, look, a);
            var assets = Option(a, "--assets") ?? Path.Combine(paths.OutputRoot, "lake_assets", project.MapName);
            var apply = a.Contains("--apply");
            var library = ModelLibrary.ForGame(paths, modPacks: paths.ModPacks);
            var template = library.Source.Read(LakeBuilder.TemplateModel);

            var raster = TiffMap.ReadGray16(tif);
            var seaTif = project.Find("LowFrequencyHeightSea") is { } seaMap ? project.LayerTifPath(seaMap) : null;
            var seaRaster = seaTif is not null && File.Exists(seaTif) ? TiffMap.ReadGray16(seaTif) : null;
            // the tile map decides where water tiles are: the lake mask follows it (generic_sea pixels only)
            Func<int, int, bool>? waterTile = null;
            var tileMapPath = Path.Combine(Path.GetDirectoryName(tif)!, "tile_map.png");
            if (File.Exists(tileMapPath))
            {
                var tm = AtlasWH3.Core.Campaign.TileMapCheck.HexTileMap.Read(tileMapPath);
                double fx = (double)tm.PixelWidth / raster.Width, fy = (double)tm.PixelHeight / raster.Height;
                waterTile = (c, r) =>
                {
                    int x = Math.Min(tm.PixelWidth - 1, (int)(c * fx)), y = Math.Min(tm.PixelHeight - 1, (int)(r * fy));
                    return tm.Pixels[y * tm.PixelWidth + x] == 0x3971b7u;   // generic_sea
                };
            }
            var lakes = LakeBuilder.Build(raster, look, template, assets, Option(a, "--region") ?? "sea_lake",
                minGround: Option(a, "--min-ground") is { } mg ? double.Parse(mg, CultureInfo.InvariantCulture) : 0.1, log: m => Console.Error.WriteLine(m), sea: seaRaster, waterTile: waterTile);

            // Entities: discs and earlier lake props inside the lakes go; one prop per lake comes in.
            var lakeHexes = lakes.SelectMany(l => l.Body).ToHashSet();
            bool InLake(double x, double z)
            {
                for (var k = 0; k < 7; k++)
                {
                    var ang = k * Math.PI / 3;
                    var r = k == 6 ? 0 : 0.7;
                    if (lakeHexes.Contains(look.HexAt((float)(x + Math.Cos(ang) * r), (float)(z + Math.Sin(ang) * r)))) return true;
                }
                return false;
            }
            var ops = new JsonArray();
            var drownedCount = 0;
            string? hostLayer = Option(a, "--layer");
            foreach (var (layer, entities) in editor.ReadAll())
                foreach (var e in entities)
                {
                    var model = e.Component("ECMesh")?["model_path"]?.Replace('\\', '/').ToLowerInvariant();
                    if (model is null || e.Transform is not var (p, _, _)) continue;
                    if (model.Contains("props/lakes/") && hostLayer is null) hostLayer = layer.Name == "3k_main_sea_lake" ? layer.Name : null;
                    var disc = model.EndsWith("props/lakes/small_lake_1.wsmodel") && InLake(p[0], p[2]);
                    var ours = model.Contains("props/lakes/190e_lake_");
                    // vegetation and rocks standing in the new water (Juyan's shrubs in game, 2026-10-04)
                    var drowned = (model.Contains("/vegetation/") || model.Contains("/trees/") || model.Contains("/rocks/"))
                                  && LakeBuilder.InWater(lakes, p[0], p[2], raster.Height, 0.3f);   // and the waterline: trees there lean over the water
                    if (drowned) drownedCount++;
                    if (disc || ours || drowned) ops.Add(new JsonObject { ["op"] = "delete", ["id"] = e.Id, ["in_layer"] = layer.Id });
                }
            hostLayer ??= editor.Layers().FirstOrDefault(l => l.Name == "3k_main_sea_lake")?.Name
                           ?? throw new InvalidOperationException("no 3k_main_sea_lake layer: pass --layer");
            var I = CultureInfo.InvariantCulture;
            foreach (var l in lakes)
                ops.Add(new JsonObject
                {
                    ["op"] = "create", ["type"] = "Prop", ["layer"] = hostLayer, ["name"] = Path.GetFileNameWithoutExtension(l.ModelPath),
                    ["fields"] = new JsonObject
                    {
                        ["ECMesh.model_path"] = l.ModelPath,
                        ["ECTransform.position"] = $"{l.CentreX.ToString("F4", I)} {l.Level.ToString("F5", I)} {l.CentreZ.ToString("F4", I)}",
                    },
                });

            var report = new JsonObject
            {
                ["project"] = editor.TerryPath, ["assets"] = assets, ["applied"] = apply,
                ["lakes"] = new JsonArray(lakes.Select(l => (JsonNode)new JsonObject
                {
                    ["model"] = l.ModelPath, ["hexes"] = l.Hexes, ["level"] = Math.Round(l.Level, 3),
                    ["centre"] = new JsonArray(Math.Round(l.CentreX, 2), Math.Round(l.CentreZ, 2)),
                    ["vertices"] = l.Vertices, ["triangles"] = l.Triangles, ["shaped_cells"] = l.ShapedCells,
                }).ToArray()),
                ["deletes"] = ops.Count(o => o!["op"]!.ToString() == "delete"), ["creates"] = lakes.Count, ["layer"] = hostLayer, ["drowned_props_deleted"] = drownedCount,
            };
            File.WriteAllText(Path.Combine(assets, "lake_ops.json"), ops.ToJsonString());
            if (apply)
            {
                var files = new List<(string, Action<string>)> { (tif, p => TiffMap.WriteGray16(p, raster)) };
                if (seaRaster is not null) files.Add((seaTif!, p => TiffMap.WriteGray16(p, seaRaster)));
                new FileJournal(editor.HistoryDir).Commit(files, "lake-build: shore shaping (LowFrequencyHeight) + lake bed (LowFrequencyHeightSea)");
                editor.Apply(ops, "lake-build: lake meshes replace water discs");
            }
            Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, type = ex.GetType().Name }));
            return 1;
        }
    }

    /// <summary>
    /// sea-carve --at x,z --radius r [--depth d] [--apply]: lowers a sea inlet's banks below sea level so the game's sea
    /// water fills it (<see cref="LakeBuilder.CarveSea"/>) and removes the small_lake_1 discs within the radius that
    /// stood in for it. Journaled like lake-build.
    /// </summary>
    private static int SeaCarve(EntityEditor editor, string tif, HexRegionLookup look, string[] a)
    {
        var I = CultureInfo.InvariantCulture;
        var at = (Option(a, "--at") ?? throw new ArgumentException("--at x,z")).Split(',').Select(v => double.Parse(v, I)).ToArray();
        var radius = double.Parse(Option(a, "--radius") ?? "3", I);
        var depth = double.Parse(Option(a, "--depth") ?? "0.35", I);
        var raster = TiffMap.ReadGray16(tif);
        var changed = LakeBuilder.CarveSea(raster, look, at[0], at[1], radius, depth);
        var ops = new JsonArray();
        foreach (var (layer, entities) in editor.ReadAll())
            foreach (var e in entities)
                if (e.Component("ECMesh")?["model_path"]?.Replace((char)92, '/').ToLowerInvariant().EndsWith("props/lakes/small_lake_1.wsmodel") == true
                    && e.Transform is var (p, _, _) && Math.Sqrt((p[0] - at[0]) * (p[0] - at[0]) + (p[2] - at[1]) * (p[2] - at[1])) <= radius)
                    ops.Add(new JsonObject { ["op"] = "delete", ["id"] = e.Id, ["in_layer"] = layer.Id });
        var apply = a.Contains("--apply");
        if (apply)
        {
            new FileJournal(editor.HistoryDir).Commit([(tif, path => TiffMap.WriteGray16(path, raster))], $"sea-carve: inlet at {at[0]:F1},{at[1]:F1} below sea level");
            if (ops.Count > 0) editor.Apply(ops, "sea-carve: remove water discs over the carved inlet");
        }
        Console.WriteLine(new JsonObject { ["applied"] = apply, ["cells_lowered"] = changed, ["discs_removed"] = ops.Count, ["ops"] = ops }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// terrain-scale --at x,z --radius r --factor f [--apply]: scales the heights above sea level of the land mass (lf
    /// above 0.02, connected) containing (x, z), within r, by f — e.g. 0.6 to flatten an island (Tamna, 2026-10-04); the
    /// coast stays at sea level. Props on it keep their footing: absolute-height props move by the ground change under
    /// them; LF-offset models (mountains drawn relative to the terrain) are left as they are. Journaled like lake-build.
    /// </summary>
    private static int TerrainScale(ProjectPaths paths, EntityEditor editor, string tif, string[] a)
    {
        var I = CultureInfo.InvariantCulture;
        var at = (Option(a, "--at") ?? throw new ArgumentException("--at x,z")).Split(',').Select(v => double.Parse(v, I)).ToArray();
        var radius = double.Parse(Option(a, "--radius") ?? "20", I);
        var factor = double.Parse(Option(a, "--factor") ?? throw new ArgumentException("--factor f"), I);
        var raster = TiffMap.ReadGray16(tif);
        double px = AtlasWH3.Core.Campaign.CameraHeightmapStep.PixelSizeX, pz = AtlasWH3.Core.Campaign.CameraHeightmapStep.PixelSizeZ;
        double step = AtlasWH3.Core.Campaign.CameraHeightmapStep.HeightStep, off = AtlasWH3.Core.Campaign.CameraHeightmapStep.HeightOffset;
        double worldH = raster.Height * pz;
        double H(int c, int r) => raster[c, r] * step + off;
        int c0 = (int)Math.Floor(at[0] / px), r0 = (int)Math.Floor((worldH - at[1]) / pz);
        int rc = (int)Math.Ceiling(radius / px), rr = (int)Math.Ceiling(radius / pz);
        // the connected land component around the seed (nearest land cell within the window)
        bool Land(int c, int r) => c >= 0 && r >= 0 && c < raster.Width && r < raster.Height && Math.Abs(c - c0) <= rc && Math.Abs(r - r0) <= rr && H(c, r) > 0.02;
        var seed = (c0, r0);
        if (!Land(c0, r0))
        {
            var best = double.MaxValue;
            for (var r = r0 - rr; r <= r0 + rr; r++)
                for (var c = c0 - rc; c <= c0 + rc; c++)
                    if (Land(c, r)) { var d = Math.Pow((c - c0) * px, 2) + Math.Pow((r - r0) * pz, 2); if (d < best) { best = d; seed = (c, r); } }
            if (best == double.MaxValue) throw new InvalidOperationException("no land within --radius");
        }
        var island = new HashSet<(int, int)> { seed };
        var q = new Queue<(int, int)>([seed]);
        while (q.Count > 0)
        {
            var (c, r) = q.Dequeue();
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (Land(c + dc, r + dr) && island.Add((c + dc, r + dr))) q.Enqueue((c + dc, r + dr));
        }
        var before = new Dictionary<(int, int), double>();
        foreach (var (c, r) in island)
        {
            var h = H(c, r); before[(c, r)] = h;
            raster[c, r] = (ushort)Math.Clamp(Math.Round((h * factor - off) / step), 0, 65535);
        }
        // props on the island: absolute heights follow the ground; LF-offset models stay
        var library = ModelLibrary.ForGame(paths, modPacks: paths.ModPacks);
        var lfOffset = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ops = new JsonArray();
        int moved = 0, offsetKept = 0;
        foreach (var (layer, entities) in editor.ReadAll())
            foreach (var e in entities)
            {
                if (e.Transform is not var (p, _, _)) continue;
                int c = (int)Math.Floor(p[0] / px), r = (int)Math.Floor((worldH - p[2]) / pz);
                if (!island.Contains((c, r))) continue;
                var model = e.Component("ECMesh")?["model_path"];
                if (model is not null)
                {
                    if (!lfOffset.TryGetValue(model, out var rel))
                        lfOffset[model] = rel = library.Load(model)?.Lods.SelectMany(l => l).Any(m => m.TerrainOffset > 0) == true;
                    if (rel) { offsetKept++; continue; }
                }
                var delta = (before[(c, r)] * factor) - before[(c, r)];
                if (Math.Abs(delta) < 1e-4) continue;
                var pos = e.Component("ECTransform")?["position"]?.Split(' ');
                if (pos is null || pos.Length < 3) continue;
                var y = double.Parse(pos[1], I) + delta;
                ops.Add(new JsonObject { ["op"] = "set", ["id"] = e.Id, ["in_layer"] = layer.Id,
                    ["fields"] = new JsonObject { ["ECTransform.position"] = $"{pos[0]} {y.ToString("F5", I)} {pos[2]}" } });
                moved++;
            }
        var apply = a.Contains("--apply");
        if (apply)
        {
            new FileJournal(editor.HistoryDir).Commit([(tif, path => TiffMap.WriteGray16(path, raster))], $"terrain-scale: land at {at[0]:F1},{at[1]:F1} x{factor}");
            if (ops.Count > 0) editor.Apply(ops, $"terrain-scale: props follow the ground (x{factor})");
        }
        var hs = before.Values.OrderBy(v => v).ToList();
        Console.WriteLine(new JsonObject
        {
            ["applied"] = apply, ["cells"] = island.Count, ["height_p50_before"] = Math.Round(hs[hs.Count / 2], 3), ["height_max_before"] = Math.Round(hs[^1], 3),
            ["height_p50_after"] = Math.Round(hs[hs.Count / 2] * factor, 3), ["height_max_after"] = Math.Round(hs[^1] * factor, 3),
            ["props_moved"] = moved, ["lf_offset_props_kept"] = offsetKept,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// river-bed [--min-depth d] [--apply]: navigable rivers (ECRiverSpline over map.hex sea hexes, e.g. the Yellow River
    /// channels) draw their water over generic_sea tiles whose ground is the sea height map (LowFrequencyHeightSea).
    /// Where that bed sits less than d (0.9) under the spline's water surface the water reads pale and sandy (Xihe /
    /// Mengmen, in game 2026-10-04). Lowers the bed to water − d across the channel (spline width, soft 0.3 edge), on
    /// sea hexes only; never raises. Journaled.
    /// </summary>
    private static int RiverBed(EntityEditor editor, AtlasWH3.Formats.Terry.TerryProject project, HexRegionLookup look, string[] a)
    {
        var I = CultureInfo.InvariantCulture;
        var minDepth = double.Parse(Option(a, "--min-depth") ?? "0.9", I);
        // --rect x0,z0,x1,z1: only bed cells inside (a local fix, e.g. Xihe / Mengmen)
        var rect = Option(a, "--rect")?.Split(',').Select(v => double.Parse(v, I)).ToArray();
        var seaMap = project.Find("LowFrequencyHeightSea") ?? throw new InvalidOperationException("no LowFrequencyHeightSea map");
        var seaTif = project.LayerTifPath(seaMap);
        var sea = TiffMap.ReadGray16(seaTif);
        double px = AtlasWH3.Core.Campaign.CameraHeightmapStep.PixelSizeX, pz = AtlasWH3.Core.Campaign.CameraHeightmapStep.PixelSizeZ;
        double step = AtlasWH3.Core.Campaign.CameraHeightmapStep.HeightStep, off = AtlasWH3.Core.Campaign.CameraHeightmapStep.HeightOffset;
        var lfMap = project.Find("LowFrequencyHeight")!;
        var lfH = TiffMap.ReadGray16(project.LayerTifPath(lfMap)).Height;
        var lfW = TiffMap.ReadGray16(project.LayerTifPath(lfMap)).Width;
        double worldH = lfH * pz;
        double cellX = lfW * px / sea.Width;
        double cellZ = worldH / sea.Height;
        int changed = 0, rivers = 0;
        var report = new JsonArray();
        foreach (var layerFile in Directory.GetFiles(Path.GetDirectoryName(seaTif)!, "*.layer"))
            foreach (var xml in System.Xml.Linq.XDocument.Load(layerFile).Descendants("entity"))
            {
                var spline = xml.Element("ECRiverSpline");
                var tr = (string?)xml.Element("ECTransform")?.Attribute("position");
                if (spline is null || tr is null) continue;
                var o = tr.Split(' ').Select(t => double.Parse(t, I)).ToArray();
                var pts = spline.Descendants("point").Select(p =>
                {
                    var v = ((string?)p.Attribute("position") ?? "0,0,0").Split(',').Select(t => double.Parse(t, I)).ToArray();
                    return (X: o[0] + v[0], Y: o[1] + v[1], Z: o[2] + v[2], W: double.Parse((string?)p.Attribute("width") ?? "1", I));
                }).ToList();
                if (pts.Count < 2) continue;
                // navigable only: most of its points on sea hexes
                var onSea = pts.Count(p => { var (c, r) = look.HexAt((float)p.X, (float)p.Z); return look.Hex.TerrainAt(c, r) == 1; });
                if (onSea * 2 < pts.Count) continue;
                rivers++;
                int before = changed;
                for (var k = 0; k + 1 < pts.Count; k++)
                {
                    var (p0, p1) = (pts[k], pts[k + 1]);
                    double reach = Math.Max(p0.W, p1.W) / 2 + 0.3;
                    int c0 = (int)Math.Floor((Math.Min(p0.X, p1.X) - reach) / cellX), c1 = (int)Math.Ceiling((Math.Max(p0.X, p1.X) + reach) / cellX);
                    int r0 = (int)Math.Floor((worldH - Math.Max(p0.Z, p1.Z) - reach) / cellZ), r1 = (int)Math.Ceiling((worldH - Math.Min(p0.Z, p1.Z) + reach) / cellZ);
                    double dx = p1.X - p0.X, dz = p1.Z - p0.Z, len2 = Math.Max(1e-9, dx * dx + dz * dz);
                    for (var r = Math.Max(0, r0); r <= Math.Min(sea.Height - 1, r1); r++)
                        for (var c = Math.Max(0, c0); c <= Math.Min(sea.Width - 1, c1); c++)
                        {
                            double x = (c + 0.5) * cellX, z = worldH - (r + 0.5) * cellZ;
                            var t = Math.Clamp(((x - p0.X) * dx + (z - p0.Z) * dz) / len2, 0, 1);
                            double qx = p0.X + dx * t, qz = p0.Z + dz * t, d = Math.Sqrt((x - qx) * (x - qx) + (z - qz) * (z - qz));
                            double half = (p0.W + (p1.W - p0.W) * t) / 2, y = p0.Y + (p1.Y - p0.Y) * t;
                            if (d > half + 0.3) continue;
                            if (rect is not null && (x < rect[0] || x > rect[2] || z < rect[1] || z > rect[3])) continue;
                            var (hc, hr) = look.HexAt((float)x, (float)z);
                            if (look.Hex.TerrainAt(hc, hr) != 1) continue;
                            var w = d <= half - 0.3 ? 1.0 : Math.Clamp((half + 0.3 - d) / 0.6, 0, 1);
                            double cur = sea[c, r] * step + off, target = y - minDepth;
                            if (cur <= target) continue;
                            var nv = cur + (target - cur) * w;
                            var raw = (ushort)Math.Clamp(Math.Round((nv - off) / step), 0, 65535);
                            if (raw < sea[c, r]) { sea[c, r] = raw; changed++; }
                        }
                }
                report.Add(new JsonObject { ["river"] = (string?)xml.Attribute("name") ?? (string?)xml.Attribute("id"), ["points"] = pts.Count, ["bed_cells_lowered"] = changed - before });
            }
        var apply = a.Contains("--apply");
        if (apply && changed > 0)
            new FileJournal(editor.HistoryDir).Commit([(seaTif, path => TiffMap.WriteGray16(path, sea))], $"river-bed: navigable riverbeds >= {minDepth} under the water{(rect is null ? "" : " in " + Option(a, "--rect"))}");
        Console.WriteLine(new JsonObject { ["applied"] = apply, ["navigable_rivers"] = rivers, ["bed_cells_lowered"] = changed, ["rivers"] = report }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
