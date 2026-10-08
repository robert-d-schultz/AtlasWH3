using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AtlasWH3.Core;

namespace AtlasWH3.App;

/// <summary>
/// Guided walkthroughs: a window's tour dims the window, rings one panel at a time and explains it on a card (Back,
/// Next, Skip; Esc skips, Enter or → goes on). Panels are tagged with <c>element.Spot("key")</c>; the step texts live in
/// one table per window (<see cref="Walkthroughs"/>, e.g. Walkthrough.Start.cs). A window joins with
/// <see cref="Enable"/>: its tour then starts by itself the first time it opens, and Help › Walkthrough for this window
/// replays it. Finished or skipped tours are remembered in <see cref="AppSettings.ToursSeen"/>.
/// </summary>
public static class Walkthrough
{
    /// <summary>The panel key a step points at.</summary>
    public static readonly DependencyProperty SpotProperty =
        DependencyProperty.RegisterAttached("Spot", typeof(string), typeof(Walkthrough), new PropertyMetadata(null));

    public static string? GetSpot(DependencyObject d) => (string?)d.GetValue(SpotProperty);
    public static void SetSpot(DependencyObject d, string? value) => d.SetValue(SpotProperty, value);

    /// <summary>Tags a panel as a walkthrough spot; returns the element for chaining.</summary>
    public static T Spot<T>(this T element, string key) where T : DependencyObject
    {
        SetSpot(element, key);
        return element;
    }

    private static readonly DependencyProperty TourKeyProperty =
        DependencyProperty.RegisterAttached("TourKey", typeof(string), typeof(Walkthrough), new PropertyMetadata(null));

    /// <summary>No tour starts by itself (self-tests, screenshots and other command-line automation).</summary>
    public static bool Suppressed { get; set; } = Environment.GetEnvironmentVariable("ATLASWH3_NO_WALKTHROUGH") == "1";

    private static readonly Dictionary<Window, TourOverlay> Running = [];

    /// <summary>Gives <paramref name="window"/> the tour <paramref name="key"/>; with <paramref name="autoStart"/> it
    /// starts once the window is shown, unless that tour was already seen.</summary>
    public static void Enable(Window window, string key, bool autoStart = true)
    {
        window.SetValue(TourKeyProperty, key);
        if (!autoStart) return;
        void Rendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= Rendered;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (!Suppressed && window.IsVisible && !AppSettings.Current.TourSeen(key)) Start(window);
            });
        }
        window.ContentRendered += Rendered;
    }

    public static bool Has(Window window) => window.GetValue(TourKeyProperty) is string key && Walkthroughs.Get(key) is not null;

    /// <summary>Starts (or restarts) the window's tour; false when it has none or nothing in it can be shown.</summary>
    public static bool Start(Window window)
    {
        if (window.GetValue(TourKeyProperty) is not string key || Walkthroughs.Get(key) is not { } tour) return false;
        if (Running.TryGetValue(window, out var open)) { open.Activate(); return true; }
        var overlay = new TourOverlay(window, tour);
        if (overlay.Progress.IsEmpty) return false;
        Running[window] = overlay;
        overlay.Closed += (_, _) => Running.Remove(window);
        overlay.Begin();
        return true;
    }

    /// <summary>Saves a PNG of every step of the window's tour into <paramref name="dir"/> (window and overlay rendered
    /// off-screen, no input sent; the Direct3D view renders black). For checking tour texts and placement:
    /// <c>AtlasWH3.exe [--scene|--tile-editor|--painter|--build] --walkthrough-shots &lt;dir&gt;</c>.</summary>
    public static async Task ShotsAsync(Window window, string dir)
    {
        await Task.Delay(2500);   // the window's own loading
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (window.GetValue(TourKeyProperty) is not string key || Walkthroughs.Get(key) is not { } tour) return;
        Directory.CreateDirectory(dir);
        var overlay = new TourOverlay(window, tour) { ShowActivated = false };
        overlay.Begin();
        do
        {
            await overlay.ShowStepAsync();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            overlay.SaveShot(Path.Combine(dir, $"{key}_{overlay.Progress.Position + 1:00}.png"));
        }
        while (overlay.Progress.Next());
        overlay.Close();
    }

    /// <summary>Clears every "seen" mark so each tour shows again the next time its window opens.</summary>
    public static void ResetAll()
    {
        AppSettings.Current.ResetTours();
        SaveSettings();
    }

    internal static void MarkSeen(string key)
    {
        if (AppSettings.Current.MarkTourSeen(key)) SaveSettings();
    }

    private static void SaveSettings()
    {
        try { AppSettings.Current.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorDialog.Log("Saving the walkthrough state failed", e); }
    }

    /// <summary>The element tagged <paramref name="spot"/> under <paramref name="root"/> (logical tree, so panels on
    /// unselected tabs and in collapsed expanders are found too).</summary>
    internal static FrameworkElement? Find(DependencyObject root, string spot)
    {
        if (root is FrameworkElement fe && GetSpot(fe) == spot) return fe;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d && Find(d, spot) is { } hit) return hit;
        return null;
    }

    /// <summary>Whether <paramref name="element"/> and every parent up to the window is set visible (a tab that is
    /// merely not selected still counts: the tour selects it).</summary>
    internal static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? d = element; d is not null and not Window; d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
}

