using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtlasWH3.Core;
using AtlasWH3.Core.Build;
using AtlasWH3.Core.Campaign;
using Microsoft.Win32;

namespace AtlasWH3.App;

/// <summary>
/// Builds a map project (<c>.atlaswh3</c>): Build all runs every ticked segment (Validate, custom steps, Compile's native
/// steps, Pack, Install) in order; Run selected runs only the highlighted row; Pack only re-packs the last output.
/// Status and logs update live; Cancel stops at the next check. The ticks are the project's build profile (saved
/// with it); the Profile tab edits the rest. Same <see cref="BuildRunner"/> as the CLI <c>build</c> command.
/// </summary>
public sealed class BuildWindow : Window
{
    private static BuildWindow? _open;

    /// <summary>Shows the (single) Build window, opening <paramref name="projectFile"/> or the last project.</summary>
    public static BuildWindow Show(Window? owner, ProjectPaths defaults, string? projectFile = null)
    {
        if (_open is null)
        {
            _open = new BuildWindow(defaults);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else _open.Activate();
        var file = projectFile ?? AppSettings.Current.RecentProjects.FirstOrDefault(File.Exists);
        if (file is not null && (_open._project?.FilePath is null || projectFile is not null)) _open.OpenProject(file);
        return _open;
    }

    /// <summary>True while a build runs (editors show a banner).</summary>
    public static bool Building => _open?._running == true;
    public static event Action? BuildingChanged;

    private sealed class Row
    {
        public required string Id;
        public required BuildSegment Segment;
        public string? Step, Custom;
        public required CheckBox Check;
        public required TextBlock StatusIcon;
        public required TextBlock Info;
        public required TreeViewItem Item;
    }

    private readonly ProjectPaths _defaults;
    private BuildProject? _project;
    private bool _dirty, _running;
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, Row> _rows = [];
    private readonly List<(string Id, string Line)> _lines = [];
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
    };
    private readonly TextBox _filter = new() { Width = 180 };
    private readonly CheckBox _onlySelected = new() { Content = "Selected row only", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _autoScroll = new() { Content = "Auto-scroll", IsChecked = true, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _projectLabel = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _progress = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ContentControl _profileHost = new();
    private readonly Grid _body = new();
    private readonly Border _empty = new();
    private Button _buildAll = null!, _runSelected = null!, _packOnly = null!, _cancel = null!, _save = null!;
    private string? _lastLogFile;

    private BuildWindow(ProjectPaths defaults)
    {
        _defaults = defaults;
        Width = 1250;
        Height = 820;
        Background = Theme.Brush("Bg");
        Foreground = Theme.Brush("Text");
        _log.FontFamily = Theme.MonoFont;
        _log.Background = Theme.Brush("Bg");
        Content = BuildLayout();
        Placement.Track(this, "build");
        Walkthrough.Enable(this, "build");
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Walkthrough.Start(this)), Key.F1, ModifierKeys.None));
        UpdateTitle();
        ShowEmptyState();
        _filter.TextChanged += (_, _) => RefreshLog();
        _onlySelected.Click += (_, _) => RefreshLog();
        _tree.SelectedItemChanged += (_, _) => { if (_onlySelected.IsChecked == true) RefreshLog(); };
        InputBindings.Add(new KeyBinding(new RelayCommand(() => SaveProject()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Run(AllRequest())), Key.F5, ModifierKeys.None));
        Closing += (_, e) =>
        {
            if (_running && MessageBox.Show(this, "A build is running. Cancel it and close?", "AtlasWH3", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
            else if (!ConfirmDiscard()) e.Cancel = true;
            else _cts?.Cancel();
        };
    }

    // ------------------------------------------------------------------ layout

    private UIElement BuildLayout()
    {
        var dock = new DockPanel();

        var bar = new DockPanel { Background = Theme.Brush("Panel"), LastChildFill = true };
        var tour = Theme.IconButton(Theme.Glyph.Info, "Walkthrough", (_, _) => Walkthrough.Start(this), null).Card("build.walkthrough").Spot("build.walkthrough");
        tour.Margin = new Thickness(0, 6, 8, 6);
        DockPanel.SetDock(tour, Dock.Right);
        bar.Children.Add(tour);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        var projectGroup = new StackPanel { Orientation = Orientation.Horizontal }.Spot("build.projectBar");
        left.Children.Add(projectGroup);
        projectGroup.Children.Add(Theme.Icon(Theme.Glyph.Map, 16, Theme.Brush("Accent")));
        _projectLabel.Margin = new Thickness(6, 0, 12, 0);
        projectGroup.Children.Add(_projectLabel.Card("build.project"));
        projectGroup.Children.Add(Theme.IconButton(Theme.Glyph.Open, "Open…", (_, _) => OpenProjectDialog(), null).Card("build.open"));
        projectGroup.Children.Add(Theme.IconButton(Theme.Glyph.New, "New…", (_, _) => NewProjectDialog(), null).Card("build.new"));
        _save = Theme.IconButton(Theme.Glyph.Save, "Save", (_, _) => SaveProject(), null).Card("build.save");
        projectGroup.Children.Add(_save);
        left.Children.Add(new Border { Width = 1, Background = Theme.Brush("BorderBrush"), Margin = new Thickness(8, 2, 12, 2) });
        _buildAll = Theme.IconButton(Theme.Glyph.Play, "Build all", (_, _) => Run(AllRequest()), null, (Style)FindResource("AccentButton")).Card("build.buildAll");
        _runSelected = Theme.IconButton(Theme.Glyph.Running, "Run selected", (_, _) => RunSelected(), null).Card("build.runSelected");
        _packOnly = Theme.IconButton(Theme.Glyph.Package, "Pack only", (_, _) => Run(new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Pack } }), null).Card("build.packOnly");
        _cancel = Theme.IconButton(Theme.Glyph.Stop, "Cancel", (_, _) => { _cts?.Cancel(); _progress.Text = "Cancelling…"; }, null).Card("build.cancel");
        _cancel.IsEnabled = false;
        var runGroup = new StackPanel { Orientation = Orientation.Horizontal }.Spot("build.runBar");
        foreach (var b in new[] { _buildAll, _runSelected, _packOnly, _cancel }) runGroup.Children.Add(b);
        left.Children.Add(runGroup);
        left.Children.Add(_progress.Card("build.progress").Spot("build.progress"));
        bar.Children.Add(left);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);

        // left: the segment / step checklist
        var treePanel = new DockPanel { Background = Theme.Brush("Panel") }.Spot("build.tree");
        var treeHint = new TextBlock
        {
            Text = "Ticked rows run on Build all, top to bottom; ticks are saved with the project. Select a row and press Run selected to run only that part. Hover a row for what it does.",
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(10, 6, 10, 8), FontSize = 11,
        };
        DockPanel.SetDock(treeHint, Dock.Bottom);
        treePanel.Children.Add(treeHint);
        var treeHeader = Theme.Header("Build steps (run top to bottom)", 8).Card("build.tree");
        treeHeader.Margin = new Thickness(10, 8, 10, 4);
        DockPanel.SetDock(treeHeader, Dock.Top);
        treePanel.Children.Add(treeHeader);
        treePanel.Children.Add(_tree);

        // right: log + profile tabs
        var tabs = new TabControl { Margin = new Thickness(0, 4, 0, 0) };
        tabs.Items.Add(new TabItem { Header = "Log", Content = BuildLogPanel() }.Card("build.tab.log").Spot("build.logTab"));
        tabs.Items.Add(new TabItem { Header = "Project settings", Content = _profileHost }.Card("build.tab.settings").Spot("build.settingsTab"));

        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 220 });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(treePanel, 0);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("Bg") };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(tabs, 2);
        _body.Children.Add(treePanel);
        _body.Children.Add(splitter);
        _body.Children.Add(tabs);

        var host = new Grid();
        host.Children.Add(_body);
        host.Children.Add(_empty);
        dock.Children.Add(host);
        return dock;
    }

    private UIElement BuildLogPanel()
    {
        var dock = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) }.Spot("build.logTools");
        tools.Children.Add(new TextBlock { Text = "Filter", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }.Card("build.filter"));
        tools.Children.Add(_filter.Card("build.filter"));
        tools.Children.Add(_onlySelected.Card("build.onlySelected"));
        tools.Children.Add(_autoScroll.Card("build.autoScroll"));
        tools.Children.Add(new Border { Width = 12 });
        tools.Children.Add(Theme.IconButton(Theme.Glyph.Copy, "Copy", (_, _) => Clipboard.SetText(_log.Text)).Card("build.copy"));
        tools.Children.Add(Theme.IconButton(Theme.Glyph.Folder, "Log file", (_, _) => Reveal(_lastLogFile)).Card("build.logFile"));
        tools.Children.Add(Theme.IconButton(Theme.Glyph.Folder, "Output folder", (_, _) => Reveal(_project is null ? null : _project.OutputDir(Paths()))).Card("build.outputFolder"));
        tools.Children.Add(Theme.IconButton(Theme.Glyph.Package, "Pack file", (_, _) => Reveal(PackPath())).Card("build.packFile"));
        _showTileErrors = Theme.IconButton(Theme.Glyph.Warning, "Show tile errors", (_, _) => ShowTileErrors()).Card("build.tileErrors");
        _showTileErrors.Visibility = Visibility.Collapsed;
        tools.Children.Add(_showTileErrors);
        DockPanel.SetDock(tools, Dock.Top);
        dock.Children.Add(tools);
        dock.Children.Add(_log.Spot("build.log"));
        return dock;
    }

    private void ShowEmptyState()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 }.Spot("build.empty");
        panel.Children.Add(Theme.Icon(Theme.Glyph.Build, 40, Theme.Brush("Accent")));
        panel.Children.Add(new TextBlock { Text = "No project open", FontSize = 20, Margin = new Thickness(0, 12, 0, 6), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = $"A project (.atlaswh3) says which map and assembly kit to build and how: compile steps, custom steps, packing and installing. " +
                   $"Create one for {_defaults.MapName} or open an existing one.",
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), TextAlignment = TextAlignment.Center,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.New, $"New project for {_defaults.MapName}…", (_, _) => NewProjectDialog(), style: (Style)FindResource("AccentButton")).Card("build.emptyNew"));
        buttons.Children.Add(Theme.IconButton(Theme.Glyph.Open, "Open project…", (_, _) => OpenProjectDialog()).Card("build.emptyOpen"));
        panel.Children.Add(buttons);
        var recent = AppSettings.Current.RecentProjects.Where(File.Exists).Take(6).ToList();
        if (recent.Count > 0)
        {
            panel.Children.Add(Theme.Header("Recent", 20));
            foreach (var r in recent)
            {
                var link = new Button { Content = r, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1) }.Card("build.recent");
                link.Click += (_, _) => OpenProject(r);
                panel.Children.Add(link);
            }
        }
        _empty.Background = Theme.Brush("Bg");
        _empty.Child = panel;
        _empty.Visibility = Visibility.Visible;
        _body.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ project

    private ProjectPaths Paths() => _project!.ToPaths(_defaults);

    private string? PackPath() =>
        _project is { } p && p.Build.Pack.Output.Length > 0 ? p.Resolve(p.Build.Pack.Output, Paths()) : null;

    private void OpenProjectDialog()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "AtlasWH3 project (*.atlaswh3)|*.atlaswh3|All files|*.*" };
        if (dlg.ShowDialog(this) == true) OpenProject(dlg.FileName);
    }

    private void NewProjectDialog()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new SaveFileDialog
        {
            Filter = "AtlasWH3 project (*.atlaswh3)|*.atlaswh3", FileName = _defaults.MapName + BuildProject.Extension,
            Title = "New project: where to save it (the default pack goes next to it)",
        };
        if (dlg.ShowDialog(this) != true) return;
        var project = BuildProject.CreateDefault(_defaults.MapName, _defaults.AssemblyKitRoot);
        project.Save(dlg.FileName);
        OpenProject(dlg.FileName);
    }

    public void OpenProject(string file)
    {
        if (_running) return;
        try
        {
            _project = BuildProject.Load(file);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ErrorDialog.Show(this, $"Could not open {Path.GetFileName(file)}.", e);
            return;
        }
        AppSettings.Current.AddRecentProject(file);
        TrySaveSettings();
        _dirty = false;
        _lines.Clear();
        RefreshLog();
        _empty.Visibility = Visibility.Collapsed;
        _body.Visibility = Visibility.Visible;
        RebuildTree();
        _profileHost.Content = new BuildProfileEditor(_project, () => Paths(), () => { MarkDirty(); RebuildTree(); });
        UpdateTitle();
        _progress.Text = "";
    }

    private bool SaveProject()
    {
        if (_project is null) return false;
        try
        {
            _project.Save();
            _dirty = false;
            UpdateTitle();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorDialog.Show(this, "Could not save the project.", e);
            return false;
        }
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty || _project is null) return true;
        var r = MessageBox.Show(this, $"Save changes to {_project.Name}?", "AtlasWH3", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r == MessageBoxResult.No || (r == MessageBoxResult.Yes && SaveProject());
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _project is null ? "no project" : (_project.Name.Length > 0 ? _project.Name : _project.Map);
        Title = AppInfo.Title($"Build — {name}{(_dirty ? " *" : "")}");
        _projectLabel.Text = _project is null ? "No project" : $"{name}  ({_project.Map})";
        _save.IsEnabled = _project is not null;
    }

    private static void TrySaveSettings()
    {
        try { AppSettings.Current.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ checklist

    private void RebuildTree()
    {
        _rows.Clear();
        _tree.Items.Clear();
        var p = _project!;
        var profile = p.Build;
        var steps = profile.Steps.Count > 0 ? profile.Steps.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                            : CampaignBuildPipeline.NativeSteps.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AddRow(_tree.Items, "validate", BuildSegment.Validate, "Validate", InfoCard.For("build.validate"), true, null);
        AddCustom(CustomStepStage.BeforeCompile);
        var compile = AddRow(_tree.Items, "compile", BuildSegment.Compile, "Compile", InfoCard.For("build.compile"), true, null);
        foreach (var step in CampaignBuildPipeline.NativeSteps)
            AddRow(compile.Item.Items, "compile:" + step.Name, BuildSegment.Compile, step.Name,
                   InfoCards.BuildStepKey(step.Name) is { } key ? InfoCard.For(key) : InfoCard.Make(step.Name, $"Replaces BOB {step.ReplacesBobAction}."), steps.Contains(step.Name),
                   on => SetStep(step.Name, on)).Step = step.Name;
        compile.Item.IsExpanded = true;
        AddCustom(CustomStepStage.AfterCompile);
        var packTarget = profile.Pack.Output.Length > 0 ? $"{profile.Pack.Mode} pack → {Path.GetFileName(p.Expand(profile.Pack.Output, Paths()))}" : "No pack output set yet (Project settings → Pack).";
        AddRow(_tree.Items, "pack", BuildSegment.Pack, "Pack", InfoCard.Make("Pack", $"{InfoCards.Get("build.pack").Text}\n{packTarget}"),
               profile.Pack.Enabled, on => { profile.Pack.Enabled = on; MarkDirty(); });
        AddCustom(CustomStepStage.AfterPack);
        AddRow(_tree.Items, "install", BuildSegment.Install, "Install", InfoCard.For("build.install"), profile.Install.Enabled,
               on => { profile.Install.Enabled = on; MarkDirty(); });
        AddCustom(CustomStepStage.AfterInstall);

        void AddCustom(CustomStepStage stage)
        {
            foreach (var c in profile.CustomSteps.Where(c => c.RunAt == stage))
            {
                var row = AddRow(_tree.Items, "custom:" + c.Name, BuildSegment.Custom, c.Name,
                                 InfoCard.Make($"Custom step: {c.Name} ({stage})", $"Runs: {c.Command} {c.Arguments}\n{InfoCards.Get("build.custom").Text}"), c.Enabled,
                                 on => { c.Enabled = on; MarkDirty(); });
                row.Custom = c.Name;
            }
        }
    }

    private Row AddRow(ItemCollection parent, string id, BuildSegment segment, string label, ToolTip card, bool on, Action<bool>? changed)
    {
        var check = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var icon = Theme.Icon(Theme.Glyph.Pending, 12, Theme.Brush("DimText"));
        icon.Width = 18;
        var info = new TextBlock { Foreground = Theme.Brush("DimText"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (segment == BuildSegment.Custom) text.FontStyle = FontStyles.Italic;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { check, icon, text, info }, ToolTip = card };
        ToolTipService.SetInitialShowDelay(header, InfoCard.ShowDelayMs);
        ToolTipService.SetShowDuration(header, 60_000);
        var item = new TreeViewItem { Header = header, Padding = new Thickness(2, 3, 2, 3) };
        if (id is "validate" or "compile:tile_list")
            item.MouseDoubleClick += (_, e) => { if (_showTileErrors.Visibility == Visibility.Visible) { ShowTileErrors(); e.Handled = true; } };
        parent.Add(item);
        var row = new Row { Id = id, Segment = segment, Check = check, StatusIcon = icon, Info = info, Item = item };
        _rows[id] = row;
        // Validate and Compile ticks only apply to this session (unticking Compile skips the segment, not its steps)
        if (changed is not null)
            check.Click += (_, _) => changed(check.IsChecked == true);
        return row;
    }

    private void SetStep(string step, bool on)
    {
        var all = CampaignBuildPipeline.NativeSteps.Select(s => s.Name).ToList();
        var ticked = _rows.Values.Where(r => r.Step is not null && r.Check.IsChecked == true).Select(r => r.Step!).ToList();
        _project!.Build.Steps = ticked.Count == all.Count ? [] : ticked;
        MarkDirty();
    }

    private void SetStatus(string id, string status, string? info = null)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        var (glyph, brush) = status switch
        {
            "running" => (Theme.Glyph.Running, "Accent"),
            "ok" => (Theme.Glyph.Check, "Ok"),
            "warning" => (Theme.Glyph.Warning, "Warn"),
            "failed" or "blocked" => (Theme.Glyph.Error, "Error"),
            "cancelled" => (Theme.Glyph.Cancelled, "DimText"),
            "skipped" => (Theme.Glyph.Skipped, "DimText"),
            _ => (Theme.Glyph.Pending, "DimText"),
        };
        row.StatusIcon.Text = glyph;
        row.StatusIcon.Foreground = Theme.Brush(brush);
        row.StatusIcon.ToolTip = status;
        row.Info.Text = info ?? (status == "running" ? "running…" : status == "pending" ? "" : status);
    }

    // ------------------------------------------------------------------ running

    private BuildRunner.Request AllRequest()
    {
        bool On(string id) => _rows.TryGetValue(id, out var r) && r.Check.IsChecked == true;
        var steps = _rows.Values.Where(r => r.Step is not null && r.Check.IsChecked == true).Select(r => r.Step!).ToList();
        var custom = _rows.Values.Where(r => r.Custom is not null && r.Check.IsChecked == true).Select(r => r.Custom!).ToList();
        var segments = new HashSet<BuildSegment>();
        if (On("validate")) segments.Add(BuildSegment.Validate);
        if (On("compile") && steps.Count > 0) segments.Add(BuildSegment.Compile);
        if (custom.Count > 0) segments.Add(BuildSegment.Custom);
        if (On("pack")) segments.Add(BuildSegment.Pack);
        if (On("install")) segments.Add(BuildSegment.Install);
        return new BuildRunner.Request { Segments = segments, Steps = steps, CustomSteps = custom };
    }

    /// <summary>Build all (the ticked segments), as the toolbar button / F5.</summary>
    public void BuildAll() => Run(AllRequest());

    private void RunSelected()
    {
        if (_tree.SelectedItem is not TreeViewItem item || _rows.Values.FirstOrDefault(r => r.Item == item) is not { } row)
        {
            _progress.Text = "Select a segment or step first.";
            return;
        }
        var request = row switch
        {
            { Step: { } step } => new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Compile }, Steps = [step] },
            { Custom: { } name } => new BuildRunner.Request { Segments = new HashSet<BuildSegment> { BuildSegment.Custom }, CustomSteps = [name] },
            { Id: "compile" } => new BuildRunner.Request
            {
                Segments = new HashSet<BuildSegment> { BuildSegment.Compile },
                Steps = _rows.Values.Where(r => r.Step is not null && r.Check.IsChecked == true).Select(r => r.Step!).ToList(),
            },
            _ => new BuildRunner.Request { Segments = new HashSet<BuildSegment> { row.Segment } },
        };
        Run(request);
    }

    private async void Run(BuildRunner.Request request)
    {
        if (_project is null || _running) return;
        if (request.Segments is { Count: 0 })
        {
            _progress.Text = "Nothing ticked to run.";
            return;
        }
        if (request.Segments?.Contains(BuildSegment.Compile) == true && request.Steps is { Count: 0 })
        {
            _progress.Text = "No compile steps ticked.";
            return;
        }
        foreach (var r in _rows.Values) SetStatus(r.Id, "pending");
        _showTileErrors.Visibility = Visibility.Collapsed;
        _lines.Clear();
        RefreshLog();
        _running = true;
        BuildingChanged?.Invoke();
        _cts = new CancellationTokenSource();
        SetRunningUi(true);
        var project = _project;
        var paths = Paths();
        var sw = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => _progress.Text = $"Building… {sw.Elapsed:m\\:ss}";
        timer.Start();
        BuildRunner.Report? report = null;
        try
        {
            var token = _cts.Token;
            report = await Task.Run(() => new BuildRunner(project, paths, e => Dispatcher.BeginInvoke(() => OnEvent(e)), token).Run(request));
        }
        catch (Exception e)
        {
            ErrorDialog.Show(this, "The build stopped with an unexpected error.", e);
        }
        finally
        {
            timer.Stop();
            _running = false;
            BuildingChanged?.Invoke();
            SetRunningUi(false);
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);   // let queued events land
        if (report is null) return;
        _lastLogFile = report.LogFile;
        var compileItems = report.Items.Where(i => i.Id.StartsWith("compile:")).ToList();
        if (compileItems.Count > 0)
            SetStatus("compile", compileItems.All(i => i.Succeeded) ? "ok" : compileItems.Any(i => i.Status == "cancelled") ? "cancelled" : "failed",
                      $"{compileItems.Sum(i => i.Seconds):F0} s step time");
        var cancelled = _cts?.IsCancellationRequested == true;
        _progress.Text = report.Succeeded ? $"Build succeeded in {Duration(report.Seconds)}"
            : cancelled ? $"Build cancelled after {Duration(report.Seconds)}"
            : $"Build failed after {Duration(report.Seconds)}: {report.Items.FirstOrDefault(i => !i.Succeeded)?.Id}";
        _progress.Foreground = Theme.Brush(report.Succeeded ? "Ok" : cancelled ? "DimText" : "Error");
    }

    private void SetRunningUi(bool running)
    {
        _buildAll.IsEnabled = _runSelected.IsEnabled = _packOnly.IsEnabled = !running;
        _cancel.IsEnabled = running;
        foreach (var r in _rows.Values) r.Check.IsEnabled = !running;   // the tree stays enabled (selection, scrolling)
        _profileHost.IsEnabled = !running;
        if (running) _progress.Foreground = Theme.Brush("Text");
    }

    private void OnEvent(BuildRunner.Event e)
    {
        switch (e.Kind)
        {
            case BuildRunner.EventKind.Started:
                SetStatus(e.Id, "running");
                if (e.Id.StartsWith("compile:")) SetStatus("compile", "running");   // compile steps log their own "start"
                else AddLine(e.Id, "— start");
                break;
            case BuildRunner.EventKind.Log:
                AddLine(e.Id, e.Message!);
                break;
            case BuildRunner.EventKind.Finished:
                var r = e.Result!;
                SetStatus(e.Id, r.Status, $"{Duration(r.Seconds)}{(r.FilesWritten > 0 ? $" · {r.FilesWritten} files" : "")}{(r.Status is "ok" ? "" : " · " + r.Status)}");
                if (_rows.TryGetValue(e.Id, out var row))
                    row.Item.ToolTip = string.Join("\n", r.Problems.Concat(r.Notes)) is { Length: > 0 } t ? t : null;
                AddLine(e.Id, $"— {r.Status} in {Duration(r.Seconds)}");
                foreach (var p in r.Problems) AddLine(e.Id, "! " + p);
                foreach (var n in r.Notes) AddLine(e.Id, BuildRunner.NoteLine(n));
                if (HasTileErrors(e.Id, r)) _showTileErrors.Visibility = Visibility.Visible;
                break;
        }
    }

    private Button _showTileErrors = null!;

    /// <summary>Validate found tile-map problems, or tile_list left holes: worth opening the tile error mode.</summary>
    private static bool HasTileErrors(string id, BuildRunner.ItemResult r) =>
        (id == "validate" && r.Problems.Concat(r.Notes).Any(n => n.Contains("tile map ") && !n.Contains("accepted tile map")))
        || (id == "compile:tile_list" && r.Notes.Any(n => n.Contains("got no tile")));

    private void ShowTileErrors()
    {
        if (_project is null) return;
        try { new CampaignTileWindow(Paths(), null, errorMode: true).Show(); }
        catch (Exception e) { ErrorDialog.Show(this, "Could not open the tile map editor.", e); }
    }

    private void AddLine(string id, string text)
    {
        _lines.Add((id, text));
        var line = $"[{id}] {text}";
        if (Shown(id, line))
        {
            _log.AppendText(line + "\n");
            if (_autoScroll.IsChecked == true) _log.ScrollToEnd();
        }
    }

    private bool Shown(string id, string line)
    {
        if (_filter.Text.Length > 0 && !line.Contains(_filter.Text, StringComparison.OrdinalIgnoreCase)) return false;
        if (_onlySelected.IsChecked == true && _tree.SelectedItem is TreeViewItem item && _rows.Values.FirstOrDefault(r => r.Item == item) is { } row)
            return row.Id == "compile" ? id.StartsWith("compile") : id == row.Id;
        return true;
    }

    private void RefreshLog()
    {
        var sb = new StringBuilder();
        foreach (var (id, text) in _lines)
        {
            var line = $"[{id}] {text}";
            if (Shown(id, line)) sb.Append(line).Append('\n');
        }
        _log.Text = sb.ToString();
        if (_autoScroll.IsChecked == true) _log.ScrollToEnd();
    }

    private static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:F1} s" : $"{(int)(seconds / 60)} min {seconds % 60:F0} s";

    private void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else _progress.Text = $"{path} does not exist yet.";
    }
}

/// <summary>An ICommand from a delegate (key bindings in code-built windows).</summary>
public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
