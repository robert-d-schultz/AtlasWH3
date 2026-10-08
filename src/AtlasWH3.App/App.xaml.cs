using System.IO;
using System.Windows;
using AtlasWH3.Core;

namespace AtlasWH3.App;

/// <summary>
/// Opens the start page, or straight into one editor: <c>--scene [project.terry]</c> (default: the campaign map's
/// .terry), <c>--tile-editor [tile_map.png]</c> (default: the kit's), <c>--tile-errors [tile_map.png]</c> (the same, in error mode), <c>--battle &lt;source folder&gt;</c>,
/// <c>--painter</c> (terrain painter), <c>--build [project.atlaswh3]</c>; plus the usual <c>--map</c> / <c>--ak</c> /
/// <c>--root</c>. The first start shows the setup dialog. Developer mode adds the test flags (--selftest, tours, --shots).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorDialog.Show(MainWindow, "Something went wrong. Your last edit may not have been applied.", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ErrorDialog.Log("Unhandled exception", args.ExceptionObject as Exception);
        // every window gets the app icon (WPF windows don't inherit the exe's)
        var icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/atlaswh3.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((w, _) =>
        {
            if (w is Window { Icon: null } window) window.Icon = icon;
        }));

        // automation (self-tests, screenshots, camera tours) never gets a walkthrough over it
        if (e.Args.Any(a => a.ToLowerInvariant() is "--selftest" or "--terrain-selftest" or "--props-selftest" or "--city-tour"
                                                   or "--region-tour" or "--shots" or "--no-walkthrough" or "--walkthrough-shots"))
            Walkthrough.Suppressed = true;

        if (!File.Exists(AppSettings.FilePath) && e.Args.Length == 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (new SettingsWindow(firstRun: true).ShowDialog() != true) { Shutdown(); return; }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        var paths = ProjectPaths.FromArgs(e.Args, out var rest);
        int Flag(string name) => Array.FindIndex(rest, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        string? Value(int i) => i >= 0 && i + 1 < rest.Length && !rest[i + 1].StartsWith("--") ? rest[i + 1] : null;
        int battle = Flag("--battle"), tiles = Flag("--tile-editor"), tileErrors = Flag("--tile-errors"), scene = Flag("--scene"), build = Flag("--build"), painter = Flag("--painter");

        // --walkthrough-shots <dir> [settings]: every tour step of the opened window as a PNG, then quit
        var walkShots = Flag("--walkthrough-shots");
        var shotDir = Value(walkShots);
        void RunShots(Window window) => _ = Dispatcher.InvokeAsync(async () =>
        {
            try { await Walkthrough.ShotsAsync(window, Path.GetFullPath(shotDir!)); }
            catch (Exception ex) { ErrorDialog.Log("Walkthrough screenshots failed", ex); }
            Shutdown();
        });

        if (build >= 0)
        {
            var window = BuildWindow.Show(null, paths, Value(build) is { } p ? Path.GetFullPath(p) : null);
            MainWindow = window;
            if (shotDir is not null) RunShots(window);
            return;
        }
        MainWindow = scene >= 0 ? new Scene.SceneWindow(paths, Value(scene) is { } s ? Path.GetFullPath(s) : null)
            : battle >= 0 && Value(battle) is { } b ? new BattleWindow(b, paths)
            : tiles >= 0 ? new CampaignTileWindow(paths, Value(tiles) is { } t ? Path.GetFullPath(t) : null)
            : tileErrors >= 0 ? new CampaignTileWindow(paths, Value(tileErrors) is { } te ? Path.GetFullPath(te) : null, errorMode: true)
            : painter >= 0 ? new MainWindow(paths)
            : Flag("--campaign-battles") >= 0 ? new CampaignBattlesWindow(paths)
            : new StartWindow(paths);
        if (shotDir is not null)
        {
            if (walkShots + 2 < rest.Length && rest[walkShots + 2].Equals("settings", StringComparison.OrdinalIgnoreCase))
                MainWindow = new SettingsWindow();
            if (MainWindow.WindowState != WindowState.Maximized) MainWindow.ShowActivated = false;
            MainWindow.Show();
            RunShots(MainWindow);
            return;
        }
        MainWindow.Show();

        if (!AppSettings.Current.DeveloperMode || MainWindow is not Scene.SceneWindow sceneWindow) return;
        var selftest = Flag("--selftest");
        if (selftest >= 0 && Value(selftest) is { } dir)
            _ = sceneWindow.SelfTestAsync(Path.GetFullPath(dir));
        var terrainTest = Flag("--terrain-selftest");
        if (terrainTest >= 0 && Value(terrainTest) is { } terrainDir)
            _ = sceneWindow.TerrainSelfTestAsync(Path.GetFullPath(terrainDir));
        var propsTest = Flag("--props-selftest");
        if (propsTest >= 0 && Value(propsTest) is { } propsDir)
            _ = sceneWindow.PropsSelfTestAsync(Path.GetFullPath(propsDir));
        foreach (var kind in new[] { "city", "region" })
        {
            var ti = Flag($"--{kind}-tour");
            if (ti >= 0 && ti + 1 < rest.Length)
                _ = sceneWindow.TourAsync(kind, Path.GetFullPath(rest[ti + 1]), ti + 2 < rest.Length && !rest[ti + 2].StartsWith("--") ? rest[ti + 2] : null);
        }
        var shots = Flag("--shots");
        if (shots >= 0 && shots + 2 < rest.Length)
            _ = sceneWindow.ShotsAsync(Path.GetFullPath(rest[shots + 1]), Path.GetFullPath(rest[shots + 2]));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { AppSettings.Current.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorDialog.Log("Saving settings failed", ex); }
        base.OnExit(e);
    }
}
