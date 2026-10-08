using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Maps;

namespace AtlasWH3.App;

/// <summary>
/// Campaign tile-map editor: paint, erase ("remove tiles"), line, fill and eyedropper on tile_map.png hexes. Every
/// stroke is an op (the same JSON ops as tiles-edit / the terry MCP tools) applied to an in-memory copy and checked at
/// once against the file on disk (<see cref="TileMapEditor.NewIssues"/>); new issues show as red (blocking) / orange
/// hexes and in the list. Save goes through <see cref="TileMapEditor"/>, so it shares the undo journal and ops log with
/// the MCP tools, and is refused while blocking issues remain unless forced.
/// </summary>
public sealed partial class CampaignTileWindow : Window
{
    private enum Tool { Navigate, Paint, Erase, Line, Fill, Pick }

    /// <summary>One undoable stroke: an op, or (a recommended fix) an array of ops applied together.</summary>
    private sealed record Stroke(JsonNode Op, Dictionary<(int Col, int Row), uint[]> Old);

    private ProjectPaths _paths;
    private string? _tileMapPath;
    private TileMapEditor? _editor;
    private CampaignTileDatabase? _db;
    private HexTileMap? _disk;
    private HexTileMap? _map;
    private TileMapOps? _ops;
    private string _diskHash = "";
    private readonly List<Stroke> _strokes = [];
    private readonly HashSet<(int Col, int Row)> _pending = [];
    private IReadOnlyList<TileMapFinding> _issues = [];
    private int _validateVersion;
    // last BOB tile-matching simulation: hexes that would get no tile, and those of them in edited hexes
    private IReadOnlyList<int[]> _simHexes = [];
    private HashSet<(int Col, int Row)> _simInEdits = [];
    private bool _simRunning;

    // stroke in progress
    private Dictionary<(int Col, int Row), uint[]>? _strokeOld;
    private HashSet<(int Col, int Row)>? _strokeHexes;

