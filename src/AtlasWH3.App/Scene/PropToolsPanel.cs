using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtlasWH3.Core.Editing;

namespace AtlasWH3.App.Scene;

/// <summary>
/// The "Props" tab of the scene editor: an asset browser (models from the game packs and linked mod packs) with
/// placement settings, and the clamp-to-ground tools. UI only: <see cref="SceneWindow"/> does the placing and
/// clamping through the entity editor (SceneWindow.Props.cs).
/// </summary>
public sealed class PropToolsPanel : ScrollViewer
{
    public enum GroundSource { Scene, Built, KitLf }

    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly CheckBox _allModels = new() { Content = "All models (not only campaign)", Margin = new Thickness(0, 0, 0, 4) };
    private readonly ListBox _list = new() { Height = 220 };
    private readonly TextBlock _count = new() { Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 2, 0, 4) };
    private readonly Image _thumb = new() { Width = 160, Height = 160, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBox _yaw = Num("0"), _scale = Num("1"), _scaleJitter = Num("0"), _yOffset = Num("0");
    private readonly CheckBox _randomYaw = new() { Content = "Random yaw", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _seatBase = new() { Content = "Seat model base on the ground (else its origin)", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _repeat = new() { Content = "Repeat: keep placing until Esc", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _place = new() { Content = "Place (click in the view)", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };

    private readonly ComboBox _ground = new() { Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBlock _groundNote = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox _mode = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBox _clampOffset = Num("0"), _threshold = Num("0.3");
    private readonly CheckBox _onlyDown = new() { Content = "Only lower floating props (leave buried ones)", Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _skipSettlements = new() { Content = "Skip settlement pieces", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBlock _sinkNote = new() { TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"), Margin = new Thickness(0, 2, 0, 2) };

    private List<string> _models = [];
    private bool _placing;

    /// <summary>The selected model changed (null: none).</summary>
    public event Action<string?>? ModelSelected;
    /// <summary>Placement mode on / off (the Place button, or <see cref="StopPlacing"/>).</summary>
    public event Action<bool>? PlacingChanged;
    /// <summary>"selected", "layer" or "view".</summary>
    public event Action<string>? ClampRequested;
    public event Action? FindFloatingRequested;
    public event Action? LoadSinkRequested;
    public event Action? GroundSourceChanged;

    public PropToolsPanel()
    {
        Background = Theme.Brush("Panel");
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var root = new StackPanel { Margin = new Thickness(8) };
        Content = root;

        root.Children.Add(Theme.Header("Add props", 0).Card("scene.props.add"));
        root.Children.Add(_search.Card("scene.props.search"));
        root.Children.Add(_allModels.Card("scene.props.all"));
        root.Children.Add(_list);
        root.Children.Add(_count);
        root.Children.Add(_thumb);
        root.Children.Add(_info);
        root.Children.Add(Row("Yaw °", _yaw, _randomYaw).Card("scene.props.yaw"));
        root.Children.Add(Row("Scale", _scale, Label("± %"), _scaleJitter).Card("scene.props.scale"));
        root.Children.Add(Row("Y offset", _yOffset).Card("scene.props.yoffset"));
        root.Children.Add(_seatBase.Card("scene.props.seatbase"));
        root.Children.Add(_repeat.Card("scene.props.repeat"));
        root.Children.Add(_place.Card("scene.props.place"));

        root.Children.Add(Theme.Header("Clamp to ground", 16).Card("scene.clamp"));
        _ground.Items.Add("Scene height: terrain + prop height patches (as BOB's trees)");
        _ground.Items.Add("Built terrain only: full_logic_map");
        _ground.Items.Add("Kit lf height map (includes unbuilt edits)");
        _ground.SelectedIndex = 0;
        root.Children.Add(_ground.Card("scene.clamp.ground"));
        root.Children.Add(_groundNote);
        _mode.Items.Add("Origin on the ground (as Terry)");
        _mode.Items.Add("Model base on the ground");
        _mode.Items.Add("Vanilla sink for mountains / rocks");
        _mode.SelectedIndex = 0;
        root.Children.Add(_mode.Card("scene.clamp.mode"));
        root.Children.Add(Row("Offset", _clampOffset).Card("scene.clamp.offset"));
        root.Children.Add(_onlyDown.Card("scene.clamp.onlydown"));
        var buttons = new WrapPanel();
        buttons.Children.Add(Button("Clamp selected", () => ClampRequested?.Invoke("selected")).Card("scene.clamp.selected"));
        buttons.Children.Add(Button("Clamp active layer", () => ClampRequested?.Invoke("layer")).Card("scene.clamp.layer"));
        buttons.Children.Add(Button("Clamp all in view", () => ClampRequested?.Invoke("view")).Card("scene.clamp.view"));
        root.Children.Add(buttons);
        root.Children.Add(Row("Float >", _threshold, Button("Find floating props", () => FindFloatingRequested?.Invoke())).Card("scene.clamp.find"));
        root.Children.Add(_skipSettlements.Card("scene.clamp.skipsettlements"));
        root.Children.Add(_sinkNote);
        root.Children.Add(Button("Load sink table…", () => LoadSinkRequested?.Invoke()).Card("scene.clamp.sinktable"));

        _search.TextChanged += (_, _) => Filter();
        _allModels.Checked += (_, _) => Filter();
        _allModels.Unchecked += (_, _) => Filter();
        _list.SelectionChanged += (_, _) => ModelSelected?.Invoke(SelectedModel);
        _list.MouseDoubleClick += (_, _) => { if (SelectedModel is not null && !_placing) SetPlacing(true); };
        _place.Click += (_, _) => SetPlacing(!_placing);
        _ground.SelectionChanged += (_, _) => GroundSourceChanged?.Invoke();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; _list.Focus(); } };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _count.Text = "Loading the model list…";
    }

    // ---------------------------------------------------------------- settings

    public string? SelectedModel => _list.SelectedItem as string;
    public bool Placing => _placing;
    public bool RandomYaw => _randomYaw.IsChecked == true;
    public double Yaw => Parse(_yaw, 0);
    public double Scale => Parse(_scale, 1) is var s && s > 0 ? s : 1;
    public double ScaleJitterPercent => Math.Clamp(Parse(_scaleJitter, 0), 0, 95);
    public double YOffset => Parse(_yOffset, 0);
    public bool SeatBase => _seatBase.IsChecked == true;
    public bool Repeat => _repeat.IsChecked == true;
    public GroundSource Ground => _ground.SelectedIndex switch { 1 => GroundSource.Built, 2 => GroundSource.KitLf, _ => GroundSource.Scene };
    public bool SkipSettlements => _skipSettlements.IsChecked == true;
    public ClampMode Mode => _mode.SelectedIndex switch { 1 => ClampMode.Base, 2 => ClampMode.VanillaSink, _ => ClampMode.Origin };
    public double ClampOffset => Parse(_clampOffset, 0);
    public bool OnlyDown => _onlyDown.IsChecked == true;
    public double FloatThreshold => Math.Max(0, Parse(_threshold, 0.3));

    public int ModelCount => _models.Count;
    public IReadOnlyList<string> Models => _models;
    public bool HasPreview => _thumb.Source is not null;
    public string PreviewInfo => _info.Text;
    public void TogglePlacing() => SetPlacing(!_placing);

    public void SetModels(List<string> models)
    {
        _models = models;
        Filter();
    }

    public void SetPreview(ImageSource? image, string info)
    {
        _thumb.Source = image;
        _info.Text = info;
    }

    public void SetGroundNote(string text) => _groundNote.Text = text;
    public void SetSinkNote(string text) => _sinkNote.Text = text;

    /// <summary>Selects a model by path (e.g. "pick model from the selected prop").</summary>
    public void Select(string model)
    {
        _search.Text = "";
        var hit = _models.FirstOrDefault(m => m.Equals(model.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (hit is null) return;
        if (!_list.Items.Contains(hit)) { _allModels.IsChecked = true; Filter(); }
        _list.SelectedItem = hit;
        _list.ScrollIntoView(hit);
    }

    public void StopPlacing()
    {
        if (_placing) SetPlacing(false);
    }

    private void SetPlacing(bool on)
    {
        if (on && SelectedModel is null) { _count.Text = "Pick a model first."; return; }
        _placing = on;
        _place.Content = on ? "Stop placing (Esc)" : "Place (click in the view)";
        _place.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        PlacingChanged?.Invoke(on);
    }

    private void Filter()
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var all = _allModels.IsChecked == true;
        var shown = _models.Where(m => (all || m.StartsWith("rigidmodels/campaign/", StringComparison.OrdinalIgnoreCase))
                                       && words.All(w => m.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var keep = SelectedModel;
        _list.ItemsSource = shown;
        if (keep is not null && shown.Contains(keep)) _list.SelectedItem = keep;
        _count.Text = $"{shown.Count:N0} of {_models.Count:N0} models";
    }

    // ---------------------------------------------------------------- helpers

    private static TextBox Num(string text) => new() { Text = text, Width = 60, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Label(string text) => new() { Text = text, Width = 26, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(string label, params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = label, Width = 64, VerticalAlignment = VerticalAlignment.Center });
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 6, 2) };
        b.Click += (_, _) => click();
        return b;
    }

    private static double Parse(TextBox box, double fallback) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;
}
