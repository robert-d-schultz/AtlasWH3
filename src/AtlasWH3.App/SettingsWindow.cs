using System.IO;
using System.Windows;
using System.Windows.Controls;
using AtlasWH3.Core;
using Microsoft.Win32;

namespace AtlasWH3.App;

/// <summary>
/// Settings and first-run setup: the game, assembly kit and data folders (each checked live), the linked mod packs
/// (read-only sources), the tile map source, developer mode, and "Prepare game data", which extracts a map's compiled
/// files and the tree DB tables from the linked packs and the user's own install. Paths apply to windows opened after
/// saving. Packs and the game data folder are never written.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s = AppSettings.Current;
    private readonly bool _firstRun;
    private readonly TextBox _game = new(), _kit = new(), _compiled = new(), _db = new(), _output = new(), _cache = new();
    private readonly CheckBox _dev = new()
    {
        Content = "Developer mode (BOB launch, tile-matching simulation, comparison tools, self-tests)",
    };
    private readonly ComboBox _map = new()
    {
        MinWidth = 340,
    };
    private readonly ListBox _packs = new() { MinHeight = 60, MaxHeight = 130 };
    private readonly TextBox _log = new() { IsReadOnly = true, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 11 };
    private readonly Button _prepare;
    private readonly TileMapSourcePanel _tileMap;

    public SettingsWindow(bool firstRun = false)
    {
        _firstRun = firstRun;
        Title = AppInfo.Title(firstRun ? "Welcome" : "Settings");
        Width = 860;
        Height = 820;
        MinHeight = 400;
        WindowStartupLocation = firstRun ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Panel");
        Foreground = Theme.Brush("Text");
        _log.FontFamily = Theme.MonoFont;
        _prepare = Theme.IconButton(Theme.Glyph.Package, "Prepare game data", async (_, _) => await PrepareAsync(),
            "Copy the map's compiled files and the tree DB tables out of the packs into the game data cache. Packs are only read.");

        var panel = new StackPanel { Margin = new Thickness(18) };
        if (firstRun)
        {
            panel.Children.Add(new TextBlock { Text = $"Welcome to {AppInfo.Product}", FontSize = 22, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 6, 0, 6),
                Text = "Check the folders below (found through Steam where possible), then prepare the game data for the map you want " +
                       "to edit. Nothing of the game's is shipped with AtlasWH3: it reads your own install.",
            });
        }
        var detected = GameSetup.FindGameFolder();
        var folders = new StackPanel().Spot("settings.folders");
        panel.Children.Add(folders);
        folders.Children.Add(Theme.Header("Folders", firstRun ? 8 : 0));
        folders.Children.Add(PathRow("Game folder", _game, _s.GameFolder, detected ?? "", p => Directory.Exists(Path.Combine(p, "data")),
                                   "settings.game"));
        folders.Children.Add(PathRow("Assembly kit", _kit, _s.AssemblyKit, Path.Combine(Effective(_game, detected ?? ""), "assembly_kit"),
                                   p => Directory.Exists(Path.Combine(p, "raw_data")), "settings.kit"));
        folders.Children.Add(PathRow("Game data cache", _compiled, _s.CompiledRoot, Defaults.LocalData + "\\vanilla",
                                   p => Directory.Exists(Path.Combine(p, "terrain", "campaigns")), "settings.compiled"));
        folders.Children.Add(PathRow("DB tables", _db, _s.DbTsvFolder, Defaults.LocalData + "\\db",
                                   p => File.Exists(Path.Combine(p, "campaign_tree_ids_tables", "data__.tsv")), "settings.db"));
        folders.Children.Add(PathRow("Output", _output, _s.OutputFolder, Defaults.LocalData + "\\output", _ => true, "settings.output"));
        folders.Children.Add(PathRow("Cache", _cache, _s.CacheFolder, Defaults.LocalData + "\\cache", _ => true, "settings.cache"));
        _dev.Card("settings.dev");
        _map.Card("settings.map");
        _prepare.Card("settings.prepare");
        _kit.LostFocus += async (_, _) => await LoadMapsAsync();

        var linked = new StackPanel().Spot("settings.packs");
        panel.Children.Add(linked);
        linked.Children.Add(Theme.Header("Linked packs (read-only)"));
        linked.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 0, 0, 4),
            Text = "Mod packs read for compiled map files, DB tables and assets, before the vanilla packs (top of the list wins). " +
                   "AtlasWH3 never writes to a pack or the game's data folder: edits go to the assembly kit or the output folder.",
        });
        linked.Children.Add(PackRow());

        var tileSource = new StackPanel().Spot("settings.tileMap");
        panel.Children.Add(tileSource);
        tileSource.Children.Add(Theme.Header("Tile map source"));
        _tileMap = new TileMapSourcePanel(_s.TileMap, () => new ProjectPaths());
        tileSource.Children.Add(_tileMap);

        var prepare = new StackPanel().Spot("settings.prepare");
        panel.Children.Add(prepare);
        prepare.Children.Add(Theme.Header("Prepare game data"));
        var prep = new StackPanel { Orientation = Orientation.Horizontal };
        prep.Children.Add(new TextBlock { Text = "Map", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        prep.Children.Add(_map);
        prep.Children.Add(new Border { Width = 8 });
        prep.Children.Add(_prepare);
        prepare.Children.Add(prep);
        prepare.Children.Add(_log);
        _log.Margin = new Thickness(0, 6, 0, 0);

        var other = new StackPanel().Spot("settings.other");
        panel.Children.Add(other);
        other.Children.Add(Theme.Header("Other"));
        _dev.IsChecked = _s.DeveloperMode;
        other.Children.Add(_dev);
        var tours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var showTour = new Button { Content = "Show the walkthrough", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) }.Card("settings.tour");
        showTour.Click += (_, _) => Walkthrough.Start(this);
        var resetTours = new Button { Content = "Reset walkthroughs", Padding = new Thickness(10, 2, 10, 2) }.Card("settings.resetTours");
        resetTours.Click += (_, _) =>
        {
            Walkthrough.ResetAll();
            resetTours.Content = "Walkthroughs reset";
        };
        tours.Children.Add(showTour);
        tours.Children.Add(resetTours);
        other.Children.Add(tours);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 10, 18, 14) }.Spot("settings.save");
        buttons.Children.Add(new TextBlock { Text = "Folder changes apply to windows opened after saving.", Foreground = Theme.Brush("DimText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var ok = new Button { Content = firstRun ? "Continue" : "Save", IsDefault = true, MinWidth = 90, Style = (Style)FindResource("AccentButton") };
        ok.Click += (_, _) => { if (Save()) { DialogResult = true; } };
        buttons.Children.Add(ok);
        if (!firstRun)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) };
            buttons.Children.Add(cancel);
        }
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });
        Content = dock;
        Loaded += async (_, _) => await LoadMapsAsync();
        Walkthrough.Enable(this, "settings", autoStart: !firstRun);
        InputBindings.Add(new System.Windows.Input.KeyBinding(new RelayCommand(() => Walkthrough.Start(this)), System.Windows.Input.Key.F1,
                                                              System.Windows.Input.ModifierKeys.None));
    }

    private UIElement PackRow()
    {
        foreach (var p in _s.LinkedPacks) _packs.Items.Add(p);
        _packs.Card("settings.packs");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_packs);
        var side = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        Button B(string text, string card, Action click)
        {
            var b = new Button { Content = text, MinWidth = 80, Margin = new Thickness(0, 0, 0, 3) }.Card(card);
            b.Click += async (_, _) => { click(); await LoadMapsAsync(); };
            side.Children.Add(b);
            return b;
        }
        B("Add…", "Link one or more .pack files, e.g. your map mod in the game's data folder. They are only read.", AddPacks);
        B("Remove", "Unlink the selected pack. The pack file itself is not touched.", () =>
        {
            if (_packs.SelectedItem is { } item) _packs.Items.Remove(item);
        });
        B("Up", "Give the selected pack a higher priority.", () => MovePack(-1));
        B("Down", "Give the selected pack a lower priority.", () => MovePack(1));
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        return grid;
    }

    private void AddPacks()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Link mod packs (read-only)", Filter = "Pack files (*.pack)|*.pack", Multiselect = true,
            InitialDirectory = Directory.Exists(GameData) ? GameData : "",
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var file in dlg.FileNames)
        {
            var full = Path.GetFullPath(file);
            if (!LinkedPackList().Contains(full, StringComparer.OrdinalIgnoreCase)) _packs.Items.Add(full);
        }
    }

    private void MovePack(int delta)
    {
        var i = _packs.SelectedIndex;
        if (i < 0 || i + delta < 0 || i + delta >= _packs.Items.Count) return;
        var item = _packs.Items[i];
        _packs.Items.RemoveAt(i);
        _packs.Items.Insert(i + delta, item);
        _packs.SelectedIndex = i + delta;
    }

    private List<string> LinkedPackList() => _packs.Items.OfType<string>().ToList();

    private UIElement PathRow(string label, TextBox box, string value, string placeholder, Func<string, bool> valid, string card)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) }.Card(card);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var mark = Theme.Icon(Theme.Glyph.Check, 13);
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);
        box.Text = value;
        box.Tag = placeholder;
        void Check()
        {
            var path = Effective(box, placeholder);
            var ok = path.Length > 0 && valid(path);
            mark.Text = ok ? Theme.Glyph.Check : Theme.Glyph.Warning;
            mark.Foreground = Theme.Brush(ok ? "Ok" : "Warn");
            mark.ToolTip = ok ? path : $"not found: {path}";
        }
        box.TextChanged += (_, _) => Check();
        Check();
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);
        var hint = new TextBlock { Text = placeholder, Foreground = Theme.Brush("DimText"), IsHitTestVisible = false, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(hint, 2);
        grid.Children.Add(hint);
        var browse = new Button { Content = "…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Choose the {label.ToLowerInvariant()} folder." };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = label };
            if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
        };
        Grid.SetColumn(browse, 3);
        grid.Children.Add(browse);
        return grid;
    }

    private static string Effective(TextBox box, string placeholder) =>
        box.Text.Trim().Length > 0 ? box.Text.Trim() : (box.Tag as string ?? placeholder);

    private string GameData => Path.Combine(Effective(_game, GameSetup.FindGameFolder() ?? ""), "data");
    private string Kit => Effective(_kit, Path.Combine(Effective(_game, GameSetup.FindGameFolder() ?? ""), "assembly_kit"));

    private int _loadVersion;

    private async Task LoadMapsAsync()
    {
        var version = ++_loadVersion;
        var data = GameData;
        var kit = Kit;
        var packs = LinkedPackList();
        var keep = (_map.SelectedItem as MapEntry)?.Name ?? (_s.MapName.Length > 0 ? _s.MapName : MapCatalog.DefaultMap);
        var problems = new List<string>();
        try
        {
            var maps = await Task.Run(() => MapCatalog.All(kit, packs, Directory.Exists(data) ? data : null, problems.Add));
            if (version != _loadVersion) return;
            _map.ItemsSource = maps;
            _map.SelectedItem = maps.FirstOrDefault(m => m.Name.Equals(keep, StringComparison.OrdinalIgnoreCase))
                                ?? maps.FirstOrDefault(m => m.Name == MapCatalog.DefaultMap) ?? maps.FirstOrDefault();
            if (!Directory.Exists(data)) problems.Add($"Game data folder not found ({data}). Set the game folder first.");
            foreach (var p in problems) _log.AppendText(p + "\n");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            _log.AppendText("Could not list the maps: " + e.Message + "\n");
        }
    }

    private async Task PrepareAsync()
    {
        if (_map.SelectedItem is not MapEntry entry) return;
        var map = entry.Name;
        var data = GameData;
        var compiled = Effective(_compiled, Defaults.LocalData + "\\vanilla");
        var db = Effective(_db, Defaults.LocalData + "\\db");
        var packs = LinkedPackList();
        _prepare.IsEnabled = false;
        void Log(string m) => Dispatcher.BeginInvoke(() => { _log.AppendText(m + "\n"); _log.ScrollToEnd(); });
        try
        {
            if (entry.Origins.SetEquals([MapOrigin.Kit]))
            {
                Log($"{map} is only in the assembly kit: there are no compiled files to copy yet. Build it (Build window), " +
                    "or link the pack that holds it, then prepare again.");
                return;
            }
            Log($"extracting {map} from {(packs.Count > 0 ? $"{packs.Count} linked pack(s), then " : "")}{data} ...");
            await Task.Run(() =>
            {
                GameSetup.ExtractCompiledMap(data, map, compiled, packs, Log);
                GameSetup.ExtractDbTables(data, db, packs, Log);
            });
            Log("done.");
        }
        catch (Exception e)
        {
            Log("failed: " + e.Message);
            ErrorDialog.Log("Prepare game data failed", e);
        }
        finally { _prepare.IsEnabled = true; }
    }

    private bool Save()
    {
        var data = GameData;
        var packs = LinkedPackList();
        foreach (var (label, box, fallback) in new[]
                 {
                     ("Game data cache", _compiled, Defaults.LocalData + "\\vanilla"), ("DB tables", _db, Defaults.LocalData + "\\db"),
                     ("Output", _output, Defaults.LocalData + "\\output"), ("Cache", _cache, Defaults.LocalData + "\\cache"),
                 })
            if (SourceGuard.WhyProtected(Effective(box, fallback), data, packs) is { } why)
            {
                MessageBox.Show(this, $"{label}: {why}.\n\nChoose a folder outside the game's data folder.", AppInfo.Product,
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        _s.GameFolder = _game.Text.Trim();
        _s.AssemblyKit = _kit.Text.Trim();
        _s.CompiledRoot = _compiled.Text.Trim();
        _s.DbTsvFolder = _db.Text.Trim();
        _s.OutputFolder = _output.Text.Trim();
        _s.CacheFolder = _cache.Text.Trim();
        _s.DeveloperMode = _dev.IsChecked == true;
        _s.TileMap = _tileMap.StoredValue;
        _s.LinkedPacks = packs;
        if (_map.SelectedItem is MapEntry m) _s.MapName = m.Name;
        try
        {
            _s.Save();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorDialog.Show(this, "Could not save the settings.", e);
            return false;
        }
    }
}
