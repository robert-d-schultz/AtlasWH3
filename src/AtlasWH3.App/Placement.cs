using System.Windows;
using AtlasWH3.Core;

namespace AtlasWH3.App;

/// <summary>Remembers a window's size, position and maximized state in <see cref="AppSettings"/> under a key.</summary>
public static class Placement
{
    public static void Track(Window window, string key)
    {
        if (AppSettings.Current.Windows.TryGetValue(key, out var p) && OnScreen(p))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = p.Left;
            window.Top = p.Top;
            window.Width = p.Width;
            window.Height = p.Height;
            if (p.Maximized) window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
            else window.WindowState = WindowState.Normal;
        }
        window.Closing += (_, _) =>
        {
            var r = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            if (r.IsEmpty || r.Width < 200 || r.Height < 150) return;
            AppSettings.Current.Windows[key] = new AppSettings.WindowPlacement(r.Left, r.Top, r.Width, r.Height, window.WindowState == WindowState.Maximized);
        };
    }

    // a saved spot from a since-removed monitor would open the window off screen
    private static bool OnScreen(AppSettings.WindowPlacement p) =>
        p.Width > 0 && p.Height > 0 &&
        p.Left + 80 > SystemParameters.VirtualScreenLeft && p.Top + 40 > SystemParameters.VirtualScreenTop &&
        p.Left + 80 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        p.Top + 40 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
}
