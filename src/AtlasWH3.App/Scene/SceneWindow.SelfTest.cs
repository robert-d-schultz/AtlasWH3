using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Scripted end-to-end check of the scene window, run in-process (no desktop input): search, select, inspector edits
/// (scalar and per-component vector), drag-move, duplicate, create, rename, folder layers and drop, visibility, then
/// undo of everything, verifying the project files are restored byte for byte. Writes screenshots and selftest.json.
/// Start with: AtlasWH3.App --scene &lt;copy of a project&gt;.terry --selftest &lt;out dir&gt;. Never point it at the real kit.
/// </summary>
public sealed partial class SceneWindow
{
    public async Task SelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new JsonObject();
        var steps = new JsonArray();
        report["steps"] = steps;
        void Step(string name, bool ok, string? detail = null)
        {
            steps.Add(new JsonObject { ["step"] = name, ["ok"] = ok, ["detail"] = detail, ["status"] = _status.Text });
        }
        try
        {
            while (!_loadedOnce) await Task.Delay(200);
            var dir = Path.GetDirectoryName(_model.TerryPath)!;
            var before = Hashes(dir);
            var prefabRoot = _model.Prefabs.Root;
            var prefabBefore = Directory.Exists(prefabRoot) ? HashTree(prefabRoot) : [];
            await Shot(outDir, "01_loaded");
            Step("load", _model.All.Any(), $"{_model.Layers.Count} layers, {_model.All.Count()} entities");

            // The most common placed type in this project (PointLight on the campaign map has fields worth editing).
            var type = _model.All.Any(i => i.Entity.Type == "PointLight") ? "PointLight"
                : _model.All.Where(i => i.Entity.Transform is not null && !AtlasWH3.Formats.Terry.TerryEntityTypes.IsLayerType(i.Entity.Type))
                    .GroupBy(i => i.Entity.Type).OrderByDescending(g => g.Count()).First().Key;
            _search.Text = "type:" + type;
            RunSearch();
            Step("search", _resultIds.Count > 0, $"{_resultIds.Count} {type}");
            await Shot(outDir, "02_search");

            _results.SelectedIndex = 0;
            var light = _resultIds[0];
            await Shot(outDir, "03_selected");
            Step("select", _selection.SetEquals([light]), light);

            // A numeric field of the selected entity's own main component.
            var (comp, field) = _model.Find(light)!.Entity.Components
                .SelectMany(c => c.Fields.Select(f => (c.Name, f.Key)))
                .FirstOrDefault(cf => cf.Name != "ECTransform" && _model.Schema.Field(cf.Name, cf.Key)?.Type is AtlasWH3.Formats.Terry.FieldType.Float or AtlasWH3.Formats.Terry.FieldType.Int);
            comp ??= "ECTransform";
            field ??= "scale";
            var number = comp == "ECTransform" ? "2 2 2" : "3";
            FieldEdited(new InspectorPanel.FieldEdit(comp, field, _ => number));
            Step($"set {comp}.{field}", _model.Find(light)?.Entity.Component(comp)?[field] == number);

            FieldEdited(new InspectorPanel.FieldEdit("ECTransform", "position", cur =>
            {
                var p = cur.Split(' ');
                p[1] = "10";
                return string.Join(' ', p);
            }));
            var y = _model.Find(light)?.Entity.Transform?.Position[1];
            Step("set position y only", y == 10, $"y={y}");

            FieldEdited(new InspectorPanel.FieldEdit(comp, field, _ => "not-a-number"));
            Step("invalid value refused", _model.Find(light)?.Entity.Component(comp)?[field] == number && _status.Foreground == Brushes.IndianRed);

            var p0 = _model.Find(light)!.Entity.Transform!.Value.Position;
            MoveSelection(2, -1);
            var p1 = _model.Find(light)!.Entity.Transform!.Value.Position;
            Step("drag move", Math.Abs(p1[0] - p0[0] - 2) < 1e-3 && Math.Abs(p1[2] - p0[2] + 1) < 1e-3, $"{p0[0]},{p0[2]} -> {p1[0]},{p1[2]}");

            Duplicate();
            var copy = _selection.Single();
            Step("duplicate", copy != light && _model.Find(copy) is not null, copy);

            var layer = _model.Find(light)!.Layer;
            var createType = new[] { "SoundMarker", "PointLight", "Prop", "Building" }
                .First(t => _model.Config.Find(t)?.AllowedIn(_model.Project.ProjectType, _model.Project.Database) == true);
            var created = Run("create", new JsonObject
            {
                ["op"] = "create", ["type"] = createType, ["layer"] = layer.Id,
                ["position"] = new JsonArray(p1[0] + 1, p1[1], p1[2] + 1),
            });
            var sound = created?[0]?["created"]?[0]?["id"]?.ToString();
            Step($"create {createType}", sound is not null && _model.Find(sound)?.Entity.Type == createType, sound);

            Run("rename", new JsonObject { ["op"] = "set", ["id"] = sound, ["name"] = "selftest_sound" });
            Step("rename", _model.Find(sound!)?.Entity.Name == "selftest_sound");

            var folderResult = Run("folder", new JsonObject { ["op"] = "create_layer", ["name"] = "selftest_folder", ["layer"] = layer.Id });
            var folder = folderResult?[0]?["created"]?[0]?["id"]?.ToString();
            DropOnLayer([sound!, copy], new LayerTreePanel.Node("layer", layer.Id, folder));
            Step("drop into folder", _model.Find(sound!)?.Entity.Parents.Contains(folder!) == true
                                     && _model.Find(copy)?.Entity.Parents.Contains(folder!) == true);

            SetSelection([sound!, copy, light], SceneView.SelectMode.Replace);
            await Shot(outDir, "04_multi_select");
            Step("multi-select inspector", _selection.Count == 3);

            Run("hide", new JsonObject { ["op"] = "layer_state", ["id"] = folder, ["visible"] = false });
            Step("hide folder", _model.IsHidden(_model.Find(sound!)!) && !_model.IsHidden(_model.Find(light)!));

            _search.Text = $"layer:{layer.Name}";
            RunSearch();
            SetSelection(_resultIds, SceneView.SelectMode.Replace);
            _view.FrameSelection();
            await Shot(outDir, "05_layer_selected");
            Step("select layer", _selection.Count > 0, $"{_selection.Count} in {layer.Name}");

            // Prefabs: place one from the library, expand it, turn the copies back into a new prefab (sandbox kits only:
            // make_prefab writes into the kit's prefab folder), then everything is undone below.
            var key = _model.Prefabs.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => _model.ContentOf(k)?.Points.Length > 0);
            if (key is null) Step("prefab library", true, $"no prefabs in {prefabRoot}; prefab steps skipped");
            else
            {
                var placed = Run("place prefab", new JsonObject
                {
                    ["op"] = "place_prefab", ["key"] = key, ["layer"] = layer.Id, ["position"] = new JsonArray(p1[0] + 3, p1[1], p1[2] + 3),
                });
                var instance = placed?[0]?["created"]?[0]?["id"]?.ToString();
                Step("place prefab", instance is not null && _model.Find(instance)?.Entity.Type == "Prefab", $"{key} → {instance}");
                SetSelection([instance!], SceneView.SelectMode.Replace);
                _view.FrameSelection();
                await Shot(outDir, "07_prefab_placed");
                var inside = _model.ContentOf(key)!.Points.Length;
                ExpandSelection();
                Step("expand prefab", _model.Find(instance!) is null && _selection.Count == inside, $"{_selection.Count} entities (prefab has {inside})");
                await Shot(outDir, "08_prefab_expanded");
                if (!_paths.AssemblyKitRoot.Equals(new AtlasWH3.Core.ProjectPaths().AssemblyKitRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var newKey = $"selftest_prefab_{DateTime.Now:HHmmss}";
                    var made = Run("make prefab", new JsonObject { ["op"] = "make_prefab", ["ids"] = Ids(_selection), ["key"] = newKey, ["folder"] = "selftest" });
                    var newInstance = made?[0]?["instance"]?["id"]?.ToString();
                    Step("make prefab", newInstance is not null && _model.Prefabs.PathOf(newKey) is not null && _model.ContentOf(newKey)?.Points.Length == inside,
                        $"{newKey}: {_model.ContentOf(newKey)?.Points.Length} entities");
                }
                else Step("make prefab", true, "skipped: real kit (use --ak <sandbox kit>)");
            }

            // Models: footprints from model bounds, and the inspector's rendered thumbnail.
            for (var wait = 0; wait < 300 && _model.ModelBounds.Count == 0 && _model.All.Any(i => SceneModel.ModelPathOf(i.Entity) is not null); wait++)
                await Task.Delay(200);
            var withModel = _model.All.Where(i => SceneModel.ModelPathOf(i.Entity) is { } mp && _model.ModelBounds.ContainsKey(mp)).ToList();
            var showcase = withModel.FirstOrDefault(i => SceneModel.ModelPathOf(i.Entity)!.Contains("settlements/", StringComparison.OrdinalIgnoreCase)) ?? withModel.FirstOrDefault();
            if (showcase is null) Step("model footprints", true, "no models in this project");
            else
            {
                SetSelection([showcase.Entity.Id], SceneView.SelectMode.Replace);
                _view.FrameSelection();
                await Task.Delay(2500); // thumbnail renders in the background
                await Shot(outDir, "09_model");
                Step("model footprints", _model.ModelBounds.Count > 0, $"{_model.ModelBounds.Count} model bounds; showing {SceneModel.ModelPathOf(showcase.Entity)}");
            }

            // 3D viewport: render, pick, and drive the move / rotate gizmos.
            _centre.SelectedIndex = 1;
            for (var wait = 0; wait < 50 && _view3d.AdapterName is null; wait++) await Task.Delay(100);
            Step("3d device", _view3d.AdapterName is not null, _view3d.AdapterName);
            if (_view3d.AdapterName is not null)
            {
                // Real window messages to the viewport's own window (not desktop input): hit test, right-drag orbit, wheel.
                var hwnd = _view3d.WindowHandle;
                var hit = (long)SendMessage(hwnd, 0x84, IntPtr.Zero, IntPtr.Zero);
                static IntPtr Xy(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
                var yaw0 = _view3d.Camera.Yaw;
                var dist0 = _view3d.Camera.Distance;
                SendMessage(hwnd, 0x204, (IntPtr)2, Xy(200, 200));   // WM_RBUTTONDOWN
                SendMessage(hwnd, 0x200, (IntPtr)2, Xy(320, 210));   // WM_MOUSEMOVE with right button
                SendMessage(hwnd, 0x205, IntPtr.Zero, Xy(320, 210)); // WM_RBUTTONUP
                var yaw1 = _view3d.Camera.Yaw;
                SendMessage(hwnd, 0x20A, (IntPtr)(120 << 16), Xy(0, 0)); // WM_MOUSEWHEEL forward
                // Fly: hold the right button and W for ~0.6 s; expect roughly 0.8 orbit distances per second.
                var t0 = _view3d.Camera.Target;
                var flyDist = _view3d.Camera.Distance;
                SendMessage(hwnd, 0x204, (IntPtr)2, Xy(200, 200));
                SendMessage(hwnd, 0x100, (IntPtr)0x57, IntPtr.Zero);   // WM_KEYDOWN 'W'
                await Task.Delay(600);
                SendMessage(hwnd, 0x101, (IntPtr)0x57, IntPtr.Zero);   // WM_KEYUP
                SendMessage(hwnd, 0x205, IntPtr.Zero, Xy(200, 200));
                var flown = System.Numerics.Vector3.Distance(t0, _view3d.Camera.Target);
                Step("3d input", hit == 1 && Math.Abs(yaw1 - yaw0) > 0.1 && _view3d.Camera.Distance < dist0 && flown > flyDist * 0.8 * 0.3,
                    $"hit test {hit} (1 = client); orbit yaw {yaw0:F2} → {yaw1:F2}; wheel distance {dist0:F1} → {_view3d.Camera.Distance:F1}; "
                    + $"flew {flown:F1} units in 0.6 s at distance {flyDist:F1}");

                // Wheel zoom toward the cursor must reach the ground, not stall on the orbit pivot: 60 notches at the
                // viewport centre (screen coordinates, as Windows sends them) from the current view.
                var cam = _view3d.Camera;
                var saved = (cam.Target, cam.Distance, cam.Yaw, cam.Pitch);
                var centre = new System.Drawing.Point(_view3d.ViewportWidth / 2, _view3d.ViewportHeight / 2);
                ClientToScreen(hwnd, ref centre);
                var h0 = _view3d.EyeAboveGround;
                for (var k = 0; k < 60; k++) SendMessage(hwnd, 0x20A, (IntPtr)(120 << 16), Xy(centre.X, centre.Y));
                var h1 = _view3d.EyeAboveGround;
                Step("3d zoom to cursor", h1 < h0 * 0.02, $"eye above ground {h0:F2} → {h1:F3} after 60 wheel notches at the centre");
                (cam.Target, cam.Distance, cam.Yaw, cam.Pitch) = saved;
                _view3d.Invalidate();
            }
            if (_view3d.AdapterName is not null && showcase is not null)
            {
                SetSelection([showcase.Entity.Id], SceneView.SelectMode.Replace);
                _view3d.FrameSelection();
                for (var wait = 0; wait < 200 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                await Task.Delay(300);
                Save3D(outDir, "10_3d_selected");
                try
                {
                var pivot = (_view3d.Pivot ?? _view3d.Camera.Target);
                // Picking: an isolated model (nothing else within 4 units), seen from above, clicked at its box centre.
                var positioned = _model.All.Where(i => i.Entity.Transform is not null && !TerryEntityTypes.IsLayerType(i.Entity.Type))
                    .Select(i => (Item: i, P: i.Entity.Transform!.Value.Position)).ToList();
                var lonely = withModel.Take(3000).FirstOrDefault(i =>
                {
                    var p = i.Entity.Transform!.Value.Position;
                    return positioned.Count(o => Math.Abs(o.P[0] - p[0]) < 4 && Math.Abs(o.P[2] - p[2]) < 4) == 1;
                });
                if (lonely is null) Step("3d pick", true, "no isolated model to test with");
                else
                {
                    SetSelection([lonely.Entity.Id], SceneView.SelectMode.Replace);
                    _view3d.FrameSelection();
                    _view3d.Camera.Pitch = 1.3f;
                    var target = _view3d.CentreOf(lonely.Entity.Id)!.Value;
                    var picked = _view3d.ScreenOf(target) is { } c0 ? _view3d.PickAt(c0) : null;
                    Step("3d pick", picked == lonely.Entity.Id, $"picked {picked} for {lonely.Entity.Id} ({SceneModel.ModelPathOf(lonely.Entity)})");
                    SetSelection([showcase.Entity.Id], SceneView.SelectMode.Replace);
                    _view3d.FrameSelection();
                    pivot = (_view3d.Pivot ?? _view3d.Camera.Target);
                }

                var before3d = _model.Find(showcase.Entity.Id)!.Entity.Transform!.Value;
                var from = _view3d.ScreenOf(pivot)!.Value;
                _view3d.SimulateGizmoDrag(Viewport3D.Viewport3DControl.GizmoMode.Move, "xz", from, new Point(from.X + 60, from.Y));
                var moved = _model.Find(showcase.Entity.Id)!.Entity.Transform!.Value;
                var dist = Math.Sqrt(Math.Pow(moved.Position[0] - before3d.Position[0], 2) + Math.Pow(moved.Position[2] - before3d.Position[2], 2));
                Step("3d gizmo move", dist > 1e-3 && Math.Abs(moved.Position[1] - before3d.Position[1]) < 1e-4, $"moved {dist:F3} on the ground plane");

                var p2 = (_view3d.Pivot ?? _view3d.Camera.Target);
                var size = System.Numerics.Vector3.Distance(_view3d.Camera.Eye, p2) * 0.12f;
                var ringA = _view3d.ScreenOf(p2 + new System.Numerics.Vector3(size, 0, 0))!.Value;
                var ringB = _view3d.ScreenOf(p2 + new System.Numerics.Vector3(0, 0, size))!.Value;
                _view3d.SimulateGizmoDrag(Viewport3D.Viewport3DControl.GizmoMode.Rotate, "ring", ringA, ringB);
                var turned = _model.Find(showcase.Entity.Id)!.Entity.Transform!.Value;
                var dYaw = TerryTransform.Wrap(turned.Rotation[1] - moved.Rotation[1]);
                var orbit = Math.Atan2(turned.Position[2] - p2.Z, turned.Position[0] - p2.X) - Math.Atan2(moved.Position[2] - p2.Z, moved.Position[0] - p2.X);
                Step("3d gizmo rotate", Math.Abs(Math.Abs(dYaw) - 90) < 2, $"yaw changed by {dYaw:F1}°; pivot {p2}; position {moved.Position[0]:F3},{moved.Position[2]:F3} → {turned.Position[0]:F3},{turned.Position[2]:F3}");
                _view3d.Mode = Viewport3D.Viewport3DControl.GizmoMode.Move;

                // Prefab contents in 3D (sandbox kits only: make_prefab writes into the kit's prefab folder).
                if (!_paths.AssemblyKitRoot.Equals(new AtlasWH3.Core.ProjectPaths().AssemblyKitRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var c0 = showcase.Entity.Transform!.Value.Position;
                    var group = withModel.Where(i => i.Entity.Transform is var (q, _, _) && Math.Abs(q[0] - c0[0]) < 3 && Math.Abs(q[2] - c0[2]) < 3)
                        .Select(i => i.Entity.Id).Take(8).ToList();
                    var made = Run("make prefab", new JsonObject
                    {
                        ["op"] = "make_prefab", ["ids"] = new JsonArray(group.Select(g => (JsonNode)g).ToArray()),
                        ["key"] = $"selftest_3d_{DateTime.Now:HHmmss}", ["folder"] = "selftest",
                    });
                    var instance = made?[0]?["instance"]?["id"]?.ToString();
                    if (instance is null) Step("3d prefab", false, "make_prefab failed: " + _status.Text);
                    else
                    {
                        SetSelection([instance], SceneView.SelectMode.Replace);
                        _view3d.FrameSelection();
                        for (var wait = 0; wait < 200 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                        await Task.Delay(300);
                        Save3D(outDir, "13_3d_prefab");
                        var parts = _view3d.PartCount(instance);
                        var pickedPrefab = _view3d.ScreenOf(_view3d.CentreOf(instance)!.Value) is { } pc ? _view3d.PickAt(pc) : null;
                        var placedAt = _model.Find(instance)!.Entity.Transform!.Value.Position;
                        var at = _view3d.ScreenOf((_view3d.Pivot ?? _view3d.Camera.Target))!.Value;
                        _view3d.SimulateGizmoDrag(Viewport3D.Viewport3DControl.GizmoMode.Move, "xz", at, new Point(at.X + 80, at.Y + 20));
                        var movedTo = _model.Find(instance)!.Entity.Transform!.Value.Position;
                        await Task.Delay(300);
                        Save3D(outDir, "14_3d_prefab_moved");
                        Step("3d prefab", parts == group.Count && pickedPrefab == instance && (movedTo[0] != placedAt[0] || movedTo[2] != placedAt[2]),
                            $"{group.Count} props → instance {instance}: {parts} parts drawn, pick → {pickedPrefab}, moved {placedAt[0]:F2},{placedAt[2]:F2} → {movedTo[0]:F2},{movedTo[2]:F2}");
                    }
                }

                }
                catch (Exception editEx)
                {
                    // Picking/gizmo/prefab steps lean on the project's layout (a showcase model in view); a failure
                    // there is reported, and the read-only world, tile and season steps below still run.
                    Step("3d edit steps", false, editEx.GetType().Name + ": " + editEx.Message);
                }

                _view3d.FrameAll();
                for (var wait = 0; wait < 100 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                for (var wait = 0; wait < 200 && !_view3d.TerrainReady; wait++) await Task.Delay(100);
                await Task.Delay(500);
                Step("3d terrain", _view3d.TerrainReady, _model.TerrainBlend is var (g, ar) ? $"ground: {ar.Groups.Count} texture groups on {g.Width}x{g.Height}" : "no blend map");
                Save3D(outDir, "11_3d_overview");
                var frames = new List<double>();
                for (var k = 0; k < 5; k++) { _view3d.Snapshot(1280, 720); frames.Add(_view3d.LastFrameMs); }
                Step("3d frame time", true, $"overview frame (CPU build + submit): {frames.Skip(1).Average():F1} ms; {_view3d.FrameBreakdown}");
                for (var wait = 0; wait < 100 && !_view3d.RiversReady; wait++) await Task.Delay(100);
                var scenes = _model.All.Where(i => i.Entity.Type == "CompositeScene").Select(i => i.Entity.Component("ECCompositeScene")?["path"]).Distinct().ToList();
                var scenesWithModels = scenes.Count(p => p is not null && _model.CompositeModels(p).Count > 0);
                Step("3d campaign world", _model.Terrain is null || (_model.Trees.Count > 0 && _model.SeaHeight is not null && _view3d.RiversReady),
                    $"{_model.TreesNote}; sea map {(_model.SeaHeight is { } sh ? $"{sh.Width}x{sh.Height}" : "none")}; rivers ready {_view3d.RiversReady}; "
                    + $"composite scenes with models {scenesWithModels}/{scenes.Count}");
                Save3D(outDir, "11_3d_overview");
                async Task CloseUp(System.Numerics.Vector3 at, float distance, string name, float pitch = 0.55f)
                {
                    _view3d.Camera.Target = at;
                    _view3d.Camera.Distance = distance;
                    _view3d.Camera.Pitch = pitch;
                    _view3d.Camera.Yaw = pitch > 1.4f ? 0 : 0.4f;
                    _view3d.Invalidate();
                    for (var wait = 0; wait < 200 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                    await Task.Delay(300);
                    Save3D(outDir, name);
                }
                if (_model.All.FirstOrDefault(i => i.Entity.Component("ECCompositeScene")?["path"]?.Contains("cow") == true)?.Entity.Transform is var (cp, _, _))
                    await CloseUp(new System.Numerics.Vector3((float)cp[0], (float)cp[1], (float)cp[2]), 2.5f, "16_3d_scene");
                if (_model.All.FirstOrDefault(i => i.Entity.Type == "River") is { } riverItem && riverItem.Entity.Outlines.FirstOrDefault() is { Points.Count: > 10 } ro)
                {
                    var mid = ro.Points[ro.Points.Count / 2];
                    var (rx, rz) = riverItem.Entity.ToWorld(mid.X, mid.Z);
                    await CloseUp(new System.Numerics.Vector3((float)rx, (float)_model.GroundY(rx, rz), (float)rz), 12, "17_3d_river");
                }
                // Tile overlay: placements from tile_list.bin, readable at a feature tile, drawn in 2D and 3D.
                if (_model.Tiles.Count > 0)
                {
                    _model.TilesShown = SceneModel.TileOverlay.Features;
                    var feature = _model.Tiles.First(t => !t.Base && t.TileSet.StartsWith("road", StringComparison.OrdinalIgnoreCase));
                    var (_, _, tcx, tcz) = _model.TileGrid;
                    double fx = (feature.X + 0.5) * tcx, fz = (feature.Y + 0.5) * tcz;
                    var read = _model.TileAt(fx, fz);
                    _view3d.Camera.Target = new System.Numerics.Vector3((float)fx, (float)_model.GroundY(fx, fz), (float)fz);
                    _view3d.Camera.Distance = 30;
                    _view3d.Camera.Pitch = 0.9f;
                    _view3d.Invalidate();
                    for (var wait = 0; wait < 300 && (!_view3d.TilesReady || _view3d.Snapshot(8, 8) is null); wait++) await Task.Delay(100);
                    await Task.Delay(500);
                    Save3D(outDir, "18_3d_tiles");
                    _centre.SelectedIndex = 0;
                    _view.FitTo(fx - 15, fz - 9, fx + 15, fz + 9);
                    await Shot(outDir, "19_2d_tiles");
                    _centre.SelectedIndex = 1;
                    Step("tile overlay", read is not null && !read.Base,
                        $"{_model.TilesNote}; at a road tile: {read?.Name} ({read?.TileSet}, {read?.Degrees}°)");
                    _model.TilesShown = SceneModel.TileOverlay.Off;

                    // Tile meshes: the placed tiles' custom meshes through the tile transform, at three tiles that
                    // have one: from above with the feature overlay (footprint check), without the meshes, and oblique.
                    for (var wait = 0; wait < 300 && !_view3d.TileMeshesReady; wait++) await Task.Delay(100);
                    var shots = 0;
                    _view3d.ShowTrees = false; // the map forest hides the tiles; their own props stay
                    foreach (var (kind, tag) in new[] { ("blockout_cliff", "20"), ("mountains_subtropical/6x4", "21"), ("mountains_temperate", "22"), ("roads_imperial", "23"), ("river_crossing/", "24"), ("river_crossing_imperial", "25") })
                    {
                        var ofKind = _model.Tiles.Where(t => t.Path.Replace('\\', '/').Contains(kind, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (ofKind.Count == 0) continue;
                        var tm = ofKind[ofKind.Count / 2];
                        double mx = (tm.X + tm.W / 2.0) * tcx, mz = (tm.Y + tm.H / 2.0) * tcz;
                        var at = new System.Numerics.Vector3((float)mx, (float)_model.GroundY(mx, mz), (float)mz);
                        var name = $"{tag}_3d_tile_{tm.Name}_{tm.Degrees}";
                        _model.TilesShown = SceneModel.TileOverlay.Features;
                        _view3d.Invalidate();
                        for (var wait = 0; wait < 300 && !_view3d.TilesReady; wait++) await Task.Delay(100);
                        await CloseUp(at, Math.Max(tm.W, tm.H) * (float)tcx * 6, name + "_top", 1.5f);
                        _view3d.ShowTileMeshes = false;
                        Save3D(outDir, name + "_top_nomesh");
                        _view3d.ShowTileMeshes = true;
                        _model.TilesShown = SceneModel.TileOverlay.Off;
                        await CloseUp(at, Math.Max(tm.W, tm.H) * (float)tcx * 5, name);
                        shots++;
                    }
                    _view3d.ShowTrees = true;
                    // A mountain range: the highest lf area, from a campaign-like angle.
                    if (_model.Terrain is var (hr, hw, hh))
                    {
                        int best = 0;
                        for (var i = 0; i < hr.Data.Length; i += 97) if (hr.Data[i] > hr.Data[best]) best = i;
                        double mx = (best % hr.Width + 0.5) * hw / hr.Width, mz = hh - (best / hr.Width + 0.5) * hh / hr.Height;
                        var at = new System.Numerics.Vector3((float)mx, (float)_model.GroundY(mx, mz), (float)mz);
                        await CloseUp(at, 30, "26_3d_mountains", 0.75f);
                        await CloseUp(at, 10, "27_3d_mountains_close", 0.6f);

                        // Region mask + cities (from the kit's map.hex), in 3D and 2D.
                        _model.ShowRegions = true;
                        _view3d.Invalidate();
                        for (var wait = 0; wait < 600 && !_view3d.RegionsReady; wait++) { _view3d.Invalidate(); await Task.Delay(100); }
                        _view3d.FrameAll();
                        await Task.Delay(500);
                        Save3D(outDir, "31_3d_regions");
                        var cityList = _model.RegionsIfBuilt?.Cities ?? [];
                        if (cityList.Count > 0)
                        {
                            var city = cityList[cityList.Count / 2];
                            await CloseUp(new System.Numerics.Vector3((float)city.X, (float)_model.GroundY(city.X, city.Z), (float)city.Z), 12, "32_3d_region_city", 0.9f);
                            _centre.SelectedIndex = 0;
                            _view.FitTo(city.X - 12, city.Z - 8, city.X + 12, city.Z + 8);
                            _view.Refresh();
                            await Shot(outDir, "33_2d_regions");
                            _centre.SelectedIndex = 1;
                            var regionThere = _model.RegionAt(city.X, city.Z);
                            Step("region mask", _model.RegionsIfBuilt is not null && regionThere == city.Region,
                                $"{_model.RegionsNote}; city of {city.Region} reads back as {regionThere}");
                        }
                        else Step("region mask", _model.RegionsIfBuilt is null, _model.RegionsNote ?? "no region mask");
                        _model.ShowRegions = false;

                        // LF-offset mountains (stored y = offset above the terrain, draped per vertex on the GPU): the
                        // 190E rock beside a cold ridge if present, else any mountain prop.
                        var drapeAt = _model.Find("0ea79483739c373")?.Entity.Transform?.Position
                            ?? _model.All.FirstOrDefault(i => SceneModel.ModelPathOf(i.Entity)?.Contains("/mountains/") == true)?.Entity.Transform?.Position;
                        if (drapeAt is { } dp)
                        {
                            await CloseUp(new System.Numerics.Vector3((float)dp[0], (float)_model.GroundY(dp[0], dp[2]), (float)dp[2]), 6, "34_3d_mountain_drape", 0.45f);
                            Step("mountain drape", true, $"close-up at {dp[0]:F2},{dp[2]:F2}");
                        }
                    }
                    Step("tile meshes", _view3d.TileMeshesReady && _view3d.TileMeshInstances > 0,
                        $"{_view3d.TileMeshInstances} tile meshes and props of {_view3d.TileMeshModels} models, plus tile rivers; {_view3d.TileGroundTiles} tiles with their own ground textures; {shots} close-ups");
                }
                else Step("tile overlay", _model.Terrain is null, _model.TilesNote);

                if (_model.Trees.Count > 0)
                {
                    // A forest close-up: the densest 4×4 unit patch near the middle of the tree list.
                    var mid = _model.Trees[_model.Trees.Count / 2];
                    _view3d.Camera.Target = new System.Numerics.Vector3(mid.X, mid.Y, mid.Z);
                    _view3d.Camera.Distance = 25;
                    _view3d.Camera.Pitch = 0.6f;
                    _view3d.Camera.Yaw = 0.3f;
                    _view3d.Invalidate();
                    for (var wait = 0; wait < 200 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                    await Task.Delay(300);
                    Save3D(outDir, "15_3d_forest");
                }
                // A closer oblique view of the selection's surroundings.
                _view3d.Camera.Target = (_view3d.Pivot ?? _view3d.Camera.Target);
                _view3d.Camera.Distance = 6;
                _view3d.Camera.Pitch = 0.5f;
                _view3d.Camera.Yaw = 0.6f;
                _view3d.Invalidate();
                for (var wait = 0; wait < 200 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                await Task.Delay(300);
                Save3D(outDir, "12_3d_close");
            }
            _centre.SelectedIndex = 0;

            var edits = _model.Editor.History().Count;
            for (var i = 0; i < edits; i++) _model.Undo();
            var after = Hashes(dir);
            {
                var prefabAfter = Directory.Exists(prefabRoot) ? HashTree(prefabRoot) : [];
                var prefabChanged = prefabBefore.Where(kv => prefabAfter.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key)
                    .Concat(prefabAfter.Keys.Except(prefabBefore.Keys)).ToList();
                Step("undo restores prefab library", prefabChanged.Count == 0, prefabChanged.Count == 0 ? $"{prefabBefore.Count} files identical" : string.Join(", ", prefabChanged));
            }
            var changed = before.Where(kv => after.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key)
                .Concat(after.Keys.Except(before.Keys)).ToList();
            Step("undo all", changed.Count == 0 && _model.Editor.History().Count == 0,
                changed.Count == 0 ? $"{edits} edits undone, {before.Count} files identical" : "changed: " + string.Join(", ", changed));
            await Shot(outDir, "06_undone");
        }
        catch (Exception ex)
        {
            Step("exception", false, ex.ToString());
        }
        report["passed"] = steps.All(s => s!["ok"]!.GetValue<bool>());
        await File.WriteAllTextAsync(Path.Combine(outDir, "selftest.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Read-only inspection: waits for the scene, then for every row of a CSV with id,x,z columns (and an optional
    /// footprint_radius) saves a 3D close-up &lt;id&gt;.png in <paramref name="outDir"/>, and exits. Nothing is edited.
    /// </summary>
    public async Task ShotsAsync(string csvPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            _centre.SelectedIndex = 1;
            for (var wait = 0; wait < 900 && (_model.Terrain is null || _view3d.Snapshot(8, 8) is null || !_view3d.TerrainReady); wait++) await Task.Delay(100);
            for (var wait = 0; wait < 300 && !_view3d.TileMeshesReady; wait++) { _view3d.Invalidate(); await Task.Delay(100); }
            var lines = File.ReadAllLines(csvPath);
            var head = lines[0].Split(',').ToList();
            int ci = head.IndexOf("id"), cx = head.IndexOf("x"), cz = head.IndexOf("z"), cr = head.IndexOf("footprint_radius");
            _model.ShowHidden = true; // findings include hidden layers
            _view3d.Refresh();
            foreach (var line in lines.Skip(1))
            {
                var f = line.Split(',');
                if (File.Exists(Path.Combine(outDir, f[ci] + ".png"))) continue; // resumable
                double x = double.Parse(f[cx], System.Globalization.CultureInfo.InvariantCulture), z = double.Parse(f[cz], System.Globalization.CultureInfo.InvariantCulture);
                var r = cr >= 0 ? float.Parse(f[cr], System.Globalization.CultureInfo.InvariantCulture) : 3f;
                // Mark the finding: select its entity (yellow box) when the id is one.
                if (_model.Find(f[ci]) is not null) { _model.ShowHidden = true; SetSelection([f[ci]], SceneView.SelectMode.Replace); }
                else SetSelection([], SceneView.SelectMode.Replace);
                _view3d.Camera.Target = new System.Numerics.Vector3((float)x, (float)_model.GroundY(x, z), (float)z);
                _view3d.Camera.Distance = Math.Max(1.5f, r * 2.2f);
                _view3d.Camera.Pitch = 0.3f;
                _view3d.Camera.Yaw = 0.5f;
                _view3d.Invalidate();
                for (var wait = 0; wait < 40 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                await Task.Delay(200);
                Save3D(outDir, f[ci]);
            }
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(outDir, "error.txt"), ex.ToString()); }
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Read-only tours for map review, saving one 3D image per item into <paramref name="outDir"/> and exiting:
    /// "city" frames every region's city (its slot-0 hexes) close; "region" frames every land region's hex bounding
    /// box obliquely and from above. Regions overlay and labels on, hidden layers drawn. <paramref name="prefix"/>
    /// limits the regions (e.g. "ironic_"). Writes index.csv (file, region, x, z).
    /// </summary>
    public async Task TourAsync(string kind, string outDir, string? prefix)
    {
        Directory.CreateDirectory(outDir);
        var index = new List<string> { "file,region,x,z" };
        try
        {
            _centre.SelectedIndex = 1;
            for (var wait = 0; wait < 900 && (_model.Terrain is null || _view3d.Snapshot(8, 8) is null || !_view3d.TerrainReady); wait++) await Task.Delay(100);
            _model.ShowHidden = true;
            _model.ShowRegions = true;
            _view3d.Refresh();
            for (var wait = 0; wait < 600 && !(_view3d.TileMeshesReady && _view3d.RegionsReady && _model.RegionsIfBuilt is not null); wait++) { _view3d.Invalidate(); await Task.Delay(100); }
            if (_model.RegionsIfBuilt is not { } map) throw new InvalidOperationException("no region mask: " + _model.RegionsNote);
            async Task Frame(System.Numerics.Vector3 at, float distance, float pitch, float yaw, string file)
            {
                _view3d.Camera.Target = at;
                _view3d.Camera.Distance = distance;
                _view3d.Camera.Pitch = pitch;
                _view3d.Camera.Yaw = yaw;
                _view3d.Invalidate();
                // Short wait: models load in the first frames; ModelsSettled can stay false on an odd model.
                for (var wait = 0; wait < 40 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
                await Task.Delay(150);
                Save3D(outDir, file);
            }
            static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            if (kind == "city")
            {
                foreach (var c in map.Cities.Where(c => prefix is null || c.Region.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var at = new System.Numerics.Vector3((float)c.X, (float)_model.GroundY(c.X, c.Z), (float)c.Z);
                    var file = "city_" + Safe(c.Region);
                    await Frame(at, 5f, 0.65f, 0.4f, file);
                    index.Add($"{file}.png,{c.Region},{c.X:F2},{c.Z:F2}");
                }
            }
            else
            {
                // Region hex bounding boxes from map.hex.
                var hex = map.Lookup.Hex;
                var boxes = new Dictionary<int, (float X0, float Z0, float X1, float Z1)>();
                for (var r = 0; r < hex.Height; r++)
                    for (var col = 0; col < hex.Width; col++)
                    {
                        var i = hex.RegionIndexAt(col, r);
                        if (i < 0 || i >= hex.LandRegions.Count) continue;
                        var (x, z) = map.Lookup.HexCentre(col, r);
                        boxes[i] = boxes.TryGetValue(i, out var b) ? (Math.Min(b.X0, x), Math.Min(b.Z0, z), Math.Max(b.X1, x), Math.Max(b.Z1, z)) : (x, z, x, z);
                    }
                foreach (var (i, b) in boxes.OrderBy(kv => hex.LandRegions[kv.Key], StringComparer.Ordinal))
                {
                    var name = hex.LandRegions[i];
                    if (prefix is not null && !name.StartsWith(prefix, StringComparison.Ordinal) || name.Contains("non_playable")) continue;
                    float cx = (b.X0 + b.X1) / 2, cz = (b.Z0 + b.Z1) / 2, size = Math.Max(b.X1 - b.X0, b.Z1 - b.Z0);
                    var at = new System.Numerics.Vector3(cx, (float)_model.GroundY(cx, cz), cz);
                    var file = "region_" + Safe(name);
                    await Frame(at, Math.Max(3f, size * 0.9f), 0.75f, 0.6f, file);
                    await Frame(at, Math.Max(3f, size * 1.1f), 1.5f, 0f, file + "_top");
                    index.Add($"{file}.png,{name},{cx:F2},{cz:F2}");
                }
            }
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(outDir, "error.txt"), ex.ToString()); }
        await File.WriteAllLinesAsync(Path.Combine(outDir, "index.csv"), index);
        Application.Current.Shutdown();
    }

    private void Save3D(string dir, string name)
    {
        const int w = 1280, h = 720;
        if (_view3d.Snapshot(w, h) is not { } bgra) return;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(f);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref System.Drawing.Point p);

    private static Dictionary<string, string> HashTree(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(f))));

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(f))));

    private async Task Shot(string dir, string name)
    {
        // Let the view render the latest state first.
        _view.Invalidate();
        await Task.Delay(400);
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        await using var f = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(f);
    }
}
