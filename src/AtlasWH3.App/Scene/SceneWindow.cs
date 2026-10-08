using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Terry-style scene editor for any .terry project (campaign map, battle prefab, ...): layer tree on the left, a
/// top-down scene view in the middle, the schema-driven inspector on the right. Every edit is an entity op batch
/// through <see cref="SceneModel"/> / <see cref="EntityEditor"/>, so it is validated, saved at once, and undoable
/// (Ctrl+Z) in the same journal the entity-* CLI and the terry MCP tools use. F5 re-reads files changed by those tools.
/// </summary>
public sealed partial class SceneWindow : Window
{
    private readonly ProjectPaths _paths;
    private SceneModel _model;
    private readonly HashSet<string> _selection = [];
    private LayerTreePanel.Node? _layerNode;

    private readonly SceneView _view = new();
    private readonly Viewport3D.Viewport3DControl _view3d = new();
    private readonly TabControl _centre = new() { Background = Theme.Brush("Bg"), BorderThickness = new Thickness(0) };
    private bool Is3D => _centre.SelectedIndex == 1;
    private readonly LayerTreePanel _tree = new();
    private readonly InspectorPanel _inspector = new();
    private readonly TextBlock _status = new() { FontFamily = new FontFamily("Consolas"), Text = "Loading..." };
    private readonly TextBlock _hover = new() { FontFamily = new FontFamily("Consolas"), Foreground = Theme.Brush("DimText") };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 4, 0) };
    private readonly ListBox _results = new() { Height = 170, Background = Brushes.Transparent, Foreground = Theme.Brush("Text"), Visibility = Visibility.Collapsed };
    private readonly TextBlock _resultInfo = new() { Foreground = Theme.Brush("DimText"), FontSize = 11, Visibility = Visibility.Collapsed };
    private List<string> _resultIds = [];
    private bool _loadedOnce;

    public SceneWindow(ProjectPaths paths, string? terryPath = null)
    {
        _paths = paths;
        _model = new SceneModel(paths, terryPath);
        Width = Math.Min(1700, SystemParameters.WorkArea.Width);
        Height = Math.Min(1000, SystemParameters.WorkArea.Height);
        WindowState = WindowState.Maximized;
        Background = Theme.Brush("Bg");
        Foreground = Theme.Brush("Text");
        Content = BuildLayout();
        Placement.Track(this, "scene");
        Walkthrough.Enable(this, "scene");
        Wire();
        Loaded += async (_, _) => await LoadAsync();
    }

    // ---------------------------------------------------------------- layout

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();
        var menu = new Menu().Spot("scene.menu");
        menu.Items.Add(MenuOf("_File",
            ("_Open Terry project…", (Action)OpenProject, "Ctrl+O"),
            ("Open _campaign map", () => _ = Switch(null), ""),
            ("_Save terrain and tree edits", SaveAll, "Ctrl+S"),
            ("_Reload from disk", () => _ = LoadAsync(), "F5"),
            (null, null, null),
            ("_Close", Close, "")).Spot("scene.file"));
        menu.Items.Add(MenuOf("_Edit",
            ("_Undo last edit", Undo, "Ctrl+Z"),
            ("_Checkpoint…", Checkpoint, ""),
            ("_Roll back to checkpoint…", Rollback, ""),
            ("_History…", ShowHistory, ""),
            (null, null, null),
            ("_Duplicate selection", Duplicate, "Ctrl+D"),
            ("_Expand selected prefabs", ExpandSelection, "Ctrl+E"),
            ("_Make prefab from selection…", MakePrefab, ""),
            ("De_lete selection", DeleteSelection, "Del"),
            ("Select _none", () => SetSelection([], SceneView.SelectMode.Replace), "Esc"),
            (null, null, null),
            ("Clamp selected to _ground", () => _ = ClampAsync("selected"), "Ctrl+G"),
            ("Clamp all in active _layer", () => _ = ClampAsync("layer"), ""),
            ("Clamp all in _view", () => _ = ClampAsync("view"), ""),
            ("Find _floating props", () => _ = FindFloatingAsync(), "")).Spot("scene.edit"));
        menu.Items.Add(MenuOf("_Create",
            ("_Entity…", () => AddEntity(null), "Ctrl+N"),
            ("_Prefab…", () => PlacePrefab(null), "Ctrl+P"),
            ("Prop from _asset browser…", ShowPropsTab, ""),
            ("New _file layer…", NewFileLayer, "")).Spot("scene.create"));
        menu.Items.Add(MenuOf("_View",
            ("_Fit all", () => { if (Is3D) _view3d.FrameAll(); else _view.FitToContent(); }, "Home"),
            ("Frame _selection", () => { if (Is3D) _view3d.FrameSelection(); else _view.FrameSelection(); }, "F"),
            ("_Top view (2D)", () => _centre.SelectedIndex = 0, "Ctrl+1"),
            ("_3D view", () => _centre.SelectedIndex = 1, "Ctrl+2")).Spot("scene.viewMenu"));
        StandardMenus.AddTo(menu, this, _paths);
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        var statusBar = new DockPanel { Background = Theme.Brush("Panel") }.Spot("scene.status");
        DockPanel.SetDock(_hover, Dock.Right);
        _hover.Margin = new Thickness(6, 3, 6, 3);
        _status.Margin = new Thickness(6, 3, 6, 3);
        statusBar.Children.Add(_hover);
        statusBar.Children.Add(_status);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });

        var left = new DockPanel { Background = Theme.Brush("Panel") };
        var searchRow = new DockPanel { Margin = new Thickness(6) }.Spot("scene.search");
        var find = new Button { Content = "Find", Padding = new Thickness(8, 1, 8, 1) };
        find.Click += (_, _) => RunSearch();
        DockPanel.SetDock(find, Dock.Right);
        searchRow.Children.Add(find);
        searchRow.Children.Add(_search);
        _search.ToolTip = "Free text (name, asset, id), type:Prop, layer:name, or field filters such as\n"
                          + "ECMesh.model_path~metasequoia   ECPointLight.radius=0.5   ECBuilding   (Enter to search)";
        var resultsPanel = new StackPanel { Margin = new Thickness(6, 0, 6, 4) };
        var selectAll = new Button { Content = "Select all results", Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
        selectAll.Click += (_, _) => { SetSelection(_resultIds, SceneView.SelectMode.Replace); _view.FrameSelection(); };
        selectAll.SetBinding(VisibilityProperty, new System.Windows.Data.Binding("Visibility") { Source = _resultInfo });
        resultsPanel.Children.Add(_resultInfo);
        resultsPanel.Children.Add(selectAll);
        resultsPanel.Children.Add(_results);
        DockPanel.SetDock(searchRow, Dock.Top);
        DockPanel.SetDock(resultsPanel, Dock.Top);
        left.Children.Add(searchRow);
        left.Children.Add(resultsPanel);
        left.Children.Add(_tree.Spot("scene.layers"));
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        grid.Children.Add(Splitter(1));
        _centre.Items.Add(new TabItem { Header = "Top (2D)", Content = _view.Spot("scene.map2d") });
        _centre.Items.Add(new TabItem { Header = "3D", Content = Build3DPanel() }.Spot("scene.tab3d"));
        _centre.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _centre)) return;
            if (Is3D) { _view3d.Selection = new HashSet<string>(_selection); _view3d.Invalidate(); }
        };
        var centrePanel = new DockPanel();
        var shared = SharedBar().Spot("scene.sharedBar");
        DockPanel.SetDock(shared, Dock.Top);
        centrePanel.Children.Add(shared);
        centrePanel.Children.Add(_centre);
        Grid.SetColumn(centrePanel, 2);
        grid.Children.Add(centrePanel);
        grid.Children.Add(Splitter(3));
        var right = new Border { Background = Theme.Brush("Panel"), Child = RightPanel(_inspector) };
        Grid.SetColumn(right, 4);
        grid.Children.Add(right);
        dock.Children.Add(grid);
        return dock;
    }

    /// <summary>Season preview and tile overlay, for both views.</summary>
    private UIElement SharedBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Background = Theme.Brush("Panel") };
        bar.Children.Add(new TextBlock { Text = "Season", Foreground = Theme.Brush("DimText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        var season = new ComboBox { Margin = new Thickness(2), MinWidth = 90, ToolTip = "Season preview: props whose season mask excludes it are hidden; trees and tile props use its models; ground textures and snow (3D) follow it" };
        season.Items.Add("All seasons");
        foreach (var name in SceneModel.Seasons) season.Items.Add(name["season_".Length..]);
        season.SelectedIndex = 2; // summer
        season.SelectionChanged += (_, _) =>
        {
            _model.Season = season.SelectedIndex <= 0 ? "" : SceneModel.Seasons[season.SelectedIndex - 1];
            _view.Refresh();
            _view3d.Refresh();
            _ = _model.LoadModelBoundsAsync();
        };
        bar.Children.Add(season);
        var showHidden = new CheckBox
        {
            Content = "Show hidden layers", Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0),
            ToolTip = "Also draw layers hidden in the project's .terry.user (they still export to the game). View only: the saved visibility is not changed.",
        };
        showHidden.Click += (_, _) =>
        {
            _model.ShowHidden = showHidden.IsChecked == true;
            _view.Refresh();
            _view3d.Refresh();
        };
        bar.Children.Add(BarSeparator());
        bar.Children.Add(showHidden);
        var regions = new CheckBox
        {
            Content = "Regions", Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0),
            ToolTip = "Region mask from the kit's map.hex (a colour per land region, dark borders) and each region's city (its main settlement slot); hover to read the region",
        };
        regions.Click += (_, _) =>
        {
            _model.ShowRegions = regions.IsChecked == true;
            _view.Refresh();
            _view3d.Invalidate();
        };
        bar.Children.Add(regions);
        bar.Children.Add(BarSeparator());
        bar.Children.Add(new TextBlock { Text = "Tiles", Foreground = Theme.Brush("DimText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
        var tiles = new ComboBox { Margin = new Thickness(2), MinWidth = 120, ToolTip = "Overlay the placed tiles from the map's compiled tile_list.bin, coloured by tile set; hover to read a tile" };
        tiles.Items.Add("Off");
        tiles.Items.Add("Features");
        tiles.Items.Add("All (incl. base)");
        tiles.SelectedIndex = 0;
        tiles.SelectionChanged += (_, _) =>
        {
            _model.TilesShown = (SceneModel.TileOverlay)tiles.SelectedIndex;
            _view.Invalidate();
            _view3d.Invalidate();
            Status(_model.TilesNote ?? "");
        };
        bar.Children.Add(tiles);
        var legend = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var (label, set) in new[] { ("road", "roads"), ("imperial road", "roads_imperial"), ("river", "river"), ("crossing", "river_crossing"),
                     ("canal", "canals"), ("coast", "sea_coast"), ("mountains", "mountains_temperate"), ("cliff", "blockout_cliff"), ("lake", "lakes") })
        {
            var c = SceneModel.TileColour(set);
            legend.Children.Add(new Border { Width = 10, Height = 10, Margin = new Thickness(6, 0, 3, 0), Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)) });
            legend.Children.Add(new TextBlock { Text = label, Foreground = Theme.Brush("DimText"), FontSize = 11 });
        }
        bar.Children.Add(legend);
        return bar;
    }

    private UIElement Build3DPanel()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Background = Theme.Brush("Panel") };
        var modes = new List<System.Windows.Controls.Primitives.ToggleButton>();
        void Mode(string label, Viewport3D.Viewport3DControl.GizmoMode mode, string tip)
        {
            var b = new System.Windows.Controls.Primitives.ToggleButton { Content = label, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2), ToolTip = tip, IsChecked = mode == _view3d.Mode };
            b.Click += (_, _) =>
            {
                _view3d.Mode = mode;
                foreach (var o in modes) o.IsChecked = ReferenceEquals(o, b);
                _view3d.Invalidate();
            };
            modes.Add(b);
            bar.Children.Add(b);
        }
        Mode("Move (W)", Viewport3D.Viewport3DControl.GizmoMode.Move, "Drag an axis arrow, or the square for the ground plane; Ctrl snaps to 0.5");
        Mode("Rotate (E)", Viewport3D.Viewport3DControl.GizmoMode.Rotate, "Drag the ring to turn about the vertical axis; Ctrl snaps to 15°");
        Mode("Scale (R)", Viewport3D.Viewport3DControl.GizmoMode.Scale, "Drag the box up/down; Ctrl snaps to 0.1");
        bar.Children.Add(BarSeparator());
        var trees = new CheckBox { Content = "Trees", IsChecked = true, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        trees.Click += (_, _) => { _view3d.ShowTrees = trees.IsChecked == true; _view3d.Invalidate(); };
        bar.Children.Add(trees);
        var water = new CheckBox { Content = "Water", IsChecked = true, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0) };
        water.Click += (_, _) => { _view3d.ShowWater = water.IsChecked == true; _view3d.Invalidate(); };
        bar.Children.Add(water);
        var tileMeshes = new CheckBox { Content = "Tile meshes", IsChecked = true, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0),
            ToolTip = "The placed tiles' own content: cliff meshes, the props of their bmd (mountains, their trees, bridges, road props) and river water" };
        tileMeshes.Click += (_, _) => { _view3d.ShowTileMeshes = tileMeshes.IsChecked == true; _view3d.Invalidate(); };
        bar.Children.Add(tileMeshes);
        bar.Children.Add(BarSeparator());
        var frame = new Button { Content = "Frame (F)", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2) };
        frame.Click += (_, _) => _view3d.FrameSelection();
        bar.Children.Add(frame);
        var all = new Button { Content = "All (Home)", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(2) };
        all.Click += (_, _) => _view3d.FrameAll();
        bar.Children.Add(all);
        var help = Theme.Icon(Theme.Glyph.Info, 14, Theme.Brush("DimText"));
        help.Margin = new Thickness(10, 0, 6, 0);
        help.ToolTip = "Mouse: right-drag orbit (+ WASD/QE fly, Shift faster) · middle-drag pan · wheel zoom · click pick · drag box-select\n" +
                       "Keys: W move, E rotate, R scale, F frame selection, Home frame all";
        bar.Children.Add(help);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_view3d);
        return dock;
    }

    private static Border BarSeparator() => new()
    {
        Width = 1, Margin = new Thickness(8, 4, 6, 4), Background = Theme.Brush("BorderBrush"),
    };

    private static GridSplitter Splitter(int column)
    {
        var s = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("Bg") };
        Grid.SetColumn(s, column);
        return s;
    }

    private static MenuItem MenuOf(string header, params (string? Header, Action? Action, string? Gesture)[] items)
    {
        var m = new MenuItem { Header = header };
        foreach (var (h, a, g) in items)
        {
            if (h is null) { m.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = h, InputGestureText = g ?? "" };
            mi.Click += (_, _) => a!();
            m.Items.Add(mi);
        }
        return m;
    }

    private void Wire()
    {
        _view.SelectionRequested += SetSelection;
        _view3d.SelectionRequested += SetSelection;
        _view3d.TransformsCommitted += CommitTransforms;
        _view3d.HoverText += t => _hover.Text = t;
        _view3d.RegionsChanged += () =>
        {
            _view.Refresh();
            if (_model.ShowRegions && _model.RegionsNote is { } note) _status.Text = "Regions: " + note;
        };
        _view3d.KeyCommand += (key, mods) =>
        {
            var ctrl = mods.HasFlag(ModifierKeys.Control);
            switch (key)
            {
                case Key.Z when ctrl: Undo(); break;
                case Key.D when ctrl: Duplicate(); break;
                case Key.N when ctrl: AddEntity(null); break;
                case Key.P when ctrl: PlacePrefab(null); break;
                case Key.E when ctrl: ExpandSelection(); break;
                case Key.Delete: DeleteSelection(); break;
                case Key.Escape: SetSelection([], SceneView.SelectMode.Replace); break;
                case Key.F5: _ = LoadAsync(); break;
            }
        };
        _view.MoveRequested += MoveSelection;
        _view.HoverChanged += p => _hover.Text = p is var (x, z) ? $"x {x:F2}  z {z:F2}" + _view3d.HoverTileText(x, z) : "";
        _tree.EntitySelected += id => { SetSelection([id], SceneView.SelectMode.Replace, fromTree: true); };
        _tree.LayerSelected += n => _layerNode = n;
        _tree.StateToggled += (n, what, value) => Run($"{what} {value}", new JsonObject { ["op"] = "layer_state", ["id"] = n.TargetId, [what] = value });
        _tree.Command += TreeCommand;
        _tree.DropRequested += DropOnLayer;
        _tree.BulkVisibility += SetLayersVisible;
        _inspector.FieldEdited += FieldEdited;
        _inspector.NameEdited += name => Run("rename", new JsonObject { ["op"] = "set", ["ids"] = Ids(_selection), ["name"] = name });
        _inspector.ComponentAdded += c => Run($"add {c}", new JsonObject { ["op"] = "add_component", ["ids"] = Ids(_selection), ["component"] = c });
        _inspector.ComponentRemoved += c => Run($"remove {c}", new JsonObject { ["op"] = "remove_component", ["ids"] = Ids(_selection), ["component"] = c });
        _inspector.PrefabOpenRequested += OpenPrefab;
        _inspector.PrefabExpandRequested += ExpandSelection;
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) RunSearch(); };
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedIndex >= 0 && _results.SelectedIndex < _resultIds.Count)
            {
                SetSelection([_resultIds[_results.SelectedIndex]], SceneView.SelectMode.Replace);
                _view.FrameSelection();
            }
        };
        WireTerrainTools();
        WirePropTools();
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox or ComboBox;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Z when ctrl && !typing: Undo(); break;
            case Key.D when ctrl && !typing: Duplicate(); break;
            case Key.N when ctrl: AddEntity(null); break;
            case Key.P when ctrl: PlacePrefab(null); break;
            case Key.E when ctrl && !typing: ExpandSelection(); break;
            case Key.O when ctrl: OpenProject(); break;
            case Key.S when ctrl: SaveAll(); break;
            case Key.F5: _ = LoadAsync(); break;
            case Key.Delete when !typing: DeleteSelection(); break;
            case Key.Escape when !typing: SetSelection([], SceneView.SelectMode.Replace); break;
            case Key.F when !typing && !ctrl: if (Is3D) _view3d.FrameSelection(); else _view.FrameSelection(); break;
            case Key.Home when !typing: if (Is3D) _view3d.FrameAll(); else _view.FitToContent(); break;
            case Key.D1 when ctrl: _centre.SelectedIndex = 0; break;
            case Key.D2 when ctrl: _centre.SelectedIndex = 1; break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- loading

    private async Task LoadAsync()
    {
        _status.Text = "Loading " + Path.GetFileName(_model.TerryPath) + " …";
        try
        {
            var model = _model;
            await Task.Run(model.Load);
            model.Changed -= ModelChanged;
            model.Changed += ModelChanged;
            model.ModelBoundsReady += () => Dispatcher.BeginInvoke(() => { _view.Refresh(); _view3d.Refresh(); Status($"Model footprints loaded ({model.ModelBounds.Count} models)."); });
            _ = model.LoadModelBoundsAsync();
            _view.Attach(model);
            _view3d.Attach(model);
            TerrainTools.Attach(model);
            AttachPropTools(model);
            _tree.Attach(model);
            _selection.RemoveWhere(id => model.Find(id) is null);
            ShowSelection();
            Status($"{model.Layers.Count} layers, {model.All.Count()} entities. Edits save immediately; Ctrl+Z undoes. History: {model.Editor.HistoryDir}");
            _loadedOnce = true;
            UpdateTitle();
        }
        catch (Exception ex)
        {
            Status("Load failed: " + ex.Message, error: true);
        }
    }

    private async Task Switch(string? terryPath)
    {
        if (!TerrainTools.ConfirmDiscard(this)) return;
        _model.Changed -= ModelChanged;
        _model = new SceneModel(_paths, terryPath);
        _selection.Clear();
        await LoadAsync();
    }

    private void OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a Terry project", Filter = "Terry project (*.terry)|*.terry",
            InitialDirectory = Path.Combine(_paths.AssemblyKitRoot, "raw_data"),
        };
        if (dialog.ShowDialog(this) == true) _ = Switch(dialog.FileName);
    }

    private void ModelChanged()
    {
        _ = _model.LoadModelBoundsAsync(); // models added by the edit
        _selection.RemoveWhere(id => _model.Find(id) is null);
        _tree.Rebuild();
        _view.Invalidate();
        _view3d.Refresh();
        ShowSelection();
    }

    // ---------------------------------------------------------------- selection

    private void SetSelection(IReadOnlyList<string> ids, SceneView.SelectMode mode) => SetSelection(ids, mode, fromTree: false);

    private void SetSelection(IReadOnlyList<string> ids, SceneView.SelectMode mode, bool fromTree)
    {
        if (mode == SceneView.SelectMode.Replace) _selection.Clear();
        foreach (var id in ids)
        {
            if (mode == SceneView.SelectMode.Toggle && !_selection.Add(id)) _selection.Remove(id);
            else _selection.Add(id);
        }
        ShowSelection();
        if (!fromTree && _selection.Count == 1) _tree.Reveal(_selection.First());
    }

    private void ShowSelection()
    {
        _view.Selection = new HashSet<string>(_selection);
        _view.Invalidate();
        _view3d.Selection = new HashSet<string>(_selection);
        _view3d.Invalidate();
        var items = _selection.Select(_model.Find).Where(i => i is not null).Cast<SceneModel.Item>().ToList();
        _inspector.Show(_model, items);
    }

    private static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(i => (JsonNode)i).ToArray());

    // ---------------------------------------------------------------- edits

    private JsonArray? Run(string label, params JsonObject[] ops)
    {
        if (ops.Length == 0) return null;
        try
        {
            var results = _model.Apply(new JsonArray(ops), label);
            var warnings = results.OfType<JsonObject>().SelectMany(r => r["warnings"] as JsonArray ?? []).Select(w => w!.ToString()).ToList();
            Status($"{label}: saved (edit {_model.Editor.History().LastOrDefault()?.Seq})" + (warnings.Count > 0 ? "  ⚠ " + string.Join("; ", warnings) : ""),
                warning: warnings.Count > 0);
            return results;
        }
        catch (Exception ex)
        {
            Status(ex.Message, error: true);
            ShowSelection(); // put the old values back in the inspector
            return null;
        }
    }

    private void FieldEdited(InspectorPanel.FieldEdit edit)
    {
        // Each entity gets its own new value (a vector edit keeps the other components); equal values share an op.
        var ops = _selection.Select(id => (id, current: _model.Find(id)?.Entity.Component(edit.Component)?[edit.Field] ?? ""))
            .GroupBy(x => edit.NewValue(x.current))
            .Select(g => new JsonObject
            {
                ["op"] = "set", ["ids"] = Ids(g.Select(x => x.id)),
                ["fields"] = new JsonObject { [$"{edit.Component}.{edit.Field}"] = g.Key },
            }).ToArray();
        Run($"set {edit.Component}.{edit.Field}", ops);
    }

    private void MoveSelection(double dx, double dz)
    {
        var ops = new List<JsonObject>();
        foreach (var id in _selection)
            if (_model.Find(id)?.Entity.Transform is var (p, _, _))
                ops.Add(new JsonObject
                {
                    ["op"] = "set", ["id"] = id,
                    ["fields"] = new JsonObject { ["ECTransform.position"] = $"{F(p[0] + dx)} {F(p[1])} {F(p[2] + dz)}" },
                });
        Run($"move {ops.Count}", [.. ops]);
    }

    private void Duplicate()
    {
        if (_selection.Count == 0) return;
        var offset = Math.Max(0.5, 15 * ViewScale());
        var results = Run($"duplicate {_selection.Count}", new JsonObject
        {
            ["op"] = "duplicate", ["ids"] = Ids(_selection), ["by"] = new JsonArray(offset, 0, -offset),
        });
        if (results?.FirstOrDefault()?["entities"] is JsonArray copies)
            SetSelection(copies.Select(c => c!["id"]!.ToString()).ToList(), SceneView.SelectMode.Replace);
    }

    private double ViewScale()
    {
        var a = _view.ToWorld(new Point(0, 0));
        var b = _view.ToWorld(new Point(1, 0));
        return Math.Abs(b.X - a.X);
    }

    private void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        if (_selection.Count > 20 && MessageBox.Show(this, $"Delete {_selection.Count} entities?", "Delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Run($"delete {_selection.Count}", new JsonObject { ["op"] = "delete", ["ids"] = Ids(_selection) });
    }

    private void AddEntity(LayerTreePanel.Node? at)
    {
        var project = _model.Project;
        var allowed = _model.Config.Types.Where(t => t.AllowedIn(project.ProjectType, project.Database))
            .Select(t => t.Type).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var type = Prompt.Choose(this, "Add entity", $"Entity type ({project.Database} {project.ProjectType}):", allowed, "Prop");
        if (string.IsNullOrWhiteSpace(type)) return;
        var node = at ?? _layerNode;
        var fileLayer = node?.FileLayer ?? _model.ActiveLayer ?? _model.Layers.FirstOrDefault()?.Id;
        if (fileLayer is null) { Status("No layer to add to; create a file layer first.", error: true); return; }
        var (x, z) = CreationPoint();
        var op = new JsonObject
        {
            ["op"] = "create", ["type"] = type, ["layer"] = fileLayer,
            ["position"] = new JsonArray(x, GroundY(x, z), z),
        };
        if (node is { Kind: "layer", Id: { } parent }) op["parent"] = parent;
        if (Run($"create {type}", op)?.FirstOrDefault()?["created"] is JsonArray created)
            SetSelection(created.Select(c => c!["id"]!.ToString()).ToList(), SceneView.SelectMode.Replace);
    }

    // ---------------------------------------------------------------- prefabs

    private void PlacePrefab(LayerTreePanel.Node? at)
    {
        var key = PrefabPicker.Show(this, _model);
        if (key is null) return;
        var node = at ?? _layerNode;
        var fileLayer = node?.FileLayer ?? _model.ActiveLayer ?? _model.Layers.FirstOrDefault()?.Id;
        if (fileLayer is null) { Status("No layer to place into; create a file layer first.", error: true); return; }
        var (x, z) = CreationPoint();
        var op = new JsonObject
        {
            ["op"] = "place_prefab", ["key"] = key, ["layer"] = fileLayer, ["position"] = new JsonArray(x, GroundY(x, z), z),
        };
        if (node is { Kind: "layer", Id: { } parent }) op["parent"] = parent;
        if (Run($"place prefab {key}", op)?.FirstOrDefault()?["created"] is JsonArray created)
            SetSelection(created.Select(c => c!["id"]!.ToString()).ToList(), SceneView.SelectMode.Replace);
    }

    private void ExpandSelection()
    {
        var instances = _selection.Where(id => _model.Find(id)?.Entity.Component("ECPrefab") is not null).ToList();
        if (instances.Count == 0) { Status("Select prefab instances to expand."); return; }
        var results = Run($"expand {instances.Count} prefab(s)", new JsonObject { ["op"] = "expand_prefab", ["ids"] = Ids(instances) });
        if (results?.FirstOrDefault()?["entities"] is JsonArray made)
            SetSelection(made.Select(c => c!["id"]!.ToString()).ToList(), SceneView.SelectMode.Replace);
    }

    private void MakePrefab()
    {
        if (_selection.Count == 0) { Status("Select the entities to turn into a prefab."); return; }
        var lib = _model.Prefabs;
        var name = Prompt.Text(this, "Make prefab",
            $"New prefab name (optionally folder/name), saved under\n{lib.Root}\nThe selection is replaced by an instance of it.");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        var op = new JsonObject { ["op"] = "make_prefab", ["ids"] = Ids(_selection), ["key"] = slash >= 0 ? name[(slash + 1)..] : name };
        if (slash > 0) op["folder"] = name[..slash];
        if (Run($"make prefab {name}", op)?.FirstOrDefault()?["instance"]?["id"]?.ToString() is { } instance)
            SetSelection([instance], SceneView.SelectMode.Replace);
    }

    private void OpenPrefab(string key)
    {
        if (_model.Prefabs.PathOf(key) is { } path) new SceneWindow(_paths, path).Show();
        else Status($"Prefab '{key}' is not in {_model.Prefabs.Root}.", error: true);
    }

    /// <summary>Where new entities go: the 3D camera's target, or the centre of the top view.</summary>
    private (double X, double Z) CreationPoint() => Is3D ? (_view3d.Camera.Target.X, _view3d.Camera.Target.Z) : _view.Centre;

    /// <summary>Gizmo drags: one batch setting each entity's transform.</summary>
    private void CommitTransforms(IReadOnlyDictionary<string, TerryTransform> changed, string label)
    {
        var ops = changed.Select(kv => new JsonObject
        {
            ["op"] = "set", ["id"] = kv.Key,
            ["fields"] = new JsonObject
            {
                ["ECTransform.position"] = string.Join(' ', kv.Value.Position.Select(F)),
                ["ECTransform.rotation"] = string.Join(' ', kv.Value.Rotation.Select(F)),
                ["ECTransform.scale"] = string.Join(' ', kv.Value.Scale.Select(F)),
            },
        }).ToArray();
        Run(label, ops);
    }

    private double GroundY(double x, double z)
    {
        if (_model.Terrain is not var (h, worldW, worldH)) return 0;
        var col = Math.Clamp((int)(x / worldW * h.Width), 0, h.Width - 1);
        var row = Math.Clamp((int)((1 - z / worldH) * h.Height), 0, h.Height - 1);
        return Math.Round(h[col, row] * CameraHeightmapStep.HeightStep + CameraHeightmapStep.HeightOffset, 4);
    }

    private void NewFileLayer()
    {
        var name = Prompt.Text(this, "New file layer", "Layer name:");
        if (!string.IsNullOrWhiteSpace(name)) Run($"create layer {name}", new JsonObject { ["op"] = "create_layer", ["name"] = name.Trim() });
    }

    private void TreeCommand(LayerTreePanel.Node node, string command)
    {
        var item = node.Id is null ? null : _model.Find(node.Id);
        switch (command)
        {
            case "new-folder" or "new-tag-layer":
                var name = Prompt.Text(this, command == "new-folder" ? "New folder layer" : "New tag layer",
                    command == "new-folder" ? "Name:" : "Meta tags (comma separated) — also the layer's name:");
                if (string.IsNullOrWhiteSpace(name)) return;
                var op = new JsonObject { ["op"] = "create_layer", ["name"] = name.Trim(), ["layer"] = node.FileLayer };
                if (command == "new-tag-layer") op["tags"] = name.Trim();
                if (node.Kind == "layer") op["parent"] = node.Id;
                Run($"create layer {name}", op);
                break;
            case "add-entity":
                AddEntity(node);
                break;
            case "place-prefab":
                PlacePrefab(node);
                break;
            case "expand-prefab":
                SetSelection([node.TargetId], SceneView.SelectMode.Replace);
                ExpandSelection();
                break;
            case "open-prefab":
                if (item?.Entity.Component("ECPrefab")?["key"] is { } key) OpenPrefab(key);
                break;
            case "rename":
                var current = node.Kind == "file" ? _model.Layers.FirstOrDefault(l => l.Id == node.FileLayer)?.Name : item?.Entity.Name;
                var newName = Prompt.Text(this, "Rename", "Name:", current ?? "");
                if (newName is not null) Run("rename", new JsonObject { ["op"] = "rename", ["id"] = node.TargetId, ["name"] = newName.Trim() });
                break;
            case "set-active":
                Run("set active layer", new JsonObject { ["op"] = "layer_state", ["id"] = node.FileLayer, ["active"] = true });
                break;
            case "toggle-export":
                var layer = _model.Layers.First(l => l.Id == node.FileLayer);
                Run("toggle export", new JsonObject { ["op"] = "layer_state", ["id"] = layer.Id, ["export"] = !layer.Export });
                break;
            case "select-all":
                SetSelection(MembersDeep(node).ToList(), SceneView.SelectMode.Replace);
                _view.FrameSelection();
                break;
            case "delete" when node.Kind == "file":
                var l = _model.Layers.First(x => x.Id == node.FileLayer);
                if (MessageBox.Show(this, $"Delete layer '{l.Name}' and its .layer file? (Ctrl+Z restores it)", "Delete layer", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    Run($"delete layer {l.Name}", new JsonObject { ["op"] = "delete_layer", ["id"] = l.Id });
                break;
            case "delete" or "delete-with-members":
                Run("delete", new JsonObject { ["op"] = "delete", ["ids"] = Ids([node.TargetId]), ["with_members"] = command == "delete-with-members" });
                break;
            case "duplicate":
                SetSelection([node.TargetId], SceneView.SelectMode.Replace);
                Duplicate();
                break;
            case "frame":
                SetSelection([node.TargetId], SceneView.SelectMode.Replace);
                _view.FrameSelection();
                break;
        }
    }

    /// <summary>Every non-layer entity under a tree node.</summary>
    private IEnumerable<string> MembersDeep(LayerTreePanel.Node node)
    {
        var all = _model.EntitiesOf(node.FileLayer);
        if (node.Kind == "file") return all.Where(e => !TerryEntityTypes.IsLayerType(e.Type)).Select(e => e.Id);
        var layers = new HashSet<string> { node.Id! };
        bool grew;
        do
        {
            grew = false;
            foreach (var e in all)
                if (TerryEntityTypes.IsLayerType(e.Type) && e.Parents.Any(layers.Contains) && layers.Add(e.Id)) grew = true;
        } while (grew);
        return all.Where(e => !TerryEntityTypes.IsLayerType(e.Type) && e.Parents.Any(layers.Contains)).Select(e => e.Id);
    }

    /// <summary>Layer panel Show all / Hide all / Show only filtered: every change as one layer_state batch (one undo).</summary>
    private void SetLayersVisible(IReadOnlyList<(string Id, bool Visible)> changes, string label)
    {
        var ops = changes.Select(c => new JsonObject { ["op"] = "layer_state", ["id"] = c.Id, ["visible"] = c.Visible }).ToArray();
        if (Run($"{label} ({ops.Length})", ops) is null) return;
        _tree.Rebuild();
        _view.Refresh();
        _view3d.Refresh();
    }

    private void DropOnLayer(IReadOnlyList<string> ids, LayerTreePanel.Node target)
    {
        var ops = new List<JsonObject>();
        foreach (var id in ids)
        {
            if (_model.Find(id) is not { } item) continue;
            var parent = target.Kind == "layer" ? target.Id : null;
            ops.Add(item.Layer.Id == target.FileLayer
                ? new JsonObject { ["op"] = "set_parent", ["id"] = id, ["parent"] = parent }
                : new JsonObject { ["op"] = "move_to_layer", ["id"] = id, ["layer"] = target.FileLayer, ["parent"] = parent });
        }
        Run($"move {ops.Count} to layer", [.. ops]);
    }

    // ---------------------------------------------------------------- history

    private void Undo()
    {
        try
        {
            var undone = _model.Undo();
            Status(undone.Count == 0 ? "Nothing to undo." : $"Undid edit {undone[0].Seq}: {undone[0].Label}");
        }
        catch (Exception ex) { Status(ex.Message, error: true); }
    }

    private void Checkpoint()
    {
        var label = Prompt.Text(this, "Checkpoint", "Checkpoint name:");
        if (string.IsNullOrWhiteSpace(label)) return;
        _model.Checkpoint(label.Trim());
        Status($"Checkpoint '{label.Trim()}' saved.");
    }

    private void Rollback()
    {
        var names = _model.Editor.Checkpoints().Keys.ToList();
        if (names.Count == 0) { Status("No checkpoints."); return; }
        var label = Prompt.Choose(this, "Roll back", "Undo every edit since checkpoint:", names, names[^1]);
        if (string.IsNullOrWhiteSpace(label)) return;
        try
        {
            var undone = _model.Rollback(label);
            Status($"Rolled back {undone.Count} edit(s) to '{label}'.");
        }
        catch (Exception ex) { Status(ex.Message, error: true); }
    }

    private void ShowHistory()
    {
        var lines = _model.Editor.History().AsEnumerable().Reverse().Take(60)
            .Select(h => $"{h.Seq,5}  {h.Time:yyyy-MM-dd HH:mm}  {h.Label}  ({h.Files.Count} files)");
        var checkpoints = _model.Editor.Checkpoints().Select(c => $"checkpoint '{c.Key}' at {c.Value}");
        MessageBox.Show(this, string.Join("\n", lines.Concat(checkpoints)) is { Length: > 0 } s ? s : "No edits yet.", "Edit history");
    }

    // ---------------------------------------------------------------- search

    private void RunSearch()
    {
        var text = _search.Text.Trim();
        _results.Items.Clear();
        _resultIds = [];
        if (text.Length == 0)
        {
            _results.Visibility = _resultInfo.Visibility = Visibility.Collapsed;
            return;
        }
        var filters = new List<Func<SceneModel.Item, bool>>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                var types = token[5..].Split(',');
                filters.Add(i => types.Contains(i.Entity.Type, StringComparer.OrdinalIgnoreCase));
            }
            else if (token.StartsWith("layer:", StringComparison.OrdinalIgnoreCase))
            {
                var l = token[6..];
                filters.Add(i => i.Layer.Name.Contains(l, StringComparison.OrdinalIgnoreCase));
            }
            else if (token.StartsWith("EC", StringComparison.Ordinal) && !token.Contains(' '))
            {
                var f = EntityEditor.FieldFilter(token);
                filters.Add(i => f(i.Entity));
            }
            else
            {
                var t = token;
                filters.Add(i => i.Entity.Id.Contains(t, StringComparison.OrdinalIgnoreCase)
                                 || (i.Entity.Name ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                 || i.Entity.Type.Contains(t, StringComparison.OrdinalIgnoreCase)
                                 || i.Entity.Components.Any(c => c.Fields.Any(kv => kv.Key is "model_path" or "key" or "vfx" or "path" or "tags"
                                                                                     && kv.Value.Contains(t, StringComparison.OrdinalIgnoreCase))));
            }
        }
        var found = _model.All.Where(i => !TerryEntityTypes.IsLayerType(i.Entity.Type) || text.Contains("type:", StringComparison.OrdinalIgnoreCase))
            .Where(i => filters.All(f => f(i))).ToList();
        _resultIds = found.Select(i => i.Entity.Id).ToList();
        foreach (var i in found.Take(2000))
            _results.Items.Add($"{LayerTreePanel.Label(i.Entity)}  [{i.Entity.Type}]  {i.Layer.Name}");
        _resultInfo.Text = $"{found.Count} match(es)" + (found.Count > 2000 ? " (first 2000 listed)" : "")
                           + "  by type: " + string.Join(", ", found.GroupBy(i => i.Entity.Type).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} {g.Count()}"));
        _results.Visibility = _resultInfo.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- helpers

    private void Status(string text, bool error = false, bool warning = false)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.IndianRed : warning ? Brushes.Orange : Theme.Brush("Text");
    }

    private static string F(double v) => LayerWriter.F(v);
}
