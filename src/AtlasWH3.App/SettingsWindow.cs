using System.IO;
using System.Windows;
using System.Windows.Controls;
using AtlasWH3.Core;
using Microsoft.Win32;

namespace AtlasWH3.App;

/// <summary>
/// Settings and first-run setup: the game and assembly kit folders (found through Steam, each checked live), AtlasWH3's
/// own data folder (shown, rarely moved), developer mode and the walkthroughs. Everything about a map (its mod packs,
/// tile map source, build) lives in its project. Paths apply to windows opened after saving.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s = AppSettings.Current;
    private readonly bool _firstRun;
    private readonly TextBox _game = new(), _kit = new();
    private readonly TextBox _data = new() { IsReadOnly = true };
    /// <summary>The data folder setting: empty = <see cref="Defaults.LocalData"/>.</summary>
    private string _dataSetting;
    private readonly string _detectedGame = GameSetup.FindGameFolder() ?? "";
    private readonly CheckBox _dev = new()
    {
        Content = "Developer mode (BOB launch, tile-matching simulation, comparison tools, self-tests)",
    };

    public SettingsWindow(bool firstRun = false)
    {
        _firstRun = firstRun;
        _dataSetting = _s.DataFolder;
        Title = AppInfo.Title(firstRun ? "Welcome" : "Settings");
        Width = 860;
        SizeToContent = SizeToContent.Height;
        MinHeight = 300;
        WindowStartupLocation = firstRun ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Panel");
        Foreground = Theme.Brush("Text");

        var panel = new StackPanel { Margin = new Thickness(18) };
        if (firstRun)
        {
            panel.Children.Add(new TextBlock { Text = $"Welcome to {AppInfo.Product}", FontSize = 22, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 6, 0, 6),
                Text = "AtlasWH3 found these through Steam: check them and continue. Nothing of the game's is shipped with AtlasWH3: " +
                       "it reads your own install and packs, and never writes to them.",
            });
        }
        var folders = new StackPanel().Spot("settings.folders");
        panel.Children.Add(folders);
        folders.Children.Add(Theme.Header("Folders", firstRun ? 8 : 0));
        folders.Children.Add(PathRow("Game folder", _game, _s.GameFolder, () => _detectedGame, "found through Steam",
                                     p => Directory.Exists(Path.Combine(p, "data")), "settings.game"));
        folders.Children.Add(PathRow("Assembly kit", _kit, _s.AssemblyKit, () => KitFor(Game), "the game's assembly_kit",
                                     p => Directory.Exists(Path.Combine(p, "raw_data")), "settings.kit"));
        // the kit follows the game folder while it is still the game's own assembly_kit
        var kitAuto = KitFor(Game);
        _game.TextChanged += (_, _) =>
        {
            var next = KitFor(Game);
            if (_kit.Text.Trim().Equals(kitAuto, StringComparison.OrdinalIgnoreCase)) _kit.Text = next;
            kitAuto = next;
        };
        folders.Children.Add(DataRow());
        _dev.Card("settings.dev");

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
        Walkthrough.Enable(this, "settings", autoStart: !firstRun);
        InputBindings.Add(new System.Windows.Input.KeyBinding(new RelayCommand(() => Walkthrough.Start(this)), System.Windows.Input.Key.F1,
                                                              System.Windows.Input.ModifierKeys.None));
    }

    /// <summary>A folder row. An empty setting shows <paramref name="auto"/>'s path in the box, and that path is saved
    /// back as empty, so it keeps following the detection.</summary>
    private UIElement PathRow(string label, TextBox box, string value, Func<string> auto, string autoNote, Func<string, bool> valid, string card)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) }.Card(card);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var mark = Theme.Icon(Theme.Glyph.Check, 13);
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);
        box.Tag = auto;
        box.Text = value.Trim().Length > 0 ? value.Trim() : auto();
        var note = new TextBlock { Foreground = Theme.Brush("DimText"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        void Check()
        {
            var path = Effective(box);
            var ok = path.Length > 0 && valid(path);
            mark.Text = ok ? Theme.Glyph.Check : Theme.Glyph.Warning;
            mark.Foreground = Theme.Brush(ok ? "Ok" : "Warn");
            mark.ToolTip = ok ? path : path.Length > 0 ? $"not found: {path}" : "not found: choose the folder";
            note.Text = path.Length > 0 && IsAuto(box) ? autoNote : "";
        }
        box.TextChanged += (_, _) => Check();
        Check();
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);
        Grid.SetColumn(note, 3);
        grid.Children.Add(note);
        var browse = new Button { Content = "…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Choose the {label.ToLowerInvariant()} folder." };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = label };
            if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
        };
        Grid.SetColumn(browse, 4);
        grid.Children.Add(browse);
        return grid;
    }

    /// <summary>AtlasWH3's own data folder: shown and openable; moved only on purpose.</summary>
    private UIElement DataRow()
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 0) }.Card("settings.data");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(152) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "AtlasWH3 data", VerticalAlignment = VerticalAlignment.Center });
        _data.Foreground = Theme.Brush("DimText");
        Grid.SetColumn(_data, 1);
        grid.Children.Add(_data);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button B(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        Button? reset = null;
        void ShowData()
        {
            _data.Text = DataFolder;
            if (reset is not null) reset.Visibility = _dataSetting.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        B("Open", "Show the folder in Explorer.", () => StandardMenus.OpenFolder(DataFolder));
        B("Move…", "Keep AtlasWH3's data somewhere else. What is already in the old folder stays there.", () =>
        {
            var dlg = new OpenFolderDialog { Title = "AtlasWH3 data folder" };
            if (dlg.ShowDialog(this) != true) return;
            _dataSetting = Path.GetFullPath(dlg.FolderName).Equals(Defaults.LocalData, StringComparison.OrdinalIgnoreCase) ? "" : dlg.FolderName;
            ShowData();
        });
        reset = B("Default", $"Back to {Defaults.LocalData}.", () => { _dataSetting = ""; ShowData(); });
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        box.Children.Add(grid);
        box.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), FontSize = 11, Margin = new Thickness(152, 2, 0, 0),
            Text = "Where AtlasWH3 keeps its build output, logs, edit history and caches. There is no need to " +
                   "change it. Move it only if you like your drives tidy: the old folder's contents are not moved along.",
        });
        ShowData();
        return box;
    }

    private static string Effective(TextBox box) =>
        box.Text.Trim().Length > 0 ? box.Text.Trim() : (box.Tag as Func<string>)?.Invoke() ?? "";

    /// <summary>True when the box holds its auto-detected path (or nothing), so it is saved as empty.</summary>
    private static bool IsAuto(TextBox box) =>
        box.Text.Trim().Length == 0 || box.Text.Trim().Equals((box.Tag as Func<string>)?.Invoke() ?? "", StringComparison.OrdinalIgnoreCase);

    private static string KitFor(string game) => game.Length > 0 ? Path.Combine(game, "assembly_kit") : "";

    private string Game => Effective(_game);
    private string GameData => Path.Combine(Game, "data");
    private string DataFolder => _dataSetting.Length > 0 ? _dataSetting : Defaults.LocalData;

    private bool Save()
    {
        if (SourceGuard.WhyProtected(DataFolder, GameData) is { } why)
        {
            MessageBox.Show(this, $"AtlasWH3 data: {why}.\n\nChoose a folder outside the game's data folder.", AppInfo.Product,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _s.GameFolder = IsAuto(_game) ? "" : _game.Text.Trim();
        _s.AssemblyKit = IsAuto(_kit) ? "" : _kit.Text.Trim();
        _s.DataFolder = _dataSetting;
        _s.DeveloperMode = _dev.IsChecked == true;
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
