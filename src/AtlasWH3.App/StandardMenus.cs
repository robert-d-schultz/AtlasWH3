using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AtlasWH3.Core;

namespace AtlasWH3.App;

/// <summary>The Build, Window and Help menus every editor shares, so they read the same everywhere.</summary>
public static class StandardMenus
{
    /// <summary>Appends Build, Window and Help to <paramref name="menu"/>.</summary>
    public static void AddTo(Menu menu, Window owner, ProjectPaths paths)
    {
        menu.Items.Add(Build(owner, paths));
        menu.Items.Add(Windows(owner, paths));
        menu.Items.Add(Help(owner));
    }

    public static MenuItem Build(Window owner, ProjectPaths paths)
    {
        var build = new MenuItem { Header = "_Build" };
        build.Items.Add(Item("Open _Build window…", () => BuildWindow.Show(owner, paths), "Ctrl+B"));
        build.Items.Add(Item("Build _all (current project)", () => BuildWindow.Show(owner, paths).BuildAll(), "F7"));
        build.Items.Add(new Separator());
        foreach (var recent in AppSettings.Current.RecentProjects.Where(File.Exists).Take(5))
            build.Items.Add(Item(Path.GetFileName(recent), () => BuildWindow.Show(owner, paths, recent), tooltip: recent));
        owner.InputBindings.Add(new System.Windows.Input.KeyBinding(new RelayCommand(() => BuildWindow.Show(owner, paths)),
            System.Windows.Input.Key.B, System.Windows.Input.ModifierKeys.Control));
        owner.InputBindings.Add(new System.Windows.Input.KeyBinding(new RelayCommand(() => BuildWindow.Show(owner, paths).BuildAll()),
            System.Windows.Input.Key.F7, System.Windows.Input.ModifierKeys.None));
        return build;
    }