/// <summary>All walkthrough texts. Each window keeps its tour in a partial file (Walkthrough.Start.cs declares
/// <c>private static readonly Tour Start = new("start", ...)</c>); every static <see cref="Tour"/> field is a tour.</summary>
public static partial class Walkthroughs
{
    /// <summary>One step: the panel it rings (<c>null</c> = a centred card with no panel), a title and 1-2 sentences.</summary>
    public sealed record Step(string? Spot, string Title, string Text);

    public sealed record Tour(string Key, string Name, Step[] Steps);

    private static readonly Lazy<Dictionary<string, Tour>> Table = new(() =>
    {
        var table = new Dictionary<string, Tour>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(Walkthroughs).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            if (field.FieldType == typeof(Tour) && field.GetValue(null) is Tour t) table[t.Key] = t;
        return table;
    });

    public static Tour? Get(string key) => Table.Value.GetValueOrDefault(key);
    public static IEnumerable<Tour> All => Table.Value.Values;
}

/// <summary>
/// The tour itself: a borderless, transparent window laid over the owner's client area (a separate window, so it also
/// covers the Direct3D view), with the dimmed backdrop, the ring around the current panel and the step card. It takes
/// the keyboard while open, so the editor's shortcuts don't fire behind it.
/// </summary>
internal sealed class TourOverlay : Window
{
    private const double Gap = 14, Pad = 5, CardWidth = 360;

    private readonly Window _owner;
    private readonly Walkthroughs.Tour _tour;
    public TourProgress Progress { get; }

