using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AtlasWH3.App.Scene;

/// <summary>Prefab browser: filter the library's keys, see what a prefab holds, pick one to place.</summary>
public static class PrefabPicker
{
    public static string? Show(Window owner, SceneModel model)
    {
        var lib = model.Prefabs;
        var all = lib.Keys.Order(StringComparer.Ordinal).ToList();
        var dialog = new Window
        {
            Title = $"Place prefab — {lib.Database} library ({all.Count})", Owner = owner, Width = 640, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Theme.Brush("Panel"),
        };
        var filter = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        var list = new ListBox { Background = Brushes.Transparent, Foreground = Theme.Brush("Text") };
        var info = new TextBlock { Foreground = Theme.Brush("DimText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), MinHeight = 36 };
        var ok = new Button { Content = "Place", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90 };
        string? result = null;

        void Refill()
        {
            list.Items.Clear();
            foreach (var k in all.Where(k => k.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(1500)) list.Items.Add(k);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        filter.TextChanged += (_, _) => Refill();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not string key) { info.Text = ""; return; }
            var def = lib.Load(key);
            var content = model.ContentOf(key);
            info.Text = def is null ? "unreadable" :
                $"{System.IO.Path.GetRelativePath(lib.Root, def.Path)}\n{def.Entities.Count} entities"
                + (content is null ? "" : $", {content.Points.Length} with nested prefabs expanded")
                + (content?.Bounds is var (x0, z0, x1, z1) ? $", footprint {x1 - x0:F1} × {z1 - z0:F1}" : "");
        };
        list.MouseDoubleClick += (_, _) => ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ok.Click += (_, _) =>
        {
            result = list.SelectedItem as string;
            dialog.DialogResult = result is not null;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var dock = new DockPanel { Margin = new Thickness(10) };
        var header = new TextBlock
        {
            Text = all.Count == 0 ? $"No prefabs in {lib.Root} yet. Select entities and use Edit → Make prefab to create one." : "Filter:",
            Foreground = Theme.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(info, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(filter);
        dock.Children.Add(buttons);
        dock.Children.Add(info);
        dock.Children.Add(list);
        dialog.Content = dock;
        dialog.Loaded += (_, _) => { Refill(); filter.Focus(); };
        return dialog.ShowDialog() == true ? result : null;
    }
}
