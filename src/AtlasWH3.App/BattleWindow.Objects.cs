using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.App;

/// <summary>Explicit tiles, catchment areas and the BOB build for <see cref="BattleWindow"/>.</summary>
public partial class BattleWindow
{
    private ExplicitTileInteraction? _tiles;
    private CatchmentInteraction? _areas;

    private static readonly HashSet<string> PlaceableSets = new(StringComparer.OrdinalIgnoreCase)
        { "settlement_cities", "settlement_ports", "resource", "start_pos", "gate_battles", "historical_battles", "river_source", "river_junction" };

    /// <summary>Called once the project is loaded.</summary>
    private void InitObjects(BattleProject p)
    {
        _tiles = new ExplicitTileInteraction(p, Push, ShowTileSelection);
        _areas = new CatchmentInteraction(p, Push, ShowAreaSelection);
        Map.Undo.Changed += ObjectsChanged;
        PreviewKeyDown += Window_KeyDown;
        PopulateTileList();
        ObjectsChanged();
    }

    /// <summary>Points the map at the object tools when one of them is chosen; returns true if it did.</summary>
    private bool SelectObjectTool()
    {
        if (_tiles == null || _areas == null) return false;
        if (ToolTileSelect.IsChecked == true || ToolTilePlace.IsChecked == true)
        {
            _tiles.PlaceLocation = ToolTilePlace.IsChecked == true ? SelectedTileLocation() : null;
            Map.Interaction = _tiles;
            ShowExplicit.IsChecked = true;
            return true;
        }
        if (ToolAreaSelect.IsChecked == true || ToolAreaDraw.IsChecked == true)
        {
            _areas.DrawNew = ToolAreaDraw.IsChecked == true;
            _areas.NewTypes = TickedTypes();
            Map.Interaction = _areas;
            ShowCatchments.IsChecked = true;
            return true;
        }
        return false;
    }

    private void Push(IUndoable undo) => Map.Undo.Push(undo);

    private void ObjectsChanged()
    {
        if (_project == null || _renderer == null) return;
        var conflicts = ExplicitTileGeometry.Conflicts(_project.ExplicitTiles, _project.TileDatabase, _project.Width, _project.Height);
        _renderer.Options.ExplicitConflicts = conflicts;
        if (_tiles != null) _tiles.Conflicts = conflicts;
        ConflictText.Text = conflicts.Count == 0
            ? ""
            : $"{conflicts.Count} explicit tile(s) overlap another tile or leave the map (red). BOB stops at the first one.";
        ShowTileSelection();
        ShowAreaSelection();
        Map.Invalidate();
    }

    // ---- explicit tiles ---------------------------------------------------------------------------------------------

