using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AtlasWH3.Core;
using AtlasWH3.Core.Build;
using Microsoft.Win32;

namespace AtlasWH3.App;

/// <summary>The front door: pick a project (or, without one, a kit map), open an editor on it, build, or reopen a recent
/// project. The choice is remembered in the settings; every editor opened here gets its paths: the project's map, kit,
/// mod packs and tile map source.</summary>
public sealed class StartWindow : Window
{
    private static StartWindow? _open;
    private ProjectPaths _paths;
    /// <summary>The project the editors open on; null = the kit's map, the game's packs only.</summary>
    private BuildProject? _project;
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
        (_project, _paths) = Selection(paths);
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
        file.Items.Add(StandardMenus.Item("_Open project…", OpenProject, tooltip: "Open an AtlasWH3 project (.atlaswh3) for the editors."));
        file.Items.Add(StandardMenus.Item("_Settings…", OpenSettings, tooltip: "Game and Assembly Kit folders, developer mode and walkthroughs."));
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
        tiles.Children.Add(Tile(Theme.Glyph.Build, "Build", "Compile the map natively and write it into your campaign's pack.",
                                () => BuildWindow.Show(this, _paths, _project?.FilePath)).Spot("start.build"));
        tiles.Children.Add(Tile(Theme.Glyph.Scene, "Scene editor (preview)", "Props, entities, prefabs and layers, in 2D and 3D.",
                                () => new Scene.SceneWindow(_paths).Show()).Spot("start.scene"));
        tiles.Children.Add(Tile(Theme.Glyph.Tiles, "Tile map (preview)", "Paint the campaign tile map hex by hex, with live validation.",
                                () => new CampaignTileWindow(_paths).Show()).Spot("start.tiles"));
        tiles.Children.Add(Tile(Theme.Glyph.Battle, "Campaign battles (preview)", "Where battles start, settlement battle maps and redirects.",
                                () => CampaignBattlesWindow.Open(this, _paths)).Card("start.battles"));
        tiles.Children.Add(Tile(Theme.Glyph.Settings, "Settings", "The game and assembly kit folders, found through Steam.",
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
            var name = ProjectName(r);
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
            Text = "1. Make your map in the Assembly Kit's Terry editor. AtlasWH3 builds what you save there; it replaces BOB.\n" +
                   "2. Open Build (Ctrl+B), create a project for the map, pick your campaign's pack, and press Build all: it " +
                   "compiles the map and writes it into that pack (backing the pack up first).\n" +
                   "3. Pick that project under Project, above, to open the editors on it.",
        });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid });
        return dock;
    }

    /// <summary>The project picker (and, without a project, the map picker) plus a line naming what the editors will
    /// open. The assembly kit is the one in Settings.</summary>
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

        var project = new ComboBox { Margin = new Thickness(0, 2, 0, 2) }.Card("start.project");
        project.Items.Add(new ComboBoxItem { Content = "No project: a map from the assembly kit, game packs only", Tag = "" });
        var projects = AppSettings.Current.RecentProjects.Where(File.Exists).ToList();
        if (_project?.FilePath is { } current && !projects.Contains(current, StringComparer.OrdinalIgnoreCase)) projects.Insert(0, current);
        foreach (var r in projects) project.Items.Add(new ComboBoxItem { Content = $"{ProjectName(r)}   ({Path.GetFileName(r)})", Tag = r, ToolTip = r });
        project.SelectedItem = project.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((string)i.Tag).Equals(_project?.FilePath ?? "", StringComparison.OrdinalIgnoreCase))
                               ?? project.Items[0];
        project.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || project.SelectedItem is not ComboBoxItem { Tag: string file }) return;
            SelectProject(file.Length > 0 ? file : null);
        };
        var openProject = new Button { Content = "Open…", Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 2, 0, 2), ToolTip = "Open a project (.atlaswh3)." };
        openProject.Click += (_, _) => OpenProject();
        var projectRow = new DockPanel();
        DockPanel.SetDock(openProject, Dock.Right);
        projectRow.Children.Add(openProject);
        projectRow.Children.Add(project);
        Add(Label("Project"), 0, 0);
        Add(projectRow, 0, 1);

        // with a project, the map is the project's: the line below names it
        var map = new ComboBox { Margin = new Thickness(0, 2, 0, 2) }.Card("start.map");
        map.Items.Add(new ComboBoxItem { Content = _paths.MapName, Tag = _paths.MapName });
        map.SelectedIndex = 0;
        if (_project is null)
        {
            Add(Label("Map"), 1, 0);
            Add(map, 1, 1);
        }

        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 6, 0, 0) };
        line.Inlines.Add("Editors open ");
        line.Inlines.Add(new System.Windows.Documents.Run(_paths.MapName) { Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(" from ");
        line.Inlines.Add(new System.Windows.Documents.Run(_paths.AssemblyKitRoot) { Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(_project is null ? "   ·   the game's packs only"
            : _paths.ModPacks.Count == 0 ? "   ·   no mod packs"
            : $"   ·   mod packs (read-only): {string.Join(", ", _paths.ModPacks.Select(Path.GetFileName))}");
        var (cardTitle, cardText) = InfoCards.Get("start.selection");
        line.Card(cardTitle, cardText + $"\nKit: {_paths.AssemblyKitRoot}" + (_project?.FilePath is { } f ? $"\nProject: {f}" : ""));
        Add(line, 2, 1);
        box.Child = grid;

        map.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || map.SelectedItem is not ComboBoxItem { Tag: string name } || name == _paths.MapName) return;
            SelectMap(name);
        };
        if (_project is null) _ = FillMapsAsync(map);
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
            foreach (var m in maps) map.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Name });
            if (!maps.Any(m => m.Name.Equals(_paths.MapName, StringComparison.OrdinalIgnoreCase)))
                map.Items.Insert(0, new ComboBoxItem
                {
                    Content = $"{_paths.MapName}  (missing: the assembly kit has no {_paths.MapName}.terry)", Tag = _paths.MapName,
                    ToolTip = Path.Combine(_paths.AkTerrainDir, _paths.MapName + ".terry"),
                });
            map.SelectedItem = map.Items.OfType<ComboBoxItem>().First(i => ((string)i.Tag).Equals(_paths.MapName, StringComparison.OrdinalIgnoreCase));
        }
        finally { _rebuilding = false; }
    }

    /// <summary>Switches the map, remembers it and rebuilds the page (menus included) on the new paths.</summary>
    private void SelectMap(string map)
    {
        var s = AppSettings.Current;
        _paths = _paths with { MapName = map };
        s.MapName = map;
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
        // pick up the saved folders
        (_project, _paths) = Selection(new ProjectPaths());
        Rebuild();
    }

    private void OpenProject()
    {
        var dlg = new OpenFileDialog { Title = "Open a project", Filter = $"AtlasWH3 project (*{BuildProject.Extension})|*{BuildProject.Extension}" };
        if (dlg.ShowDialog(this) == true) SelectProject(dlg.FileName);
    }

    /// <summary>Makes <paramref name="file"/> (null = none) the project the editors open on, remembers it and rebuilds the page.</summary>
    private void SelectProject(string? file)
    {
        var s = AppSettings.Current;
        s.CurrentProject = file is null ? "" : Path.GetFullPath(file);
        if (file is not null) s.AddRecentProject(file);
        try { s.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorDialog.Log("Could not save the project choice", e); }
        (_project, _paths) = Selection(new ProjectPaths());
        if (file is not null && _project is null) ErrorDialog.Show(this, $"Could not open {Path.GetFileName(file)}: see the log.", null);
        Rebuild();
    }

    /// <summary>The remembered project and its paths; without one (or with a map or packs given on the command line), the
    /// kit's remembered map on <paramref name="paths"/>.</summary>
    private static (BuildProject?, ProjectPaths) Selection(ProjectPaths paths)
    {
        var s = AppSettings.Current;
        if (paths.MapName == MapCatalog.DefaultMap && paths.ModPacks.Count == 0 && s.CurrentProject.Length > 0 && File.Exists(s.CurrentProject))
        {
            try
            {
                var project = BuildProject.Load(s.CurrentProject);
                return (project, project.ToPaths(paths));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                ErrorDialog.Log($"Could not open the project {s.CurrentProject}", e);
            }
        }
        return (null, MapCatalog.WithSelection(paths, s));
    }

    private static string ProjectName(string path)
    {
        try { return BuildProject.Load(path) is var p && p.Name.Length > 0 ? p.Name : Path.GetFileNameWithoutExtension(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
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
