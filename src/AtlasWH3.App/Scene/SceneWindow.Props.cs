using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using Microsoft.Win32;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Scene editor props tools (<see cref="PropToolsPanel"/>): placing new props from the asset browser by clicking in
/// the 2D or 3D view, and clamping props onto the ground. Every placement and every clamp is one entity-editor batch
/// (written at once, one Ctrl+Z).
/// </summary>
public sealed partial class SceneWindow
{
    private PropToolsPanel? _propTools;
    private PropToolsPanel PropTools => _propTools ??= new PropToolsPanel();
    private PlacementTool? _placementTool;
    private SceneView.ITool? _toolBeforePlacing;
    private readonly Random _rng = new();

    // the built ground (scene height; full_logic_map) per loaded model, built in the background on first use
    private SceneModel? _groundFor;
    private readonly Dictionary<PropToolsPanel.GroundSource, Task<(GroundHeight? Ground, string Why)>> _builtGround = [];

    private Dictionary<string, double> _sinkLearnt = [];
    private Dictionary<string, double>? _sinkLoaded;
    private string? _sinkLoadedFrom;
    private readonly Dictionary<string, (double MinY, double Offset)> _facts = new(StringComparer.OrdinalIgnoreCase);
    private int _previewSeq;

    private void WirePropTools()
    {
        var p = PropTools;
        p.ModelSelected += m => _ = ShowModelPreview(m);
        p.PlacingChanged += SetPlacing;
        p.ClampRequested += scope => _ = ClampAsync(scope);
        p.FindFloatingRequested += () => _ = FindFloatingAsync();
        p.LoadSinkRequested += LoadSinkTable;
        p.GroundSourceChanged += () => { _sinkLearnt = []; _ = UpdateGroundNote(); };
        _view3d.PlaceAt = null;
        PreviewKeyDown += (_, e) =>
        {
            var typing = Keyboard.FocusedElement is TextBox or ComboBox;
            if (e.Key == Key.Escape && PropTools.Placing && !typing) { PropTools.StopPlacing(); e.Handled = true; }
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control && !typing) { _ = ClampAsync("selected"); e.Handled = true; }
        };
        _view3d.KeyCommand += (key, mods) =>
        {
            if (key == Key.Escape) PropTools.StopPlacing();
            else if (key == Key.G && mods == ModifierKeys.Control) _ = ClampAsync("selected");
        };
    }

    /// <summary>After a (re)load: the model list for the browser, and the ground source note.</summary>
    private void AttachPropTools(SceneModel model)
    {
        if (!ReferenceEquals(_groundFor, model))
        {
            _groundFor = model;
            _builtGround.Clear();
            _facts.Clear();
            PropTools.StopPlacing();
            _ = Task.Run(() => PropPlacement.BrowsableModels(model.Models.Source.Enumerate(PropPlacement.ModelExtensions), all: true))
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && ReferenceEquals(model, _model)) PropTools.SetModels(t.Result);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        _sinkLearnt = [];
        _ = UpdateGroundNote();
    }

    private void ShowPropsTab()
    {
        if (_rightTabs is null) return;
        foreach (TabItem tab in _rightTabs.Items)
            if (ReferenceEquals(tab.Content, PropTools)) _rightTabs.SelectedItem = tab;
    }

    // ---------------------------------------------------------------- asset browser

    private async Task ShowModelPreview(string? path)
    {
        var seq = ++_previewSeq;
        if (path is null) { PropTools.SetPreview(null, ""); return; }
        PropTools.SetPreview(null, "Loading " + Path.GetFileName(path) + " …");
        var models = _model.Models;
        var (image, info) = await Task.Run(() =>
        {
            var (m, error) = models.TryLoad(path);
            if (m is null) return (null, "Cannot load: " + (error ?? "not found"));
            var thumb = ModelThumbnail.Render(m);
            var off = m.Lods.SelectMany(l => l).Select(x => x.TerrainOffset).DefaultIfEmpty(0).Max();
            lock (_facts) _facts[path] = (m.Bounds.Length >= 6 ? m.Bounds[1] : 0, off);
            var text = $"{ModelThumbnail.SizeText(m.Bounds)}\n{m.Triangles():N0} triangles, {m.Lods.Count} LODs, from {models.Source.Locate(path) ?? "?"}"
                       + (off > 0 ? "\nLF-offset mountain: y is stored relative to the terrain." : "")
                       + (m.Problems.Count > 0 ? "\n⚠ " + string.Join("; ", m.Problems.Take(2)) : "");
            return ((System.Windows.Media.ImageSource?)thumb, text);
        });
        if (seq == _previewSeq) PropTools.SetPreview(image, info);
    }

    // ---------------------------------------------------------------- placing

    private void SetPlacing(bool on)
    {
        if (on)
        {
            if (_placementTool is null) _placementTool = new PlacementTool(this);
            if (!ReferenceEquals(_view.Tool, _placementTool)) _toolBeforePlacing = _view.Tool;
            _view.Tool = _placementTool;
            _view3d.PlaceAt = g => _ = PlaceAsync(g.X, g.Z);
            Status($"Placing {Path.GetFileName(PropTools.SelectedModel)}: click on the terrain in the 2D or 3D view (Esc ends).");
        }
        else
        {
            if (ReferenceEquals(_view.Tool, _placementTool)) _view.Tool = _toolBeforePlacing;
            _toolBeforePlacing = null;
            _view3d.PlaceAt = null;
            Status("Placement ended.");
        }
    }

    /// <summary>The 2D view's placement tool: a left click places, no drag painting.</summary>
    private sealed class PlacementTool(SceneWindow owner) : SceneView.ITool
    {
        public void Begin(double x, double z) => _ = owner.PlaceAsync(x, z);
        public void Drag(double x, double z) { }
        public void End() { }
        public double CursorRadius => 0.3;
        public void DrawOverlay(uint[] buffer, int w, int h, double ox, double oz, double scale) { }
    }

    private bool _placingBusy;

    private async Task PlaceAsync(double x, double z)
    {
        var model = PropTools.SelectedModel;
        if (model is null || _placingBusy) return;
        var node = _layerNode;
        var fileLayer = node?.FileLayer ?? _model.ActiveLayer ?? _model.Layers.FirstOrDefault()?.Id;
        if (fileLayer is null) { Status("No layer to place into; create a file layer first.", error: true); return; }
        _placingBusy = true;
        try
        {
            var ground = await GroundAsync();
            var facts = await Task.Run(() => FactsOf(model));
            var yaw = PropTools.RandomYaw ? _rng.NextDouble() * 360 - 180 : PropTools.Yaw;
            var jitter = PropTools.ScaleJitterPercent / 100;
            var scale = Math.Round(PropTools.Scale * (1 + (_rng.NextDouble() * 2 - 1) * jitter), 4);
            var probe = new ClampProp("", model, x, 0, z, scale, facts.MinY, facts.Offset);
            var (y, _) = GroundClamp.Target(probe, ground.At, PropTools.SeatBase ? ClampMode.Base : ClampMode.Origin, PropTools.YOffset);
            var op = PropPlacement.CreateOp(model, fileLayer, Math.Round(x, 4), Math.Round(y, 4), Math.Round(z, 4), yaw, scale,
                node is { Kind: "layer", Id: { } parent } ? parent : null);
            if (Run($"place {Path.GetFileNameWithoutExtension(model)}", op)?.FirstOrDefault()?["created"] is JsonArray created)
            {
                SetSelection(created.Select(c => c!["id"]!.ToString()).ToList(), SceneView.SelectMode.Replace);
                Status($"Placed {Path.GetFileName(model)} at {x:F2}, {y:F3}, {z:F2} (ground: {GroundName(ground)}). "
                       + (PropTools.Repeat ? "Click again to place another; Esc ends." : ""));
            }
            if (!PropTools.Repeat) PropTools.StopPlacing();
        }
        finally { _placingBusy = false; }
    }

    // ---------------------------------------------------------------- ground

    /// <summary>The ground the Props tab asks for: the scene height or the bare full_logic_map (both fall back to the kit lf
    /// when there is no matching build), or the kit lf.</summary>
    private async Task<GroundHeight> GroundAsync()
    {
        var model = _model;
        var kit = GroundHeight.FromFunc("kit lf map", model.GroundY);
        if (PropTools.Ground == PropToolsPanel.GroundSource.KitLf) return kit;
        var (built, _) = await BuiltGroundAsync(model, PropTools.Ground);
        return built ?? kit;
    }

    private Task<(GroundHeight? Ground, string Why)> BuiltGroundAsync(SceneModel model, PropToolsPanel.GroundSource source)
    {
        if (ReferenceEquals(_groundFor, model) && _builtGround.TryGetValue(source, out var cached)) return cached;
        var paths = _paths with { MapName = model.Project.MapName };
        var task = Task.Run(() =>
        {
            var (w, h) = model.Terrain is var (_, tw, th) ? (tw, th) : (0d, 0d);
            Func<double, double, double>? reference = model.Terrain is null ? null : model.GroundY;
            string why;
            var g = source == PropToolsPanel.GroundSource.Scene
                ? GroundHeight.Scene(paths, paths.MapName, out why, reference, w, h)
                : GroundHeight.Built(paths, paths.MapName, out why, reference, w, h);
            return (g, why);
        });
        if (ReferenceEquals(_groundFor, model)) _builtGround[source] = task;
        return task;
    }

    private static string GroundName(GroundHeight g) =>
        !g.IsBuilt ? "kit lf" : g.Description.StartsWith("scene", StringComparison.Ordinal) ? "scene height" : "full_logic_map";

    private async Task UpdateGroundNote()
    {
        var model = _model;
        if (PropTools.Ground == PropToolsPanel.GroundSource.KitLf)
        {
            PropTools.SetGroundNote(model.Terrain is null ? "This project has no lf height map." : "Kit lf height map: current edits, no tile detail (rivers, roads, mountain tiles).");
            return;
        }
        PropTools.SetGroundNote("Loading the built terrain (full_logic_map, height patches) …");
        var (g, why) = await BuiltGroundAsync(model, PropTools.Ground);
        if (!ReferenceEquals(model, _model)) return;
        PropTools.SetGroundNote(g is not null
            ? "Using " + g.Description + (why.Length > 0 ? "; " + why : "") + ". Rebuild the map after height edits to update it."
            : why + ". Using the kit lf map instead.");
    }

    /// <summary>Bounds min y and the LF-offset share of a model (0, 0 when it cannot be loaded).</summary>
    private (double MinY, double Offset) FactsOf(string path)
    {
        lock (_facts)
            if (_facts.TryGetValue(path, out var f)) return f;
        (double, double) result = (0, 0);
        try
        {
            if (_model.Models.Load(path) is { } m)
                result = (m.Bounds.Length >= 6 ? m.Bounds[1] : 0, m.Lods.SelectMany(l => l).Select(x => x.TerrainOffset).DefaultIfEmpty(0).Max());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
        lock (_facts) _facts[path] = result;
        return result;
    }

    // ---------------------------------------------------------------- clamping

    /// <summary>Props of a scope: selected / active layer / in view. Groups' members, prefab instances and non-props are left out.</summary>
    private List<SceneModel.Item> PropsIn(string scope)
    {
        bool IsProp(SceneModel.Item i) => i.Entity.Group is null && i.Entity.Component("ECPropMesh") is not null
                                          && SceneModel.ModelPathOf(i.Entity) is not null && i.Entity.Transform is not null;
        return scope switch
        {
            "selected" => _selection.Select(_model.Find).OfType<SceneModel.Item>().Where(IsProp).ToList(),
            "layer" => _model.ActiveLayer is { } active
                ? _model.All.Where(i => i.Layer.Id == active && IsProp(i) && !_model.IsHidden(i) && !_model.IsFrozen(i)).ToList()
                : [],
            "all" => _model.All.Where(IsProp).ToList(),
            _ => _model.All.Where(i => IsProp(i) && !_model.IsHidden(i) && !_model.IsFrozen(i) && InView(i)).ToList(),
        };
    }

    private bool InView(SceneModel.Item item)
    {
        if (item.Entity.Transform is not var (p, _, _)) return false;
        if (Is3D)
        {
            float w = _view3d.ViewportWidth, h = _view3d.ViewportHeight;
            var pt = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            if (Vector3.Distance(pt, _view3d.Camera.Eye) > _view3d.Camera.Far) return false;
            return _view3d.Camera.ToScreen(pt, w, h) is { } s && s.X >= 0 && s.Y >= 0 && s.X <= w && s.Y <= h;
        }
        var (x0, z1) = _view.ToWorld(new Point(0, 0));
        var (x1, z0) = _view.ToWorld(new Point(_view.ActualWidth, _view.ActualHeight));
        return p[0] >= Math.Min(x0, x1) && p[0] <= Math.Max(x0, x1) && p[2] >= Math.Min(z0, z1) && p[2] <= Math.Max(z0, z1);
    }

    private async Task<List<ClampProp>> ClampPropsAsync(IReadOnlyList<SceneModel.Item> items) => await Task.Run(() =>
        items.Select(i =>
        {
            var (p, _, s) = i.Entity.Transform!.Value;
            var path = SceneModel.ModelPathOf(i.Entity)!;
            var (minY, off) = FactsOf(path);
            return new ClampProp(i.Entity.Id, path, p[0], p[1], p[2], s[1], minY, off);
        }).ToList());

    private bool _clampBusy;

    private async Task ClampAsync(string scope)
    {
        if (_clampBusy) return;
        var items = PropsIn(scope);
        var what = scope switch { "selected" => "selected props", "layer" => "props in the active layer", _ => "props in view" };
        if (items.Count == 0)
        {
            Status(scope == "layer" && _model.ActiveLayer is null ? "No active layer." : $"No {what} to clamp (groups, prefab instances and non-props are skipped).");
            return;
        }
        _clampBusy = true;
        try
        {
            Status($"Clamping {items.Count} {what} …");
            var ground = await GroundAsync();
            var props = await ClampPropsAsync(items);
            var mode = PropTools.Mode;
            var sink = mode == ClampMode.VanillaSink ? await SinkTableAsync(ground) : null;
            var (offset, onlyDown) = (PropTools.ClampOffset, PropTools.OnlyDown);
            var moves = await Task.Run(() => GroundClamp.Plan(props, ground.For, mode, offset, sink, onlyDown));
            if (moves.Count == 0) { Status($"All {items.Count} {what} already sit on the ground ({GroundName(ground)})."); return; }
            if (moves.Count > 200 && MessageBox.Show(this, $"Move {moves.Count} of {items.Count} {what} onto the ground ({GroundName(ground)})?\nOne Ctrl+Z undoes it.",
                    "Clamp to ground", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var ops = GroundClamp.Ops(moves, id => _model.Find(id)?.Entity.Component("ECTransform")?["position"]);
            if (Run($"clamp {moves.Count} to ground", ops) is not null)
            {
                var sunk = moves.Count(m => m.How == "sink");
                Status($"Clamped {moves.Count} of {items.Count} {what} to the ground ({GroundName(ground)}): "
                       + $"{moves.Count(m => m.Delta < 0)} lowered, {moves.Count(m => m.Delta > 0)} raised, largest move {moves.Max(m => Math.Abs(m.Delta)):0.###}"
                       + (sunk > 0 ? $", {sunk} by vanilla sink" : "") + ". Ctrl+Z undoes it.");
            }
        }
        catch (Exception ex) { Status("Clamp failed: " + ex.Message, error: true); }
        finally { _clampBusy = false; }
    }

    private async Task FindFloatingAsync()
    {
        var items = PropsIn("all").Where(i => !_model.IsFrozen(i) && _model.IsDrawn(i)).ToList();
        Status($"Checking {items.Count} props …");
        var ground = await GroundAsync();
        var props = await ClampPropsAsync(items);
        var threshold = PropTools.FloatThreshold;
        var skipSettlements = PropTools.SkipSettlements;
        var floating = await Task.Run(() => GroundClamp.Floating(props.Where(p => !skipSettlements || !IsSettlementPiece(p.Model)), ground.For, threshold));
        SetSelection(floating.Select(f => f.Id).ToList(), SceneView.SelectMode.Replace);
        Status(floating.Count == 0
            ? $"No prop's base is more than {threshold:0.###} above the ground ({GroundName(ground)}, {items.Count} props checked)."
            : $"Selected {floating.Count} floating props (base > {threshold:0.###} above the ground, {GroundName(ground)}). "
              + "Note: props standing on other props or mountain tiles can be meant to float. Ctrl+G clamps them.");
        if (floating.Count > 0) { if (Is3D) _view3d.FrameSelection(); else _view.FrameSelection(); }
    }

    /// <summary>The sink table for Vanilla sink: a loaded file, else learnt from this map's mountains and rocks.</summary>
    private async Task<IReadOnlyDictionary<string, double>> SinkTableAsync(GroundHeight ground)
    {
        if (_sinkLoaded is not null) return _sinkLoaded;
        if (_sinkLearnt.Count == 0)
        {
            var items = PropsIn("all").Where(i => GroundClamp.IsMountainOrRock(SceneModel.ModelPathOf(i.Entity)!)).ToList();
            var props = await ClampPropsAsync(items);
            _sinkLearnt = await Task.Run(() => GroundClamp.LearnSink(props, ground.For));
            PropTools.SetSinkNote($"Sink depths learnt from this map: {_sinkLearnt.Count} mountain / rock models.");
        }
        return _sinkLearnt;
    }

    /// <summary>Settlement models: they stand on platforms, walls and other pieces, so "floating" says little about them.</summary>
    private static bool IsSettlementPiece(string model) => GroundClamp.Key(model).Contains("/settlements/");

    private void LoadSinkTable()
    {
        var dialog = new OpenFileDialog { Title = "Load a sink table", Filter = "Sink table (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _sinkLoaded = GroundClamp.ReadSinkTable(dialog.FileName);
            _sinkLoadedFrom = dialog.FileName;
            PropTools.SetSinkNote($"Sink depths from {Path.GetFileName(_sinkLoadedFrom)}: {_sinkLoaded.Count} models.");
        }
        catch (Exception ex) { Status("Cannot read the sink table: " + ex.Message, error: true); }
    }
}