    private void PopulateTileList()
    {
        if (_project == null) return;
        var filter = TileSearch.Text.Trim();
        var selected = SelectedTileLocation();
        TileList.Items.Clear();
        foreach (var loc in _project.TileDatabase.Locations.OrderBy(l => l))
        {
            var tile = _project.TileDatabase.TileAt(loc)!;
            if (!PlaceableSets.Contains(tile.TileSet) && !tile.HasColour) continue;
            var name = loc.Replace(@"terrain\tiles\battle\", "").TrimEnd('\\').Replace('\\', '/');
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListBoxItem { Content = $"{name}  ({tile.Width}x{tile.Height})", Tag = loc };
            TileList.Items.Add(item);
            if (loc == selected) TileList.SelectedItem = item;
        }
    }

    private string? SelectedTileLocation() => (TileList.SelectedItem as ListBoxItem)?.Tag as string;

    private void TileSearch_Changed(object sender, TextChangedEventArgs e) => PopulateTileList();

    private void PlaceTile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_tiles == null) return;
        _tiles.PlaceRotation = PlaceRotationCombo.SelectedIndex * 90;
        if (ToolTilePlace.IsChecked == true) _tiles.PlaceLocation = SelectedTileLocation();
        Map.InvalidateVisual();
    }

    private void ShowTileSelection()
    {
        if (_tiles == null || _project == null) return;
        if (_tiles.Selected < 0 || _tiles.Selected >= _project.ExplicitTiles.Count)
        {
            TileSelectedText.Text = "Nothing selected.";
            return;
        }
        var t = _project.ExplicitTiles[_tiles.Selected];
        TileSelectedText.Text = $"#{_tiles.Selected}: {t.Location.Replace("terrain/tiles/battle/", "")}\nat {t.X},{t.Y}, rotation {t.Rotation}" +
                                (_tiles.Conflicts.Contains(_tiles.Selected) ? "\nOVERLAPS another tile or leaves the map" : "");
    }

    private void RotateTile()
    {
        if (ToolTilePlace.IsChecked == true)
        {
            PlaceRotationCombo.SelectedIndex = (PlaceRotationCombo.SelectedIndex + 1) % 4;
            return;
        }
        _tiles?.EditSelected("Rotate explicit tile", t => t with { Rotation = (t.Rotation + 90) % 360 });
    }

    private void TileRotate_Click(object sender, RoutedEventArgs e) => RotateTile();
    private void TileDelete_Click(object sender, RoutedEventArgs e) => _tiles?.EditSelected("Delete explicit tile", _ => null);

    private void CheckExplicit_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _tiles == null) return;
        var conflicts = ExplicitTileGeometry.Conflicts(_project.ExplicitTiles, _project.TileDatabase, _project.Width, _project.Height);
        if (conflicts.Count == 0)
        {
            MessageBox.Show(this, $"All {_project.ExplicitTiles.Count} explicit tiles fit.", "Explicit tiles");
            return;
        }
        var first = conflicts.Min();
        ToolTileSelect.IsChecked = true;
        _tiles.Select(first);
        Map.CentreOn(_project.ExplicitTiles[first].X, _project.ExplicitTiles[first].Y);
        MessageBox.Show(this, $"{conflicts.Count} explicit tile(s) overlap another tile or leave the map; selected the first.", "Explicit tiles");
    }

    // ---- catchment areas --------------------------------------------------------------------------------------------

    private List<string> TickedTypes()
    {
        var types = new List<string>();
        if (TypeAmbush.IsChecked == true) types.Add("land_ambush");
        if (TypeStandard.IsChecked == true) types.Add("settlement_standard");
        if (TypeUnfortified.IsChecked == true) types.Add("settlement_unfortified");
        if (TypeGate.IsChecked == true) types.Add("gate_battle");
        return types.Count > 0 ? types : ["land_ambush"];
    }

    private BattleCatchment? SelectedArea() =>
        _areas != null && _project != null && _areas.Selected >= 0 && _areas.Selected < _project.Catchments.Count
            ? _project.Catchments[_areas.Selected]
            : null;

    private void ShowAreaSelection()
    {
        if (SelectedArea() is not { } c)
        {
            AreaSelectedText.Text = "Nothing selected. Tab cycles overlapping areas.";
            return;
        }
        AreaSelectedText.Text = $"{c.Id}  ({c.Box.MaxX - c.Box.MinX + 1}x{c.Box.MaxY - c.Box.MinY + 1} cells)";
        TypeAmbush.IsChecked = c.Types.Contains("land_ambush");
        TypeStandard.IsChecked = c.Types.Contains("settlement_standard");
        TypeUnfortified.IsChecked = c.Types.Contains("settlement_unfortified");
        TypeGate.IsChecked = c.Types.Contains("gate_battle");
        AreaRedirect.Text = c.RedirectTo;
        AreaCulture.Text = c.Culture;
        AreaCx.Text = c.Centre.X.ToString();
        AreaCy.Text = c.Centre.Y.ToString();
        AreaX0.Text = c.Box.MinX.ToString();
        AreaY0.Text = c.Box.MinY.ToString();
        AreaX1.Text = c.Box.MaxX.ToString();
        AreaY1.Text = c.Box.MaxY.ToString();
    }

    private void AreaType_Click(object sender, RoutedEventArgs e)
    {
        if (_areas == null) return;
        var types = TickedTypes();
        _areas.NewTypes = types;
        if (SelectedArea() != null) _areas.EditSelected("Change catchment types", c => c with { Types = types });
    }

    private void AreaApply_Click(object sender, RoutedEventArgs e)
    {
        if (_areas == null || SelectedArea() == null) return;
        var fields = new[] { AreaCx, AreaCy, AreaX0, AreaY0, AreaX1, AreaY1 };
        var v = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++)
            if (!int.TryParse(fields[i].Text.Trim(), out v[i]))
            {
                MessageBox.Show(this, "Centre and box must be whole cell numbers.", "Catchment area");
                return;
            }
        var box = new CellBox(Math.Min(v[2], v[4]), Math.Min(v[3], v[5]), Math.Max(v[2], v[4]), Math.Max(v[3], v[5]));
        _areas.EditSelected("Edit catchment area", c => c with
        {
            Types = TickedTypes(), RedirectTo = AreaRedirect.Text.Trim(), Culture = AreaCulture.Text.Trim(),
            Centre = (v[0], v[1]), Box = box,
        });
    }

    private void AreaDelete_Click(object sender, RoutedEventArgs e) => _areas?.EditSelected("Delete catchment area", _ => null);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || _project == null) return;
        var tilesActive = _tiles != null && Map.Interaction == _tiles;
        var areasActive = _areas != null && Map.Interaction == _areas;
        switch (e.Key)
        {
            case Key.R when tilesActive:
                RotateTile();
                break;
            case Key.Delete when tilesActive:
                _tiles!.EditSelected("Delete explicit tile", _ => null);
                break;
            case Key.Delete when areasActive:
                _areas!.EditSelected("Delete catchment area", _ => null);
                break;
            case Key.Tab when areasActive:
                _areas!.Cycle();
                break;
            case Key.Escape:
                _tiles?.Select(-1);
                _areas?.Select(-1);
                break;
            default:
                return;
        }
        e.Handled = true;
        Map.Invalidate();
        Map.InvalidateVisual();
    }

    // ---- BOB ----------------------------------------------------------------------------------------------------------

    private string MapFolder => new DirectoryInfo(_project!.Dir).Name;

    private string? ReferenceFolder()
    {
        var vanilla = Path.Combine(Path.GetDirectoryName(_paths.VanillaRoot) ?? "", "terrain", "battles", MapFolder);
        return Directory.Exists(vanilla) ? vanilla : null;
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } p) return;
        var kit = p.Paths.AssemblyKitRoot;
        var expected = kit == null ? null : Path.GetFullPath(Path.Combine(kit, "raw_data", "terrain", "battles", MapFolder));
        if (expected == null || !string.Equals(expected.TrimEnd('\\'), p.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "BOB can only build a project that sits in an Assembly Kit, at\n<kit>\\raw_data\\terrain\\battles\\<map>.\n\n" +
                                  $"This project is at\n{p.Dir}", "Build with BOB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!BobBattleBuild.RulesOk(p.Dir))
        {
            if (MessageBox.Show(this, "This map's rules.bob doesn't turn on save_meta_data_map, so BOB would not write tile_map.tiles.\n\n" +
                                      "Write the correct rules.bob?", "Build with BOB", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK) return;
            BobBattleBuild.WriteRules(p.Dir);
        }
        var conflicts = ExplicitTileGeometry.Conflicts(p.ExplicitTiles, p.TileDatabase, p.Width, p.Height);
        if (conflicts.Count > 0 && MessageBox.Show(this,
                $"{conflicts.Count} explicit tile(s) overlap or leave the map; BOB will stop at the first one.\n\nBuild anyway?",
                "Build with BOB", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var running = BobBattleBuild.RunningBob(kit!);
        if (running.Any(r => r.SameKit))
        {
            MessageBox.Show(this, "BOB is already running in this Assembly Kit (" +
                                  string.Join(", ", running.Where(r => r.SameKit).Select(r => $"{r.Name} pid {r.Id}")) +
                                  ").\nBOB must run one action at a time; try again when it has finished.",
                "Build with BOB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (running.Count > 0 && MessageBox.Show(this,
                "BOB is running from another kit:\n" + string.Join("\n", running.Select(r => $"{r.Name} pid {r.Id}: {r.Path}")) +
                "\n\nBuild in this kit anyway?", "Build with BOB", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        if (p.ChangedParts().Count > 0) Save();

        var log = new BuildLogWindow($"BOB build: {p.MapName}") { Owner = this };
        log.Show();
        IsEnabled = false;
        try
        {
            foreach (var action in BobBattleBuild.Actions)
            {
                log.Append($"=== {action} ===");
                var result = await BobBattleBuild.RunAsync(kit!, MapFolder, action, TimeSpan.FromMinutes(30));
                log.Append(result.Log.Trim());
                if (result.ErrorLog.Trim().Length > 0) log.Append("--- bob_error.log ---\n" + result.ErrorLog.Trim());
                log.Append($"{action}: {(result.Ok ? "OK" : "FAILED")} in {result.Duration.TotalSeconds:F0} s " +
                           $"(exit {result.ExitCode?.ToString() ?? "-"}{(result.TimedOut ? ", timed out" : "")})\n");
                if (!result.Ok) return;
            }
            var built = Path.Combine(kit!, "working_data", "terrain", "battles", MapFolder);
            log.Append($"Output: {built}");
            if (ReferenceFolder() is { } reference)
            {
                log.Append($"\n=== Compared with {reference} ===");
                foreach (var line in await Task.Run(() => BobBattleBuild.Compare(built, reference, p.TileDatabase))) log.Append(line);
            }
        }
        catch (Exception ex)
        {
            log.Append("Build failed: " + ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } p || p.Paths.AssemblyKitRoot is not { } kit) return;
        var built = Path.Combine(kit, "working_data", "terrain", "battles", MapFolder);
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Reference compiled folder (e.g. extracted vanilla terrain\\battles\\<map>)",
            InitialDirectory = ReferenceFolder() ?? built,
        };
        if (dialog.ShowDialog(this) != true) return;
        var log = new BuildLogWindow($"Compare: {MapFolder}") { Owner = this };
        log.Show();
        log.Append($"Build:     {built}\nReference: {dialog.FolderName}\n");
        try
        {
            foreach (var line in await Task.Run(() => BobBattleBuild.Compare(built, dialog.FolderName, p.TileDatabase))) log.Append(line);
        }
        catch (Exception ex)
        {
            log.Append("Compare failed: " + ex.Message);
        }
    }
}

/// <summary>A plain scrolling log window for BOB runs and comparisons.</summary>
public sealed class BuildLogWindow : Window
{
    private readonly TextBox _text = new()
    {
        IsReadOnly = true,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Theme.Brush("Bg"),
        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 220, 220)),
    };

    public BuildLogWindow(string title)
    {
        Title = title;
        Width = 900;
        Height = 600;
        Content = _text;
    }

    public void Append(string line)
    {
        _text.AppendText(line + "\n");
        _text.ScrollToEnd();
    }
}
