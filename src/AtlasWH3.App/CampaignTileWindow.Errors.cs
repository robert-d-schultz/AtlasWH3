using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AtlasWH3.Core.Campaign.TileMapCheck;

namespace AtlasWH3.App;

/// <summary>
/// Tile error mode (the Errors tab, F8): every tile-map error is highlighted (red errors, amber warnings, magenta holes;
/// dots when zoomed out) and listed by code, each with a recommended fix from <see cref="TileErrors"/>. Selecting an
/// error centres it and previews its fix over the map; Apply (Enter) makes the fix an ordinary unsaved stroke (Ctrl+Z
/// undoes it, Save journals it). Holes come from the BOB tile-matching simulation (Find holes, 1-3 min).
/// </summary>
public sealed partial class CampaignTileWindow
{
    private readonly TabControl _tabs = new();
    private bool _errorMode, _startInErrorMode, _fixRunning;
    private DateTime? _holesCheckedAt;
    private IReadOnlyList<TileError> _errors = [];
    private List<TileError> _visible = [];
    private TileError? _selectedError;
    private int _errorVersion, _verifyVersion;
    private byte[]? _climate;

    private readonly TextBlock _errorSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TreeView _errorTree = new() { Height = 330, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _errorDetail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly CheckBox _showWarnings = new() { Content = "Show warnings", IsChecked = true };
    private Button _applyFix = null!;

    private UIElement BuildErrorsPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(Theme.Header("Tile errors", 0));
        panel.Children.Add(_errorSummary);
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        tools.Children.Add(Theme.IconButton(Theme.Glyph.Running, "Re-check", (_, _) => FindErrors(), "Check the whole map again"));
        tools.Children.Add(Theme.IconButton(Theme.Glyph.Warning, "Find holes", (_, _) => Simulate(),
            "Run BOB's tile matching on the map (1-3 min, in the background) to find points that get no tile"));
        panel.Children.Add(tools);
        _showWarnings.Click += (_, _) => { RefreshErrorList(); RefreshOverlay(); };
        panel.Children.Add(_showWarnings);
        ScrollViewer.SetHorizontalScrollBarVisibility(_errorTree, ScrollBarVisibility.Disabled);
        _errorTree.SelectedItemChanged += (_, _) =>
        {
            if (_errorTree.SelectedItem is TreeViewItem { Tag: TileError e }) SelectError(e, centre: true);
        };
        panel.Children.Add(_errorTree);

        panel.Children.Add(Theme.Header("Selected"));
        panel.Children.Add(_errorDetail);
        var fixRow = new WrapPanel();
        _applyFix = Theme.IconButton(Theme.Glyph.Check, "Apply fix", (_, _) => ApplySelectedFix(), "Apply the recommended fix (Enter)", (Style)FindResource("AccentButton"));
        fixRow.Children.Add(_applyFix);
        fixRow.Children.Add(Theme.IconButton(Theme.Glyph.Up, "Prev", (_, _) => Step(-1), "Previous error (Shift+N)"));
        fixRow.Children.Add(Theme.IconButton(Theme.Glyph.Down, "Next", (_, _) => Step(+1), "Next error (N)"));
        panel.Children.Add(fixRow);
        var allRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        allRow.Children.Add(Theme.IconButton(Theme.Glyph.Build, "Fix all of this type", (_, _) => FixAll(onlySelectedCode: true),
            "Apply every verified fix for the selected error's code"));
        allRow.Children.Add(Theme.IconButton(Theme.Glyph.Build, "Fix all safe", (_, _) => FixAll(onlySelectedCode: false),
            "Apply every verified fix on the map (one undoable stroke)"));
        panel.Children.Add(allRow);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Theme.Brush("DimText"), FontSize = 11,
            Text = "Red: errors, amber: warnings, magenta: holes (Find holes). The selected error is ringed; its fix shows on the map. " +
                   "Each fix is checked with BOB's tile matching around it before it can be applied. Fixes are unsaved strokes: " +
                   "Ctrl+Z undoes, Ctrl+S saves.",
        });
        UpdateErrorDetail();
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush("Panel"), Content = panel };
    }

    private void SetErrorMode(bool on)
    {
        if (_errorMode == on) return;
        _errorMode = on;
        if (on)
        {
            _tool = Tool.Navigate;
            _toolButtons[Tool.Navigate].IsChecked = true;
            FindErrors();
        }
        else
        {
            _view.Ghost = null;
            _view.Selected = null;
            _view.Markers = null;
        }
        RefreshOverlay();
        _view.InvalidateVisual();
    }

    /// <summary>Checks the current map (unsaved strokes included) in the background; the latest call wins.</summary>
    private void FindErrors()
    {
        if (_map == null || _db == null) return;
        var version = ++_errorVersion;
        var map = new HexTileMap(_map.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone());
        var holes = _holesCheckedAt is null ? null : _simHexes;
        var db = _db;
        var vanilla = TileMapValidator.DefaultVanillaTileMap(_paths);
        var editor = _editor!;
        var climate = _climate;
        _errorSummary.Text = "Checking the map…";
        Task.Run(() => (Errors: TileErrors.Find(map, db, holes, null, suggest: true, vanilla), Climate: climate ?? editor.Climate(map))).ContinueWith(t =>
        {
            if (version != _errorVersion) return;
            if (!t.IsCompletedSuccessfully)
            {
                _errorSummary.Text = "Check failed: " + t.Exception?.InnerException?.Message;
                return;
            }
            _climate = t.Result.Climate;
            var previous = _selectedError;
            var previousIndex = previous is null ? -1 : _visible.IndexOf(previous);
            _errors = t.Result.Errors.Where(e => e.Col >= 0).ToList();
            RefreshErrorList();
            var again = previous is null ? null : _visible.FirstOrDefault(e => e.Code == previous.Code && e.Col == previous.Col && e.Row == previous.Row);
            if (again is null && previousIndex >= 0 && _visible.Count > 0)
                SelectInTree(_visible[Math.Min(previousIndex, _visible.Count - 1)]);   // it was fixed: carry on with the next one
            else
                SelectError(again, centre: false);
            RefreshOverlay();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshErrorList()
    {
        var showWarnings = _showWarnings.IsChecked == true;
        _visible = _errors.Where(e => showWarnings || e.Severity == TileMapFinding.Error)
            .OrderBy(e => e.Severity == TileMapFinding.Error ? 0 : 1).ThenBy(e => e.Code).ThenBy(e => e.Row).ThenBy(e => e.Col).ToList();
        int errors = _errors.Count(e => e.Severity == TileMapFinding.Error), warnings = _errors.Count - errors;
        var holes = _errors.Count(e => e.Code == TileErrors.HoleCode);
        _errorSummary.Text = $"{errors:N0} errors, {warnings:N0} warnings; {_errors.Count(e => e.Fix is not null):N0} with a recommended fix.\n" +
                             (_holesCheckedAt is { } at ? $"Holes: {holes:N0} (found {at:HH:mm}{(_pending.Count > 0 ? ", before some edits" : "")})."
                                                        : "Holes not checked yet: press Find holes.");
        _errorTree.Items.Clear();
        foreach (var group in _visible.GroupBy(e => e.Code))
        {
            var list = group.ToList();
            var header = new TextBlock
            {
                Text = $"{group.Key}  ({list.Count:N0}, {list.Count(e => e.Fix is not null):N0} fixable)",
                Foreground = Theme.Brush(list[0].Severity == TileMapFinding.Error ? "Error" : "Warn"), FontWeight = FontWeights.SemiBold,
                ToolTip = TileErrors.AdviceFor(group.Key),
            };
            var node = new TreeViewItem { Header = header, Tag = group.Key, IsExpanded = _visible.Count < 300 };
            foreach (var e in list.Take(1000))
                node.Items.Add(new TreeViewItem
                {
                    Header = $"[{e.Col},{e.Row}]  {e.Message}{(e.Fix is null ? "  (manual)" : "")}", Tag = e,
                    Foreground = Theme.Brush(e.Fix is null ? "DimText" : "Text"),
                });
            if (list.Count > 1000) node.Items.Add(new TreeViewItem { Header = $"… {list.Count - 1000:N0} more (fix some first)" });
            _errorTree.Items.Add(node);
        }
    }

    private void RefreshErrorOverlay()
    {
        Array.Clear(_view.Overlay);
        foreach (var h in _pending) _view.Overlay[_map!.Index(h.Col, h.Row)] = 1;
        var markers = new List<(int, int, byte)>(_visible.Count);
        foreach (var e in _visible)
        {
            var kind = e.Code == TileErrors.HoleCode ? (byte)7 : e.Severity == TileMapFinding.Error ? (byte)5 : (byte)6;
            ref var o = ref _view.Overlay[_map!.Index(e.Col, e.Row)];
            if (o is not 5 and not 7) o = kind;
            markers.Add((e.Col, e.Row, kind));
        }
        _view.Markers = markers;
        _view.Invalidate();
        _view.InvalidateVisual();
    }

    private void SelectError(TileError? e, bool centre)
    {
        _selectedError = e;
        _view.Selected = e is null ? null : (e.Col, e.Row);
        _view.Ghost = e?.Fix?.Preview.ToDictionary(h => _map!.Index(h.Col, h.Row), h => h.Rgb);
        if (e is not null && centre) _view.CentreOn(e.Col, e.Row, Math.Max(14, 2 / _view.View.Scale));
        UpdateErrorDetail();
        if (e is { Fix.Verified: false }) VerifySelected(e);
        _view.Invalidate();
        _view.InvalidateVisual();
    }

    private void UpdateErrorDetail()
    {
        if (_selectedError is not { } e)
        {
            _errorDetail.Text = "Select an error in the list (or press N).";
            if (_applyFix != null) _applyFix.IsEnabled = false;
            return;
        }
        var text = $"{e.Code} at [{e.Col},{e.Row}]\n{e.Message}\n\n";
        text += e.Fix is { } f
            ? $"Recommended: {f.Summary}.\nLocal issue score {f.ScoreBefore} → {f.ScoreAfter}. " +
              (f.Verified ? $"Tile matching around it: holes {f.HolesBefore} → {f.HolesAfter}." : "Checking it for holes…")
            : $"No safe automatic fix (none that keeps BOB's tile matching whole). {e.Advice}";
        _errorDetail.Text = text;
        _applyFix.IsEnabled = e.Fix is { Verified: true } && !_fixRunning;
    }

    /// <summary>Checks the selected error's fix with BOB's tile matching around it (a fraction of a second); a fix that
    /// would open a hole is replaced by the next candidate, or dropped.</summary>
    private void VerifySelected(TileError e)
    {
        if (_map == null || _db == null || _climate == null) return;
        var version = ++_verifyVersion;
        var map = new HexTileMap(_map.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone());
        var (db, climate, vanilla) = (_db, _climate, TileMapValidator.DefaultVanillaTileMap(_paths));
        Task.Run(() => TileErrors.Suggest(map, db, e, vanilla, climate)).ContinueWith(t =>
        {
            if (version != _verifyVersion || !ReferenceEquals(_selectedError, e) || !t.IsCompletedSuccessfully) return;
            var checkedError = e with { Fix = t.Result };
            _errors = _errors.Select(x => ReferenceEquals(x, e) ? checkedError : x).ToList();
            var i = _visible.IndexOf(e);
            if (i >= 0) _visible[i] = checkedError;
            foreach (TreeViewItem group in _errorTree.Items)
                foreach (var item in group.Items.OfType<TreeViewItem>())
                    if (ReferenceEquals(item.Tag, e))
                    {
                        item.Tag = checkedError;
                        item.Header = $"[{e.Col},{e.Row}]  {e.Message}{(checkedError.Fix is null ? "  (manual)" : "")}";
                        item.Foreground = Theme.Brush(checkedError.Fix is null ? "DimText" : "Text");
                    }
            _selectedError = checkedError;
            _view.Ghost = checkedError.Fix?.Preview.ToDictionary(h => _map!.Index(h.Col, h.Row), h => h.Rgb);
            _view.Invalidate();
            _view.InvalidateVisual();
            UpdateErrorDetail();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Step(int delta)
    {
        if (_visible.Count == 0) return;
        var i = _selectedError is null ? -1 : _visible.IndexOf(_selectedError);
        var next = _visible[((i < 0 ? (delta > 0 ? -1 : 0) : i) + delta + _visible.Count) % _visible.Count];
        SelectInTree(next);
    }

    private void SelectInTree(TileError e)
    {
        foreach (TreeViewItem group in _errorTree.Items)
            foreach (var item in group.Items.OfType<TreeViewItem>())
                if (ReferenceEquals(item.Tag, e))
                {
                    group.IsExpanded = true;
                    item.IsSelected = true;
                    item.BringIntoView();
                    return;
                }
        SelectError(e, centre: true);
    }

    private void ApplySelectedFix()
    {
        if (_selectedError is not { Fix: { } fix } e) return;
        var index = _visible.IndexOf(e);
        ApplyOps(fix.Ops, fix.Preview.Select(h => (h.Col, h.Row)));
        _status.Text = $"Applied: {fix.Summary} at [{e.Col},{e.Row}]  (Ctrl+Z undoes)";
        // move on to the next error while the re-check runs
        if (index >= 0 && index + 1 < _visible.Count) SelectInTree(_visible[index + 1]);
    }

    /// <summary>Applies ops as ONE stroke (a fix may repaint several hexes in several colours).</summary>
    private void ApplyOps(JsonArray ops, IEnumerable<(int Col, int Row)>? area)
    {
        var before = area?.Where(h => _ops!.InMap(h.Col, h.Row)).Distinct().ToDictionary(h => h, OldPixels);
        var snapshot = before is null ? (uint[])_map!.Pixels.Clone() : null;
        var tileOps = new TileMapOps(_map!, _db!);
        foreach (var op in ops) tileOps.Apply((JsonObject)op!);
        var old = new Dictionary<(int, int), uint[]>();
        foreach (var h in tileOps.Changed)
            old[h] = before?.GetValueOrDefault(h) ?? Pixels(snapshot!, h);
        if (old.Count > 0) Push(new Stroke(ops.DeepClone(), old));
        else RefreshOverlay();
    }

    private void FixAll(bool onlySelectedCode)
    {
        if (_map == null || _db == null || _fixRunning) return;
        var code = onlySelectedCode ? _selectedError?.Code ?? (_errorTree.SelectedItem as TreeViewItem)?.Tag as string : null;
        if (onlySelectedCode && code is null) { _status.Text = "Select an error (or a group) first."; return; }
        var targets = _visible.Where(e => code is null || e.Code == code).ToList();
        if (targets.Count == 0) return;
        _fixRunning = true;
        UpdateErrorDetail();
        var map = new HexTileMap(_map.PixelWidth, _map.PixelHeight, (uint[])_map.Pixels.Clone());
        var db = _db;
        var vanilla = TileMapValidator.DefaultVanillaTileMap(_paths);
        _status.Text = $"Fixing {targets.Count:N0} {(code ?? "")} errors…";
        var climate = _climate ?? _editor!.Climate(map);
        var progress = new Progress<(int Done, int Total)>(p => _status.Text = $"Fixing… {p.Done:N0} / {p.Total:N0} errors (each fix is checked for holes)");
        Task.Run(() => TileErrors.FixAll(map, db, targets, climate, verifiedOnly: true, vanilla, progress: progress)).ContinueWith(t =>
        {
            _fixRunning = false;
            UpdateErrorDetail();
            if (!t.IsCompletedSuccessfully) { _status.Text = "Fix failed: " + t.Exception?.InnerException?.Message; return; }
            var r = t.Result;
            if (r.Ops.Count == 0) { _status.Text = "No verified fixes to apply."; return; }
            ApplyOps(r.Ops, r.Changes.Select(h => (h.Col, h.Row)));
            _status.Text = $"Fixed {r.Fixed:N0} errors ({r.Changes.Count:N0} hexes repainted, one stroke: Ctrl+Z undoes); " +
                           $"{r.Skipped:N0} need a manual edit or an unverified fix.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ErrorKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter: ApplySelectedFix(); e.Handled = true; break;
            case System.Windows.Input.Key.N: Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1); e.Handled = true; break;
        }
    }

    private void ErrorHover((int Col, int Row) h)
    {
        var here = _visible.Where(e => e.Col == h.Col && e.Row == h.Row).ToList();
        _status.Text = $"hex [{h.Col},{h.Row}]  {_ops!.SetAt(h.Col, h.Row) ?? $"#{_ops.Colour(h.Col, h.Row):x6}"}" +
                       (here.Count == 0 ? "   |   no errors here"
                           : "   |   " + string.Join("  ·  ", here.Select(e => $"{e.Code}: {e.Message}{(e.Fix is { } f ? $" → fix: {f.Summary}" : " (manual)")}")));
    }
}
