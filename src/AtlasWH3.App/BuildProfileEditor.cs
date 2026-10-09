using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AtlasWH3.Core;
using AtlasWH3.Core.Build;
using AtlasWH3.Core.Campaign;
using Microsoft.Win32;

namespace AtlasWH3.App;

/// <summary>The Build window's Profile tab: edits a <see cref="BuildProject"/> in place and calls back on every change.</summary>
public sealed class BuildProfileEditor : ScrollViewer
{
    private enum Browse { None, Folder, File, SaveFile }

    private readonly BuildProject _project;
    private readonly Func<ProjectPaths> _paths;
    private readonly Action _changed;
    private readonly ObservableCollection<CustomStep> _custom;
    private readonly ObservableCollection<PackContent> _contents;

    public BuildProfileEditor(BuildProject project, Func<ProjectPaths> paths, Action changed)
    {
        _project = project;
        _paths = paths;
        _changed = changed;
        _custom = new ObservableCollection<CustomStep>(project.Build.CustomSteps);
        _contents = new ObservableCollection<PackContent>(project.Build.Pack.Contents);
        _custom.CollectionChanged += (_, _) => { project.Build.CustomSteps = [.. _custom]; changed(); };
        _contents.CollectionChanged += (_, _) => { project.Build.Pack.Contents = [.. _contents]; changed(); };
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = BuildForm();
    }

