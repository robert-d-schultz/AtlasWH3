using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AtlasWH3.Core;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.Terrain;
using AtlasWH3.Core.Editing;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Scene;

/// <summary>
/// The scene editor's terrain tools: height brushes on the .terry's Height (land) and HeightSea (the sea tiles' ground)
/// maps, and the tree editor on the CampaignTree map, through a <see cref="KitTerrainEditSession"/>. Each map is a stack
/// of Terry layers: the tools paint what the scene shows (the composite) and write the change into the layer chosen under
/// Layer. The session edits the scene's own rasters, so the 2D view redraws live; at the end of each stroke the touched
/// hexes' trees are regenerated (BOB's placement) and the 3D view re-meshes. Undo/redo per stroke; Save writes the layer
/// TIFs in their own format, backed up through the terrain_edits journal. While a target is chosen the panel is the 2D
/// view's <see cref="SceneView.Tool"/>.
/// </summary>
public sealed class TerrainToolsPanel : ScrollViewer, SceneView.ITool
{
    private enum TreeMode { Paint, Erase, FillConnected, FillRegion }

    private static readonly string[] HeightModeNames = ["Raise", "Lower", "Smooth", "Flatten", "Set to value", "Noise"];

    private readonly ProjectPaths _paths;
    private readonly SceneView _view;
    private readonly Viewport3D.Viewport3DControl _view3d;
    private SceneModel? _model;
    private KitTerrainEditSession? _session;

    private readonly TextBlock _sources = Dim("");
    private readonly ComboBox _target = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly ComboBox _layer = new() { Margin = new Thickness(0, 2, 0, 2), ToolTip = "The Terry layer the strokes are written to (the map's visible layers, bottom first)" };
    private readonly StackPanel _layerBox = new();
    private readonly StackPanel _heightBox = new();
    private readonly StackPanel _treeBox = new();
    private readonly ComboBox _heightMode = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly ComboBox _treeMode = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly Slider _radius = new() { Minimum = -2.3, Maximum = 4.6, Value = Math.Log(2), Margin = new Thickness(0, 2, 0, 2) };  // log(world units)
    private readonly Slider _strength = new() { Minimum = 0.01, Maximum = 1, Value = 0.4, Margin = new Thickness(0, 2, 0, 2) };
    private readonly Slider _softness = new() { Minimum = 0, Maximum = 1, Value = 0.7, Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBlock _radiusText = Dim(""), _strengthText = Dim(""), _softnessText = Dim("");
    private readonly TextBox _value = new() { Width = 70, Text = "0" };
    private readonly TextBlock _valueWorld = Dim("");
    private readonly ComboBox _restrict = new() { Margin = new Thickness(0, 2, 0, 2), ToolTip = "Which hexes the paint brush and fills may change" };
    private readonly CheckBox _treeOverlay = Check("Show tree map", "Draw the CampaignTree map over the 2D view (each hex's colour)");
    private readonly CheckBox _seaOverlay = Check("Show sea floor (sea target)", "Tint where the HeightSea map is below sea level, by depth");
    private readonly TextBox _fillLimit = new() { Width = 50, Text = "0", ToolTip = "Fills stop this far (world units) from the clicked hex; 0 = no limit" };
    private readonly ListBox _species = new() { Height = 260, Background = Brushes.Transparent, Foreground = Theme.Brush("Text") };
    private readonly TextBlock _readout = new() { FontFamily = new FontFamily("Consolas"), Foreground = Theme.Brush("DimText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
    private readonly Button _undo = Btn("Undo"), _redo = Btn("Redo"), _save = Btn("Save"), _build = Btn("Build…");
    private byte?[] _speciesIndex = [];
    private uint[] _paletteBgra = new uint[256];

    /// <summary>Status-bar text (message, error).</summary>
    public event Action<string, bool>? Status;

    /// <summary>The panel is the visible tab: Ctrl+Z / Ctrl+Y go to terrain edits.</summary>
    public bool IsShown { get; set; }

    public bool HasUnsaved => _session?.IsDirty() == true;

    /// <summary>Raised after every edit, undo, redo, save or project change (the window title shows a * while unsaved).</summary>
    public event Action? DirtyChanged;

    private KitTarget? Target => _target.SelectedIndex switch { 1 => KitTarget.Land, 2 => KitTarget.Sea, 3 => KitTarget.Trees, _ => null };

    public TerrainToolsPanel(ProjectPaths paths, SceneView view, Viewport3D.Viewport3DControl view3d)
    {
        _paths = paths;
        _view = view;
        _view3d = view3d;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = Build();
        _view.HoverChanged += p => { if (p is var (x, z)) ShowReadout(x, z); };
        Detach("Open a campaign project to edit its terrain.");
    }

    // ---------------------------------------------------------------- layout

    private static TextBlock Dim(string text) => new() { Text = text, Foreground = Theme.Brush("DimText"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text) => new() { Text = text, Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 6, 0, 0) };
    private static CheckBox Check(string text, string tip) => new() { Content = text, ToolTip = tip, Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 4, 0, 0) };
    private static Button Btn(string text) => new() { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) };

    private UIElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(8) };
        root.Children.Add(Theme.Header("Terrain & trees", 0));
        root.Children.Add(Dim("Paints the kit sources (the .terry's Height, HeightSea and CampaignTree maps): you paint what the view shows, "
                              + "and the change goes into the chosen layer. Left-drag paints in the 2D view; Alt+click picks the value or species "
                              + "under the cursor; [ and ] resize the brush; right/middle drag still pans."));
        root.Children.Add(_sources);

