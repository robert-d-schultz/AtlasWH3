using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AtlasWH3.Core;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.App;

/// <summary>
/// Editor for a campaign-battle terrain's BOB sources (raw_data\terrain\battles\&lt;map&gt;): paint tile_map.png with
/// exact palette colours and edit the land/sea heights. Save writes only what changed, after a backup.
/// </summary>
public partial class BattleWindow : Window
{
    public static readonly RoutedCommand UndoCommand = new();
    public static readonly RoutedCommand RedoCommand = new();
    public static readonly RoutedCommand SaveCommand = new();

    private readonly string _dir;
    private readonly ProjectPaths _paths;
    private BattleProject? _project;
    private BattleRenderer? _renderer;
    private readonly TilePaintTool _paint = new();
    private readonly HeightTool _height = new(new HeightBrush());
    private Dictionary<uint, int> _usage = new();

    public BattleWindow(string projectDir, ProjectPaths paths)
    {
        _dir = projectDir;
        _paths = paths;
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(UndoCommand, (_, _) => Map.Undo.Undo(), (_, e) => e.CanExecute = Map.Undo.CanUndo));
        CommandBindings.Add(new CommandBinding(RedoCommand, (_, _) => Map.Undo.Redo(), (_, e) => e.CanExecute = Map.Undo.CanRedo));
        CommandBindings.Add(new CommandBinding(SaveCommand, (_, _) => Save(), (_, e) => e.CanExecute = _project != null));
        Map.HoverChanged += UpdateStatus;
        Map.Edited += RefreshUsage;
        Map.Undo.Changed += RefreshUsage;
        Loaded += async (_, _) => await LoadAsync();
        Closing += Window_Closing;
    }

    private async Task LoadAsync()
    {
        StatusText.Text = $"Loading {_dir} ...";
        try
        {
            var battlePaths = BattlePaths.For(_dir, _paths);
            _project = await Task.Run(() => BattleProject.Load(_dir, battlePaths));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
            ErrorDialog.Show(this, "Load failed.", ex);
            return;
        }
        var p = _project;
        _renderer = new BattleRenderer(p);
        Title = AppInfo.Title($"Battle map (experimental) — {p.MapName}");
        Map.Load(p, _renderer);
        RefreshUsage();
        InitObjects(p);
        ProjectInfo.Text =
            $"{p.Dir}\n{p.Width}x{p.Height} cells, land {p.Land.Width}x{p.Land.Height}, sea {p.Sea.Width}x{p.Sea.Height}\n" +
            $"{p.Palette.Entries.Count} palette colours, {p.ExplicitTiles.Count} explicit tiles, {p.Catchments.Count} catchment areas\n" +
            $"tile database: {p.Paths.TileDatabaseDir}\nplacement groups: {p.Paths.PlacementGroupsXml ?? "(not found)"}" +
            (p.Notes.Count > 0 ? "\n\n" + string.Join("\n", p.Notes) : "");
        Tool_Changed(this, new RoutedEventArgs());
        Layers_Changed(this, new RoutedEventArgs());
        StatusText.Text = $"Loaded {p.MapName}: {p.Width}x{p.Height} cells.";
    }

    private void RefreshUsage()
    {
        if (_project == null) return;
        var usage = new Dictionary<uint, int>();
        var map = _project.TileMap;
        for (var y = 0; y < map.Height; y++)
        for (var x = 0; x < map.Width; x++)
        {
            var rgb = BattleProject.RgbAt(map, x, y);
            usage[rgb] = usage.GetValueOrDefault(rgb) + 1;
        }
        _usage = usage;
        PopulatePalette();
    }

    private void PopulatePalette()
    {
        if (_project == null) return;
        var selected = (PaletteList.SelectedItem as FrameworkElement)?.Tag as PaletteEntry;
        var filter = PaletteSearch.Text.Trim();
        var usedOnly = UsedOnlyCheck.IsChecked == true;
        PaletteList.Items.Clear();
        foreach (var e in _project.Palette.Entries
                     .Where(e => !usedOnly || _usage.ContainsKey(e.Rgb))
                     .Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                      || e.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => _usage.GetValueOrDefault(e.Rgb)))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = e, ToolTip = $"{e.Label}\n#{e.Rgb:X6} ({e.R},{e.G},{e.B})\n{e.Detail}" };
            row.Children.Add(new Rectangle { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), Fill = new SolidColorBrush(Color.FromRgb(e.R, e.G, e.B)) });
            row.Children.Add(new TextBlock { Text = $"{e.Label}  ({_usage.GetValueOrDefault(e.Rgb):N0})" });
            PaletteList.Items.Add(row);
            if (selected != null && selected.Rgb == e.Rgb) PaletteList.SelectedItem = row;
        }
        var unknown = _usage.Keys.Where(k => k != 0 && _project.Palette.Find(k) == null).ToList();
        if (unknown.Count > 0)
            StatusText.Text = $"{unknown.Count} colour(s) on the map are not in the palette (shown magenta; BOB places nothing there): " +
                              string.Join(", ", unknown.Take(5).Select(k => $"#{k:X6}"));
    }

    private void Select(uint rgb)
    {
        foreach (FrameworkElement item in PaletteList.Items)
            if (item.Tag is PaletteEntry e && e.Rgb == rgb)
            {
                PaletteList.SelectedItem = item;
                PaletteList.ScrollIntoView(item);
                return;
            }
        // Not visible under the current filter: select it directly.
        _paint.Rgb = rgb;
        ShowSelected(_project?.Palette.Find(rgb), rgb);
    }

    private void ShowSelected(PaletteEntry? e, uint rgb)
    {
        SelectedSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        SelectedLabel.Text = e?.Label ?? (rgb == 0 ? "no tile (black)" : $"#{rgb:X6} (not in palette)");
        var bright = ((rgb >> 16) & 0xFF) * 0.3 + ((rgb >> 8) & 0xFF) * 0.59 + (rgb & 0xFF) * 0.11;
        SelectedLabel.Foreground = bright > 140 ? Brushes.Black : Brushes.White;
        if (_renderer != null && HighlightCheck.IsChecked == true)
        {
            _renderer.Options.Highlight = rgb;
            Map.Invalidate();
        }
    }

    private void Palette_Selected(object sender, SelectionChangedEventArgs e)
    {
        if ((PaletteList.SelectedItem as FrameworkElement)?.Tag is not PaletteEntry entry) return;
        _paint.Rgb = entry.Rgb;
        ShowSelected(entry, entry.Rgb);
    }

    private void PaletteSearch_Changed(object sender, RoutedEventArgs e) => PopulatePalette();

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        Map.ClickAction = null;
        Map.Interaction = null;
        Map.ActiveTool = null;
        if (SelectObjectTool())
        {
            Layers_Changed(sender, e);
            return;
        }
        IBattleTool? tool = null;
        void Height(HeightMode mode)
        {
            _height.Brush.Mode = mode;
            tool = _height;
        }
        if (ToolPaint.IsChecked == true) tool = _paint;
        else if (ToolRaise.IsChecked == true) Height(HeightMode.Raise);
        else if (ToolLower.IsChecked == true) Height(HeightMode.Lower);
        else if (ToolSmooth.IsChecked == true) Height(HeightMode.Smooth);
        else if (ToolFlatten.IsChecked == true) Height(HeightMode.Flatten);
        else if (ToolNoise.IsChecked == true) Height(HeightMode.Noise);
        else if (ToolFill.IsChecked == true)
            Map.ClickAction = (cx, cy) =>
            {
                if (TileFill.Fill(_project, cx, cy, _paint.Rgb) is { } undo) Map.Undo.Push(undo);
            };
        else if (ToolPick.IsChecked == true)
            Map.ClickAction = (cx, cy) => Select(BattleProject.RgbAt(_project.TileMap, cx, cy));
        _height.Target = HeightTargetCombo.SelectedIndex == 1 ? HeightTarget.Sea : HeightTarget.Land;
        Map.ActiveTool = tool;
        Brush_Changed(sender, e);
    }

    private void Brush_Changed(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        _paint.RadiusCells = RadiusSlider.Value;
        _paint.ReplaceColourUnderStart = ReplaceOnlyCheck.IsChecked == true;
        if (!_paint.ReplaceColourUnderStart) _paint.OnlyReplace = null;
        _height.RadiusCells = RadiusSlider.Value;
        _height.Brush.Strength = StrengthSlider.Value;
        _height.Brush.Softness = SoftnessSlider.Value;
        RadiusLabel.Text = $"Radius: {RadiusSlider.Value:F1} cells";
        StrengthLabel.Text = $"Strength (height): {StrengthSlider.Value:F2}";
        SoftnessLabel.Text = $"Softness (height): {SoftnessSlider.Value:F2}";
        Map.InvalidateVisual();
    }

    private void Brush_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Brush_Changed(sender, (RoutedEventArgs)e);
    private void Tool_Changed(object sender, SelectionChangedEventArgs e) => Tool_Changed(sender, (RoutedEventArgs)e);

    private void Layers_Changed(object sender, RoutedEventArgs e)
    {
        if (_renderer == null) return;
        var o = _renderer.Options;
        o.Mode = (BattleViewMode)Math.Max(0, ViewModeCombo.SelectedIndex);
        o.Hillshade = ShowShade.IsChecked == true;
        o.Water = ShowWater.IsChecked == true;
        o.Grid = ShowGrid.IsChecked == true;
        o.ExplicitTiles = ShowExplicit.IsChecked == true;
        o.Catchments = ShowCatchments.IsChecked == true;
        o.Highlight = HighlightCheck.IsChecked == true ? _paint.Rgb : null;
        Map.Invalidate();
    }

    private void Layers_Changed(object sender, SelectionChangedEventArgs e) => Layers_Changed(sender, (RoutedEventArgs)e);

    private void UpdateStatus((double X, double Y)? hover)
    {
        if (_project is not { } p) return;
        if (hover is not { } v)
        {
            StatusText.Text = $"zoom {p.LandPerCell / Map.View.Scale:F1} px/cell   |   undo steps: {Map.Undo.Count}   |   unsaved: {Unsaved()}";
            return;
        }
        var ix = (int)v.X;
        var iy = (int)v.Y;
        var cx = Math.Min(ix / p.LandPerCell, p.Width - 1);
        var cy = Math.Min(iy / p.LandPerCell, p.Height - 1);
        var rgb = BattleProject.RgbAt(p.TileMap, cx, cy);
        var entry = p.Palette.Find(rgb);
        var land = p.Land[ix, iy];
        var sea = p.Sea[Math.Min(ix * p.Sea.Width / p.Land.Width, p.Sea.Width - 1), Math.Min(iy * p.Sea.Height / p.Land.Height, p.Sea.Height - 1)];
        var tile = p.ExplicitTiles.FirstOrDefault(t => p.TileDatabase.TileAt(t.Location) is { } bt
                                                       && cx >= t.X && cy >= t.Y && cx < t.X + t.Size(bt).W && cy < t.Y + t.Size(bt).H);
        var catchments = p.Catchments.Count(c => cx >= c.Box.MinX && cx <= c.Box.MaxX && cy >= c.Box.MinY && cy <= c.Box.MaxY);
        StatusText.Text =
            $"cell {cx},{cy}   |   {(entry?.Label ?? (rgb == 0 ? "no tile" : $"#{rgb:X6} NOT IN PALETTE"))}   |   climate {p.ClimateAt(cx, cy) ?? "?"}" +
            $"   |   land {land}  sea {sea}{(sea > land ? " (under water)" : "")}" +
            (tile != null ? $"   |   explicit {tile.Location.Split('/').Last()} rot {tile.Rotation}" : "") +
            (catchments > 0 ? $"   |   {catchments} catchment area(s)" : "") +
            $"   |   zoom {p.LandPerCell / Map.View.Scale:F1} px/cell";
    }

    private string Unsaved() => _project?.ChangedParts() is { Count: > 0 } c ? string.Join(", ", c) : "none";

    private void Save()
    {
        if (_project == null) return;
        try
        {
            var result = _project.Save(System.IO.Path.Combine(_paths.OutputRoot, "backups"));
            StatusText.Text = result.Written.Count == 0
                ? "Nothing to save."
                : $"Saved {result.Written.Count} file(s)" + (result.BackupDir != null ? $"; backup in {result.BackupDir}" : "");
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "Save failed.", ex);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_project?.ChangedParts() is not { Count: > 0 } changed) return;
        var answer = MessageBox.Show(this, $"Save changes ({string.Join(", ", changed)}) before closing?", "Battle map",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) e.Cancel = true;
        else if (answer == MessageBoxResult.Yes) Save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", _dir) { UseShellExecute = true });

    private void Fit_Click(object sender, RoutedEventArgs e) => Map.FitToWindow();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
