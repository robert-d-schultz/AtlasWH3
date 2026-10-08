using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AtlasWH3.Formats.Terry;

namespace AtlasWH3.App.Scene;

/// <summary>
/// Terry's property inspector, generated from the component schema: every component of the selection with an editor
/// per field type (bool → checkbox, enum → list of the values the kit uses, vectors → one box per component, colour →
/// RGBA with a swatch, paths/strings → an editable list of the values used in this project). With several entities
/// selected, only shared components are shown; differing values show blank, and a blank vector component keeps each
/// entity's own value. The panel only raises edits; the window applies them as entity ops.
/// </summary>
public sealed class InspectorPanel : ScrollViewer
{
    /// <summary>An edit of one field: compute the new value from an entity's current value.</summary>
    public sealed record FieldEdit(string Component, string Field, Func<string, string> NewValue);

    private readonly StackPanel _root = new() { Margin = new Thickness(8) };
    private SceneModel? _model;
    private IReadOnlyList<SceneModel.Item> _items = [];

    public event Action<FieldEdit>? FieldEdited;
    public event Action<string?>? NameEdited;
    public event Action<string>? ComponentAdded;
    public event Action<string>? ComponentRemoved;
    public event Action<string>? PrefabOpenRequested;
    public event Action? PrefabExpandRequested;

    private static readonly Brush Label = new SolidColorBrush(Color.FromRgb(170, 170, 170));
    private static readonly Brush Box = new SolidColorBrush(Color.FromRgb(45, 45, 48));
    private static readonly Brush Edge = new SolidColorBrush(Color.FromRgb(70, 70, 74));