        root.Children.Add(Label("Tool"));
        foreach (var t in new[] { "Off (select / move entities)", "Land height (Height)", "Sea floor height (HeightSea)", "Trees (CampaignTree)" }) _target.Items.Add(t);
        _target.SelectedIndex = 0;
        _target.SelectionChanged += (_, _) => TargetChanged();
        root.Children.Add(_target);
        _layerBox.Children.Add(Label("Layer"));
        _layerBox.Children.Add(_layer);
        _layer.SelectionChanged += (_, _) => LayerChanged();
        root.Children.Add(_layerBox);

        root.Children.Add(Label("Brush"));
        root.Children.Add(Row(_radius, _radiusText));
        _radius.ValueChanged += (_, _) => BrushChanged();

        // height
        foreach (var m in HeightModeNames) _heightMode.Items.Add(m);
        _heightMode.SelectedIndex = 0;
        _heightMode.SelectionChanged += (_, _) => BrushChanged();
        _heightBox.Children.Add(_heightMode);
        _heightBox.Children.Add(Row(_strength, _strengthText));
        _heightBox.Children.Add(Row(_softness, _softnessText));
        _strength.ValueChanged += (_, _) => BrushChanged();
        _softness.ValueChanged += (_, _) => BrushChanged();
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        valueRow.Children.Add(new TextBlock { Text = "Value (world y) ", Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center });
        valueRow.Children.Add(_value);
        var seaLevel = Btn("Sea level");
        seaLevel.Margin = new Thickness(6, 0, 0, 0);
        seaLevel.ToolTip = $"Set to value {CampaignTerrain.SeaLevel}: the height of the sea's water planes";
        seaLevel.Click += (_, _) =>
        {
            _value.Text = CampaignTerrain.SeaLevel.ToString(CultureInfo.InvariantCulture);
            _heightMode.SelectedIndex = (int)KitHeightMode.SetValue;
        };
        valueRow.Children.Add(seaLevel);
        _heightBox.Children.Add(valueRow);
        _heightBox.Children.Add(_valueWorld);
        _value.TextChanged += (_, _) => BrushChanged();
        _seaOverlay.IsChecked = true;
        _seaOverlay.Click += (_, _) => _view.Invalidate();
        _heightBox.Children.Add(_seaOverlay);
        root.Children.Add(_heightBox);