    private readonly Canvas _canvas = new();
    private readonly System.Windows.Shapes.Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)) };
    private readonly Border _ring = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
    private readonly Border _card = new();
    private readonly TextBlock _count = new(), _title = new(), _text = new();
    private readonly Button _back = new(), _next = new(), _skip = new();

    private FrameworkElement? _target;
    private readonly List<(Selector Tabs, object? Was)> _tabs = [];
    private readonly List<Expander> _expanded = [];
    private bool _ending;

    public TourOverlay(Window owner, Walkthroughs.Tour tour)
    {
        _owner = owner;
        _tour = tour;
        var root = (DependencyObject?)owner.Content ?? owner;
        Progress = new TourProgress(tour.Steps.Select(s =>
        {
            if (s.Spot is null) return true;
            if (Walkthrough.Find(root, s.Spot) is not { } e)
            {
                ErrorDialog.Log($"Walkthrough '{tour.Key}': no panel tagged '{s.Spot}'", null);
                return false;
            }
            return Walkthrough.IsShown(e);
        }).ToList());

        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = true;
        Title = $"{tour.Name} walkthrough";

        _ring.BorderBrush = Theme.Brush("Accent");
        _ring.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = ((SolidColorBrush)Theme.Brush("Accent")).Color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.8 };
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_ring);
        _canvas.Children.Add(BuildCard());
        Content = _canvas;

        PreviewKeyDown += OnKey;
        Closing += (_, _) => End(markSeen: true);
        owner.LocationChanged += OwnerMoved;
        owner.SizeChanged += OwnerMoved;
        owner.StateChanged += OwnerState;
        owner.Closed += OwnerClosed;
    }

    private UIElement BuildCard()
    {
        _count.Foreground = Theme.Brush("DimText");
        _count.FontSize = 11;
        _title.FontSize = 16;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Theme.Brush("Text");
        _title.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new Thickness(0, 4, 0, 6);
        _text.Foreground = Theme.Brush("Text");
        _text.TextWrapping = TextWrapping.Wrap;
        _text.LineHeight = 19;

        _skip.Content = "Skip tour";
        _skip.Click += (_, _) => Close();
        _back.Content = "Back";
        _back.MinWidth = 70;
        _back.Margin = new Thickness(0, 0, 6, 0);
        _back.Click += (_, _) => Move(-1);
        _next.MinWidth = 80;
        _next.Style = (Style)Application.Current.Resources["AccentButton"];
        _next.Click += (_, _) => Move(+1);

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_skip, Dock.Left);
        buttons.Children.Add(_skip);
        DockPanel.SetDock(_next, Dock.Right);
        DockPanel.SetDock(_back, Dock.Right);
        buttons.Children.Add(_next);
        buttons.Children.Add(_back);

        var body = new StackPanel();
        body.Children.Add(_count);
        body.Children.Add(_title);
        body.Children.Add(_text);
        body.Children.Add(buttons);
        _card.Child = body;
        _card.Width = CardWidth;
        _card.Padding = new Thickness(16, 12, 16, 12);
        _card.Background = Theme.Brush("Panel");
        _card.BorderBrush = Theme.Brush("Accent");
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(6);
        _card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.6 };
        return _card;
    }

    public void Begin()
    {
        FitOwner();
        Show();
        _ = ShowStepAsync();
    }

    /// <summary>The owner window with the tour drawn over it, as a PNG.</summary>
    public void SaveShot(string file)
    {
        if (_owner.Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(root);
        int W(double v) => Math.Max(1, (int)Math.Ceiling(v * dpi.DpiScaleX));
        int H(double v) => Math.Max(1, (int)Math.Ceiling(v * dpi.DpiScaleY));
        var under = new RenderTargetBitmap(W(root.ActualWidth), H(root.ActualHeight), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        under.Render(root);
        var over = new RenderTargetBitmap(W(root.ActualWidth), H(root.ActualHeight), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        over.Render(_canvas);
        var both = new DrawingVisual();
        using (var dc = both.RenderOpen())
        {
            var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(Theme.Brush("Bg"), null, rect);
            dc.DrawImage(under, rect);
            dc.DrawImage(over, rect);
        }
        var final = new RenderTargetBitmap(W(root.ActualWidth), H(root.ActualHeight), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        final.Render(both);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(final));
        using var stream = File.Create(file);
        png.Save(stream);
    }

    private void Move(int delta)
    {
        if (delta > 0 && !Progress.Next()) { Close(); return; }
        if (delta < 0 && !Progress.Back()) return;
        _ = ShowStepAsync();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Enter or Key.Right or Key.Space or Key.PageDown: Move(+1); break;
            case Key.Left or Key.Back or Key.PageUp: Move(-1); break;
            case Key.Tab: return;                                    // move between the card's buttons
            case Key.System when e.SystemKey == Key.F4: return;   // Alt+F4 closes the tour
        }
        e.Handled = true;    // the editor behind doesn't get the keys while the tour is open
    }

    internal async Task ShowStepAsync()
    {
        var step = _tour.Steps[Progress.Current];
        _count.Text = $"{_tour.Name} · {Progress.Label}";
        _title.Text = step.Title;
        _text.Text = step.Text;
        _back.IsEnabled = !Progress.IsFirst;
        _next.Content = Progress.IsLast ? "Done" : "Next";
        _target = step.Spot is null ? null : Walkthrough.Find((DependencyObject?)_owner.Content ?? _owner, step.Spot);
        if (_target is not null)
        {
            Reveal(_target);
            _target.BringIntoView();
            // let the tab switch / scroll lay out before measuring
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        Layout();
        _next.Focus();
    }

    /// <summary>Selects the tabs and opens the expanders the target sits in (put back when the tour ends).</summary>
    private void Reveal(FrameworkElement target)
    {
        var chain = new List<DependencyObject>();
        for (var d = LogicalTreeHelper.GetParent(target); d is not null and not Window; d = LogicalTreeHelper.GetParent(d)) chain.Add(d);
        chain.Reverse();   // outermost first
        foreach (var d in chain)
        {
            if (d is TabItem { IsSelected: false } tab && ItemsControl.ItemsControlFromItemContainer(tab) is Selector tabs)
            {
                if (!_tabs.Any(t => ReferenceEquals(t.Tabs, tabs))) _tabs.Add((tabs, tabs.SelectedItem));
                tab.IsSelected = true;
            }
            else if (d is Expander { IsExpanded: false } ex)
            {
                _expanded.Add(ex);
                ex.IsExpanded = true;
            }
        }
        if (target is Expander { IsExpanded: false } own)
        {
            _expanded.Add(own);
            own.IsExpanded = true;
        }
        _owner.UpdateLayout();
    }

    private void OwnerMoved(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        FitOwner();
        Layout();
    }

    private void OwnerState(object? sender, EventArgs e)
    {
        if (_owner.WindowState == WindowState.Minimized) { Hide(); return; }
        if (!IsVisible && !_ending) Show();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { FitOwner(); Layout(); });
    }

    private void OwnerClosed(object? sender, EventArgs e) => Close();

    /// <summary>Lays the overlay exactly over the owner's client area.</summary>
    private void FitOwner()
    {
        if (_owner.Content is not FrameworkElement root || PresentationSource.FromVisual(root) is not { CompositionTarget: { } ct }) return;
        var topLeft = ct.TransformFromDevice.Transform(root.PointToScreen(new Point(0, 0)));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = Math.Max(1, root.ActualWidth);
        Height = Math.Max(1, root.ActualHeight);
        _canvas.Width = Width;
        _canvas.Height = Height;
    }

    /// <summary>Dim everything but the target, ring it, and place the card beside it (or centred for a welcome step).</summary>
    private void Layout()
    {
        var w = _canvas.Width;
        var h = _canvas.Height;
        if (double.IsNaN(w) || double.IsNaN(h)) return;
        var full = new RectangleGeometry(new Rect(0, 0, w, h));
        Rect? hole = null;
        if (_target is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } && _owner.Content is Visual root && _target.IsDescendantOf(root))
        {
            var r = _target.TransformToAncestor(root).TransformBounds(new Rect(0, 0, _target.ActualWidth, _target.ActualHeight));
            r.Inflate(Pad, Pad);
            r.Intersect(new Rect(0, 0, w, h));
            if (!r.IsEmpty) hole = r;
        }
        if (hole is { } hr)
        {
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hr, 5, 5));
            _ring.Width = hr.Width;
            _ring.Height = hr.Height;
            Canvas.SetLeft(_ring, hr.X);
            Canvas.SetTop(_ring, hr.Y);
            _ring.Visibility = Visibility.Visible;
        }
        else
        {
            _dim.Data = full;
            _ring.Visibility = Visibility.Collapsed;
        }

        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        var cw = CardWidth;
        var ch = _card.DesiredSize.Height;
        var (x, y) = hole is { } t ? Place(t, cw, ch, w, h) : ((w - cw) / 2, (h - ch) / 2);
        Canvas.SetLeft(_card, Math.Clamp(x, 8, Math.Max(8, w - cw - 8)));
        Canvas.SetTop(_card, Math.Clamp(y, 8, Math.Max(8, h - ch - 8)));
    }

    /// <summary>Below a thin bar (menu, toolbar), else right of the panel, left, below, above; a panel that fills the
    /// window gets the card inside it.</summary>
    private static (double X, double Y) Place(Rect t, double cw, double ch, double w, double h)
    {
        double ClampY(double y) => Math.Clamp(y, 8, Math.Max(8, h - ch - 8));
        double ClampX(double x) => Math.Clamp(x, 8, Math.Max(8, w - cw - 8));
        if (t.Height < 70 && t.Bottom + Gap + ch <= h - 8) return (ClampX(t.Left), t.Bottom + Gap);
        if (t.Right + Gap + cw <= w - 8) return (t.Right + Gap, ClampY(t.Top));
        if (t.Left - Gap - cw >= 8) return (t.Left - Gap - cw, ClampY(t.Top));
        if (t.Bottom + Gap + ch <= h - 8) return (ClampX(t.Left), t.Bottom + Gap);
        if (t.Top - Gap - ch >= 8) return (ClampX(t.Left), t.Top - Gap - ch);
        return (ClampX(t.Right - cw - 24), ClampY(t.Bottom - ch - 24));
    }

    private void End(bool markSeen)
    {
        if (_ending) return;
        _ending = true;
        _owner.LocationChanged -= OwnerMoved;
        _owner.SizeChanged -= OwnerMoved;
        _owner.StateChanged -= OwnerState;
        _owner.Closed -= OwnerClosed;
        foreach (var ex in _expanded) ex.IsExpanded = false;
        foreach (var (tabs, was) in Enumerable.Reverse(_tabs))
            if (was is not null) tabs.SelectedItem = was;
        if (markSeen) Walkthrough.MarkSeen(_tour.Key);
        if (_owner.IsVisible) _owner.Activate();
    }
}