    public InspectorPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _root;
    }

    public void Show(SceneModel model, IReadOnlyList<SceneModel.Item> items)
    {
        _model = model;
        _items = items;
        Rebuild();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        if (_model is null || _items.Count == 0)
        {
            _root.Children.Add(new TextBlock { Text = "Nothing selected.\n\nClick an entity in the view or the tree; drag a box to select many.", Foreground = Label, TextWrapping = TextWrapping.Wrap });
            return;
        }
        var first = _items[0].Entity;
        var types = _items.Select(i => i.Entity.Type).Distinct().ToList();
        _root.Children.Add(new TextBlock
        {
            Text = _items.Count == 1 ? first.Type : $"{_items.Count} entities ({string.Join(", ", types.Take(4))}{(types.Count > 4 ? ", …" : "")})",
            FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(150, 200, 255)),
        });
        if (_items.Count == 1)
        {
            _root.Children.Add(ReadOnlyRow("id", first.Id));
            _root.Children.Add(ReadOnlyRow("layer", _items[0].Layer.Name));
            if (first.Parents.Count > 0)
                _root.Children.Add(ReadOnlyRow("in", string.Join(", ", first.Parents.Select(p => _model.Find(p)?.Entity.Name ?? p))));
            var name = TextBoxFor(first.Name ?? "", false, v => NameEdited?.Invoke(v));
            _root.Children.Add(Row("name", name));
        }
        else
            _root.Children.Add(ReadOnlyRow("layers", string.Join(", ", _items.Select(i => i.Layer.Name).Distinct().Take(5))));

        if (_items.Count == 1 && SceneModel.ModelPathOf(first) is { } modelPath)
            _root.Children.Add(ModelThumbnail(modelPath));
        if (_items.All(i => i.Entity.Component("ECPrefab") is not null))
            _root.Children.Add(PrefabBlock());

        var shared = first.Components.Select(c => c.Name)
            .Where(n => _items.All(i => i.Entity.Component(n) is not null)).ToList();
        var def = types.Count == 1 ? _model.Config.Find(first.Type) : null;
        foreach (var comp in shared) _root.Children.Add(ComponentBlock(comp, def));

        if (types.Count == 1) _root.Children.Add(AddComponentRow(first, def, shared));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (System.Windows.Media.Imaging.BitmapSource? Image, string Caption)> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A rendered preview of the entity's model (made off the UI thread, cached per path).</summary>
    private UIElement ModelThumbnail(string path)
    {
        const int size = 200;
        var image = new Image { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 2) };
        var caption = new TextBlock { Foreground = Label, FontSize = 11, TextWrapping = TextWrapping.Wrap, Text = "rendering model…" };
        var panel = new StackPanel();
        panel.Children.Add(image);
        panel.Children.Add(caption);
        void Show((System.Windows.Media.Imaging.BitmapSource? Image, string Caption) t)
        {
            image.Source = t.Image;
            image.Visibility = t.Image is null ? Visibility.Collapsed : Visibility.Visible;
            caption.Text = t.Caption;
        }
        if (Thumbnails.TryGetValue(path, out var cached)) { Show(cached); return panel; }
        var model = _model!;
        Task.Run(() =>
        {
            (System.Windows.Media.Imaging.BitmapSource?, string) result;
            try
            {
                var (rm, error) = model.Models.TryLoad(path);
                if (rm is null) result = (null, $"model not drawable: {error}");
                else
                {
                    var rgba = AtlasWH3.Core.Rendering.ModelPreview.Render(rm, model.Models, new AtlasWH3.Core.Rendering.ModelPreview.Options(size, Background: 0xFF2D2D30));
                    for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); // RGBA → BGRA
                    var bmp = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, rgba, size * 4);
                    bmp.Freeze();
                    var b = rm.Bounds;
                    result = (bmp, $"{rm.Triangles()} triangles, {rm.Lods.Count} LODs, size {b[3] - b[0]:0.##} × {b[4] - b[1]:0.##} × {b[5] - b[2]:0.##}"
                                   + (rm.Problems.Count > 0 ? "\n⚠ " + string.Join("; ", rm.Problems) : ""));
                }
            }
            catch (Exception ex) { result = (null, "preview failed: " + ex.Message); }
            Thumbnails[path] = result;
            Dispatcher.BeginInvoke(() => Show(result));
        });
        return panel;
    }

    /// <summary>Prefab instances: what the key resolves to, and open / expand.</summary>
    private UIElement PrefabBlock()
    {
        var keys = _items.Select(i => i.Entity.Component("ECPrefab")!["key"] ?? "").Distinct().ToList();
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (keys.Count == 1)
        {
            var path = _model!.Prefabs.PathOf(keys[0]);
            var info = path is null ? "not found in the prefab library"
                : _model.ContentOf(keys[0]) is { } c ? $"{c.Points.Length} entities inside (nested expanded)" : "empty";
            panel.Children.Add(new TextBlock
            {
                Text = $"Prefab '{keys[0]}': {info}", Foreground = path is null ? Brushes.Orange : Label, TextWrapping = TextWrapping.Wrap,
                ToolTip = path,
            });
            if (path is not null)
            {
                var open = new Button { Content = "Open prefab", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 4, 4, 0) };
                open.Click += (_, _) => PrefabOpenRequested?.Invoke(keys[0]);
                buttons.Children.Add(open);
            }
        }
        var expand = new Button
        {
            Content = _items.Count == 1 ? "Expand (break apart)" : $"Expand {_items.Count} instances",
            Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 4, 0, 0),
            ToolTip = "Replace the instance by copies of the prefab's entities, placed and overridden as the game sees them (Ctrl+E)",
        };
        expand.Click += (_, _) => PrefabExpandRequested?.Invoke();
        buttons.Children.Add(expand);
        panel.Children.Add(buttons);
        var overrides = _items.Sum(i => i.Entity.Component("ECPrefab")!.ChildElements);
        if (overrides > 0)
            panel.Children.Add(new TextBlock { Text = $"{overrides} override(s) on inner entities (applied on expand)", Foreground = Label, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        return panel;
    }

    private UIElement ComponentBlock(string component, EntityTypeDefinition? def)
    {
        var slot = def?.Components.FirstOrDefault(c => c.Type == component);
        var readOnly = slot?.ReadOnly == true;
        var schema = _model!.Schema.Find(component);
        var panel = new StackPanel { Margin = new Thickness(4, 2, 0, 4) };

        var headerPanel = new DockPanel { LastChildFill = true };
        var structural = component is "ECLayer" or "ECLayerFile" or "ECFileLayer" or "ECTransform";
        // Required slots, and conditional ones decided by the project (tile_database, project_type), stay.
        var removable = slot is null || (slot.Conditional && slot.Parameter is not ("tile_database" or "project_type"));
        if (!structural && !readOnly && removable)
        {
            var remove = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), ToolTip = $"Remove {component}", Background = Brushes.Transparent, Foreground = Brushes.IndianRed, BorderThickness = new Thickness(0) };
            remove.Click += (_, _) => ComponentRemoved?.Invoke(component);
            DockPanel.SetDock(remove, Dock.Right);
            headerPanel.Children.Add(remove);
        }
        headerPanel.Children.Add(new TextBlock
        {
            Text = component.StartsWith("EC") ? component[2..] : component,
            Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold,
            ToolTip = component + (readOnly ? " (read-only in Terry)" : "") + (slot is { Conditional: true } ? $" (when {slot.Parameter}={slot.Value})" : ""),
        });

        // Field order: the entity's own attributes, then schema fields it lacks (settable: added on write).
        var names = _items[0].Entity.Component(component)!.Fields.Select(f => f.Key).ToList();
        foreach (var f in schema?.Fields ?? []) if (!names.Contains(f.Name)) names.Add(f.Name);
        foreach (var field in names)
        {
            var values = _items.Select(i => i.Entity.Component(component)![field]).ToList();
            var fs = schema?.Fields.FirstOrDefault(x => x.Name == field);
            var editor = EditorFor(component, field, fs, values, readOnly);
            panel.Children.Add(Row(field, editor, fs));
        }
        var children = _items[0].Entity.Component(component)!.ChildElements;
        if (children > 0)
            panel.Children.Add(new TextBlock
            {
                Text = $"{children} child element(s) ({string.Join(", ", schema?.Children.Keys.AsEnumerable() ?? [])}) — not editable in the inspector yet",
                Foreground = Label, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
        if (schema is { Count: 0 })
            panel.Children.Add(new TextBlock { Text = "Fields unknown: this component never appears in the kit.", Foreground = Brushes.Orange, FontSize = 11 });

        return new Expander
        {
            Header = headerPanel, Content = panel, IsExpanded = component is not ("ECDLCMask" or "ECTerrainClamp" or "ECPropHeightPatch"),
            Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 4, 0, 0), BorderBrush = Edge, BorderThickness = new Thickness(0, 1, 0, 0),
        };
    }

    private UIElement EditorFor(string component, string field, FieldSchema? fs, List<string?> values, bool readOnly)
    {
        var distinct = values.Distinct().ToList();
        var mixed = distinct.Count > 1;
        var value = mixed ? null : distinct[0];
        void Set(string v) => FieldEdited?.Invoke(new FieldEdit(component, field, _ => v));
        var type = fs?.Type ?? FieldType.String;

        switch (type)
        {
            case FieldType.Bool:
                var cb = new CheckBox { IsThreeState = mixed, IsChecked = mixed ? null : value == "true", IsEnabled = !readOnly, VerticalAlignment = VerticalAlignment.Center };
                cb.Click += (_, _) => Set(cb.IsChecked == true ? "true" : "false");
                return cb;
            case FieldType.Vec2 or FieldType.Vec3 or FieldType.Vec4 or FieldType.Colour:
                return VectorEditor(component, field, type, values, readOnly);
            case FieldType.Enum:
                return ComboFor(value, fs?.Values ?? [], readOnly, Set);
            case FieldType.Path or FieldType.String:
                // Values used in this project for this field, most common first (filled when the list opens).
                var combo = ComboFor(value, [], readOnly, Set);
                combo.DropDownOpened += (_, _) =>
                {
                    if (combo.Items.Count > 0 || _model is null) return;
                    foreach (var v in _model.All.Select(i => i.Entity.Component(component)?[field]).Where(v => !string.IsNullOrEmpty(v))
                                 .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(300))
                        combo.Items.Add(v.Key);
                };
                return combo;
            default:
                return TextBoxFor(value ?? "", readOnly, Set, mixed);
        }
    }

    private UIElement VectorEditor(string component, string field, FieldType type, List<string?> values, bool readOnly)
    {
        var n = type switch { FieldType.Vec2 => 2, FieldType.Vec3 => 3, _ => 4 };
        var parsed = values.Select(v => (v ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var grid = new UniformGridPanel(n + (type == FieldType.Colour ? 1 : 0));
        var boxes = new List<TextBox>();
        for (var i = 0; i < n; i++)
        {
            var k = i;
            var parts = parsed.Select(p => p.ElementAtOrDefault(k)).Distinct().ToList();
            var tb = TextBoxFor(parts.Count == 1 ? parts[0] ?? "" : "", readOnly, text =>
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return;
                FieldEdited?.Invoke(new FieldEdit(component, field, current =>
                {
                    var cur = current.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    while (cur.Count < n) cur.Add(type == FieldType.Colour ? "255" : "0");
                    cur[k] = text;
                    return string.Join(' ', cur);
                }));
            }, parts.Count > 1);
            tb.ToolTip = type == FieldType.Colour ? "RGBA"[k].ToString() : "XYZW"[k].ToString();
            boxes.Add(tb);
            grid.Add(tb);
        }
        if (type == FieldType.Colour && parsed.Count > 0 && parsed[0].Length == 4
            && parsed[0].All(p => byte.TryParse(p, out _)))
            grid.Add(new Border
            {
                Margin = new Thickness(2), BorderBrush = Edge, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(255, byte.Parse(parsed[0][0]), byte.Parse(parsed[0][1]), byte.Parse(parsed[0][2]))),
            });
        return grid;
    }

    private static ComboBox ComboFor(string? value, IEnumerable<string> options, bool readOnly, Action<string> commit)
    {
        var combo = new ComboBox { IsEditable = true, IsEnabled = !readOnly, Background = Box, Foreground = Brushes.Black, Text = value ?? "" };
        foreach (var o in options) combo.Items.Add(o);
        combo.Text = value ?? "";
        var initial = combo.Text;
        void Commit()
        {
            if (combo.Text != initial)
            {
                initial = combo.Text;
                commit(combo.Text);
            }
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s && s != initial)
            {
                initial = s;
                commit(s);
            }
        };
        combo.LostKeyboardFocus += (_, e) => { if (!combo.IsKeyboardFocusWithin) Commit(); };
        combo.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return combo;
    }

    private static TextBox TextBoxFor(string value, bool readOnly, Action<string> commit, bool mixed = false)
    {
        var tb = new TextBox
        {
            Text = value, IsReadOnly = readOnly, Background = Box, Foreground = Theme.Brush("Text"), BorderBrush = Edge,
            CaretBrush = Brushes.White, Margin = new Thickness(1), ToolTip = mixed ? "(mixed values)" : null,
        };
        if (mixed) tb.Background = new SolidColorBrush(Color.FromRgb(55, 50, 40));
        var initial = value;
        void Commit()
        {
            if (tb.Text == initial) return;
            initial = tb.Text;
            commit(tb.Text);
        }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        tb.LostKeyboardFocus += (_, _) => Commit();
        return tb;
    }

    private UIElement AddComponentRow(TerryEntityData entity, EntityTypeDefinition? def, List<string> present)
    {
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in def?.Components ?? []) candidates.Add(s.Type);
        if (_model!.Schema.EntityTemplates.GetValueOrDefault(entity.Type) is { } t)
            foreach (var layout in t.Variants.Keys.Append(string.Join(",", t.Components)))
                foreach (var c in layout.Split(',')) candidates.Add(c);
        candidates.ExceptWith(present);
        if (candidates.Count == 0) return new TextBlock();
        var combo = new ComboBox { ItemsSource = candidates.ToList(), MinWidth = 160, Margin = new Thickness(0, 0, 4, 0) };
        var add = new Button { Content = "Add component", Padding = new Thickness(6, 1, 6, 1) };
        add.Click += (_, _) => { if (combo.SelectedItem is string c) ComponentAdded?.Invoke(c); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(combo);
        row.Children.Add(add);
        return row;
    }

    private static UIElement Row(string label, UIElement editor, FieldSchema? fs = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tip = fs is null ? label : $"{label}: {fs.Type.ToString().ToLowerInvariant()}" + (fs.Default is { } d ? $", default '{d}'" : "")
                                       + (fs.Source != "corpus" ? $" ({fs.Source})" : "");
        var text = new TextBlock { Text = label, Foreground = Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tip };
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private static UIElement ReadOnlyRow(string label, string value) =>
        Row(label, new TextBox { Text = value, IsReadOnly = true, Background = Brushes.Transparent, Foreground = Theme.Brush("Text"), BorderThickness = new Thickness(0) });

    /// <summary>A grid of equal columns (vector components).</summary>
    private sealed class UniformGridPanel : Grid
    {
        public UniformGridPanel(int columns)
        {
            for (var i = 0; i < columns; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        public void Add(UIElement e)
        {
            SetColumn(e, Children.Count);
            Children.Add(e);
        }
    }
}