    private UIElement BuildForm()
    {
        var b = _project.Build;
        var form = new StackPanel { Margin = new Thickness(14, 6, 14, 14), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };

        form.Children.Add(Theme.Header("Project", 4).Card("profile.header.project"));
        form.Children.Add(Field("Name", () => _project.Name, v => _project.Name = v, key: "profile.name"));
        form.Children.Add(Field("Map", () => _project.Map, v => _project.Map = v, key: "profile.map"));
        form.Children.Add(Field("Assembly kit", () => _project.AssemblyKit, v => _project.AssemblyKit = v, Browse.Folder, "profile.kit"));
        form.Children.Add(Field("Game data", () => _project.GameData, v => _project.GameData = v, Browse.Folder, "profile.gameData"));
        form.Children.Add(Field("Mod packs", () => string.Join("; ", _project.ModPacks), v => _project.ModPacks = Split(v, ';'),
                                key: "profile.modPacks"));

        form.Children.Add(Theme.Header("Tile map source").Card("profile.header.tileMap"));
        var tileMap = new TileMapSourcePanel(_project.TileMap, _paths);
        tileMap.Changed += () => { _project.TileMap = tileMap.StoredValue; _changed(); };
        form.Children.Add(tileMap);

        form.Children.Add(Theme.Header("Compile").Card("profile.header.compile"));
        form.Children.Add(Field("Output folder", () => b.Output, v => b.Output = v, Browse.Folder, "profile.output"));
        form.Children.Add(Field("Accepted tile-map errors", () => string.Join(", ", b.AcceptTileMap), v => b.AcceptTileMap = Split(v, ','),
                                key: "profile.acceptTileMap"));
        form.Children.Add(Combo("Patch mask", Enum.GetValues<PatchMaskMode>(), () => b.PatchMask, v => b.PatchMask = v, "profile.patchMask"));
        form.Children.Add(Field("Devastated project", () => b.DevastatedMap, v => b.DevastatedMap = v.Trim(), key: "profile.devastatedMap"));
        form.Children.Add(Field("Delete before compile", () => string.Join(", ", b.Clean), v => b.Clean = Split(v, ','),
                                key: "profile.clean"));
        form.Children.Add(Field("Terrain backup folder", () => b.Backup, v => b.Backup = v, Browse.Folder, "profile.backup"));

        form.Children.Add(Theme.Header("Custom steps").Card("profile.header.custom"));
        form.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 0, 0, 6),
            Text = "External commands run as part of the build (python scripts, CAIME, an RPFM startpos build…). Tokens in command, arguments and " +
                   "working folder: {project} {ak} {map} {game} {out} {pack}. They also get ATLASWH3_AK, ATLASWH3_MAP, ATLASWH3_OUT, ATLASWH3_GAME, " +
                   "ATLASWH3_PACK, ATLASWH3_PROJECT and ATLASWH3_CLI as environment variables.",
        });
        form.Children.Add(CustomGrid());

        form.Children.Add(Theme.Header("Pack").Card("profile.header.pack"));
        form.Children.Add(Combo("Pack mode", Enum.GetValues<PackMode>(), () => b.Pack.Mode, v => b.Pack.Mode = v, "profile.packMode"));
        form.Children.Add(Field("Output pack", () => b.Pack.Output, v => b.Pack.Output = v, Browse.SaveFile, "profile.packOutput"));
        form.Children.Add(Field("Merge base", () => b.Pack.Base, v => b.Pack.Base = v, Browse.File, "profile.packBase"));
        form.Children.Add(Field("Replace folders", () => string.Join(", ", b.Pack.ReplaceDirs), v => b.Pack.ReplaceDirs = Split(v, ','),
                                key: "profile.replaceDirs"));
        form.Children.Add(new TextBlock { Text = "Contents (later rows win for the same pack path)", Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 8, 0, 4) }.Card("profile.contents"));
        form.Children.Add(ContentsGrid());

        form.Children.Add(Theme.Header("Install").Card("profile.header.install"));
        form.Children.Add(Check("Keep a backup of the pack being replaced", () => b.Install.Backup, v => b.Install.Backup = v, "profile.installBackup"));
        form.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"),
            Text = "Install copies the output pack into the game's data folder (skipped when it is already there). It refuses while the game is running.",
        });
        return form;
    }

    // ------------------------------------------------------------------ grids

    private UIElement CustomGrid()
    {
        var grid = new DataGrid
        {
            ItemsSource = _custom, AutoGenerateColumns = false, CanUserAddRows = false, MinHeight = 90, MaxHeight = 260,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
        };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = ColumnHeader("On", "profile.custom.on"), Binding = new Binding(nameof(CustomStep.Enabled)) });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Name", "profile.custom.name"), Binding = new Binding(nameof(CustomStep.Name)), Width = 140 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = ColumnHeader("Runs", "profile.custom.runs"), ItemsSource = Enum.GetValues<CustomStepStage>(), SelectedItemBinding = new Binding(nameof(CustomStep.RunAt)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Command", "profile.custom.command"), Binding = new Binding(nameof(CustomStep.Command)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Arguments", "profile.custom.arguments"), Binding = new Binding(nameof(CustomStep.Arguments)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Working folder", "profile.custom.workingDir"), Binding = new Binding(nameof(CustomStep.WorkingDir)), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Timeout (s)", "profile.custom.timeout"), Binding = new Binding(nameof(CustomStep.TimeoutSeconds)), Width = 80 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = ColumnHeader("Continue on error", "profile.custom.continue"), Binding = new Binding(nameof(CustomStep.ContinueOnError)) });
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(() => { _project.Build.CustomSteps = [.. _custom]; _changed(); });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.New, "Add", (_, _) =>
        {
            var name = UniqueName("step");
            _custom.Add(new CustomStep { Name = name, Command = "python", Arguments = "script.py" });
            grid.SelectedIndex = _custom.Count - 1;
        }).Card("profile.custom.add"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Delete, "Remove", (_, _) => { if (grid.SelectedItem is CustomStep s) _custom.Remove(s); }).Card("profile.custom.remove"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Up, "Up", (_, _) => Move(grid, -1)).Card("profile.custom.up"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Down, "Down", (_, _) => Move(grid, +1)).Card("profile.custom.down"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Open, "Browse command…", (_, _) =>
        {
            if (grid.SelectedItem is not CustomStep s) return;
            var dlg = new OpenFileDialog { Filter = "Programs and scripts|*.exe;*.bat;*.cmd;*.py;*.ps1|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            if (dlg.FileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) { s.Command = "python"; s.Arguments = $"\"{dlg.FileName}\""; }
            else if (dlg.FileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) { s.Command = "powershell"; s.Arguments = $"-ExecutionPolicy Bypass -File \"{dlg.FileName}\""; }
            else s.Command = dlg.FileName;
            grid.Items.Refresh();
            _project.Build.CustomSteps = [.. _custom];
            _changed();
        }).Card("profile.custom.browse"));
        return new StackPanel { Children = { grid, buttons } };
    }

    private UIElement ContentsGrid()
    {
        var grid = new DataGrid
        {
            ItemsSource = _contents, AutoGenerateColumns = false, CanUserAddRows = false, MinHeight = 90, MaxHeight = 240,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Source (file or folder on disk)", "profile.contents.source"), Binding = new Binding(nameof(PackContent.Source)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Path in pack", "profile.contents.path"), Binding = new Binding(nameof(PackContent.Path)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = ColumnHeader("Optional", "profile.contents.optional"), Binding = new Binding(nameof(PackContent.Optional)) });
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(() => { _project.Build.Pack.Contents = [.. _contents]; _changed(); });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.New, "Add folder…", (_, _) =>
        {
            var dlg = new OpenFolderDialog();
            if (dlg.ShowDialog() == true) _contents.Add(new PackContent { Source = dlg.FolderName, Path = "" });
        }).Card("profile.contents.addFolder"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.New, "Add file…", (_, _) =>
        {
            var dlg = new OpenFileDialog();
            if (dlg.ShowDialog() == true) _contents.Add(new PackContent { Source = dlg.FileName, Path = Path.GetFileName(dlg.FileName) });
        }).Card("profile.contents.addFile"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Delete, "Remove", (_, _) => { if (grid.SelectedItem is PackContent c) _contents.Remove(c); }).Card("profile.contents.remove"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Build, "Default contents", (_, _) =>
        {
            _contents.Clear();
            foreach (var c in BuildProject.CreateDefault(_project.Map).Build.Pack.Contents) _contents.Add(c);
        }).Card("profile.contents.default"));
        return new StackPanel { Children = { grid, buttons } };
    }

    private void Move(DataGrid grid, int delta)
    {
        var i = grid.SelectedIndex;
        if (i < 0 || i + delta < 0 || i + delta >= _custom.Count) return;
        _custom.Move(i, i + delta);
        grid.SelectedIndex = i + delta;
    }

    private string UniqueName(string stem)
    {
        for (var n = 1; ; n++)
            if (_custom.All(c => c.Name != $"{stem} {n}")) return $"{stem} {n}";
    }

    // ------------------------------------------------------------------ fields

    private UIElement Field(string label, Func<string> get, Action<string> set, Browse browse = Browse.None, string? key = null)
    {
        var grid = Row(label, key);
        var box = new TextBox { Text = get(), VerticalContentAlignment = VerticalAlignment.Center };
        box.LostFocus += (_, _) =>
        {
            if (box.Text == get()) return;
            set(box.Text.Trim());
            _changed();
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        if (browse != Browse.None)
        {
            var button = new Button { Content = "…", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), }.Card("profile.browse");
            button.Click += (_, _) =>
            {
                string? picked = browse switch
                {
                    Browse.Folder => new OpenFolderDialog() is var f && f.ShowDialog() == true ? f.FolderName : null,
                    Browse.File => new OpenFileDialog() is var o && o.ShowDialog() == true ? o.FileName : null,
                    _ => new SaveFileDialog { Filter = "Pack (*.pack)|*.pack|All files|*.*" } is var s && s.ShowDialog() == true ? s.FileName : null,
                };
                if (picked is null) return;
                box.Text = picked;
                set(picked);
                _changed();
            };
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        return grid;
    }

    private UIElement Combo<T>(string label, T[] values, Func<T> get, Action<T> set, string? key = null) where T : struct, Enum
    {
        var grid = Row(label, key);
        var combo = new ComboBox { ItemsSource = values, SelectedItem = get(), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is T v) { set(v); _changed(); } };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return grid;
    }

    private UIElement Check(string label, Func<bool> get, Action<bool> set, string key)
    {
        var box = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(160, 4, 0, 4) }.Card(key);
        box.Click += (_, _) => { set(box.IsChecked == true); _changed(); };
        return box;
    }

    private static Grid Row(string label, string? key)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        if (key is not null) grid.Card(key);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }

    private static List<string> Split(string v, char sep) =>
        v.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>A grid column header with an info card.</summary>
    private static TextBlock ColumnHeader(string text, string key) => new TextBlock { Text = text }.Card(key);
}