    public static MenuItem Windows(Window owner, ProjectPaths paths)
    {
        var window = new MenuItem { Header = "_Window" };
        window.Items.Add(Item("_Start page", () => StartWindow.ShowSingle(paths)));
        window.Items.Add(Item("Campaign _scene editor", () => new Scene.SceneWindow(paths).Show()));
        window.Items.Add(Item("Campaign _tile map", () => new CampaignTileWindow(paths).Show()));
        window.Items.Add(Item("Terrain _painter", () => new MainWindow(paths).Show()));
        window.Items.Add(Item("_Battle map (experimental)…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Battle terrain source folder (raw_data\\terrain\\battles\\<map>, holding the .terry)",
                InitialDirectory = Path.Combine(paths.AssemblyKitRoot, "raw_data", "terrain", "battles"),
            };
            if (dialog.ShowDialog(owner) == true) new BattleWindow(dialog.FolderName, paths).Show();
        }));
        window.Items.Add(Item("Bu_ild", () => BuildWindow.Show(owner, paths)));
        window.Items.Add(Item("Se_ttings…", () => OpenSettings(owner), tooltip: SettingsTip));
        window.SubmenuOpened += (_, _) =>
        {
            // the open windows, to switch between them
            while (window.Items.Count > 7) window.Items.RemoveAt(7);
            var open = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w.Owner is null && !string.IsNullOrEmpty(w.Title)).ToList();
            if (open.Count < 2) return;
            window.Items.Add(new Separator());
            foreach (var w in open)
            {
                var mi = Item(w.Title, () => { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); });
                mi.IsChecked = w == owner;
                window.Items.Add(mi);
            }
        };
        return window;
    }

    public static MenuItem Help(Window owner)
    {
        var help = new MenuItem { Header = "_Help" };
        var tour = Item("_Walkthrough for this window", () => Walkthrough.Start(owner), "F1",
                        "A short guided tour of this window's panels. It also starts by itself the first time a window opens.");
        help.Items.Add(tour);
        if (owner is StartWindow)
            help.Items.Add(Item("_Reset all walkthroughs", () =>
            {
                Walkthrough.ResetAll();
                MessageBox.Show(owner, "Every window's walkthrough will start again the next time that window opens.", AppInfo.Product);
            }, tooltip: "Show each window's walkthrough again the next time it opens."));
        help.SubmenuOpened += (_, _) => tour.IsEnabled = Walkthrough.Has(owner);
        owner.InputBindings.Add(new System.Windows.Input.KeyBinding(new RelayCommand(() => Walkthrough.Start(owner)),
            System.Windows.Input.Key.F1, System.Windows.Input.ModifierKeys.None));
        help.Items.Add(new Separator());
        help.Items.Add(Item("_Settings…", () => OpenSettings(owner), tooltip: SettingsTip));
        help.Items.Add(new Separator());
        help.Items.Add(Item("_Read me", () => OpenFile(Path.Combine(AppContext.BaseDirectory, "README.md"))));
        help.Items.Add(Item("_Known issues", () => OpenFile(Path.Combine(AppContext.BaseDirectory, "KNOWN_ISSUES.md"))));
        help.Items.Add(Item("Open _log folder", () => OpenFolder(ErrorDialog.LogDir)));
        help.Items.Add(Item("Open _settings folder", () => OpenFolder(AppSettings.Folder)));
        help.Items.Add(new Separator());
        help.Items.Add(Item("_About AtlasWH3", () => new AboutWindow { Owner = owner }.ShowDialog()));
        return help;
    }

    private static string SettingsTip => InfoCards.Get("menu.settings").Text;

    /// <summary>The Settings page, from any editor.</summary>
    public static void OpenSettings(Window owner) => new SettingsWindow { Owner = owner }.ShowDialog();

    public static MenuItem Item(string header, Action click, string gesture = "", string? tooltip = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture, ToolTip = tooltip };
        item.Click += (_, _) =>
        {
            try { click(); }
            catch (Exception e) { ErrorDialog.Show(Application.Current.MainWindow, $"{header.Replace("_", "").TrimEnd('…')} failed.", e); }
        };
        return item;
    }

    private static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show($"{Path.GetFileName(path)} is not in this build ({AppContext.BaseDirectory}).", AppInfo.Product);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start("explorer.exe", $"\"{path}\"");
    }
}

/// <summary>Version, credits and third-party notices.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About " + AppInfo.Product;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Panel");
        Foreground = Theme.Brush("Text");
        var panel = new StackPanel { Margin = new Thickness(20) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Theme.Icon(Theme.Glyph.Map, 34, Theme.Brush("Accent")));
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        names.Children.Add(new TextBlock { Text = AppInfo.Product, FontSize = 24, FontWeight = FontWeights.SemiBold });
        names.Children.Add(new TextBlock { Text = $"Version {AppInfo.Version}{(AppInfo.Commit is { } c ? $" ({c[..Math.Min(7, c.Length)]})" : "")}", Foreground = Theme.Brush("DimText") });
        head.Children.Add(names);
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock { Text = AppInfo.Tagline, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"),
            Text = "Edits campaign maps from the Assembly Kit's sources and builds them natively, without BOB. " +
                   "Alpha software: back up your assembly kit and packs before building over them.\n\n" +
                   "Free and open source under the MIT licence (see LICENSE).\n\n" +
                   "Not affiliated with Creative Assembly or SEGA. Total War: THREE KINGDOMS and its Assembly Kit are their property; " +
                   "AtlasWH3 reads them from your own installation and ships none of their data.",
        });
        panel.Children.Add(Theme.Header("Third-party libraries", 14));
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("DimText"),
            Text = "SkiaSharp, Vortice.Windows (Direct3D 11), BCnEncoder.NET, BitMiracle LibTiff.NET, CommunityToolkit.HighPerformance. " +
                   "See THIRD_PARTY_NOTICES.md.",
        });
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);
        Content = panel;
    }
}
