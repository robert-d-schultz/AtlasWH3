using System.IO;
using System.Windows;
using System.Windows.Controls;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign.TileMapCheck;
using Microsoft.Win32;

namespace AtlasWH3.App;

/// <summary>
/// Edits a <see cref="TileMapSource"/>: kit / file / pack, the path, the file inside the pack, and the folder the tile
/// map editor saves to (packs are never written). Shows the resolved source and save target for
/// <paramref name="paths"/>. Used by the tile map window (File &gt; Tile map source…), Settings and the project editor.
/// </summary>
public sealed class TileMapSourcePanel : StackPanel
{
    private readonly Func<ProjectPaths> _paths;
    private readonly ComboBox _kind = new() { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _path = new(), _entry = new(), _save = new();
    private readonly Grid _pathRow, _entryRow, _saveRow;
    private readonly TextBlock _resolved = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private bool _loading;

    public event Action? Changed;

    public TileMapSourcePanel(TileMapSource? value, Func<ProjectPaths> paths, double labelWidth = 130)
    {
        _paths = paths;
        _resolved.Foreground = Theme.Brush("DimText");
        _kind.Items.Add(new ComboBoxItem { Content = "Assembly kit raw_data", Tag = TileMapSourceKind.Kit });
        _kind.Items.Add(new ComboBoxItem { Content = "File or folder", Tag = TileMapSourceKind.File });
        _kind.Items.Add(new ComboBoxItem { Content = "Inside a .pack", Tag = TileMapSourceKind.Pack });
        Children.Add(Row("Read tile map from", _kind, labelWidth, null, "Where the tile map editor and the build's tile_list step read tile_map.png"));
        _pathRow = Row("Path", _path, labelWidth, BrowsePath, "File: a tile_map.png or the folder holding one. Pack: the .pack file. {map} allowed.");
        _entryRow = Row("File in pack", _entry, labelWidth, null, $"Empty = {TileMapSource.DefaultInternalPath}");
        _saveRow = Row("Save edits to", _save, labelWidth, BrowseSave,
                       "Folder for the edited tile_map.png. Empty = the kit's map folder (pack) or the file itself (file). Packs are never written.");
        Children.Add(_pathRow);
        Children.Add(_entryRow);
        Children.Add(_saveRow);
        Children.Add(_resolved);
        _kind.SelectionChanged += (_, _) => Update();
        foreach (var box in new[] { _path, _entry, _save }) box.TextChanged += (_, _) => Update();
        Value = value ?? TileMapSource.Kit;
    }

    public TileMapSource Value
    {
        get => new()
        {
            Kind = (_kind.SelectedItem as ComboBoxItem)?.Tag is TileMapSourceKind k ? k : TileMapSourceKind.Kit,
            Path = _path.Text.Trim(),
            InternalPath = _entry.Text.Trim(),
            SaveFolder = _save.Text.Trim(),
        };
        set
        {
            _loading = true;
            _kind.SelectedIndex = (int)value.Kind;
            _path.Text = value.Path;
            _entry.Text = value.InternalPath;
            _save.Text = value.SaveFolder;
            _loading = false;
            Update();
        }
    }

    /// <summary>The value to store: null for the plain kit default.</summary>
    public TileMapSource? StoredValue => Value is { IsKit: true } ? null : Value;

    private void Update()
    {
        var v = Value;
        _pathRow.Visibility = v.IsKit ? Visibility.Collapsed : Visibility.Visible;
        _entryRow.Visibility = v.Kind == TileMapSourceKind.Pack ? Visibility.Visible : Visibility.Collapsed;
        _saveRow.Visibility = v.IsKit ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var p = _paths();
            _resolved.Text = $"Reads {v.Describe(p)}\nSaves {v.EditPath(p)}" +
                             (v.SeparateTarget(p) ? "\nThe editor first copies the source there (one undoable step); the build reads that copy once it exists." : "");
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            _resolved.Text = e.Message;
        }
        if (!_loading) Changed?.Invoke();
    }

    private void BrowsePath()
    {
        if (Value.Kind == TileMapSourceKind.Pack)
        {
            var dlg = new OpenFileDialog { Filter = "Pack (*.pack)|*.pack|All files|*.*", Title = "Pack holding the tile map" };
            if (dlg.ShowDialog() == true) _path.Text = dlg.FileName;
        }
        else
        {
            var dlg = new OpenFileDialog { Filter = "Tile map (*.png)|*.png|All files|*.*", Title = "tile_map.png" };
            if (dlg.ShowDialog() == true) _path.Text = dlg.FileName;
        }
    }

    private void BrowseSave()
    {
        var dlg = new OpenFolderDialog { Title = "Folder to save the edited tile_map.png in" };
        if (dlg.ShowDialog() == true) _save.Text = dlg.FolderName;
    }

    private static Grid Row(string label, FrameworkElement input, double labelWidth, Action? browse, string tip)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = tip };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        if (browse is not null)
        {
            var b = new Button { Content = "…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Browse" };
            b.Click += (_, _) => browse();
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        return grid;
    }
}

/// <summary>The tile map window's File &gt; Tile map source… dialog.</summary>
public sealed class TileMapSourceDialog : Window
{
    private readonly TileMapSourcePanel _panel;
    private readonly CheckBox _default = new() { Content = "Also make this the default (Settings)", Margin = new Thickness(0, 8, 0, 0) };

    public TileMapSource? Result { get; private set; }
    public bool MakeDefault => _default.IsChecked == true;

    public TileMapSourceDialog(TileMapSource current, ProjectPaths paths)
    {
        Title = AppInfo.Title("Tile map source");
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Panel");
        Foreground = Theme.Brush("Text");
        _panel = new TileMapSourcePanel(current, () => paths);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(_panel);
        _default.Foreground = Theme.Brush("Text");
        root.Children.Add(_default);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Open", IsDefault = true, MinWidth = 90 };
        ok.Click += (_, _) => { Result = _panel.Value; DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) });
        root.Children.Add(buttons);
        Content = root;
    }
}
