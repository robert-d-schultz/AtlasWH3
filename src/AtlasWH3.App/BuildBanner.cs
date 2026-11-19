using System.Windows;
using System.Windows.Controls;

namespace AtlasWH3.App;

/// <summary>The strip an editor shows while a build runs: the editors save into the Terry project at once, and a build
/// reading it at the same time may take an edit in one step and miss it in another.</summary>
public static class BuildBanner
{
    public static UIElement For(Window window)
    {
        var line = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Bg"),
            Text = "A build is running. Edits saved now may reach some of its steps and not others: build again after it finishes.",
        };
        var bar = new Border { Background = Theme.Brush("Warn"), Padding = new Thickness(8, 3, 8, 3), Child = line };
        void Update() => bar.Visibility = BuildWindow.Building ? Visibility.Visible : Visibility.Collapsed;
        BuildWindow.BuildingChanged += Update;
        window.Closed += (_, _) => BuildWindow.BuildingChanged -= Update;
        Update();
        return bar;
    }
}