    private readonly CampaignTileView _view = new();
    private readonly TextBlock _status = new() { FontFamily = new FontFamily("Consolas"), Text = "Loading..." };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ListBox _palette = new() { Height = 260, Background = Brushes.Transparent, Foreground = Theme.Brush("Text") };
    private readonly ListBox _issueList = new() { Height = 180, Background = Brushes.Transparent, Foreground = Theme.Brush("Text") };
    private readonly ListBox _history = new() { Height = 120, Background = Brushes.Transparent, Foreground = Theme.Brush("Text") };
    private readonly Slider _radius = new() { Minimum = 0, Maximum = 12, Value = 1, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _radiusLabel = new() { Text = "Brush radius: 1 hex" };
    private readonly CheckBox _allowWarnings = new() { Content = "Save despite warnings", Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly Dictionary<Tool, RadioButton> _toolButtons = [];
    private Tool _tool = Tool.Navigate;

    public CampaignTileWindow(ProjectPaths paths, string? tileMapPath = null, bool errorMode = false)
    {
        _paths = paths;
        _tileMapPath = tileMapPath;
        _startInErrorMode = errorMode;
        Title = AppInfo.Title($"Campaign tile map — {paths.MapName}");
        Width = 1500;
        Height = 950;
        Background = Theme.Brush("Bg");
        Foreground = Theme.Brush("Text");
        Content = BuildLayout();
        Placement.Track(this, "tiles");
        Walkthrough.Enable(this, "tiles");

        _view.HoverChanged += Hover;
        _view.HexDown += HexDown;
        _view.HexDrag += HexDrag;
        _view.HexUp += HexUp;
        _radius.ValueChanged += (_, _) => { _radiusLabel.Text = $"Brush radius: {(int)_radius.Value} hex{((int)_radius.Value == 1 ? "" : "es")}"; UpdateBrush(); };
        _allowWarnings.Click += (_, _) => { RefreshOverlay(); RefreshIssues(); };
        PreviewKeyDown += Key;
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();
        var menu = new Menu().Spot("tiles.menu");
        var file = new MenuItem { Header = "_File" }.Spot("tiles.file");
        file.Items.Add(Gesture(Item("_Save", (_, _) => Save(false)), "Ctrl+S"));
        file.Items.Add(Item("Save _anyway (ignore issues)", (_, _) => Save(true)));
        file.Items.Add(Item("_Reload from disk (drop unsaved edits)", async (_, _) => await LoadAsync()));
        file.Items.Add(Item("Tile map s_ource…", async (_, _) => await ChooseSourceAsync()));
        file.Items.Add(new Separator());
        file.Items.Add(Item("_Close", (_, _) => Close()));
        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(Gesture(Item("_Undo unsaved stroke", (_, _) => UndoStroke()), "Ctrl+Z"));
        edit.Items.Add(Item("Undo last _saved batch", (_, _) => UndoSaved()));
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("_Fit to window", (_, _) => _view.FitToWindow()));
        var check = new MenuItem { Header = "_Check" };
        check.Items.Add(Item("_Simulate BOB Tilemap (whole map, 1-3 min)", (_, _) => Simulate()));
        check.Items.Add(Item("_Clear simulation marks", (_, _) => { _simHexes = []; _simInEdits = []; RefreshOverlay(); RefreshIssues(); }));
        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        menu.Items.Add(check);
        check.Visibility = AppSettings.Current.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        StandardMenus.AddTo(menu, this, _paths);
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        var statusBar = new Border { Background = Theme.Brush("Panel"), Padding = new Thickness(6, 3, 6, 3), Child = _status }.Spot("tiles.status");
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);

        var panel = new StackPanel { Margin = new Thickness(10) };
        var toolGroup = new StackPanel().Spot("tiles.tools");
        panel.Children.Add(toolGroup);
        toolGroup.Children.Add(Header("Tool", top: 0));
        foreach (var (tool, text) in new[]
        {
            (Tool.Navigate, "Navigate (no edit)"), (Tool.Paint, "Paint tiles (brush)"), (Tool.Erase, "Remove tiles (brush: back to surrounding land/sea)"),
            (Tool.Line, "Line (click points, Enter = draw, Esc = cancel)"), (Tool.Fill, "Fill connected area (click)"), (Tool.Pick, "Eyedropper (click)"),
        })
        {
            var rb = new RadioButton { Content = text, Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 2, 0, 2), IsChecked = tool == Tool.Navigate, GroupName = "tool" };
            rb.Checked += (_, _) => SetTool(tool);
            _toolButtons[tool] = rb;
            toolGroup.Children.Add(rb);
        }
        toolGroup.Children.Add(_radiusLabel);
        toolGroup.Children.Add(_radius);
        var paletteGroup = new StackPanel().Spot("tiles.palette");
        panel.Children.Add(paletteGroup);
        paletteGroup.Children.Add(Header("Tile set (paint, line, fill)"));
        paletteGroup.Children.Add(_palette);
        var issuesGroup = new StackPanel().Spot("tiles.issues");
        panel.Children.Add(issuesGroup);
        issuesGroup.Children.Add(Header("New issues from unsaved edits"));
        issuesGroup.Children.Add(_summary);
        _issueList.MouseDoubleClick += (_, _) => { if (_issueList.SelectedItem is ListBoxItem { Tag: int[] h }) _view.CentreOn(h[0], h[1]); };
        issuesGroup.Children.Add(_issueList);
        issuesGroup.Children.Add(_allowWarnings);
        var save = new Button { Content = "Save to tile_map.png", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(4) }.Spot("tiles.save");
        save.Click += (_, _) => Save(false);
        issuesGroup.Children.Add(save);
        var historyGroup = new StackPanel().Spot("tiles.history");
        panel.Children.Add(historyGroup);
        historyGroup.Children.Add(Header("Saved edits (shared with the terry MCP tools)"));
        historyGroup.Children.Add(_history);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.Brush("DimText"),
            Text = "Right/middle drag pans, wheel zooms. Double-click an issue to go to it. After saving, build the map (Ctrl+B: " +
                   "tile_list, then global_map + global_mesh) and check holes (tiles-holes / check_tile_holes).",
        });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush("Panel"), Content = panel };
        _tabs.Items.Add(new TabItem { Header = "Paint", Content = scroll });
        _tabs.Items.Add(new TabItem { Header = "Errors (F8)", Content = BuildErrorsPanel() }.Spot("tiles.errorsTab"));
        _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, _tabs)) SetErrorMode(_tabs.SelectedIndex == 1); };
        _tabs.Width = 340;
        DockPanel.SetDock(_tabs, Dock.Left);
        dock.Children.Add(_tabs);
        dock.Children.Add(_view.Spot("tiles.map"));
        return dock;
    }

    private static MenuItem Item(string header, RoutedEventHandler click) { var m = new MenuItem { Header = header }; m.Click += click; return m; }
    private static MenuItem Gesture(MenuItem m, string gesture) { m.InputGestureText = gesture; return m; }

    private static TextBlock Header(string text, double top = 10) => new()
    {
        Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0xcc, 0xff)), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4),
    };

    // ---------------------------------------------------------------- loading

    private async Task LoadAsync()
    {
        if (_strokes.Count > 0 && MessageBox.Show(this, "Drop the unsaved tile edits and reload?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _status.Text = "Loading tile map and tile database...";
        try
        {
            var editor = _editor ?? new TileMapEditor(_paths, _tileMapPath);
            if (!await SeedAsync(editor)) { _status.Text = $"Not loaded: {editor.TileMapPath}"; return; }
            var (disk, db) = await Task.Run(() => (editor.Load(), _db ?? editor.Database));
            _editor = editor;
            _db = db;
            _disk = disk;
            _diskHash = FileJournal.Hash(File.ReadAllBytes(editor.TileMapPath));
            _map = new HexTileMap(disk.PixelWidth, disk.PixelHeight, (uint[])disk.Pixels.Clone());
            _ops = new TileMapOps(_map, db);
            _strokes.Clear();
            _pending.Clear();
            _issues = [];
            _view.Load(_map);
            FillPalette();
            RefreshHistory();
            RefreshIssues();
            _status.Text = $"{editor.TileMapPath}   {disk.Width} x {disk.Height} hexes" +
                           (editor.Source.SeparateTarget(_paths) ? $"   (source {editor.Source.Describe(_paths)})" : "");
            if (_startInErrorMode) { _startInErrorMode = false; _tabs.SelectedIndex = 1; }
            else if (_errorMode) FindErrors();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _status.Text = "Cannot load: " + ex.Message;
            MessageBox.Show(this, ex.Message, Title);
        }
    }

    /// <summary>A pack (or a file saved elsewhere) is copied to the save target first, as one undoable journal step;
    /// asks before replacing a target that differs from the source.</summary>
    private async Task<bool> SeedAsync(TileMapEditor editor)
    {
        if (await Task.Run(() => editor.NeedsSeed(out var r) ? r : null) is not { } reason) return true;
        var exists = File.Exists(editor.TileMapPath);
        var ask = exists
            ? $"{reason}.\n\nCopy the source over it? (Undo last saved batch restores it.)\n\nNo = edit the existing file as it is."
            : $"{reason}.\n\nCopy {editor.Source.Describe(_paths)} there to start editing? The source is only read.";
        var answer = MessageBox.Show(this, ask, Title, exists ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel);
        if (answer is MessageBoxResult.Cancel) return false;
        if (answer is MessageBoxResult.No) return true;
        await Task.Run(editor.SeedFromSource);
        return true;
    }

    private async Task ChooseSourceAsync()
    {
        var dlg = new TileMapSourceDialog(_editor?.Source ?? _paths.TileMap, _paths) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        if (_strokes.Count > 0 && MessageBox.Show(this, "Drop the unsaved tile edits and switch the tile map source?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _strokes.Clear();
        _paths = _paths with { TileMap = dlg.Result };
        _tileMapPath = null;
        _editor = null;
        if (dlg.MakeDefault)
        {
            AppSettings.Current.TileMap = dlg.Result.IsKit ? null : dlg.Result;
            try { AppSettings.Current.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorDialog.Show(this, "Could not save the settings.", e); }
        }
        await LoadAsync();
    }

    private void FillPalette()
    {
        var counts = _map!.HexColours().GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var area = TileMapValidator.Category(null);
        _palette.Items.Clear();
        foreach (var s in _db!.TileSets.OrderBy(s => TileMapValidator.Category(s.Name) != area).ThenByDescending(s => counts.GetValueOrDefault(s.Rgb)).ThenBy(s => s.Name))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(s.R, s.G, s.B)), Stroke = Brushes.Black, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = $"{s.Name}  ({counts.GetValueOrDefault(s.Rgb):N0})", Foreground = counts.ContainsKey(s.Rgb) ? Theme.Brush("Text") : Theme.Brush("DimText") });
            _palette.Items.Add(new ListBoxItem { Content = row, Tag = s.Name });
        }
        _palette.SelectedIndex = 0;
    }

    private string? SelectedSet => (_palette.SelectedItem as ListBoxItem)?.Tag as string;

    // ---------------------------------------------------------------- tools

    private void SetTool(Tool tool)
    {
        _tool = tool;
        _view.LinePoints.Clear();
        UpdateBrush();
        _view.InvalidateVisual();
    }

    private void UpdateBrush() => _view.BrushRadius = _tool is Tool.Paint or Tool.Erase ? (int)_radius.Value : -1;

    private void HexDown((int Col, int Row) hex)
    {
        if (_ops == null || _map == null) return;
        switch (_tool)
        {
            case Tool.Paint:
            case Tool.Erase:
                if (_tool == Tool.Paint && SelectedSet == null) return;
                _strokeOld = [];
                _strokeHexes = [];
                Dab(hex);
                break;
            case Tool.Line:
                _view.LinePoints.Add(hex);
                _view.InvalidateVisual();
                break;
            case Tool.Fill:
                if (SelectedSet is { } set)
                    Apply(new JsonObject { ["op"] = "fill", ["set"] = set, ["at"] = new JsonArray(hex.Col, hex.Row) }, null);
                break;
            case Tool.Pick:
                var name = _ops.SetAt(hex.Col, hex.Row);
                foreach (ListBoxItem item in _palette.Items)
                    if (Equals(item.Tag, name)) { _palette.SelectedItem = item; _palette.ScrollIntoView(item); }
                break;
        }
    }

    private void HexDrag((int Col, int Row) hex)
    {
        if (_strokeHexes != null) Dab(hex);
    }

    /// <summary>Paint applies at once; erase only marks (its result depends on the whole area) until the button is up.</summary>
    private void Dab((int Col, int Row) hex)
    {
        var rgb = _tool == Tool.Paint ? _ops!.ResolveSet(SelectedSet!) : 0u;
        foreach (var h in _ops!.Circle(hex.Col, hex.Row, (int)_radius.Value))
        {
            if (!_strokeHexes!.Add(h)) continue;
            if (_tool == Tool.Paint)
            {
                _strokeOld![h] = OldPixels(h);
                _ops.Set(h.Col, h.Row, rgb);
            }
            _view.Overlay[_map!.Index(h.Col, h.Row)] = 4;
        }
        _view.Invalidate();
    }

    private void HexUp()
    {
        if (_strokeHexes == null) return;
        var hexes = new JsonArray(_strokeHexes.OrderBy(h => h.Row).ThenBy(h => h.Col).Select(h => (JsonNode)new JsonArray(h.Col, h.Row)).ToArray());
        if (_tool == Tool.Paint)
        {
            var op = new JsonObject { ["op"] = "paint", ["set"] = SelectedSet, ["hexes"] = hexes };
            var old = _strokeOld!.Where(kv => !SameAs(kv.Key, kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (old.Count > 0) Push(new Stroke(op, old));
            else RefreshOverlay();
        }
        else
        {
            Apply(new JsonObject { ["op"] = "erase", ["hexes"] = hexes }, _strokeHexes);
        }
        _strokeHexes = null;
        _strokeOld = null;
    }

    private void FinishLine()
    {
        if (_view.LinePoints.Count >= 2 && SelectedSet is { } set)
        {
            var pts = new JsonArray(_view.LinePoints.Select(p => (JsonNode)new JsonArray(p.Col, p.Row)).ToArray());
            Apply(new JsonObject { ["op"] = "line", ["set"] = set, ["points"] = pts }, TileMapOps.Line(_view.LinePoints));
        }
        _view.LinePoints.Clear();
        _view.InvalidateVisual();
    }

    /// <summary>Applies an op, recording the old pixels of the hexes it can change (all hexes when unknown).</summary>
    private void Apply(JsonObject op, IEnumerable<(int Col, int Row)>? area)
    {
        var before = area?.Where(h => _ops!.InMap(h.Col, h.Row)).Distinct().ToDictionary(h => h, OldPixels);
        var snapshot = before is null ? (uint[])_map!.Pixels.Clone() : null;
        try
        {
            var ops = new TileMapOps(_map!, _db!);
            ops.Apply(op);
            var old = new Dictionary<(int, int), uint[]>();
            foreach (var h in ops.Changed)
                old[h] = before?.GetValueOrDefault(h) ?? Pixels(snapshot!, h);
            if (old.Count > 0) Push(new Stroke(op, old));
            else RefreshOverlay();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            if (snapshot != null) snapshot.CopyTo(_map!.Pixels, 0);
            else foreach (var (h, px) in before!) Restore(h, px);
            RefreshOverlay();
            MessageBox.Show(this, ex.Message, Title);
        }
    }

    private void Push(Stroke stroke)
    {
        _strokes.Add(stroke);
        foreach (var h in stroke.Old.Keys) UpdatePending(h);
        Validate();
        if (_errorMode) FindErrors();
    }

    private void UndoStroke()
    {
        if (_strokes.Count == 0) return;
        var s = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        foreach (var (h, px) in s.Old) { Restore(h, px); UpdatePending(h); }
        Validate();
        if (_errorMode) FindErrors();
    }

    private uint[] OldPixels((int Col, int Row) h) => Pixels(_map!.Pixels, h);

    private uint[] Pixels(uint[] pixels, (int Col, int Row) h)
    {
        var r = new uint[4];
        for (var d = 0; d < 4; d++)
        {
            var (x, y) = _map!.HexPixel(h.Col, h.Row, d & 1, d >> 1);
            r[d] = pixels[y * _map.PixelWidth + x];
        }
        return r;
    }

    private void Restore((int Col, int Row) h, uint[] px)
    {
        for (var d = 0; d < 4; d++)
        {
            var (x, y) = _map!.HexPixel(h.Col, h.Row, d & 1, d >> 1);
            _map.Pixels[y * _map.PixelWidth + x] = px[d];
        }
    }

    private bool SameAs((int Col, int Row) h, uint[] px) => OldPixels(h).SequenceEqual(px);

    private void UpdatePending((int Col, int Row) h)
    {
        if (Pixels(_disk!.Pixels, h).SequenceEqual(OldPixels(h))) _pending.Remove(h);
        else _pending.Add(h);
    }

    // ---------------------------------------------------------------- validation

    private void Validate()
    {
        RefreshOverlay();
        var version = ++_validateVersion;
        if (_pending.Count == 0)
        {
            _issues = [];
            RefreshIssues();
            return;
        }
        var edited = new HexTileMap(_map!.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone());
        var pending = _pending.ToList();
        _summary.Text = $"{pending.Count} hexes changed - checking...";
        Task.Run(() => _editor!.NewIssues(_disk!, edited, pending).New).ContinueWith(t =>
        {
            if (version != _validateVersion) return;
            _issues = t.IsCompletedSuccessfully ? t.Result : [];
            if (t.Exception != null) _summary.Text = "check failed: " + t.Exception.InnerException?.Message;
            RefreshOverlay();
            RefreshIssues();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool AllowWarnings => _allowWarnings.IsChecked == true;

    private void RefreshOverlay()
    {
        if (_map == null) return;
        if (_errorMode) { RefreshErrorOverlay(); return; }
        Array.Clear(_view.Overlay);
        foreach (var h in _pending) _view.Overlay[_map.Index(h.Col, h.Row)] = 1;
        foreach (var h in _simHexes)
            _view.Overlay[_map.Index(h[0], h[1])] = _simInEdits.Contains((h[0], h[1])) ? (byte)3 : (byte)2;
        foreach (var f in _issues)
            foreach (var h in f.AllHexes)
                if (h[0] >= 0 && h[1] >= 0 && h[0] < _map.Width && h[1] < _map.Height)
                {
                    ref var o = ref _view.Overlay[_map.Index(h[0], h[1])];
                    o = Math.Max(o, TileMapEditor.Blocks(f, AllowWarnings) ? (byte)3 : (byte)2);
                }
        _view.Invalidate();
    }

    private void RefreshIssues()
    {
        _issueList.Items.Clear();
        var blocking = _issues.Count(f => TileMapEditor.Blocks(f, AllowWarnings));
        _summary.Text = _pending.Count == 0 ? "No unsaved edits."
            : $"{_pending.Count} hexes changed in {_strokes.Count} strokes; {_issues.Count} new issues ({blocking} blocking).";
        if (_simHexes.Count > 0)
        {
            _issueList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = $"BOB simulation: {_simHexes.Count} hexes get no tile (holes), {_simInEdits.Count} in your edits (red); the rest were there before (orange).",
                    TextWrapping = TextWrapping.Wrap, Width = 280,
                },
                Foreground = _simInEdits.Count > 0 ? Brushes.OrangeRed : Brushes.Orange,
                Tag = _simInEdits.Count > 0 ? new[] { _simInEdits.First().Col, _simInEdits.First().Row } : _simHexes[0],
            });
            foreach (var (c, r) in _simInEdits.Take(50))
                _issueList.Items.Add(new ListBoxItem { Content = $"  hole in edit at [{c},{r}]", Foreground = Brushes.OrangeRed, Tag = new[] { c, r } });
        }
        foreach (var f in _issues)
        {
            var block = TileMapEditor.Blocks(f, AllowWarnings);
            _issueList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = $"{(block ? "BLOCKING" : f.Severity)} {f.Code} ({f.Count}): {f.Message}", TextWrapping = TextWrapping.Wrap, Width = 280 },
                Foreground = block ? Brushes.OrangeRed : Brushes.Orange,
                Tag = f.AllHexes.FirstOrDefault(),
            });
        }
    }

    private void RefreshHistory()
    {
        _history.Items.Clear();
        if (_editor == null) return;
        foreach (var h in _editor.Journal.History().AsEnumerable().Reverse())
            _history.Items.Add($"#{h.Seq}  {h.Time:MM-dd HH:mm}  {h.Label}");
        if (_history.Items.Count == 0) _history.Items.Add("(none yet)");
    }

    /// <summary>Runs the BOB tile-matching port on the edited map (unsaved strokes included) in the background.</summary>
    private void Simulate()
    {
        if (_editor == null || _map == null || _simRunning) return;
        _simRunning = true;
        var map = new HexTileMap(_map.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone());
        var pending = _pending.ToList();
        _status.Text = "Simulating BOB Tilemap on the whole map (1-3 min)... you can keep editing; results are for the map as it was now.";
        Task.Run(() => _editor.Simulate(map, pending)).ContinueWith(t =>
        {
            _simRunning = false;
            if (!t.IsCompletedSuccessfully)
            {
                _status.Text = "Simulation failed: " + t.Exception?.InnerException?.Message;
                return;
            }
            var r = t.Result;
            _simHexes = r.NoTileHexes;
            _simInEdits = r.InEdited.Select(h => (h[0], h[1])).ToHashSet();
            _holesCheckedAt = DateTime.Now;
            RefreshOverlay();
            RefreshIssues();
            if (_errorMode) FindErrors();
            _status.Text = $"Simulation ({r.Elapsed.TotalSeconds:F0} s): {r.Summary.Placed:N0} tiles placed; {r.NoTileHexes.Count} hexes get no tile, " +
                           $"{r.InEdited.Count} of them in edited hexes.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------------------------------------------------------- save / undo saved

    private void Save(bool force)
    {
        if (_editor == null || _map == null) return;
        if (_pending.Count == 0) { _status.Text = "Nothing to save."; return; }
        var nowHash = File.Exists(_editor.TileMapPath) ? FileJournal.Hash(File.ReadAllBytes(_editor.TileMapPath)) : "";
        if (nowHash != _diskHash && !Rebase()) return;
        var ops = new JsonArray(_strokes.SelectMany(s => s.Op is JsonArray many ? many.Select(o => o!.DeepClone()) : [s.Op.DeepClone()]).ToArray());
        var label = $"GUI: {string.Join(", ", _strokes.GroupBy(s => s.Op is JsonArray ? "fix" : s.Op["op"]?.ToString()).Select(g => $"{g.Key} x{g.Count()}"))}";
        var result = _editor.Save(new HexTileMap(_map.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone()), _pending, ops, label, force, AllowWarnings);
        if (!result.Written)
        {
            _issues = result.NewIssues;
            RefreshOverlay();
            RefreshIssues();
            MessageBox.Show(this, $"Not saved: the edits cause {result.Blocking} blocking issue(s). Fix them (Ctrl+Z undoes strokes), tick " +
                                  "'Save despite warnings', or use File > Save anyway.", Title);
            return;
        }
        _disk = new HexTileMap(_map.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone());
        _diskHash = FileJournal.Hash(File.ReadAllBytes(_editor.TileMapPath));
        _strokes.Clear();
        _pending.Clear();
        _issues = [];
        RefreshOverlay();
        RefreshIssues();
        RefreshHistory();
        _status.Text = $"Saved as edit #{result.Seq} ({result.Changed.Count} hexes). Build tile_list (Ctrl+B) to rebuild tile_list.bin.";
        if (_errorMode)
        {
            FindErrors();
            if (_holesCheckedAt is not null) Simulate();     // holes were checked before: re-check them on the saved map
        }
    }

    /// <summary>The file changed on disk since it was loaded (an MCP edit, a builder): reload it and re-apply the
    /// unsaved strokes on top.</summary>
    private bool Rebase()
    {
        if (MessageBox.Show(this, "tile_map.png changed on disk since it was opened (another tool edited it).\n\n" +
                                  "Reload it and re-apply your unsaved strokes on top?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return false;
        var strokes = _strokes.Select(s => s.Op).ToList();
        var disk = _editor!.Load();
        _disk = disk;
        _diskHash = FileJournal.Hash(File.ReadAllBytes(_editor.TileMapPath));
        _map = new HexTileMap(disk.PixelWidth, disk.PixelHeight, (uint[])disk.Pixels.Clone());
        _ops = new TileMapOps(_map, _db!);
        _view.Load(_map);
        _strokes.Clear();
        _pending.Clear();
        foreach (var op in strokes)
            if (op is JsonArray many) ApplyOps(many, null);
            else Apply((JsonObject)op, null);
        RefreshHistory();
        return true;
    }

    private void UndoSaved()
    {
        if (_editor == null) return;
        if (_strokes.Count > 0) { MessageBox.Show(this, "Save or undo the unsaved strokes first.", Title); return; }
        try
        {
            var undone = _editor.Undo();
            if (undone.Count == 0) { _status.Text = "No saved edits to undo."; return; }
            _ = LoadAsync();
            _status.Text = $"Undid saved edit #{undone[0].Seq} ({undone[0].Label}).";
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Title);
        }
    }

    // ---------------------------------------------------------------- input / status

    private void Key(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { UndoStroke(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(false); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter && _tool == Tool.Line) { FinishLine(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape && _tool == Tool.Line) { _view.LinePoints.Clear(); _view.InvalidateVisual(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.F8) { _tabs.SelectedIndex = _errorMode ? 0 : 1; e.Handled = true; }
        else if (_errorMode && Keyboard.FocusedElement is not TextBox) ErrorKey(e);
    }

    private void Hover((int Col, int Row)? hex)
    {
        if (_ops == null || hex is not { } h) return;
        var (wx, wz) = TileMapEditor.HexToWorld(h.Col, h.Row);
        if (_errorMode) { ErrorHover(h); return; }
        var issue = _issues.FirstOrDefault(f => f.AllHexes.Any(x => x[0] == h.Col && x[1] == h.Row));
        _status.Text = $"hex [{h.Col},{h.Row}]  {_ops.SetAt(h.Col, h.Row) ?? $"#{_ops.Colour(h.Col, h.Row):x6} (no tile set)"}   world x {wx:F2} z {wz:F2}" +
                       $"   |   zoom {2 / _view.View.Scale:F1} px/hex   |   unsaved: {_pending.Count} hexes, {_strokes.Count} strokes" +
                       (issue != null ? $"   |   {issue.Code}: {issue.Message}" : "");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_strokes.Count > 0 && MessageBox.Show(this, "Close and lose the unsaved tile edits?", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
