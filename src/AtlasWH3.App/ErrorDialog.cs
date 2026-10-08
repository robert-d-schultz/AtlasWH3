using System.IO;
using System.Windows;
using System.Windows.Controls;
using AtlasWH3.Core;

namespace AtlasWH3.App;

/// <summary>A friendly error message with the technical details folded away (and a Copy button for bug reports).
/// Every shown error is also appended to the log folder.</summary>
public sealed class ErrorDialog : Window
{
    public static string LogDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtlasWH3", "logs");

    public static void Show(Window? owner, string message, Exception? error = null)
    {
        Log(message, error);
        var dlg = new ErrorDialog(message, error);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        dlg.ShowDialog();
    }

    public static void Log(string message, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, $"errors_{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss} {AppInfo.Product} {AppInfo.Version}: {message}\n{error}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ErrorDialog(string message, Exception? error)
    {
        Title = AppInfo.Product;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 700;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Panel");
        Foreground = Theme.Brush("Text");

        var panel = new StackPanel { Margin = new Thickness(16) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Theme.Icon(Theme.Glyph.Error, 22, Theme.Brush("Error")));
        head.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0), MaxWidth = 470, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        if (error is not null)
        {
            panel.Children.Add(new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32, 8, 0, 0), Foreground = Theme.Brush("DimText") });
            var details = new TextBox
            {
                Text = error.ToString(), IsReadOnly = true, FontFamily = Theme.MonoFont, FontSize = 11, Height = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            panel.Children.Add(new Expander { Header = "Details", Content = details, Margin = new Thickness(0, 12, 0, 0) });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (error is not null)
            buttons.Children.Add(Theme.IconButton(Theme.Glyph.Copy, "Copy details", (_, _) =>
                Clipboard.SetText($"{AppInfo.Product} {AppInfo.Version}\n{message}\n{error}"), "Copy for a bug report"));
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
