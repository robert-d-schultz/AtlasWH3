using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtlasWH3.App.Scene;
using AtlasWH3.Core;
using AtlasWH3.Core.Battle;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Battle;

namespace AtlasWH3.App;

/// <summary>
/// The Campaign battles workspace: the catchment areas of the campaign-battle terrain (battle_locations_map.bin) on a
/// map, each region's settlement battle with a status and a one-click fix, and redirects to battle maps. Reads the linked
/// packs, then the game's; saves to the output folder, a new mod pack or the kit's working_data, never to the packs.
/// </summary>
public sealed class CampaignBattlesWindow : Window
{
    private const string ModDataKey = "battles.modData";
    private const string NoRowKey = "battles.noRow";

    private readonly ProjectPaths _paths;
    private CampaignBattleWorkspace? _ws;
    private IReadOnlyList<RegionBattleStatus> _statuses = [];
    private readonly UndoStack _undo = new();
    private List<CatchmentList>? _dragBefore;
    private string? _blmFile;

    private readonly CampaignBattleView _view = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), FontSize = 11 };
    private readonly StackPanel _listChecks = new();
    private readonly ComboBox _activeList = new() { Margin = new Thickness(0, 2, 0, 6) };
    private readonly CheckBox _gaps = new() { Content = "Show coverage gaps (active list)" };
    private readonly CheckBox _markers = new() { Content = "Show settlements", IsChecked = true };
    private readonly RadioButton _toolSelect = new() { Content = "Select, move, resize", IsChecked = true, GroupName = "battleTool" };
    private readonly RadioButton _toolDraw = new() { Content = "Draw new area", GroupName = "battleTool" };

    // area tab
    private readonly TextBlock _areaList = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBox _areaName = new();
    private readonly TextBox _areaRedirect = new();
    private readonly TextBlock _areaCheck = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Error"), FontSize = 11 };
    private readonly TextBox _areaFaction = new();
    private readonly CheckBox _noRow = new() { Content = "Its pack ships the battles_tables row (don't write one)", Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBox _x0 = new(), _y0 = new(), _x1 = new(), _y1 = new(), _cx = new(), _cy = new();
    private readonly CheckBox _north = new() { Content = "N" }, _south = new() { Content = "S" }, _east = new() { Content = "E" }, _west = new() { Content = "W" };
    private readonly StackPanel _areaPanel = new() { Margin = new Thickness(10) };

    // regions tab
    private readonly TextBlock _regionSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _regionFilter = new() { Margin = new Thickness(0, 0, 4, 0) };
    private readonly ComboBox _regionState = new() { Width = 120 };
    private readonly ListBox _regionList = new();
    private readonly TextBlock _regionIssues = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _regionKind = new() { Width = 170, Margin = new Thickness(0, 0, 4, 0) };
    private readonly TabControl _tabs = new();

    public static CampaignBattlesWindow Open(Window? owner, ProjectPaths paths)
    {
        var w = new CampaignBattlesWindow(paths);
        w.Show();
        return w;
    }

    public CampaignBattlesWindow(ProjectPaths paths)
    {
        _paths = paths;
        Title = AppInfo.Title("Campaign battles");
        Width = 1500;
        Height = 920;
        Background = Theme.Brush("Bg");
        Foreground = Theme.Brush("Text");
        Content = BuildLayout();
        Placement.Track(this, "battles");
        Walkthrough.Enable(this, "battles");
        _undo.Changed += () => { RefreshAfterEdit(); };
        Loaded += async (_, _) => await LoadAsync();
        Closing += OnClosing;
        InputBindings.Add(new KeyBinding(new RelayCommand(Undo), Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(Redo), Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Save()), Key.S, ModifierKeys.Control));
    }

    // ------------------------------------------------------------------ layout

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();
        var menu = new Menu().Spot("battles.menu");
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(StandardMenus.Item("_Reload from packs", () => Reload(null), tooltip: "Reads battle_locations_map.bin again from the linked packs, then the game's."));
        file.Items.Add(StandardMenus.Item("_Open catchment file (.bin)…", OpenBinFile, tooltip: "Edit a loose battle_locations_map.bin instead of the pack copy."));
        file.Items.Add(StandardMenus.Item("_Mod data folder…", PickModData, tooltip: "An RPFM extract of your mod (db\\ and text\\db\\): region names and suggested kinds."));
        file.Items.Add(new Separator());
        file.Items.Add(StandardMenus.Item("_Save to output folder", () => Save(), "Ctrl+S", "Writes battle_locations_map.bin and any needed battles_tables rows under the output folder."));
        file.Items.Add(StandardMenus.Item("_Export mod pack…", ExportPack, tooltip: "A new .pack with the edited battle_locations_map.bin."));
        file.Items.Add(StandardMenus.Item("Write to _kit (working_data)", WriteToKit, tooltip: "Copies the file into the assembly kit's working_data, where its pack step picks it up. The old file is backed up."));
        file.Items.Add(StandardMenus.Item("Open output _folder", () => OpenFolder(OutputDir)));
        file.Items.Add(new Separator());
        file.Items.Add(StandardMenus.Item("_Close", Close));
        menu.Items.Add(file);
        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(StandardMenus.Item("_Undo", Undo, "Ctrl+Z"));
        edit.Items.Add(StandardMenus.Item("_Redo", Redo, "Ctrl+Y"));
        edit.Items.Add(new Separator());
        edit.Items.Add(StandardMenus.Item("_Delete area", DeleteArea, "Del"));
        menu.Items.Add(edit);
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(StandardMenus.Item("_Fit to window", _view.FitToWindow, "F"));
        menu.Items.Add(view);
        StandardMenus.AddTo(menu, this, _paths);
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);

        var statusBar = new Border { Background = Theme.Brush("Panel"), Child = _status.Card("battles.status") }.Spot("battles.status");
        _status.FontFamily = Theme.MonoFont;
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);

        // left
        var left = new StackPanel { Margin = new Thickness(10) };
        var lists = new StackPanel().Spot("battles.lists");
        lists.Children.Add(Theme.Header("Catchment lists", 0).Card("battles.lists"));
        lists.Children.Add(_listChecks);
        lists.Children.Add(new TextBlock { Text = "Active list", Margin = new Thickness(0, 8, 0, 0) }.Card("battles.activeList"));
        lists.Children.Add(_activeList.Card("battles.activeList"));
        lists.Children.Add(_gaps.Card("battles.gaps"));
        lists.Children.Add(_markers.Card("battles.markers"));
        left.Children.Add(lists);
        var tools = new StackPanel().Spot("battles.tools");
        tools.Children.Add(Theme.Header("Tool"));
        tools.Children.Add(_toolSelect.Card("battles.toolSelect"));
        tools.Children.Add(_toolDraw.Card("battles.toolDraw"));
        left.Children.Add(tools);
        left.Children.Add(Theme.Header("Map"));
        left.Children.Add(_summary.Card("battles.summary"));
        var legend = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.Brush("DimText") };
        legend.Inlines.Add(new System.Windows.Documents.Run("● OK  ") { Foreground = new SolidColorBrush(StateColour(RegionBattleState.Ok)) });
        legend.Inlines.Add(new System.Windows.Documents.Run("● problem  ") { Foreground = new SolidColorBrush(StateColour(RegionBattleState.NoCatchment)) });
        legend.Inlines.Add(new System.Windows.Documents.Run("● redirect") { Foreground = new SolidColorBrush(StateColour(RegionBattleState.RedirectMissing)) });
        left.Children.Add(legend.Card("battles.markers"));
        var leftScroll = new ScrollViewer { Width = 250, Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush("Panel") };
        DockPanel.SetDock(leftScroll, Dock.Left);
        dock.Children.Add(leftScroll);

        _activeList.SelectionChanged += (_, _) => { _view.ActiveList = _activeList.SelectedItem as string ?? BattleLocations.Standard; _view.RebuildBase(); };
        _gaps.Click += (_, _) => { _view.ShowGaps = _gaps.IsChecked == true; _view.RebuildBase(); };
        _markers.Click += (_, _) => { _view.ShowMarkers = _markers.IsChecked == true; _view.InvalidateVisual(); };
        _toolSelect.Checked += (_, _) => _view.Tool = CampaignBattleView.ToolMode.Select;
        _toolDraw.Checked += (_, _) => _view.Tool = CampaignBattleView.ToolMode.Draw;

        // right
        _tabs.Width = 380;
        _tabs.Items.Add(new TabItem { Header = "Regions", Content = BuildRegionsTab().Spot("battles.regions") });
        _tabs.Items.Add(new TabItem { Header = "Area", Content = new ScrollViewer { Content = BuildAreaTab(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.Spot("battles.area") });
        DockPanel.SetDock(_tabs, Dock.Right);
        dock.Children.Add(_tabs);

        dock.Children.Add(_view.Spot("battles.map"));
        _view.HoverChanged += UpdateStatus;
        _view.SelectionChanged += ShowSelectedArea;
        _view.EditStarting += () => _dragBefore = _ws?.Map.CloneLists();
        _view.EditFinished += changed =>
        {
            if (changed && _dragBefore is not null) PushSnapshot("Move or resize area", _dragBefore);
            _dragBefore = null;
        };
        _view.AreaDrawn += DrawArea;
        _view.MarkerClicked += tag =>
        {
            if (tag is not RegionBattleStatus s) return;
            _tabs.SelectedIndex = 0;
            _regionState.SelectedIndex = 0;
            _regionFilter.Text = "";
            var row = _regionList.Items.OfType<RegionRow>().FirstOrDefault(r => r.Status.Settlement.RegionKey == s.Settlement.RegionKey);
            if (row is null) return;
            _regionList.SelectedItem = row;
            _regionList.ScrollIntoView(row);
        };
        _view.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteArea(); e.Handled = true; }
            else if (e.Key == Key.F) { _view.FitToWindow(); e.Handled = true; }
        };
        return dock;
    }

    private FrameworkElement BuildAreaTab()
    {
        var p = _areaPanel;
        p.Children.Add(Theme.Header("Selected area", 0));
        p.Children.Add(_areaList.Card("battles.area.list"));
        p.Children.Add(Label("Name", "battles.area.name"));
        p.Children.Add(_areaName.Card("battles.area.name"));
        p.Children.Add(Label("Battle redirection (battle map folder)", "battles.area.redirect"));
        var redirectRow = new DockPanel();
        var pick = new Button { Content = "Pick…", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0) }.Card("battles.area.pick");
        pick.Click += (_, _) => PickRedirect();
        DockPanel.SetDock(pick, Dock.Right);
        redirectRow.Children.Add(pick);
        redirectRow.Children.Add(_areaRedirect.Card("battles.area.redirect"));
        p.Children.Add(redirectRow);
        p.Children.Add(_noRow.Card("battles.area.noRow"));
        p.Children.Add(_areaCheck.Card("battles.area.check"));
        _areaRedirect.TextChanged += (_, _) => CheckRedirectText();
        _noRow.Click += (_, _) => SetNoRow(_areaRedirect.Text.Trim(), _noRow.IsChecked == true);
        p.Children.Add(Label("Defending faction restriction", "battles.area.faction"));
        p.Children.Add(_areaFaction.Card("battles.area.faction"));
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition());
        void Row(int r, string label, string card, TextBox a, TextBox b)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }.Card(card);
            Grid.SetRow(t, r);
            grid.Children.Add(t);
            a.Margin = b.Margin = new Thickness(0, 1, 0, 1);
            Grid.SetRow(a, r); Grid.SetColumn(a, 1); grid.Children.Add(a.Card(card));
            Grid.SetRow(b, r); Grid.SetColumn(b, 3); grid.Children.Add(b.Card(card));
        }
        Row(0, "Box min x, y", "battles.area.box", _x0, _y0);
        Row(1, "Box max x, y", "battles.area.box", _x1, _y1);
        Row(2, "Centre x, y", "battles.area.centre", _cx, _cy);
        p.Children.Add(grid);
        var dirs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        dirs.Children.Add(new TextBlock { Text = "Approaches ", VerticalAlignment = VerticalAlignment.Center });
        foreach (var c in new[] { _north, _south, _east, _west }) { c.Margin = new Thickness(0, 0, 8, 0); dirs.Children.Add(c); }
        p.Children.Add(dirs.Card("battles.area.approaches"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Check, "Apply", (_, _) => ApplyArea()).Card("battles.area.apply"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Delete, "Delete", (_, _) => DeleteArea()).Card("battles.area.delete"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Copy, "battles_tables row", (_, _) => CopyBattlesRow()).Card("battles.area.row"));
        p.Children.Add(buttons);
        p.IsEnabled = false;
        return p;
    }

    private FrameworkElement BuildRegionsTab()
    {
        var p = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel();
        top.Children.Add(_regionSummary.Card("battles.regions.summary"));
        var filterRow = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        DockPanel.SetDock(_regionState, Dock.Right);
        filterRow.Children.Add(_regionState.Card("battles.regions.state"));
        filterRow.Children.Add(_regionFilter.Card("battles.regions.filter"));
        top.Children.Add(filterRow);
        DockPanel.SetDock(top, Dock.Top);
        p.Children.Add(top);

        _regionState.ItemsSource = new[] { "All", "Problems", "New regions" };
        _regionState.SelectedIndex = 1;
        _regionState.SelectionChanged += (_, _) => FillRegionList();
        _regionFilter.TextChanged += (_, _) => FillRegionList();

        var bottom = new StackPanel();
        bottom.Children.Add(_regionIssues.Card("battles.regions.issues"));
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        _regionKind.ItemsSource = new[] { RegionKind.Vanilla }.Concat(RegionKind.Applicable()).ToList();
        kindRow.Children.Add(_regionKind.Card("battles.regions.kind"));
        kindRow.Children.Add(Theme.IconButton(Theme.Glyph.Check, "Apply kind", (_, _) => ApplyKind()).Card("battles.regions.apply"));
        bottom.Children.Add(kindRow);
        var buttons = new WrapPanel();
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Check, "Fix", (_, _) => FixSelected()).Card("battles.regions.fix"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Build, "Fix all listed", (_, _) => FixAll()).Card("battles.regions.fixAll"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Map, "Go to", (_, _) => GoToSelected()).Card("battles.regions.goto"));
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        p.Children.Add(bottom);

        var header = RegionHeader();
        DockPanel.SetDock(header, Dock.Top);
        p.Children.Add(header);
        _regionList.ItemTemplate = RegionTemplate();
        ScrollViewer.SetHorizontalScrollBarVisibility(_regionList, ScrollBarVisibility.Disabled);
        _regionList.SelectionChanged += (_, _) => ShowSelectedRegion();
        _regionList.MouseDoubleClick += (_, _) => GoToSelected();
        p.Children.Add(_regionList.Card("battles.regions.list"));
        return p;
    }

    private static TextBlock Label(string text, string card) =>
        new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 1), Foreground = Theme.Brush("DimText") }.Card(card);

    // ------------------------------------------------------------------ load

    private string? ModDataDir => AppSettings.Current.Values.GetValueOrDefault(ModDataKey);
    private string OutputDir => Path.Combine(_paths.OutputRoot, "campaign_battles");

    private async Task LoadAsync()
    {
        _status.Text = "Reading the catchments, the campaign map and the battle maps from the packs…";
        try
        {
            var blm = _blmFile;
            var mod = ModDataDir;
            _ws = await Task.Run(() => CampaignBattleWorkspace.Load(_paths, blm, mod));
        }
        catch (Exception ex)
        {
            _status.Text = "Load failed: " + ex.Message;
            ErrorDialog.Show(this, "Could not load the campaign battles.", ex);
            return;
        }
        _undo.Clear();
        var ws = _ws;
        foreach (var f in (AppSettings.Current.Values.GetValueOrDefault(NoRowKey) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            ws.NoRowFolders.Add(f);
        _listChecks.Children.Clear();
        _view.VisibleLists.Clear();
        foreach (var list in ws.Map.Lists)
        {
            var c = CampaignBattleView.ColourOf(list.Key);
            var check = new CheckBox
            {
                IsChecked = list.Key is BattleLocations.Standard or BattleLocations.Unfortified,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Border { Width = 12, Height = 12, Background = new SolidColorBrush(c), Margin = new Thickness(0, 0, 6, 0) },
                        new TextBlock { Text = $"{list.Key} ({list.Areas.Count})" },
                    },
                },
                Tag = list.Key,
            }.Card("battles.lists");
            if (check.IsChecked == true) _view.VisibleLists.Add(list.Key);
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _view.VisibleLists.Add((string)check.Tag); else _view.VisibleLists.Remove((string)check.Tag);
                _view.InvalidateVisual();
            };
            _listChecks.Children.Add(check);
        }
        _activeList.ItemsSource = ws.Map.Lists.Select(l => l.Key).ToList();
        _activeList.SelectedItem = BattleLocations.Standard;
        _view.Load(ws.Map);
        Analyze();
        UpdateTitle();
        _status.Text = $"Loaded {ws.Map.AreaCount} areas from {ws.Source}." + (ws.Notes.Count > 0 ? "  " + ws.Notes[0] : "");
    }

    private async void Reload(string? blmFile)
    {
        if (!ConfirmDiscard()) return;
        _blmFile = blmFile;
        await LoadAsync();
    }

    private void OpenBinFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "battle_locations_map.bin|*.bin", Title = "Catchment file to edit" };
        if (dialog.ShowDialog(this) == true) Reload(dialog.FileName);
    }

    private void PickModData()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Your mod as an RPFM extract (the folder holding db\\ and text\\db\\)",
            InitialDirectory = ModDataDir ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        AppSettings.Current.Values[ModDataKey] = dialog.FolderName;
        AppSettings.Current.Save();
        Reload(_blmFile);
    }

    // ------------------------------------------------------------------ regions

    /// <summary>A row of the Regions list (public for WPF bindings).</summary>
    public sealed record RegionRow(RegionBattleStatus Status)
    {
        public string Name => Status.DisplayName == Status.Settlement.RegionKey ? Status.Settlement.RegionKey : $"{Status.DisplayName} ({Status.Settlement.RegionKey})";
        public string Now => Status.Current?.Display() ?? "—";
        public string Suggested => Status.Suggested?.Display() ?? "";
        public string State => Status.StateText;
        public Brush StateBrush => new SolidColorBrush(StateColour(Status.State));
    }

    /// <summary>Four columns, shared by the header and the item template.</summary>
    private static readonly double[] RegionColumns = [128, 78, 82, 70];

    private static DataTemplate RegionTemplate()
    {
        var cols = string.Concat(RegionColumns.Select(w => $"<ColumnDefinition Width=\"{w}\"/>"));
        const string ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            $"<DataTemplate {ns}><Grid><Grid.ColumnDefinitions>{cols}</Grid.ColumnDefinitions>" +
            "<TextBlock Text=\"{Binding Name}\" ToolTip=\"{Binding Name}\" TextTrimming=\"CharacterEllipsis\" Margin=\"0,0,4,0\"/>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding Now}\" TextTrimming=\"CharacterEllipsis\" Margin=\"0,0,4,0\"/>" +
            "<TextBlock Grid.Column=\"2\" Text=\"{Binding Suggested}\" TextTrimming=\"CharacterEllipsis\" Margin=\"0,0,4,0\"/>" +
            "<TextBlock Grid.Column=\"3\" Text=\"{Binding State}\" Foreground=\"{Binding StateBrush}\" TextTrimming=\"CharacterEllipsis\"/>" +
            "</Grid></DataTemplate>");
    }

    private static Grid RegionHeader()
    {
        var g = new Grid { Margin = new Thickness(4, 0, 0, 2) };
        string[] names = ["Region", "Now", "Suggested", "Status"];
        for (var i = 0; i < names.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RegionColumns[i]) });
            var t = new TextBlock { Text = names[i], Foreground = Theme.Brush("DimText"), FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    private static Color StateColour(RegionBattleState s) => s switch
    {
        RegionBattleState.Ok => Color.FromRgb(0x6C, 0xCB, 0x5F),
        RegionBattleState.RedirectMissing or RegionBattleState.WrongType => Color.FromRgb(0xE5, 0xC0, 0x7B),
        _ => Color.FromRgb(0xF0, 0x6A, 0x6A),
    };

    private void Analyze()
    {
        if (_ws is null) return;
        _statuses = _ws.Regions.Analyze();
        var problems = _statuses.Count(s => s.State != RegionBattleState.Ok);
        var newOnes = _statuses.Where(s => s.IsNew).ToList();
        var byState = _statuses.Where(s => s.State != RegionBattleState.Ok).GroupBy(s => s.StateText).Select(g => $"{g.Key}: {g.Count()}");
        _regionSummary.Text = _statuses.Count == 0
            ? "No regions: the campaign map's map_data.esf was not found."
            : $"{_statuses.Count} settlements on {_paths.MapName} ({newOnes.Count} new): {_statuses.Count - problems} OK, {problems} with a problem" +
              (problems > 0 ? $" ({string.Join(", ", byState)})" : "") +
              (_ws.Mod is null ? ".\nNo mod data folder: names and suggested kinds are missing (File › Mod data folder…)." : ".");
        // the campaign map does not fit the battle grid (e.g. a resized map): say so where the counts are
        var gridNote = _ws.Notes.FirstOrDefault(n => n.Contains("battle grid", StringComparison.Ordinal));
        if (gridNote is not null) _regionSummary.Text = gridNote + "\n\n" + _regionSummary.Text;
        _regionSummary.Foreground = gridNote is not null ? Theme.Brush("Error") : Theme.Brush("Text");
        _view.Markers = _statuses.Where(s => s.State != RegionBattleState.OffGrid)
            .Select(s => new CampaignBattleView.Marker(s.Settlement.X, s.Settlement.CatchmentY(_ws.Map.Height), StateColour(s.State), s)).ToList();
        _view.InvalidateVisual();
        FillRegionList();
        UpdateSummary();
    }

    private void FillRegionList()
    {
        var selected = (_regionList.SelectedItem as RegionRow)?.Status.Settlement.RegionKey;
        var filter = _regionFilter.Text.Trim();
        var rows = _statuses
            .Where(s => _regionState.SelectedIndex switch { 1 => s.State != RegionBattleState.Ok, 2 => s.IsNew, _ => true })
            .Where(s => filter.Length == 0 || s.Settlement.RegionKey.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                           || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.State == RegionBattleState.Ok).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new RegionRow(s)).ToList();
        _regionList.ItemsSource = rows;
        if (selected is not null) _regionList.SelectedItem = rows.FirstOrDefault(r => r.Status.Settlement.RegionKey == selected);
    }

    private RegionBattleStatus? SelectedRegion => (_regionList.SelectedItem as RegionRow)?.Status;

    private void ShowSelectedRegion()
    {
        if (SelectedRegion is not { } s) { _regionIssues.Text = ""; return; }
        _regionIssues.Text = $"{s.Settlement.RegionKey}: hex {s.Settlement.X},{s.Settlement.Y} (battle cell {s.Settlement.X},{s.Settlement.CatchmentY(_ws!.Map.Height)})" +
                             (s.IsNew ? ", new region" : "") +
                             $"\nWalled siege area: {s.Std?.Name ?? "none"}{Redirect(s.Std)}\nUnwalled area: {s.Unf?.Name ?? "none"}{Redirect(s.Unf)}" +
                             (s.Issues.Count > 0 ? "\n• " + string.Join("\n• ", s.Issues) : "");
        _regionKind.SelectedItem = s.Suggested ?? s.Current ?? RegionKind.Vanilla;
        static string Redirect(CatchmentArea? a) => a is { Redirection.Length: > 0 } ? $" → {a.Redirection}" : "";
    }

    private void GoToSelected()
    {
        if (SelectedRegion is not { } s || _ws is null) return;
        var cy = s.Settlement.CatchmentY(_ws.Map.Height);
        _view.CentreOn(s.Settlement.X, cy, 12);
        if ((s.Std ?? s.Unf) is { } area)
            _view.Select(s.Std is not null ? BattleLocations.Standard : BattleLocations.Unfortified, area);
    }

    private void FixSelected()
    {
        if (SelectedRegion is not { } s || _ws is null) return;
        RegionEdit($"Fix {s.DisplayName}", () => _ws.Regions.Fix(s));
    }

    private void ApplyKind()
    {
        if (SelectedRegion is not { } s || _ws is null || _regionKind.SelectedItem is not RegionKind kind) return;
        RegionEdit($"{s.DisplayName} → {kind.Display()}", () => _ws.Regions.Apply(s, kind));
    }

    private void FixAll()
    {
        if (_ws is null) return;
        var targets = (_regionList.ItemsSource as IEnumerable<RegionRow> ?? []).Select(r => r.Status)
            .Where(s => s.State is not RegionBattleState.Ok and not RegionBattleState.OffGrid).ToList();
        if (targets.Count == 0) { _status.Text = "Nothing to fix in the listed regions."; return; }
        var before = _ws.Map.CloneLists();
        var fixedCount = 0;
        var failed = new List<string>();
        foreach (var s in targets)
        {
            // re-analyse: an earlier fix may have added an area covering this settlement
            var r = _ws.Regions.Fix(_ws.Regions.Analyze(s.Settlement));
            if (r.Success) fixedCount++; else failed.Add(r.Message);
        }
        PushSnapshot($"Fix {fixedCount} regions", before);
        _status.Text = $"Fixed {fixedCount} of {targets.Count} regions." + (failed.Count > 0 ? $" Skipped {failed.Count}: {failed[0]}" : "");
        if (failed.Count > 1)
            MessageBox.Show(this, string.Join("\n", failed.Take(30)), "Regions not fixed", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RegionEdit(string description, Func<RegionFixResult> edit)
    {
        var before = _ws!.Map.CloneLists();
        var result = edit();
        if (!result.Success)
        {
            _ws.Map.Lists = before;
            _status.Text = result.Message;
            MessageBox.Show(this, result.Message, "Campaign battles", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PushSnapshot(description, before);
        _status.Text = result.Message;
    }

    // ------------------------------------------------------------------ areas

    private void ShowSelectedArea()
    {
        if (_view.Selected is not { } sel)
        {
            _areaPanel.IsEnabled = false;
            _areaList.Text = "Nothing selected. Click an area on the map.";
            return;
        }
        var a = sel.Area;
        _areaPanel.IsEnabled = true;
        _areaList.Text = $"{sel.List}   ({BattlesTable.TypeForList(sel.List)} battles)";
        _areaList.Foreground = new SolidColorBrush(CampaignBattleView.ColourOf(sel.List));
        _areaName.Text = a.Name;
        _areaRedirect.Text = a.Redirection;
        _areaFaction.Text = a.DefendingFactionRestriction;
        _x0.Text = a.Box.MinX.ToString(); _y0.Text = a.Box.MinY.ToString();
        _x1.Text = a.Box.MaxX.ToString(); _y1.Text = a.Box.MaxY.ToString();
        _cx.Text = a.Centre.X.ToString(); _cy.Text = a.Centre.Y.ToString();
        _north.IsChecked = a.North; _south.IsChecked = a.South; _east.IsChecked = a.East; _west.IsChecked = a.West;
        if (_tabs.SelectedIndex != 1 && Keyboard.FocusedElement is not TextBox) _tabs.SelectedIndex = 1;
        CheckRedirectText();
    }

    private void CheckRedirectText()
    {
        if (_ws is null || _view.Selected is not { } sel) { _areaCheck.Text = ""; return; }
        var folder = _areaRedirect.Text.Trim();
        _areaCheck.Text = string.Join("\n", _ws.CheckRedirect(sel.List, folder));
        var known = _ws.KnownBattles.ContainsKey(BattlesTable.MapPath(folder));
        _noRow.IsEnabled = folder.Length > 0 && !known;
        _noRow.IsChecked = known || _ws.NoRowFolders.Contains(folder);
    }

    /// <summary>Remembers that a redirect folder's pack ships its own battles_tables row, so Save writes none.</summary>
    private void SetNoRow(string folder, bool noRow)
    {
        if (_ws is null || folder.Length == 0) return;
        if (noRow) _ws.NoRowFolders.Add(folder); else _ws.NoRowFolders.Remove(folder);
        AppSettings.Current.Values[NoRowKey] = string.Join(";", _ws.NoRowFolders.Order(StringComparer.OrdinalIgnoreCase));
        AppSettings.Current.Save();
    }

    private void PickRedirect()
    {
        if (_ws is null) return;
        var choice = Prompt.Choose(this, "Battle map", "Battle-map folder (terrain\\battles\\<folder>) to redirect to:", _ws.BattleFolders, _areaRedirect.Text);
        if (choice is not null) _areaRedirect.Text = choice.Trim();
    }

    private void ApplyArea()
    {
        if (_ws is null || _view.Selected is not { } sel) return;
        static int? N(TextBox t) => int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (N(_x0) is not { } x0 || N(_y0) is not { } y0 || N(_x1) is not { } x1 || N(_y1) is not { } y1 || N(_cx) is not { } cx || N(_cy) is not { } cy)
        {
            MessageBox.Show(this, "Box and centre need whole numbers.", "Campaign battles", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var before = _ws.Map.CloneLists();
        var a = sel.Area;
        a.Name = _areaName.Text.Trim();
        a.Redirection = _areaRedirect.Text.Trim();
        a.DefendingFactionRestriction = _areaFaction.Text.Trim();
        a.Box = new CellBox(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
        a.Centre = (cx, cy);
        a.North = _north.IsChecked == true; a.South = _south.IsChecked == true; a.East = _east.IsChecked == true; a.West = _west.IsChecked == true;
        PushSnapshot($"Edit area {a.Name}", before);
    }

    private void DeleteArea()
    {
        if (_ws is null || _view.Selected is not { } sel) return;
        var before = _ws.Map.CloneLists();
        _ws.Map.List(sel.List)?.Areas.Remove(sel.Area);
        _view.Select("", null);
        PushSnapshot($"Delete area {sel.Area.Name}", before);
    }

    private void DrawArea(CellBox box)
    {
        if (_ws is null) return;
        var list = _view.ActiveList;
        var before = _ws.Map.CloneLists();
        var area = CatchmentOps.Add(_ws.Map, list, box, CatchmentOps.UniqueName(_ws.Map, $"atlaswh3_{list}"));
        PushSnapshot($"New area in {list}", before);
        if (!_view.VisibleLists.Contains(list))
        {
            _view.VisibleLists.Add(list);
            foreach (var c in _listChecks.Children.OfType<CheckBox>()) if ((string)c.Tag == list) c.IsChecked = true;
        }
        _view.Select(list, area);
    }

    private void CopyBattlesRow()
    {
        if (_ws is null || _view.Selected is not { } sel) return;
        var folder = _areaRedirect.Text.Trim();
        if (folder.Length == 0) { _status.Text = "Set a redirect first: the row points at that battle map folder."; return; }
        var tsv = BattlesTable.ToTsv([BattlesTable.Row(folder, BattlesTable.TypeForList(sel.List))]);
        Clipboard.SetText(tsv);
        _status.Text = _ws.KnownBattles.ContainsKey(BattlesTable.MapPath(folder))
            ? $"Copied. Note: battles_tables already has a row for {folder}; you only need one for a custom map."
            : $"Copied a battles_tables row for {folder} (paste it into battles_tables in RPFM). Save also writes it to the output folder.";
    }

    // ------------------------------------------------------------------ undo, save

    private sealed class SnapshotEdit(CampaignBattlesWindow window, string description, List<CatchmentList> before, List<CatchmentList> after) : IUndoable
    {
        public string Description => description;
        public void Undo() => window.Restore(before);
        public void Redo() => window.Restore(after);
    }

    private void PushSnapshot(string description, List<CatchmentList> before)
    {
        if (_ws is null) return;
        var after = _ws.Map.CloneLists();
        _undo.Push(new SnapshotEdit(this, description, before, after));
    }

    /// <summary>Puts a snapshot back (a copy, so the snapshot stays intact), keeping the selection by list and index.</summary>
    private void Restore(List<CatchmentList> lists)
    {
        if (_ws is null) return;
        var sel = _view.Selected is { } s ? (s.List, _ws.Map.List(s.List)?.Areas.IndexOf(s.Area) ?? -1) : ("", -1);
        _ws.Map.Lists = lists.Select(l => l.Clone()).ToList();
        var area = sel.Item2 >= 0 ? _ws.Map.List(sel.Item1)?.Areas.ElementAtOrDefault(sel.Item2) : null;
        _view.Select(sel.Item1, area);
    }

    private void Undo() { if (_undo.Undo() is { } a) _status.Text = "Undid: " + a.Description; }
    private void Redo() { if (_undo.Redo() is { } a) _status.Text = "Redid: " + a.Description; }

    private void RefreshAfterEdit()
    {
        if (_ws is null) return;
        _view.RebuildBase();
        Analyze();
        ShowSelectedArea();
        UpdateTitle();
        var counts = _ws.Map.Lists.ToDictionary(l => l.Key, l => l.Areas.Count);
        foreach (var c in _listChecks.Children.OfType<CheckBox>())
            if (c.Content is StackPanel sp && sp.Children[1] is TextBlock t) t.Text = $"{c.Tag} ({counts.GetValueOrDefault((string)c.Tag)})";
    }

    private void UpdateTitle() => Title = (_ws?.IsDirty == true ? "* " : "") + AppInfo.Title($"Campaign battles — {_paths.MapName}");

    private void UpdateSummary()
    {
        if (_ws is null) return;
        _summary.Text = $"Catchments: {_ws.Source}\nGrid {_ws.Map.Width}x{_ws.Map.Height}, {_ws.Map.AreaCount} areas\n" +
                        $"Regions: {_ws.CampaignSource ?? "none"}\nBattle maps in packs: {_ws.BattleFolders.Count}\n" +
                        $"Uncovered battle land ({_view.ActiveList}): {CatchmentOps.UncoveredLand(_ws.Map, _view.ActiveList):N0} cells" +
                        (_ws.Notes.Count > 0 ? "\n\n" + string.Join("\n", _ws.Notes) : "");
    }

    private void UpdateStatus((int X, int Y)? cell)
    {
        if (_ws is null) return;
        var dirty = _ws.IsDirty ? "unsaved changes" : "saved";
        if (cell is not { } c) { _status.Text = $"{_ws.Map.AreaCount} areas   |   zoom {_view.Zoom:0.#} px/cell   |   {dirty}   |   undo steps {_undo.Count}"; return; }
        var covering = _ws.Map.Lists.Select(l => (l.Key, n: l.Areas.Count(a => a.Box.Contains(c.X, c.Y)))).Where(t => t.n > 0)
            .Select(t => $"{t.Key} {t.n}");
        _status.Text = $"cell {c.X},{c.Y} (hex {c.X},{_ws.Map.Height - 1 - c.Y})   |   {(_ws.Map.IsLand(c.X, c.Y) ? "battle land" : "not battle land")}" +
                       $"   |   {(covering.Any() ? string.Join(", ", covering) : "no areas")}   |   {dirty}";
    }

    private bool Save()
    {
        if (_ws is null) return false;
        try
        {
            var written = _ws.SaveLoose(OutputDir);
            UpdateTitle();
            _status.Text = $"Saved {string.Join(", ", written.Select(Path.GetFileName))} to {OutputDir}. Pack it with File › Export mod pack, or import with RPFM.";
            return true;
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "Save failed.", ex);
            return false;
        }
    }

    private void ExportPack()
    {
        if (_ws is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Mod pack|*.pack", FileName = "atlaswh3_campaign_battles.pack", InitialDirectory = Directory.Exists(OutputDir) ? OutputDir : _paths.OutputRoot,
            Title = "New mod pack with the edited catchments",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _ws.ExportPack(dialog.FileName);
            Save();
            _status.Text = $"Wrote {dialog.FileName}. Add the battles_tables TSV from {OutputDir}\\db with RPFM if it lists any rows.";
        }
        catch (Exception ex) { ErrorDialog.Show(this, "Export failed.", ex); }
    }

    private void WriteToKit()
    {
        if (_ws is null) return;
        var target = Path.Combine(_paths.AkWorkingDir, "terrain", "battles", _ws.TerrainFolder, "battle_locations_map.bin");
        if (MessageBox.Show(this, $"Write the catchments to\n{target}?\n\nAn existing file is backed up to the output folder first.", "Write to kit",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var path = _ws.WriteToKit();
            Save();
            _status.Text = $"Wrote {path}.";
        }
        catch (Exception ex) { ErrorDialog.Show(this, "Write to kit failed.", ex); }
    }

    private bool ConfirmDiscard()
    {
        if (_ws?.IsDirty != true) return true;
        return Prompt.AskSave(this, "Campaign battles", "The catchments have unsaved changes. Save them to the output folder first?") switch
        {
            Prompt.SaveChoice.Save => Save(),
            Prompt.SaveChoice.DontSave => true,
            _ => false,
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }
}
