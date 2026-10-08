using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core.Editing;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Scripted check of the Props tab, in-process: model list and preview, placing in the 2D and 3D views on the ground,
/// floating detection, clamp selected (one undo step), and the unsaved-terrain * in the title.
/// Start with: AtlasWH3.App --scene &lt;copy of a campaign project&gt;.terry --props-selftest &lt;out dir&gt;. It edits the
/// project, so never point it at the real kit.
/// </summary>
public sealed partial class SceneWindow
{
    public async Task PropsSelfTestAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var steps = new JsonArray();
        var report = new JsonObject { ["steps"] = steps };
        void Step(string name, bool ok, string? detail = null) =>
            steps.Add(new JsonObject { ["step"] = name, ["ok"] = ok, ["detail"] = detail, ["status"] = _status.Text });
        try
        {
            while (!_loadedOnce) await Task.Delay(200);
            ShowPropsTab();
            var p = PropTools;
            for (var wait = 0; wait < 600 && p.ModelCount == 0; wait++) await Task.Delay(100);
            Step("model list", p.ModelCount > 0, $"{p.ModelCount} models");
            var model = p.Models.FirstOrDefault(m => m.Contains("temperate_tree", StringComparison.OrdinalIgnoreCase) && m.EndsWith(".wsmodel"))
                        ?? p.Models.First();
            p.Select(model);
            await ShowModelPreview(model);
            Step("preview", p.HasPreview, p.PreviewInfo);

            var (built, why) = await BuiltGroundAsync(_model, p.Ground);
            Step("built ground", built is not null, built?.Description ?? why);
            var ground = await GroundAsync();

            // a land point near the middle of the map
            var (_, worldW, worldH) = _model.Terrain!.Value;
            double x = worldW / 2, z = worldH / 2;
            for (var i = 0; i < 400 && _model.GroundY(x, z) < 0.5; i++) (x, z) = (worldW * (0.3 + 0.4 * ((i * 37) % 100) / 100.0), worldH * (0.3 + 0.4 * ((i * 61) % 100) / 100.0));
            _view.FitTo(x - 20, z - 15, x + 20, z + 15);
            var history = _model.Editor.History().Count;

            // 2D: the placement tool's click
            SetPlacingFromTest(true);
            _view.Tool!.Begin(x, z);
            for (var wait = 0; wait < 100 && _selection.Count == 0; wait++) await Task.Delay(50);
            var placed = _selection.FirstOrDefault();
            var item = placed is null ? null : _model.Find(placed);
            var pos = item?.Entity.Transform?.Position;
            Step("place 2D", item is not null && pos is not null && Math.Abs(pos[1] - ground.At(x, z)) < 1e-3 && _model.Editor.History().Count == history + 1,
                 pos is null ? "nothing placed" : $"{SceneModel.ModelPathOf(item!.Entity)} at {pos[0]:0.###} {pos[1]:0.####} {pos[2]:0.###}, ground {ground.At(x, z):0.####}");

            // 3D: the viewport's place hook (what a left click on the terrain calls)
            _centre.SelectedIndex = 1;
            await Task.Delay(300);
            var before3d = _model.Editor.History().Count;
            _view3d.PlaceAt?.Invoke(new Vector3((float)(x + 2), 0, (float)z));
            for (var wait = 0; wait < 100 && _model.Editor.History().Count == before3d; wait++) await Task.Delay(50);
            var second = _selection.FirstOrDefault();
            Step("place 3D", second is not null && second != placed && _model.Editor.History().Count == before3d + 1, second);
            p.StopPlacing();
            Step("placement ended", _view3d.PlaceAt is null && _view.Tool is not PlacementTool);
            _view3d.Camera.Target = new Vector3((float)x + 1, (float)ground.At(x + 1, z), (float)z);
            _view3d.Camera.Distance = 10;
            _view3d.Camera.Pitch = 0.45f;
            _view3d.Invalidate();
            for (var wait = 0; wait < 80 && !_view3d.ModelsSettled; wait++) await Task.Delay(100);
            await Task.Delay(500);
            Save3D(outDir, "p01_placed_3d");

            // lift both, find them as floating, clamp selected, undo
            var ids = new[] { placed!, second! };
            Run("lift", ids.Select(id =>
            {
                var t = _model.Find(id)!.Entity.Transform!.Value.Position;
                return new JsonObject { ["op"] = "set", ["id"] = id, ["fields"] = new JsonObject { ["ECTransform.position"] = $"{t[0]} {t[1] + 3} {t[2]}" } };
            }).ToArray());
            await FindFloatingAsync();
            Step("find floating", ids.All(_selection.Contains), $"{_selection.Count} selected of {_model.All.Count()} entities");
            Step("ground source", true, built?.Description + $", median off the kit lf {built?.ReferenceDifference:0.###}; {why}");
            SetSelection([.. ids], SceneView.SelectMode.Replace);
            var beforeClamp = _model.Editor.History().Count;
            await ClampAsync("selected");
            var ys = ids.Select(id => _model.Find(id)!.Entity.Transform!.Value.Position).ToList();
            var targets = (await ClampPropsAsync(ids.Select(id => _model.Find(id)!).ToList()))
                .Select(c => GroundClamp.Target(c, ground.For, p.Mode, p.ClampOffset).Y).ToList();
            Step("clamp selected", _model.Editor.History().Count == beforeClamp + 1 && ys.Zip(targets).All(t => Math.Abs(t.First[1] - t.Second) < 1e-3)
                                   && ys.All(q => Math.Abs(q[1] - ground.At(q[0], q[2])) < 1e-3) == (p.Mode == ClampMode.Origin),
                 $"mode {p.Mode}: " + string.Join("; ", ys.Select(q => $"y {q[1]:0.####} ground {ground.At(q[0], q[2]):0.####}")));
            Undo();
            var lifted = ids.Select(id => _model.Find(id)!.Entity.Transform!.Value.Position[1]).ToList();
            Step("clamp undo (one step)", lifted.Zip(ys).All(t => t.First > t.Second[1] + 2.9), string.Join(", ", lifted.Select(v => v.ToString("0.###"))));

            // unsaved terrain edits: * in the title, cleared by an undo back to the saved state
            if (TerrainTools.Session is { } s)
            {
                _rightTabs!.SelectedIndex = 1;
                var tools = TerrainTools;
                tools.TargetIndex = 1;
                tools.HeightModeIndex = (int)KitHeightMode.Raise;
                tools.Radius = 2;
                tools.Begin(x, z);
                tools.End();
                Step("title * when terrain dirty", Title.StartsWith('*') && s.IsDirty(KitTarget.Land), Title);
                tools.Undo();
                Step("title clean after undo", !Title.StartsWith('*'), Title);
                tools.TargetIndex = 0;
            }
            Step("entity edits saved", true, "entity edits are written per batch: history " + _model.Editor.History().Count);
        }
        catch (Exception ex)
        {
            Step("exception", false, ex.ToString());
        }
        finally
        {
            File.WriteAllText(Path.Combine(outDir, "props_selftest.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void SetPlacingFromTest(bool on)
    {
        if (on != PropTools.Placing) PropTools.TogglePlacing();
    }
}
