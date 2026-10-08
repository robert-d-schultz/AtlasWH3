using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AtlasWH3.Core;
using AtlasWH3.Core.Build;

namespace AtlasWH3.App;

/// <summary>The front door: pick the assembly kit and map, open an editor on it, build, or reopen a recent project.
/// The kit and map are remembered in the settings; every editor opened here gets that map's paths.</summary>
public sealed class StartWindow : Window
{
    private static StartWindow? _open;
    private ProjectPaths _paths;
    private bool _rebuilding;

    public static void ShowSingle(ProjectPaths paths)
    {
        if (_open is null)
        {
            _open = new StartWindow(paths);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else _open.Activate();
    }

    public StartWindow(ProjectPaths paths)
    {
        _paths = MapCatalog.WithSelection(paths, AppSettings.Current);
        _open ??= this;
        Closed += (_, _) => { if (_open == this) _open = null; };
        Title = AppInfo.Title("Start");
        Width = 980;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Brush("Bg");
        Foreground = Theme.Brush("Text");
        Content = BuildLayout();
        Placement.Track(this, "start");
        Walkthrough.Enable(this, "start");
    }

    /// <summary>Closes the other windows one by one first, so an unsaved-edits prompt's Cancel keeps the app running
    /// (Application.Shutdown ignores a cancelled Closing).</summary>
    private void ExitApp()
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w != this).ToList())
        {
            w.Close();
            if (PresentationSource.FromVisual(w) is not null) return;   // still open: the user cancelled
        }
        Application.Current.Shutdown();
    }

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();
        var menu = new Menu().Spot("start.menu");
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(StandardMenus.Item("_Settings…", OpenSettings, tooltip: "Folders, linked packs, tile map source and developer mode."));
        file.Items.Add(new Separator());
        file.Items.Add(StandardMenus.Item("E_xit", ExitApp));
        menu.Items.Add(file);
        StandardMenus.AddTo(menu, this, _paths);
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        var grid = new Grid { Margin = new Thickness(36, 24, 36, 24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 440 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

        // left: title + editor tiles
        var left = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Theme.Icon(Theme.Glyph.Map, 36, Theme.Brush("Accent")));
        var names = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        names.Children.Add(new TextBlock { Text = AppInfo.Product, FontSize = 30, FontWeight = FontWeights.SemiBold });
        names.Children.Add(new TextBlock { Text = $"{AppInfo.Tagline} · {AppInfo.Version}", Foreground = Theme.Brush("DimText") });
        title.Children.Add(names);
        left.Children.Add(title);

        left.Children.Add(MapSelector());

        var tiles = new WrapPanel();
        tiles.Children.Add(Tile(Theme.Glyph.Scene, "Scene editor", "Props, entities, prefabs and layers, in 2D and 3D.",
                                () => new Scene.SceneWindow(_paths).Show()).Spot("start.scene"));
        tiles.Children.Add(Tile(Theme.Glyph.Tiles, "Tile map", "Paint the campaign tile map hex by hex, with live validation.",
                                () => new CampaignTileWindow(_paths).Show()).Spot("start.tiles"));
        tiles.Children.Add(Tile(Theme.Glyph.Terrain, "Terrain painter", "Heights, ground textures and trees on the compiled map.",
                                () => new MainWindow(_paths).Show()).Spot("start.painter"));
        tiles.Children.Add(Tile(Theme.Glyph.Build, "Build", "Compile the map natively, run custom steps, pack and install.",
                                () => BuildWindow.Show(this, _paths)).Spot("start.build"));
        tiles.Children.Add(Tile(Theme.Glyph.Battle, "Campaign battles", "Where battles start, settlement battle maps and redirects.",
                                () => CampaignBattlesWindow.Open(this, _paths)).Card("start.battles"));
        tiles.Children.Add(Tile(Theme.Glyph.Settings, "Settings", "Folders, linked mod packs, tile map source and game data.",
                                OpenSettings).Card("start.settings").Spot("start.settingsTile"));
        left.Children.Add(tiles);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // right: recent projects
        var right = new StackPanel();
        var recentPanel = new StackPanel().Spot("start.recent");
        right.Children.Add(recentPanel);
        recentPanel.Children.Add(Theme.Header("Recent projects", 6));
        var recent = AppSettings.Current.RecentProjects.Where(File.Exists).Take(8).ToList();
        if (recent.Count == 0)
            recentPanel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"),
                Text = "No projects yet. A project (.atlaswh3) holds a map's build profile: open Build and create one.",
            });
        foreach (var r in recent)
        {
            string name;
            try { name = BuildProject.Load(r) is var p && p.Name.Length > 0 ? p.Name : Path.GetFileNameWithoutExtension(r); }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { name = Path.GetFileNameWithoutExtension(r); }
            var b = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 6, 10, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = name, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = r, Foreground = Theme.Brush("DimText"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
                ToolTip = r,
            };
            b.Click += (_, _) => BuildWindow.Show(this, _paths, r);
            recentPanel.Children.Add(b);
        }
        var started = new StackPanel().Spot("start.getStarted");
        right.Children.Add(started);
        started.Children.Add(Theme.Header("Get started", 20));
        started.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"),
            Text = "1. Check your game and assembly kit folders in File > Settings.\n" +
                   "2. Edit the map in the Scene editor or Tile map.\n" +
                   "3. Open Build (Ctrl+B), create a project for the map and press Build all.\n" +
                   "Back up your assembly kit before building over it: this is alpha software.",
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid });
        return dock;
    }

    /// <summary>Kit and map pickers plus a line naming what the editors will open.</summary>
    private UIElement MapSelector()
    {
        var box = new Border
        {
            Margin = new Thickness(0, 22, 12, 14), Padding = new Thickness(12, 10, 12, 10), Background = Theme.Brush("Panel"),
            CornerRadius = new CornerRadius(4),
        }.Spot("start.selector");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition());
        void Add(UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
        TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 10, 2) };

        var kits = MapCatalog.Kits(Defaults.GameFolder).ToList();
        if (!kits.Contains(_paths.AssemblyKitRoot, StringComparer.OrdinalIgnoreCase)) kits.Insert(0, _paths.AssemblyKitRoot);
        var kit = new ComboBox { Margin = new Thickness(0, 2, 0, 2) }.Card("start.kit");
        foreach (var k in kits) kit.Items.Add(new ComboBoxItem { Content = Path.GetFileName(k), Tag = k, ToolTip = k });
        kit.SelectedItem = kit.Items.OfType<ComboBoxItem>().First(i => ((string)i.Tag).Equals(_paths.AssemblyKitRoot, StringComparison.OrdinalIgnoreCase));
        Add(Label("Assembly kit"), 0, 0);
        Add(kit, 0, 1);

        var map = new ComboBox { Margin = new Thickness(0, 2, 0, 2) }.Card("start.map");
        map.Items.Add(new ComboBoxItem { Content = _paths.MapName, Tag = _paths.MapName });
        map.SelectedIndex = 0;
        Add(Label("Map"), 1, 0);
        Add(map, 1, 1);

        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 6, 0, 0) };
        line.Inlines.Add("Editors open ");
        line.Inlines.Add(new System.Windows.Documents.Run(_paths.MapName) { Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(" from ");
        line.Inlines.Add(new System.Windows.Documents.Run(Path.GetFileName(_paths.AssemblyKitRoot)) { Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(_paths.ModPacks.Count == 0 ? "   ·   no linked packs"
            : $"   ·   {_paths.ModPacks.Count} linked pack(s), read-only: {string.Join(", ", _paths.ModPacks.Select(Path.GetFileName))}");
        var (cardTitle, cardText) = InfoCards.Get("start.selection");
        line.Card(cardTitle, cardText + $"\nKit: {_paths.AssemblyKitRoot}\nCompiled cache: {_paths.TerrainDir}");
        Add(line, 2, 1);
        box.Child = grid;

        kit.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || kit.SelectedItem is not ComboBoxItem { Tag: string root }) return;
            Select(root, null);
        };
        map.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || map.SelectedItem is not ComboBoxItem { Tag: string name } || name == _paths.MapName) return;
            Select(null, name);
        };
        _ = FillMapsAsync(map);
        return box;
    }

    private async Task FillMapsAsync(ComboBox map)
    {
        var kit = _paths.AssemblyKitRoot;
        var packs = _paths.ModPacks;
        IReadOnlyList<MapEntry> maps;
        try { maps = await Task.Run(() => MapCatalog.All(kit, packs)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorDialog.Log("Could not list the maps", e); return; }
        _rebuilding = true;
        try
        {
            map.Items.Clear();
            foreach (var m in maps) map.Items.Add(new ComboBoxItem { Content = m.ToString(), Tag = m.Name });
            if (!maps.Any(m => m.Name.Equals(_paths.MapName, StringComparison.OrdinalIgnoreCase)))
                map.Items.Insert(0, new ComboBoxItem { Content = $"{_paths.MapName}  (not in this kit or the linked packs)", Tag = _paths.MapName });
            map.SelectedItem = map.Items.OfType<ComboBoxItem>().First(i => ((string)i.Tag).Equals(_paths.MapName, StringComparison.OrdinalIgnoreCase));
        }
        finally { _rebuilding = false; }
    }

    /// <summary>Switches the kit and/or map, remembers them and rebuilds the page (menus included) on the new paths.</summary>
    private void Select(string? kit, string? map)
    {
        var s = AppSettings.Current;
        if (kit is not null)
        {
            _paths = _paths with { AssemblyKitRoot = kit };
            s.AssemblyKit = kit;
            // keep the map when the new kit has it, else the kit's main map or its first one
            var maps = MapCatalog.KitMaps(kit);
            if (maps.Count > 0 && !maps.Contains(_paths.MapName, StringComparer.OrdinalIgnoreCase))
                map = maps.FirstOrDefault(m => m == MapCatalog.DefaultMap) ?? maps[0];
        }
        if (map is not null)
        {
            _paths = _paths with { MapName = map };
            s.MapName = map;
        }
        try { s.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorDialog.Log("Could not save the map selection", e); }
        Rebuild();
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            InputBindings.Clear();
            Content = BuildLayout();
        }
        finally { _rebuilding = false; }
    }

    private void OpenSettings()
    {
        if (new SettingsWindow { Owner = this }.ShowDialog() != true) return;
        // pick up the saved folders, map and linked packs
        var s = AppSettings.Current;
        _paths = _paths with
        {
            AssemblyKitRoot = Defaults.AssemblyKit,
            MapName = s.MapName.Length > 0 ? s.MapName : _paths.MapName,
            ModPacks = s.LinkedPacks.Where(File.Exists).ToList(),
            VanillaRoot = Defaults.CompiledRoot, GameDataDir = Defaults.GameData, DbTsvRoot = Defaults.DbTsv,
            OutputRoot = Defaults.Output, CacheRoot = Defaults.Cache, TileMap = Defaults.TileMap,
        };
        Rebuild();
    }

    private static Button Tile(string glyph, string title, string text, Action open)
    {
        var content = new StackPanel { Width = 180 };
        content.Children.Add(Theme.Icon(glyph, 26, Theme.Brush("Accent")));
        content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText") });
        var b = new Button
        {
            Content = content, Padding = new Thickness(16), Margin = new Thickness(0, 0, 12, 12),
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
            Background = Theme.Brush("Panel"), Height = 140,
        };
        b.Click += (_, _) =>
        {
            try { open(); }
            catch (Exception e) { ErrorDialog.Show(Window.GetWindow(b), $"Could not open {title}.", e); }
        };
        return b;
    }
}
