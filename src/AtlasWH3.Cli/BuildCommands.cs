using System.Text.Json;
using AtlasWH3.Core;
using AtlasWH3.Core.Build;

/// <summary>
/// build: runs a .atlaswh3 project's build (the same <see cref="BuildRunner"/> as the GUI's Build window).
/// new-project: writes a .atlaswh3 project with the default build profile.
/// setup: first-run data from the game install (the GUI's Settings > Prepare game data).
/// </summary>
static class BuildCommands
{
    public static readonly HashSet<string> Names = ["build", "new-project", "setup"];

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            return command switch { "new-project" => NewProject(paths, a), "setup" => Setup(paths, a), _ => Build(paths, a) };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"{command}: {e.Message}");
            return 2;
        }
    }

    // build --project <file> [--segments validate,compile,custom,pack,install] [--steps a,b] [--custom name,..]
    //       [--out <dir>] [--pack-output <file>] [--json]
    private static int Build(ProjectPaths paths, string[] a)
    {
        var file = Option(a, "--project") ?? throw new ArgumentException("build needs --project <file.atlaswh3>");
        var project = BuildProject.Load(file);
        if (Option(a, "--out") is { } outDir) project.Build.Output = Path.GetFullPath(outDir);
        if (Option(a, "--pack-output") is { } packOut) project.Build.Pack.Output = Path.GetFullPath(packOut);
        var json = a.Contains("--json");

        // --ak / --map / --root / --pack given on the command line win over the project
        var defaults = new ProjectPaths();
        var resolved = project.ToPaths(defaults);
        if (paths.AssemblyKitRoot != defaults.AssemblyKitRoot) resolved = resolved with { AssemblyKitRoot = paths.AssemblyKitRoot };
        if (paths.VanillaRoot != defaults.VanillaRoot) resolved = resolved with { VanillaRoot = paths.VanillaRoot };
        if (paths.ModPacks.Count > 0) resolved = resolved with { ModPacks = paths.ModPacks };

        var request = new BuildRunner.Request
        {
            Segments = List(a, "--segments")?.Select(s => Enum.Parse<BuildSegment>(s, ignoreCase: true)).ToHashSet(),
            Steps = List(a, "--steps"),
            CustomSteps = List(a, "--custom"),
        };
        var log = json ? Console.Error : Console.Out;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); log.WriteLine("cancelling..."); };
        var runner = new BuildRunner(project, resolved, e =>
        {
            switch (e.Kind)
            {
                case BuildRunner.EventKind.Started: log.WriteLine($"=== {e.Id}"); break;
                case BuildRunner.EventKind.Log: log.WriteLine($"[{e.Id}] {e.Message}"); break;
                case BuildRunner.EventKind.Finished:
                    var r = e.Result!;
                    log.WriteLine($"=== {e.Id} {r.Status} {r.Seconds:F1} s{(r.FilesWritten > 0 ? $", {r.FilesWritten} files" : "")}");
                    foreach (var p in r.Problems) log.WriteLine($"    ! {p}");
                    foreach (var n in r.Notes) log.WriteLine($"    {BuildRunner.NoteLine(n)}");
                    break;
            }
        }, cancel.Token);
        var report = runner.Run(request);
        if (json) Console.WriteLine(JsonSerializer.Serialize(report, BuildProject.Json));
        else Console.WriteLine($"{(report.Succeeded ? "build succeeded" : "build FAILED")} in {report.Seconds:F1} s, log {report.LogFile}");
        return report.Succeeded ? 0 : 1;
    }

    // new-project <file.atlaswh3> [--map <name>] [--ak <kit>]   (global --map / --ak)
    private static int NewProject(ProjectPaths paths, string[] a)
    {
        var file = a.FirstOrDefault(s => !s.StartsWith("--")) ?? throw new ArgumentException("new-project <file.atlaswh3>");
        if (!file.EndsWith(BuildProject.Extension, StringComparison.OrdinalIgnoreCase)) file += BuildProject.Extension;
        if (File.Exists(file)) throw new IOException($"{file} exists");
        var defaults = new ProjectPaths();
        var project = BuildProject.CreateDefault(paths.MapName, paths.AssemblyKitRoot != defaults.AssemblyKitRoot ? paths.AssemblyKitRoot : "");
        project.Save(file);
        Console.WriteLine($"wrote {Path.GetFullPath(file)} (map {project.Map})");
        return 0;
    }

    // setup [--db <dir>] [--compiled <dir>] [--maps]   (global --map; defaults: the settings' folders)
    private static int Setup(ProjectPaths paths, string[] a)
    {
        Console.WriteLine($"game data {paths.GameDataDir}{(Directory.Exists(paths.GameDataDir) ? "" : " (NOT FOUND)")}");
        if (a.Contains("--maps"))
        {
            foreach (var m in GameSetup.VanillaMaps(paths.GameDataDir)) Console.WriteLine(m);
            return 0;
        }
        var db = Option(a, "--db");
        var compiled = Option(a, "--compiled");
        if (db is null && compiled is null) { db = paths.DbTsvRoot; compiled = paths.VanillaRoot; }
        if (compiled is not null) GameSetup.ExtractCompiledMap(paths.GameDataDir, paths.MapName, compiled, Console.WriteLine);
        if (db is not null) foreach (var f in GameSetup.ExtractDbTables(paths.GameDataDir, db, Console.WriteLine)) Console.WriteLine($"wrote {f}");
        return 0;
    }

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static List<string>? List(string[] a, string name) =>
        Option(a, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