        // trees
        foreach (var m in new[] { "Paint species", "Erase (clear the layer)", "Fill connected (same species)", "Fill region (map.hex)" }) _treeMode.Items.Add(m);
        _treeMode.SelectedIndex = 0;
        _treeMode.SelectionChanged += (_, _) => { BrushChanged(); _view.InvalidateVisual(); };
        _treeBox.Children.Add(_treeMode);
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        limitRow.Children.Add(new TextBlock { Text = "Fill limit (world units, 0 = none) ", Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center });
        limitRow.Children.Add(_fillLimit);
        _treeBox.Children.Add(limitRow);
        foreach (var r in new[] { "Change any hex", "Only empty hexes (add trees)", "Only hexes with trees (re-species)" }) _restrict.Items.Add(r);
        _restrict.SelectedIndex = 0;
        _restrict.SelectionChanged += (_, _) => BrushChanged();
        _treeBox.Children.Add(_restrict);
        _treeOverlay.IsChecked = true;
        _treeOverlay.Click += (_, _) => _view.Invalidate();
        _treeBox.Children.Add(_treeOverlay);
        _treeBox.Children.Add(Label("Species (palette colour · tree ids · hexes)"));
        _species.SelectionChanged += (_, _) => BrushChanged();
        _treeBox.Children.Add(_species);
        root.Children.Add(_treeBox);

