using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.IO.Path;
using AtlasWH3.Core;
using AtlasWH3.Core.Editing;
using AtlasWH3.Core.Rendering;
using AtlasWH3.Formats.Trees;

namespace AtlasWH3.App;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand UndoCommand = new();
    public static readonly RoutedCommand RedoCommand = new();

    private readonly ProjectPaths _paths;
    private TerrainData? _terrain;
    private TerrainRenderer? _renderer;
    private CampaignTreeList? _trees;
    private TreeDatabase? _treeDb;
    private TreeOverlay? _treeOverlay;
    private TerrainTextureSet? _textures;

    private readonly HeightBrush _heightBrush = new();
    private readonly BlendBrush _blendBrush = new();
    private readonly Core.Operations.PendingExpansion _pending = new();
    private readonly TreeEditLog _treeLog = new();
    private TreeBrush? _treeBrush;

    public MainWindow() : this(ProjectPaths.FromArgs(Environment.GetCommandLineArgs().Skip(1).ToArray(), out _)) { }

    public MainWindow(ProjectPaths paths)
    {
        _paths = paths;
        InitializeComponent();
        Title = AppInfo.Title($"Terrain painter — {paths.MapName}");
        StandardMenus.AddTo(MainMenu, this, paths);
        Walkthrough.Enable(this, "painter");
        Placement.Track(this, "painter");
        LaunchBobItem.Visibility = AppSettings.Current.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        CommandBindings.Add(new CommandBinding(UndoCommand, (_, _) => Map.Undo.Undo(), (_, e) => e.CanExecute = Map.Undo.CanUndo));
        CommandBindings.Add(new CommandBinding(RedoCommand, (_, _) => Map.Undo.Redo(), (_, e) => e.CanExecute = Map.Undo.CanRedo));
        Map.HoverChanged += UpdateStatus;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = $"Loading {_paths.VanillaRoot} ...";
        try
        {
            await Task.Run(() =>
            {
                _terrain = TerrainData.LoadVanilla(_paths);
                _textures = TerrainTextureSet.Load(_paths, _terrain.TextureArrays);
                _renderer = new TerrainRenderer(_terrain, _textures);
                _trees = CampaignTreeList.Load(_paths.TreeList);
                if (File.Exists(_paths.TreeIdsTsv) && File.Exists(_paths.TreeVariantsTsv))
                    _treeDb = TreeDatabase.Load(_paths.TreeIdsTsv, _paths.TreeVariantsTsv);
                _treeOverlay = new TreeOverlay(_trees, _treeDb);
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
            ErrorDialog.Show(this, $"Could not load the map {_paths.MapName} from {_paths.VanillaRoot}.", ex);
            return;
        }

        Map.Trees = _treeOverlay;
        Map.Load(_terrain!, _renderer!);
        _treeBrush = new TreeBrush(_trees!, _treeDb, _treeLog) { HiddenSpecies = _treeOverlay!.HiddenTypes };
        PopulateTextureList();
        PopulateTreeList();
        PopulateSpecies();
        // Keep the species counts current after any edit, undo or redo.
        Map.Undo.Changed += PopulateTreeList;
        StatusText.Text = $"Loaded: {_terrain!.Width}x{_terrain.HeightPx} terrain, {_trees!.TotalInstances:N0} trees in {_trees.Types.Count} species.";
    }

    private void PopulateTextureList()
    {
        TextureList.Items.Clear();
        foreach (var group in _terrain!.TextureArrays.Groups)
        {
            var avg = _textures?.Textures[group.Index]?.Average ?? TerrainRenderer.FallbackColour(group.Index);
            var swatch = new Rectangle
            {
                Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0),
                Fill = new SolidColorBrush(Color.FromRgb((byte)avg, (byte)(avg >> 8), (byte)(avg >> 16))),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = group.Index };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = $"{group.Index,2}  {group.Name}" });
            TextureList.Items.Add(row);
        }
        // the last used texture, else temperate_1
        TextureList.SelectedIndex = int.TryParse(AppSettings.Current.Values.GetValueOrDefault("painter.texture"), out var t) && t < TextureList.Items.Count ? t
            : Math.Min(27, TextureList.Items.Count - 1);
        TextureList.SelectionChanged += (_, _) => AppSettings.Current.Values["painter.texture"] = TextureList.SelectedIndex.ToString();
    }

    private void PopulateSpecies()
    {
        // All placeable species: the DB ids plus anything already on the map.
        var names = (_treeDb?.Ids.Keys ?? Enumerable.Empty<string>())
            .Concat(_trees!.Types.Select(t => t.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();
        SpeciesCombo.ItemsSource = names;
        var last = AppSettings.Current.Values.GetValueOrDefault("painter.species");
        SpeciesCombo.SelectedItem = names.FirstOrDefault(n => n == last) ?? names.FirstOrDefault(n => n == "temperate_tree_katsura_medium_1") ?? names.FirstOrDefault();
        SpeciesCombo.SelectionChanged += (_, _) => { if (SpeciesCombo.SelectedItem is string sp) AppSettings.Current.Values["painter.species"] = sp; };
    }

    private void PopulateTreeList()
    {
        TreeTypeList.Items.Clear();
        foreach (var type in _trees!.Types.Where(t => t.Instances.Count > 0).OrderBy(t => t.Name))
        {
            var colour = _treeOverlay!.ColourFor(type.Name);
            var check = new CheckBox { IsChecked = !_treeOverlay.HiddenTypes.Contains(type.Name), Tag = type.Name };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0),
                Fill = new SolidColorBrush(Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour)),
            });
            content.Children.Add(new TextBlock { Text = $"{type.Name} ({type.Instances.Count:N0})" });
            check.Content = content;
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _treeOverlay.HiddenTypes.Remove(type.Name);
                else _treeOverlay.HiddenTypes.Add(type.Name);
                Map.Invalidate();
            };
            TreeTypeList.Items.Add(check);
        }
    }

    private void SetAllTrees(bool visible)
    {
        if (_treeOverlay == null) return;
        foreach (CheckBox check in TreeTypeList.Items)
        {
            check.IsChecked = visible;
            var name = (string)check.Tag;
            if (visible) _treeOverlay.HiddenTypes.Remove(name);
            else _treeOverlay.HiddenTypes.Add(name);
        }
        Map.Invalidate();
    }

    private void TreesAll_Click(object sender, RoutedEventArgs e) => SetAllTrees(true);
    private void TreesNone_Click(object sender, RoutedEventArgs e) => SetAllTrees(false);

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        TerrainBrush? brush = null;
        if (ToolRaise.IsChecked == true) { _heightBrush.Mode = HeightMode.Raise; brush = _heightBrush; }
        else if (ToolLower.IsChecked == true) { _heightBrush.Mode = HeightMode.Lower; brush = _heightBrush; }
        else if (ToolSmooth.IsChecked == true) { _heightBrush.Mode = HeightMode.Smooth; brush = _heightBrush; }
        else if (ToolFlatten.IsChecked == true) { _heightBrush.Mode = HeightMode.Flatten; brush = _heightBrush; }
        else if (ToolNoise.IsChecked == true) { _heightBrush.Mode = HeightMode.Noise; brush = _heightBrush; }
        else if (ToolPaint.IsChecked == true) brush = _blendBrush;
        else if (_treeBrush != null && ToolTreePlace.IsChecked == true) { _treeBrush.Mode = TreeBrushMode.Place; brush = _treeBrush; }
        else if (_treeBrush != null && ToolTreeScatter.IsChecked == true) { _treeBrush.Mode = TreeBrushMode.Scatter; brush = _treeBrush; }
        else if (_treeBrush != null && ToolTreeErase.IsChecked == true) { _treeBrush.Mode = TreeBrushMode.Erase; brush = _treeBrush; }
        Map.ActiveBrush = brush;
        Brush_Changed(sender, e);
    }

    private void Brush_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        foreach (var brush in new TerrainBrush?[] { _heightBrush, _blendBrush, _treeBrush })
        {
            if (brush == null) continue;
            brush.Radius = RadiusSlider.Value;
            brush.Strength = StrengthSlider.Value;
            brush.Softness = SoftnessSlider.Value;
        }
        if (TextureList.SelectedItem is StackPanel { Tag: int group })
            _blendBrush.Group = (byte)group;
        if (_treeBrush != null)
        {
            if (SpeciesCombo.SelectedItem is string species) _treeBrush.Species = species;
            _treeBrush.Density = DensitySlider.Value;
            _treeBrush.MinSpacing = SpacingSlider.Value;
            _treeBrush.AvoidWater = AvoidWaterCheck.IsChecked == true;
            _treeBrush.EraseOnlySpecies = EraseOnlySpeciesCheck.IsChecked == true ? _treeBrush.Species : null;
        }
        DensityLabel.Text = $"Scatter density: {DensitySlider.Value:F1} trees / unit²";
        SpacingLabel.Text = $"Min spacing: {SpacingSlider.Value:F2} units";
        RadiusLabel.Text = $"Radius: {RadiusSlider.Value:F0} px ({RadiusSlider.Value * 595.1 / 7136:F1} world units)";
        StrengthLabel.Text = $"Strength: {StrengthSlider.Value:F2}";
        SoftnessLabel.Text = $"Softness: {SoftnessSlider.Value:F2}";
    }

    private void Brush_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Brush_Changed(sender, (RoutedEventArgs)e);
    private void Brush_Changed(object sender, SelectionChangedEventArgs e) => Brush_Changed(sender, (RoutedEventArgs)e);

    private void Layers_Changed(object sender, RoutedEventArgs e)
    {
        if (_renderer == null || _treeOverlay == null) return;
        _renderer.Options.ShowTextures = ShowTextures.IsChecked == true;
        _renderer.Options.ShowWater = ShowWater.IsChecked == true;
        _treeOverlay.Visible = ShowTrees.IsChecked == true;
        Map.Invalidate();
    }

    private void Layers_Changed(object sender, SelectionChangedEventArgs e) => Layers_Changed(sender, (RoutedEventArgs)e);

    private void UpdateStatus((double X, double Y)? hover)
    {
        if (_terrain == null) return;
        if (hover is not { } p)
        {
            StatusText.Text = $"Zoom {1 / Map.View.Scale:F2}x   |   undo steps: {Map.Undo.Count}";
            return;
        }
        var ix = (int)p.X;
        var iy = (int)p.Y;
        var (wx, wz) = _terrain.Coords.ToWorld(p.X, p.Y, _terrain.Width, _terrain.HeightPx);
        var h = _terrain.Height[ix, iy];
        var sea = _terrain.SeaAt(ix, iy);
        var group = _terrain.BlendGroup[ix, iy];
        var groupName = group < _terrain.TextureArrays.Groups.Count ? _terrain.TextureArrays.Groups[group].Name : "?";
        StatusText.Text =
            $"world x {wx,7:F2}  z {wz,7:F2}   |   px {ix},{iy}   |   height {h} (y {HeightScale.ToWorld(h):F2})" +
            $"{(sea > h ? $"  water depth {(sea - h) * HeightScale.UnitsPerStep:F2}" : "")}   |   texture {group} {groupName}" +
            $"   |   zoom {1 / Map.View.Scale:F2}x   |   undo steps: {Map.Undo.Count}" +
            (NearestTreeInfo(p.X, p.Y) is { } tree ? $"   |   {tree}" : "");
    }

    private void OpenScene_Click(object sender, RoutedEventArgs e) => new Scene.SceneWindow(_paths).Show();

    private void OpenSceneProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a Terry project", Filter = "Terry project (*.terry)|*.terry",
            InitialDirectory = Path.Combine(_paths.AssemblyKitRoot, "raw_data"),
        };
        if (dialog.ShowDialog(this) == true) new Scene.SceneWindow(_paths, dialog.FileName).Show();
    }

    private void OpenTileMap_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = @"Campaign tile_map.png (raw_data\terrain\campaigns\<map>\tile_map.png)",
            Filter = "Tile map (*.png)|*.png",
            InitialDirectory = Directory.Exists(_paths.AkTerrainDir) ? _paths.AkTerrainDir : _paths.AssemblyKitRoot,
            FileName = "tile_map.png",
        };
        if (dialog.ShowDialog(this) != true) return;
        // a kit file (<kit>\raw_data\terrain\campaigns\<map>\tile_map.png) shares its undo journal with the terry MCP tools
        var mapDir = Path.GetDirectoryName(dialog.FileName)!;
        var campaigns = Path.GetDirectoryName(mapDir);
        var rawData = campaigns is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(campaigns));
        if (Path.GetFileName(dialog.FileName).Equals("tile_map.png", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(campaigns ?? "").Equals("campaigns", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(rawData ?? "").Equals("raw_data", StringComparison.OrdinalIgnoreCase))
            new CampaignTileWindow(_paths with { AssemblyKitRoot = Path.GetDirectoryName(rawData)!, MapName = Path.GetFileName(mapDir), TileMap = AtlasWH3.Core.Campaign.TileMapCheck.TileMapSource.Kit }).Show();
        else
            new CampaignTileWindow(_paths, dialog.FileName).Show();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Map.FitToWindow();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void ExportAk_Click(object sender, RoutedEventArgs e)
    {
        if (_terrain == null) return;
        var answer = MessageBox.Show(this,
            $"Write the terrain (height, sea height, ground textures) into the Assembly Kit?\n\n{_paths.AkTerrainDir}\n\n" +
            $"Existing files are backed up first to:\n{Path.Combine(_paths.OutputRoot, "backups")}\n\n" +
            "Yes = write to Assembly Kit   |   No = write to a test folder in output\\ak_export",
            "Export to Assembly Kit", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        var target = answer == MessageBoxResult.Yes ? _paths.AkTerrainDir : Path.Combine(_paths.OutputRoot, "ak_export");

        IsEnabled = false;
        StatusText.Text = "Exporting...";
        try
        {
            var terrain = _terrain;
            var treeInput = _trees != null ? new Core.Exporters.TreeExportInput(_trees, _treeDb, _treeLog) : null;
            var result = await Task.Run(() => new Core.Exporters.AkExporter(_paths).Export(terrain, _pending, target,
                msg => Dispatcher.Invoke(() => StatusText.Text = msg), treeInput));
            StatusText.Text = $"Exported {result.Written.Count} files to {result.TargetDir}";
            var dbNote = _pending.IsEmpty ? "" : "\n\n" + _pending.DescribeDbChanges(_paths.MapName);
            if (answer == MessageBoxResult.Yes)
            {
                _pending.Clear();
                _treeLog.Clear();
            }
            MessageBox.Show(this,
                $"Wrote {result.Written.Count} files to\n{result.TargetDir}" +
                (result.BackupDir != null ? $"\n\nBackup of previous files:\n{result.BackupDir}" : "") +
                (result.Notes.Count > 0 ? "\n\n" + string.Join("\n", result.Notes) : "") +
                dbNote +
                "\n\nBuild the map (Build > Open Build window, Ctrl+B) to compile it.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
            ErrorDialog.Show(this, "Export failed.", ex);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (_terrain == null) return;
        var dialog = new ExpandDialog(_terrain.Width, _terrain.HeightPx) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (Map.Undo.CanUndo && MessageBox.Show(this, "Expanding clears the undo history. Continue?", "Expand",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var options = new Core.Operations.ExpandOptions { ExtendBlendEdges = dialog.ExtendTextures };
        var result = Core.Operations.ExpandCanvas.Apply(_terrain, _trees, dialog.HexPadding, options);
        _pending.Add(result);
        _treeLog.Shift(result.ShiftX, result.ShiftZ);
        Map.Undo.Clear();
        Map.FitToWindow();
        StatusText.Text = $"Expanded to {_terrain.Width}x{_terrain.HeightPx} px, world {result.NewWorldWidth:F2} x {result.NewWorldHeight:F2}. " +
                          "Export to the Assembly Kit to apply it to layers and AK maps.";
    }

    private void DbChanges_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, _pending.IsEmpty ? "No pending expansion." : _pending.DescribeDbChanges(_paths.MapName),
            "Pending expansion", MessageBoxButton.OK, MessageBoxImage.Information);

    private void LaunchBob_Click(object sender, RoutedEventArgs e)
    {
        var bob = Path.Combine(_paths.AssemblyKitRoot, "binaries", "bob.modder.x64.exe");
        if (!File.Exists(bob))
        {
            MessageBox.Show(this, $"BOB not found at\n{bob}", "Launch BOB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bob)
        {
            WorkingDirectory = Path.GetDirectoryName(bob)!,
            UseShellExecute = true,
        });
    }

    private void ExportTrees_Click(object sender, RoutedEventArgs e)
    {
        if (_trees == null) return;
        try
        {
            var path = Core.Exporters.TreeExporter.WriteCompiled(_trees, _paths.OutputRoot, _paths.MapName);
            StatusText.Text = $"Wrote {path}";
            MessageBox.Show(this,
                $"Wrote {_trees.TotalInstances:N0} trees to\n{path}\n\n" +
                $"Add it to your mod pack at\n{Core.Exporters.TreeExporter.PackPath(_paths.MapName)}\n\n" +
                "Use File > Export to Assembly Kit as well to update the AK tree paint, so a later BOB rebuild keeps these edits.",
                "Trees exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "Tree export failed.", ex);
        }
    }

    private void RemoveSpecies_Click(object sender, RoutedEventArgs e)
    {
        if (_trees == null || SpeciesCombo.SelectedItem is not string species) return;
        var count = _trees.Types.FirstOrDefault(t => t.Name == species)?.Instances.Count ?? 0;
        if (count == 0)
        {
            MessageBox.Show(this, $"There are no '{species}' trees on the map.", "Remove species");
            return;
        }
        if (MessageBox.Show(this, $"Remove all {count:N0} '{species}' trees? (Undo with Ctrl+Z)", "Remove species",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (TreeBulkOps.RemoveSpecies(_trees, species, _treeLog) is { } undo)
            Map.Undo.Push(undo);
    }

    /// <summary>Nearest tree to a map pixel within a few screen pixels, for the status bar.</summary>
    private string? NearestTreeInfo(double mx, double my)
    {
        if (_trees == null || _terrain == null || _treeOverlay is not { Visible: true }) return null;
        var (wx, wz) = _terrain.Coords.ToWorld(mx, my, _terrain.Width, _terrain.HeightPx);
        var maxDist = 6 * Map.View.Scale * _terrain.Coords.WorldWidth / _terrain.Width;
        var best = maxDist * maxDist;
        TreeInstance? found = null;
        string? name = null;
        foreach (var type in _trees.Types)
        {
            if (_treeOverlay.HiddenTypes.Contains(type.Name)) continue;
            foreach (var t in type.Instances)
            {
                var d = (t.X - wx) * (t.X - wx) + (t.Z - wz) * (t.Z - wz);
                if (d < best) { best = d; found = t; name = type.Name; }
            }
        }
        if (found is not { } f) return null;
        return $"tree {name} v{f.Variant}";
    }
}
