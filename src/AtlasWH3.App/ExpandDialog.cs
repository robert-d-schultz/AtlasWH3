using System.Windows;
using System.Windows.Controls;
using AtlasWH3.Core.Operations;

namespace AtlasWH3.App;

/// <summary>Asks for the per-side hex padding (the same numbers used in CAIME's Edit > Resize).</summary>
public sealed class ExpandDialog : Window
{
    private readonly TextBox _left = Box("0"), _top = Box("0"), _right = Box("0"), _bottom = Box("0");
    private readonly CheckBox _extendTextures = new() { Content = "Extend edge ground textures outward", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _preview = new() { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly int _hexW, _hexH;

    public HexPadding HexPadding { get; private set; }
    public bool ExtendTextures => _extendTextures.IsChecked == true;

    public ExpandDialog(int lfWidth, int lfHeight)
    {
        _hexW = lfWidth / HexPadding.LfPxPerHex;
        _hexH = lfHeight / HexPadding.LfPxPerHex;
        Title = "Expand map canvas";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(int r, string label, UIElement input)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, r); Grid.SetRow(input, r); Grid.SetColumn(input, 1);
            grid.Children.Add(text); grid.Children.Add(input);
        }
        Row(0, "West (left), hexes", _left);
        Row(1, "North (top), hexes", _top);
        Row(2, "East (right), hexes", _right);
        Row(3, "South (bottom), hexes", _bottom);

        var ok = new Button { Content = "Expand", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        ok.Click += (_, _) => { if (TryRead(out var p)) { HexPadding = p; DialogResult = true; } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(4) };
        root.Children.Add(new TextBlock
        {
            Text = "Enter the same padding you use in CAIME (Edit > Resize). New cells become open sea; " +
                   "sculpt or import land into them afterwards.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 12, 12, 0),
        });
        root.Children.Add(grid);
        var extra = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        extra.Children.Add(_extendTextures);
        extra.Children.Add(_preview);
        extra.Children.Add(buttons);
        root.Children.Add(extra);
        Content = root;

        foreach (var box in new[] { _left, _top, _right, _bottom })
            box.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private static TextBox Box(string text) => new() { Text = text, Margin = new Thickness(0, 2, 0, 2) };

    private bool TryRead(out HexPadding padding)
    {
        padding = default;
        if (!int.TryParse(_left.Text, out var l) || !int.TryParse(_top.Text, out var t) ||
            !int.TryParse(_right.Text, out var r) || !int.TryParse(_bottom.Text, out var b) ||
            l < 0 || t < 0 || r < 0 || b < 0)
            return false;
        padding = new HexPadding(l, t, r, b);
        return !padding.IsZero;
    }

    private void UpdatePreview()
    {
        if (!TryRead(out var p))
        {
            _preview.Text = "Enter non-negative whole numbers (at least one side > 0).";
            return;
        }
        var hexW = _hexW + p.Left + p.Right;
        var hexH = _hexH + p.Top + p.Bottom;
        _preview.Text = $"Hex grid: {_hexW} x {_hexH} -> {hexW} x {hexH}" +
                        (hexW % 2 != 0 || hexH % 2 != 0 ? "   (CAIME requires even sizes!)" : "") +
                        $"\nTerrain: {hexW * HexPadding.LfPxPerHex} x {hexH * HexPadding.LfPxPerHex} px";
    }
}
