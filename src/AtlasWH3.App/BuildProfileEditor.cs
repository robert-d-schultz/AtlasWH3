using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AtlasWH3.Core;
using AtlasWH3.Core.Build;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;
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
    /// <summary>Each text field's commit (it runs on losing focus; <see cref="CommitPending"/> runs them all).</summary>
    private readonly List<Action> _commits = [];
    private DataGrid? _customGrid;
    /// <summary>A custom-step cell edit ended and its deferred change callback has not run yet; the version drops a
    /// deferred callback that <see cref="CommitPending"/> already ran.</summary>
    private bool _gridChanged;
    private int _gridVersion;

    public BuildProfileEditor(BuildProject project, Func<ProjectPaths> paths, Action changed)
    {
        _project = project;
        _paths = paths;
        _changed = changed;
        _custom = new ObservableCollection<CustomStep>(project.Build.CustomSteps);
        _custom.CollectionChanged += (_, _) => { project.Build.CustomSteps = [.. _custom]; changed(); };
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Content = BuildForm();
    }

    /// <summary>Applies whatever is being typed (a text field, a custom-step cell) to the project now: Save, Build and
    /// closing call this, since a key binding leaves the focus where it is.</summary>
    public void CommitPending()
    {
        foreach (var commit in _commits) commit();
        _customGrid?.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        if (!_gridChanged) return;
        _gridChanged = false;
        _gridVersion++;
        _project.Build.CustomSteps = [.. _custom];
        _changed();
    }

    private UIElement BuildForm()
    {
        var b = _project.Build;
        var form = new StackPanel { Margin = new Thickness(14, 6, 14, 14), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };

        form.Children.Add(Theme.Header("Project", 4).Card("profile.header.project"));
        form.Children.Add(Field("Name", () => _project.Name, v => _project.Name = v, key: "profile.name"));
        form.Children.Add(Field("Map", () => _project.Map, v => _project.Map = v, key: "profile.map"));

        form.Children.Add(Theme.Header("Pack").Card("profile.header.pack"));
        form.Children.Add(Check("Back up the pack before each build", () => b.BackupPack, v => b.BackupPack = v, "profile.backupPack"));
        form.Children.Add(Field("Pack file", () => b.PackFile, v => b.PackFile = v, Browse.SaveFile, "profile.packFile",
                                () => _project.PackOutput(_paths())));
        form.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(160, 2, 0, 6),
            Text = "Your campaign's pack. The build writes the compiled map into it: its own files are replaced, everything else " +
                   "(DB, scripts, map_data.esf, pathfinding.ppd) is kept.",
        });
        form.Children.Add(Field("Pieces pack (optional)", () => b.PiecesPackFile, v => b.PiecesPackFile = v,
                                Browse.SaveFile, "profile.piecesPackFile", () => _project.PiecesPackOutput(_paths())));
        form.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(160, 2, 0, 6),
            Text = "Only for a campaign that ships its event-area pieces in a pack of their own, like Old World: the map's " +
                   "and the devastated map's pieces go there instead. Empty: everything goes into the pack above.",
        });
        form.Children.Add(ListField("Other mod packs", "profile.modPacks", () => _project.ModPacks, v => _project.ModPacks = v, ordered: true, entry: null,
            ("Add…", () =>
            {
                var game = _paths().GameDataDir;
                var dlg = new OpenFileDialog
                {
                    Title = "Other mod packs the map uses (only read)", Filter = "Pack files (*.pack)|*.pack", Multiselect = true,
                    InitialDirectory = Directory.Exists(game) ? game : "",
                };
                return dlg.ShowDialog() == true ? dlg.FileNames : [];
            })));

        form.Children.Add(Theme.Header("Compile").Card("profile.header.compile"));
        var output = Row("Compiled files", "profile.output");
        var outputPath = new TextBlock { Text = _project.OutputDir(_paths()), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(outputPath, 1);
        output.Children.Add(outputPath);
        form.Children.Add(output);
        form.Children.Add(AcceptedErrors(b));
        form.Children.Add(Combo("Patch mask", Enum.GetValues<PatchMaskMode>(), () => b.PatchMask, v => b.PatchMask = v, "profile.patchMask"));
        form.Children.Add(Check("Keep props outside the quadtree", () => b.KeepOutsideQuadtree, v => b.KeepOutsideQuadtree = v, "profile.keepOutsideQuadtree"));
        form.Children.Add(Check("Shroud at 0 along the west edge", () => b.ZeroShroudWestEdge, v => b.ZeroShroudWestEdge = v, "profile.zeroShroudWestEdge"));
        form.Children.Add(ListField("Lookup: regions past 1024", "profile.lookupLast", () => b.LookupLast, v => b.LookupLast = v, ordered: false,
            entry: new TextBox { ToolTip = "A region key, e.g. wh3_main_combi_region_altdorf" },
            ("Add file…", () =>
            {
                var dlg = new OpenFileDialog { Title = "Text files of region keys (one per line)", Filter = "Text files (*.txt)|*.txt|All files|*.*", Multiselect = true };
                return dlg.ShowDialog() == true ? dlg.FileNames : [];
            })));

        form.Children.Add(Theme.Header("Custom steps (advanced)").Card("profile.header.custom"));
        form.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 0, 0, 6),
            Text = "External commands run as part of the build (python scripts, CAIME, an RPFM startpos build…). Tokens in command, arguments and " +
                   "working folder: {project} (the project's folder), {ak} (the Assembly Kit), {map}, {game} (the game's data folder), " +
                   "{out} (the compiled files) and {pack} (the pack file). They also get ATLASWH3_AK, ATLASWH3_MAP, ATLASWH3_OUT, " +
                   "ATLASWH3_GAME, ATLASWH3_PACK, ATLASWH3_PROJECT and ATLASWH3_CLI as environment variables.",
        });
        form.Children.Add(CustomGrid());
        return form;
    }

    // ------------------------------------------------------------------ lists

    /// <summary>An editable list: its items, the <paramref name="adds"/> buttons (each returns the items to add), an
    /// optional <paramref name="entry"/> box with its own Add, Remove, and Up / Down when <paramref name="ordered"/>.</summary>
    private UIElement ListField(string label, string key, Func<List<string>> get, Action<List<string>> set, bool ordered, TextBox? entry,
                                params (string Text, Func<IEnumerable<string>> Add)[] adds)
    {
        var grid = Row(label, key);
        grid.Children.OfType<TextBlock>().First().VerticalAlignment = VerticalAlignment.Top;
        var list = new ListBox { MinHeight = 48, MaxHeight = 150 };
        foreach (var item in get()) list.Items.Add(item);
        void Commit() { set(list.Items.OfType<string>().ToList()); _changed(); }
        void AddItems(IEnumerable<string> items)
        {
            foreach (var item in items.Select(i => i.Trim()).Where(i => i.Length > 0))
                if (!list.Items.OfType<string>().Contains(item, StringComparer.OrdinalIgnoreCase)) list.Items.Add(item);
            Commit();
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        Button B(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        if (entry is not null)
        {
            entry.MinWidth = 260;
            entry.Margin = new Thickness(0, 0, 4, 0);
            buttons.Children.Add(entry);
            void AddEntry() { AddItems([entry.Text]); entry.Clear(); }
            entry.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddEntry(); };
            B("Add", AddEntry);
        }
        foreach (var (text, add) in adds) B(text, () => AddItems(add()));
        B("Remove", () => { if (list.SelectedItem is { } item) { list.Items.Remove(item); Commit(); } });
        if (ordered)
        {
            void Move(int delta)
            {
                var i = list.SelectedIndex;
                if (i < 0 || i + delta < 0 || i + delta >= list.Items.Count) return;
                var item = list.Items[i];
                list.Items.RemoveAt(i);
                list.Items.Insert(i + delta, item);
                list.SelectedIndex = i + delta;
                Commit();
            }
            B("Up", () => Move(-1));
            B("Down", () => Move(1));
        }
        var panel = new StackPanel { Children = { list, buttons } };
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    /// <summary>The tile-map errors the build may go ahead with, one tick box each.</summary>
    private UIElement AcceptedErrors(BuildProfile b)
    {
        var grid = Row("Accepted tile-map errors", "profile.acceptTileMap");
        grid.Children.OfType<TextBlock>().First().VerticalAlignment = VerticalAlignment.Top;
        var panel = new StackPanel();
        var codes = TileMapValidator.ErrorCodes.ToList();
        foreach (var other in b.AcceptTileMap.Where(c => codes.All(k => k.Code != c)))
            codes.Add((other, "No longer reported by the tile map check."));
        foreach (var (code, meaning) in codes)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(meaning);
            text.Inlines.Add(new System.Windows.Documents.Run("   " + code) { Foreground = Theme.Brush("DimText"), FontSize = 11 });
            var box = new CheckBox { Content = text, IsChecked = b.AcceptTileMap.Contains(code), Margin = new Thickness(0, 2, 0, 2) };
            box.Click += (_, _) =>
            {
                b.AcceptTileMap.Remove(code);
                if (box.IsChecked == true) b.AcceptTileMap.Add(code);
                _changed();
            };
            panel.Children.Add(box);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Ticked: the build goes ahead when the tile map has this. Only tick what you know the game tolerates.",
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
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
        grid.Columns.Add(new DataGridComboBoxColumn { Header = ColumnHeader("Runs", "profile.custom.runs"), ItemsSource = Enum.GetValues<CustomStepStage>().Where(s => s != CustomStepStage.AfterInstall).ToList(), SelectedItemBinding = new Binding(nameof(CustomStep.RunAt)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Command", "profile.custom.command"), Binding = new Binding(nameof(CustomStep.Command)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Arguments", "profile.custom.arguments"), Binding = new Binding(nameof(CustomStep.Arguments)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Working folder", "profile.custom.workingDir"), Binding = new Binding(nameof(CustomStep.WorkingDir)), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = ColumnHeader("Timeout (s)", "profile.custom.timeout"), Binding = new Binding(nameof(CustomStep.TimeoutSeconds)), Width = 80 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = ColumnHeader("Continue on error", "profile.custom.continue"), Binding = new Binding(nameof(CustomStep.ContinueOnError)) });
        _customGrid = grid;
        // deferred: the cell's binding writes the value after this event
        grid.CellEditEnding += (_, _) =>
        {
            _gridChanged = true;
            var version = _gridVersion;
            Dispatcher.BeginInvoke(() =>
            {
                if (version != _gridVersion) return;   // CommitPending ran it already
                _gridChanged = false;
                _project.Build.CustomSteps = [.. _custom];
                _changed();
            });
        };

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

    /// <summary>A text field showing the setting as saved (tokens such as {game} kept) and, with
    /// <paramref name="resolved"/>, the path it stands for underneath. It commits on losing focus and on
    /// <see cref="CommitPending"/>.</summary>
    private UIElement Field(string label, Func<string> get, Action<string> set, Browse browse = Browse.None, string? key = null,
                            Func<string?>? resolved = null)
    {
        var grid = Row(label, key);
        var box = new TextBox { Text = get(), VerticalContentAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { Foreground = Theme.Brush("DimText"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        void ShowResolved()
        {
            var full = resolved?.Invoke();
            hint.Text = full is not null && !full.Equals(get(), StringComparison.OrdinalIgnoreCase) ? full : "";
            hint.Visibility = hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void Commit()
        {
            var text = box.Text.Trim();
            if (text == get()) return;
            set(text);
            ShowResolved();
            _changed();
        }
        _commits.Add(Commit);
        box.LostFocus += (_, _) => Commit();
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        if (resolved is not null)
        {
            // the resolved path on a second row under the box
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(hint, 1);
            Grid.SetColumn(hint, 1);
            grid.Children.Add(hint);
            ShowResolved();
        }
        if (browse != Browse.None)
        {
            var button = new Button { Content = "…", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0) }.Card("profile.browse");
            button.Click += (_, _) =>
            {
                string? picked = browse switch
                {
                    Browse.Folder => new OpenFolderDialog() is var f && f.ShowDialog() == true ? f.FolderName : null,
                    Browse.File => new OpenFileDialog() is var o && o.ShowDialog() == true ? o.FileName : null,
                    _ => new SaveFileDialog
                    {
                        Filter = "Pack (*.pack)|*.pack|All files|*.*", OverwritePrompt = false,
                        InitialDirectory = Directory.Exists(_paths().GameDataDir) ? _paths().GameDataDir : "",
                    } is var s && s.ShowDialog() == true ? s.FileName : null,
                };
                if (picked is null) return;
                if (browse != Browse.Folder) picked = BuildProject.WithTokens(picked, _paths(), _project.Folder);
                box.Text = picked;
                Commit();
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

    /// <summary>A grid column header with an info card.</summary>
    private static TextBlock ColumnHeader(string text, string key) => new TextBlock { Text = text }.Card(key);
}