        root.Children.Add(_readout);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        _undo.ToolTip = "Undo the last terrain/tree stroke (Ctrl+Z while this tab is shown)";
        _redo.ToolTip = "Redo (Ctrl+Y)";
        _save.ToolTip = "Write the changed layer TIFs in their own format; the originals are backed up first in output\\terrain_edits";
        _build.ToolTip = "Open the Build window: re-run heightmaps, tile_list, camera_heightmap (heights) and trees so the game sees the edits";
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        _save.Click += (_, _) => Save();
        _build.Click += (_, _) => BuildWindow.Show(Window.GetWindow(this), _paths with { MapName = _model?.Project.MapName ?? _paths.MapName });
        foreach (var b in new[] { _undo, _redo, _save, _build }) buttons.Children.Add(b);
        root.Children.Add(buttons);
        root.Children.Add(_state);
        TargetChanged();
        return root;
    }

    private static DockPanel Row(Slider slider, TextBlock text)
    {
        var row = new DockPanel();
        text.Width = 120;
        text.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(text, Dock.Right);
        row.Children.Add(text);
        row.Children.Add(slider);
        return row;
    }

    // ---------------------------------------------------------------- session

    /// <summary>Opens a session on the model's kit sources (kept when the same model is re-attached after F5).</summary>
    public void Attach(SceneModel model)
    {
        if (ReferenceEquals(model, _model) && _session is not null) { UpdateState(); return; }
        _model = model;
        _session = null;
        if (model.Terrain is not { } terrain)
        {
            Detach("No campaign terrain in this project" + (model.TerrainNote is { } note ? $" ({note})." : "."));
            return;
        }
        var project = model.Project;
        if (KitTerrainEditSession.MapLayers.Of(project, "Height") is not { } landLayers)
        {
            Detach("The Height map has no visible layer to paint.");
            return;
        }
        try
        {
            // output\terrain_edits\<map>; a project outside the kit's own campaign folder (a copy) gets its own journal.
            var dir = Path.GetDirectoryName(project.Path)!;
            var mapPaths = _paths with { MapName = project.MapName };
            var journalDir = FileJournal.EditDir(mapPaths, "terrain_edits");
            if (!Path.GetFullPath(dir).TrimEnd('\\').Equals(Path.GetFullPath(mapPaths.AkTerrainDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                journalDir = Path.Combine(journalDir, "custom_" + FileJournal.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToLowerInvariant()))[..8].ToLowerInvariant());
            var journal = new FileJournal(journalDir);
            var seaLayers = terrain.SeaFloor is null ? null : KitTerrainEditSession.MapLayers.Of(project, "HeightSea");
            var treeLayers = model.TreeMap is null ? null : KitTerrainEditSession.MapLayers.Of(project, "CampaignTree");
            _session = new KitTerrainEditSession(journal, terrain.WorldW,
                landLayers, terrain.Land,
                seaLayers, seaLayers is null ? null : () => terrain.SeaFloor!,
                treeLayers, model.TreeMap?.Map, model.TreeMap?.Palette);
            _session.Changed += _ => _view.Invalidate();
            for (var i = 0; i < 256; i++)
            {
                var rgb = (uint)_session.PaletteRgb(i);
                _paletteBgra[i] = 0xFF000000u | rgb;
            }
            _sources.Text = string.Join("\n", new[] { (KitTarget.Land, "Height"), (KitTarget.Sea, "HeightSea"), (KitTarget.Trees, "CampaignTree") }.Select(p =>
                _session.Has(p.Item1)
                    ? $"{p.Item2}: {string.Join(", ", _session.Layers(p.Item1).Select(l => l.Name))}"
                    : $"{p.Item2}: none" + (p.Item1 == KitTarget.Trees && model.TreesNote is { } note ? $" ({note})" : "")));
            IsEnabled = true;
            FillSpecies();
            TargetChanged();
            UpdateState();
        }
        catch (Exception ex)
        {
            Detach("Terrain tools unavailable: " + ex.Message);
        }
    }

    private void Detach(string why)
    {
        _session = null;
        _sources.Text = why;
        _target.SelectedIndex = 0;
        _species.Items.Clear();
        UpdateState();
    }

    /// <summary>Asks before dropping unsaved terrain edits (switching project, closing). True to go on.</summary>
    public bool ConfirmDiscard(Window owner)
    {
        if (!HasUnsaved) return true;
        var answer = Prompt.AskSave(owner, "Unsaved terrain edits",
            "You have unsaved Terrain & trees edits in " + DirtyList() + ".\n\nSave them to the kit before leaving? "
            + "Don't save drops them; the files stay as they were at the last save. (Entity edits are already saved.)");
        if (answer == Prompt.SaveChoice.Cancel) return false;
        if (answer == Prompt.SaveChoice.Save) return Save();
        return true;
    }

    /// <summary>Saves the unsaved terrain / tree edits (File > Save, Ctrl+S). False when the save failed or was declined.</summary>
    public bool SaveEdits() => !HasUnsaved || Save();

    private string DirtyList() => _session is null ? "" : string.Join(", ", _session.DirtyLayers().Select(l => Path.GetFileName(l.Path)));

    private void FillSpecies()
    {
        _species.Items.Clear();
        if (_session?.TreeMap is null) { _speciesIndex = []; return; }
        var counts = _session.TreeCounts();
        var groups = _model?.TreeDb?.ColourGroups();
        var list = new List<byte?>();
        for (var i = 0; i < 256; i++)
        {
            var rgb = (uint)_session.PaletteRgb(i);
            var known = groups?.ContainsKey(rgb) == true;
            if (i == KitTerrainEditSession.NoTreeIndex || known || counts[i] > 0) list.Add((byte)i);
        }
        _speciesIndex = [.. list];
        foreach (var i in list)
        {
            var index = i!.Value;
            var rgb = (uint)_session.PaletteRgb(index);
            string name;
            if (index == KitTerrainEditSession.NoTreeIndex) name = "no tree";
            else if (groups?.TryGetValue(rgb, out var ids) == true) name = ids.Length == 1 ? ids[0] : $"{ids[0]} +{ids.Length - 1}";
            else name = "(not in campaign_tree_ids)";
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                Width = 14, Height = 14, Margin = new Thickness(0, 1, 6, 1), BorderBrush = Theme.Brush("BorderBrush"), BorderThickness = new Thickness(1),
                Background = index == KitTerrainEditSession.NoTreeIndex ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)),
            });
            row.Children.Add(new TextBlock { Text = $"{index,3} {name}", FontFamily = new FontFamily("Consolas"), FontSize = 11 });
            row.Children.Add(new TextBlock { Text = $"  {counts[index]:N0}", Foreground = Theme.Brush("DimText"), FontSize = 11 });
            if (groups?.TryGetValue(rgb, out var all) == true) row.ToolTip = string.Join("\n", all);
            _species.Items.Add(row);
        }
        var first = list.FindIndex(i => i != KitTerrainEditSession.NoTreeIndex);
        _species.SelectedIndex = Math.Max(0, first);
    }

    private void SelectSpecies(byte index)
    {
        var at = Array.IndexOf(_speciesIndex, index);
        if (at >= 0) { _species.SelectedIndex = at; _species.ScrollIntoView(_species.SelectedItem); }
    }

    // ---------------------------------------------------------------- settings

    private void TargetChanged()
    {
        var target = Target;
        if (target is { } t && _session?.Has(t) != true)
        {
            if (_session is not null) Status?.Invoke($"This project has no {t} map to edit.", true);
            _target.SelectedIndex = 0;
            return;
        }
        if (target == KitTarget.Sea && _session!.SeaIfLoaded is null)
        {
            var cursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            try { _ = _session.Sea; }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Status?.Invoke("HeightSea not read: " + ex.Message, true);
                _target.SelectedIndex = 0;
                return;
            }
            finally { Mouse.OverrideCursor = cursor; }
        }
        FillLayers(target);
        _heightBox.Visibility = target is KitTarget.Land or KitTarget.Sea ? Visibility.Visible : Visibility.Collapsed;
        _treeBox.Visibility = target == KitTarget.Trees ? Visibility.Visible : Visibility.Collapsed;
        _seaOverlay.Visibility = target == KitTarget.Sea ? Visibility.Visible : Visibility.Collapsed;
        _view.Tool = target is null ? null : this;
        BrushChanged();
        _view.Invalidate();
    }

    private bool _fillingLayers;

    /// <summary>The layer list of the chosen target, its edited layer selected.</summary>
    private void FillLayers(KitTarget? target)
    {
        _fillingLayers = true;
        _layer.Items.Clear();
        if (_session is not null && target is { } t)
        {
            var current = _session.LayerOf(t);
            foreach (var l in _session.Layers(t))
            {
                _layer.Items.Add(l.Opacity < 1 ? $"{l.Name} (opacity {l.Opacity:0.##})" : l.Name);
                if (l.Id == current?.Id) _layer.SelectedIndex = _layer.Items.Count - 1;
            }
        }
        _layerBox.Visibility = _layer.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _fillingLayers = false;
    }

    private void LayerChanged()
    {
        if (_fillingLayers || _session is null || Target is not { } t || _layer.SelectedIndex < 0) return;
        var layer = _session.Layers(t)[_layer.SelectedIndex];
        try { _session.SetLayer(t, layer.Id); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { Status?.Invoke(ex.Message, true); }
        Status?.Invoke($"{t} strokes now go into layer '{layer.Name}' ({Path.GetFileName(layer.Path)}).", false);
    }

    private double RadiusWorld => Math.Exp(_radius.Value);

    private void BrushChanged()
    {
        _radiusText.Text = $"radius {RadiusWorld:0.##}";
        _strengthText.Text = $"strength {_strength.Value:0.00}";
        _softnessText.Text = $"softness {_softness.Value:0.00}";
        var ok = float.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value);
        _valueWorld.Text = ok ? $"sea level is {CampaignTerrain.SeaLevel}" : "not a number";
        if (_session is null) return;
        _session.RadiusWorld = RadiusWorld;
        var b = _session.HeightBrush;
        b.Mode = (KitHeightMode)Math.Max(0, _heightMode.SelectedIndex);
        b.Strength = _strength.Value;
        b.Softness = _softness.Value;
        if (ok) b.Value = value;
        if (_species.SelectedIndex >= 0 && _species.SelectedIndex < _speciesIndex.Length && _speciesIndex[_species.SelectedIndex] is { } index)
            _session.TreeIndex = index;
        _session.TreeFilter = _restrict.SelectedIndex switch
        {
            1 => i => i == KitTerrainEditSession.NoTreeIndex,
            2 => i => i != KitTerrainEditSession.NoTreeIndex,
            _ => (Func<byte, bool>?)null,
        };
        _view.InvalidateVisual();
    }

    // self-test access
    internal KitTerrainEditSession? Session => _session;
    internal int TargetIndex { get => _target.SelectedIndex; set => _target.SelectedIndex = value; }
    internal int HeightModeIndex { set => _heightMode.SelectedIndex = value; }
    internal int TreeModeIndex { set => _treeMode.SelectedIndex = value; }
    internal double Radius { set => _radius.Value = Math.Log(value); }
    internal double Strength { set => _strength.Value = value; }
    internal double Softness { set => _softness.Value = value; }
    internal string ValueText { set => _value.Text = value; }
    internal void SelectTreeIndex(byte index) => SelectSpecies(index);
    internal bool SaveNow() => Save();
    internal string StateText => _state.Text;

    /// <summary>[ / ] resize the brush.</summary>
    public void ScaleRadius(double factor) => _radius.Value = Math.Clamp(_radius.Value + Math.Log(factor), _radius.Minimum, _radius.Maximum);

    // ---------------------------------------------------------------- tool (2D view)

    public double CursorRadius => _session is null || Target is null ? 0
        : Target == KitTarget.Trees && (TreeMode)_treeMode.SelectedIndex is TreeMode.FillConnected or TreeMode.FillRegion ? 0 : RadiusWorld;

    private (KitTarget Target, bool Painting)? _stroke;

    public void Begin(double x, double z)
    {
        if (_session is null || Target is not { } target) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Pick(target, x, z);
            return;
        }
        try
        {
            if (target == KitTarget.Trees && (TreeMode)_treeMode.SelectedIndex is TreeMode.FillConnected or TreeMode.FillRegion)
            {
                var region = (TreeMode)_treeMode.SelectedIndex == TreeMode.FillRegion;
                Func<double, double, string?>? regionAt = null;
                if (region)
                {
                    if (_model?.Regions is null) { Status?.Invoke("No region map (map.hex) for a region fill: " + _model?.RegionsNote, true); return; }
                    regionAt = _model.RegionAt;
                }
                double.TryParse(_fillLimit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit);
                var edit = _session.FillTrees(x, z, region ? TreeFillScope.Region : TreeFillScope.Connected, erase: false, maxRadius: Math.Max(0, limit), regionAt: regionAt);
                if (edit is null) Status?.Invoke("Fill: nothing to change.", false);
                AfterEdit(edit);
                return;
            }
            _session.BeginStroke(target, x, z, erase: target == KitTarget.Trees && (TreeMode)_treeMode.SelectedIndex == TreeMode.Erase);
            _stroke = (target, true);
        }
        catch (Exception ex) { Status?.Invoke(ex.Message, true); }
    }

    public void Drag(double x, double z)
    {
        if (_stroke is null || _session is null) return;
        _session.StrokeTo(x, z);
    }

    public void End()
    {
        if (_stroke is null || _session is null) return;
        _stroke = null;
        AfterEdit(_session.EndStroke());
    }

    private void Pick(KitTarget target, double x, double z)
    {
        if (_session is null) return;
        if (target == KitTarget.Trees)
        {
            var (c, r) = _session.NearestHex(x, z);
            var index = _session.HexIndex(c, r);
            SelectSpecies(index);
            Status?.Invoke($"Picked species {index} at hex ({c}, {r}).", false);
        }
        else if (_session.ValueAt(target, x, z) is { } value)
        {
            _value.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            Status?.Invoke($"Picked {target} height {value:0.###}.", false);
        }
    }

    /// <summary>After a stroke, fill, undo or redo: trees of the touched hexes follow, 3D re-meshes, counts refresh.</summary>
    private void AfterEdit(KitTerrainEditSession.Edit? edit)
    {
        if (edit is not null && _model is not null)
        {
            if (edit.Hexes.Length > 0) _model.RegenerateTrees(edit.Hexes);
            if (edit.Target is KitTarget.Land or KitTarget.Sea) _view3d.ReloadTerrain();
            _view3d.ReloadTrees();
            if (edit.Target == KitTarget.Trees) RefreshCounts();
        }
        _view.Invalidate();
        UpdateState();
    }

    private void RefreshCounts()
    {
        var keep = _species.SelectedIndex;
        FillSpecies();
        if (keep >= 0 && keep < _species.Items.Count) _species.SelectedIndex = keep;
    }

    public void DrawOverlay(uint[] buffer, int w, int h, double ox, double oz, double scale)
    {
        if (_session is null) return;
        if (Target == KitTarget.Trees && _treeOverlay.IsChecked == true && _session.TreeMap is { } map && _session.Grid is { } g)
        {
            var palette = _paletteBgra;
            double kx = 2 / g.ColumnStep, kz = 2 / g.RowStep;
            Parallel.For(0, h, y =>
            {
                var wz = oz - (y + 0.5) * scale;
                var py = (int)Math.Floor(map.Height - 1 - wz * kz + 0.5);
                if ((uint)py >= (uint)map.Height) return;
                var line = y * w;
                for (var x = 0; x < w; x++)
                {
                    var px = (int)Math.Floor((ox + (x + 0.5) * scale) * kx + 1);
                    if ((uint)px >= (uint)map.Width) continue;
                    var index = map[px, py];
                    if (index == KitTerrainEditSession.NoTreeIndex) continue;
                    buffer[line + x] = Mix(buffer[line + x], palette[index], 0.6);
                }
            });
        }
        else if (Target == KitTarget.Sea && _seaOverlay.IsChecked == true && _session.SeaIfLoaded is { } sea)
        {
            var frame = _session.Frame(KitTarget.Sea);
            Parallel.For(0, h, y =>
            {
                var wz = oz - (y + 0.5) * scale;
                var sy = (int)Math.Round((1 - wz / frame.WorldH) * sea.Height - 0.5);
                if ((uint)sy >= (uint)sea.Height) return;
                var line = y * w;
                for (var x = 0; x < w; x++)
                {
                    var wx = ox + (x + 0.5) * scale;
                    var sx = (int)Math.Round(wx / frame.WorldW * sea.Width - 0.5);
                    if ((uint)sx >= (uint)sea.Width) continue;
                    var depth = CampaignTerrain.SeaLevel - sea[sx, sy];
                    if (depth <= 0) continue;
                    // light near the coast, dark in deep water
                    var t = Math.Clamp(depth / 20.0, 0, 1);
                    buffer[line + x] = Mix(buffer[line + x], Rgb((byte)(60 - 40 * t), (byte)(170 - 110 * t), (byte)(230 - 80 * t)), 0.55);
                }
            });
        }
    }

    private static uint Rgb(byte r, byte g, byte b) => 0xFF000000u | (uint)r << 16 | (uint)g << 8 | b;

    private static uint Mix(uint a, uint b, double t)
    {
        uint Ch(int shift) => (uint)Math.Clamp(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t, 0, 255) << shift;
        return 0xFF000000u | Ch(16) | Ch(8) | Ch(0);
    }

    private void ShowReadout(double x, double z)
    {
        if (_session is null || !IsVisible) return;
        var parts = new List<string>();
        foreach (var t in new[] { KitTarget.Land, KitTarget.Sea })
            if (_session.ValueAt(t, x, z) is { } value)
                parts.Add($"{(t == KitTarget.Land ? "land" : "sea floor"),-9} y {value,8:0.000}");
        if (_session.Grid is { } g && x >= 0 && z >= 0 && x <= _session.WorldW && z <= _session.WorldH)
        {
            var (c, r) = _session.NearestHex(x, z);
            parts.Add($"hex  ({c}, {r})  species {_session.HexIndex(c, r)}");
        }
        _readout.Text = string.Join("\n", parts);
    }

    // ---------------------------------------------------------------- undo / save

    public bool Undo()
    {
        if (_session?.CanUndo != true) return false;
        AfterEdit(_session.Undo());
        Status?.Invoke("Undid terrain edit.", false);
        return true;
    }

    public bool Redo()
    {
        if (_session?.CanRedo != true) return false;
        AfterEdit(_session.Redo());
        Status?.Invoke("Redid terrain edit.", false);
        return true;
    }

    private bool Save()
    {
        if (_session is null) return true;
        var owner = Window.GetWindow(this);
        var label = "scene editor: " + DirtyList();
        try
        {
            (IReadOnlyList<string> Files, int Seq) result;
            try { result = _session.Save(label); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("changed on disk", StringComparison.Ordinal))
            {
                if (MessageBox.Show(owner, ex.Message + "\n\nOverwrite them anyway? (the current files are backed up first)", "Files changed on disk",
                                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
                result = _session.Save(label, force: true);
            }
            if (result.Files.Count == 0) { Status?.Invoke("Nothing to save.", false); return true; }
            Status?.Invoke($"Saved {string.Join(", ", result.Files.Select(Path.GetFileName))} (backup: {Path.Combine(_session.Journal.Dir, result.Seq.ToString("D5"))}).", false);
            UpdateState();
            return true;
        }
        catch (Exception ex)
        {
            Status?.Invoke("Save failed: " + ex.Message, true);
            return false;
        }
    }

    private void UpdateState()
    {
        DirtyChanged?.Invoke();
        _undo.IsEnabled = _session?.CanUndo == true;
        _redo.IsEnabled = _session?.CanRedo == true;
        _undo.ToolTip = _session?.UndoName is { } u ? $"Undo {u} (Ctrl+Z while this tab is shown)" : "Nothing to undo";
        _redo.ToolTip = _session?.RedoName is { } r ? $"Redo {r} (Ctrl+Y)" : "Nothing to redo";
        _save.IsEnabled = HasUnsaved;
        _build.IsEnabled = _session is not null;
        if (_session is null) { _state.Text = ""; return; }
        var lines = new List<string>();
        if (HasUnsaved) lines.Add("Unsaved: " + DirtyList());
        lines.AddRange(StaleNotes());
        _state.Text = lines.Count == 0 ? "Sources saved; build output is up to date." : string.Join("\n", lines);
        _state.Foreground = HasUnsaved ? Brushes.Orange : Theme.Brush("DimText");
    }

    /// <summary>Build outputs (default compiled folder) older than the sources they come from.</summary>
    private IEnumerable<string> StaleNotes()
    {
        if (_session is null || _model is null) yield break;
        var ctx = new CampaignBuildContext(_paths with { MapName = _model.Project.MapName });
        DateTime Newest(params KitTarget[] targets) => targets.SelectMany(_session.Layers).Select(l => l.Path).Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        var heights = ctx.OutFile("full_logic_map.compressed_map");
        var trees = Path.Combine(ctx.TargetRoot, AtlasWH3.Core.Exporters.TreeExporter.PackPath(ctx.MapName));
        var stale = new List<string>();
        if (!File.Exists(heights) || File.GetLastWriteTimeUtc(heights) < Newest(KitTarget.Land, KitTarget.Sea))
            stale.Add("heightmaps, tile_list, camera_heightmap");
        if (!File.Exists(trees) || File.GetLastWriteTimeUtc(trees) < Newest(KitTarget.Land, KitTarget.Trees))
            stale.Add("trees");
        if (stale.Count > 0)
            yield return $"Build output in {ctx.TargetRoot} is older than the saved sources: re-run {string.Join(", ", stale)} (Build…) before testing in game.";
    }
}
