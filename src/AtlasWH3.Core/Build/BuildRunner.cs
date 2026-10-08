using System.Diagnostics;
using System.Text.Json;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;

namespace AtlasWH3.Core.Build;

/// <summary>Build segments, in run order.</summary>
public enum BuildSegment { Validate, Compile, Custom, Pack, Install }

/// <summary>
/// Runs a <see cref="BuildProject"/>'s build: Validate → custom "beforeCompile" → Compile (native steps) → "afterCompile"
/// → Pack → "afterPack" → Install → "afterInstall". Stops at the first failure. Used by the GUI's Build window, the CLI
/// <c>build</c> command and the MCP <c>build_project</c> tool.
/// </summary>
public sealed class BuildRunner
{
    /// <summary>Item ids: <c>validate</c>, <c>compile:&lt;step&gt;</c>, <c>custom:&lt;name&gt;</c>, <c>pack</c>, <c>install</c>.</summary>
    public sealed record Event(string Id, EventKind Kind, string? Message = null, ItemResult? Result = null);
    public enum EventKind { Started, Log, Finished }

    /// <summary>Status: ok, warning (ok with notes), failed, blocked, skipped, cancelled.</summary>
    public sealed record ItemResult(string Id, string Status, double Seconds, IReadOnlyList<string> Problems,
                                    IReadOnlyList<string> Notes, int FilesWritten)
    {
        public bool Succeeded => Status is "ok" or "warning";
    }

    public sealed record Report(string Project, string Map, double Seconds, bool Succeeded, IReadOnlyList<ItemResult> Items, string? LogFile);

    /// <summary>What to run. Null segments = every enabled segment; null steps = the profile's steps.</summary>
    public sealed record Request
    {
        public IReadOnlySet<BuildSegment>? Segments { get; init; }
        public IReadOnlyList<string>? Steps { get; init; }
        /// <summary>Only these custom steps (by name), else every enabled one in the selected segments.</summary>
        public IReadOnlyList<string>? CustomSteps { get; init; }
    }

    public const string GameProcess = "Warhammer3";
    /// <summary>Notes starting with this make an item's status "warning" instead of "ok".</summary>
    public const string WarningPrefix = "warning: ";

    private readonly BuildProject _project;
    private readonly ProjectPaths _paths;
    private readonly Action<Event> _events;
    private readonly CancellationToken _cancel;
    private readonly List<ItemResult> _items = [];
    private StreamWriter? _log;

    public BuildRunner(BuildProject project, ProjectPaths paths, Action<Event>? events = null, CancellationToken cancel = default)
    {
        _project = project;
        _paths = paths;
        _events = events ?? (_ => { });
        _cancel = cancel;
    }

    /// <summary>Folder for build logs and pack backups (the app's output folder).</summary>
    public string LogDir => Path.Combine(_paths.OutputRoot, "build_logs");

    public Report Run(Request? request = null)
    {
        request ??= new Request();
        var sw = Stopwatch.StartNew();
        var profile = _project.Build;
        bool Want(BuildSegment s) => request.Segments?.Contains(s) ?? s switch
        {
            BuildSegment.Pack => profile.Pack.Enabled,
            BuildSegment.Install => profile.Install.Enabled,
            _ => true,
        };

        Directory.CreateDirectory(LogDir);
        var logFile = Path.Combine(LogDir, $"build_{Safe(_project.Name.Length > 0 ? _project.Name : _project.Map)}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        using (_log = new StreamWriter(logFile) { AutoFlush = true })
        {
            Write("", $"AtlasWH3 build: {_project.FilePath ?? _project.Name}  map {_project.Map}  output {_project.OutputDir(_paths)}");
            var ok = true;
            if (ok && Want(BuildSegment.Validate)) ok = Validate();
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.BeforeCompile, request);
            if (ok && Want(BuildSegment.Compile)) ok = Compile(request.Steps);
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.AfterCompile, request);
            if (ok && Want(BuildSegment.Pack)) ok = Pack();
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.AfterPack, request);
            if (ok && Want(BuildSegment.Install)) ok = Install();
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.AfterInstall, request);
            var report = new Report(_project.Name, _project.Map, sw.Elapsed.TotalSeconds, ok && !_cancel.IsCancellationRequested, _items, logFile);
            Write("", $"{(report.Succeeded ? "build succeeded" : _cancel.IsCancellationRequested ? "build cancelled" : "build FAILED")} in {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(Path.ChangeExtension(logFile, ".json"), JsonSerializer.Serialize(report, BuildProject.Json));
            return report;
        }
    }

    // ------------------------------------------------------------------ segments

    private bool Validate() => Item("validate", notes =>
    {
        var problems = new List<string>();
        var ctx = Context();
        // Steps that read another selected step's output are checked when they run (their inputs don't exist yet).
        var selected = SelectedSteps(null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in selected)
        {
            var step = CampaignBuildPipeline.Find(name);
            if (step.DependsOn.Any(selected.Contains)) continue;
            problems.AddRange(step.CheckInputs(ctx).Select(p => $"{name}: {p}"));
        }
        _cancel.ThrowIfCancellationRequested();
        if (selected.Contains("tile_list") && File.Exists(Path.Combine(_paths.AkTerrainDir, "tile_map.png")))
        {
            var report = TileMapValidator.Run(_paths, new() { Log = m => Log("validate", m) });
            foreach (var f in report.Findings.Where(f => f.Severity == TileMapFinding.Warning).Take(20))
                notes.Add($"{WarningPrefix}tile map {f.Code}: {f.Message}");
            foreach (var f in report.Findings.Where(f => f.Severity == TileMapFinding.Error))
                if (_project.Build.AcceptTileMap.Contains(f.Code)) notes.Add($"accepted tile map {f.Code}: {f.Message}");
                else problems.Add($"tile map {f.Code}: {f.Message} (fix it, or accept the code in the build profile)");
        }
        return (problems, 0);
    });

    private bool Compile(IReadOnlyList<string>? steps)
    {
        var selected = SelectedSteps(steps);
        var outDir = _project.OutputDir(_paths);
        var terrainOut = Path.Combine(outDir, "terrain", "campaigns", _project.Map);

        if (_project.Build.Backup.Length > 0 && !Item("compile:backup", _ =>
        {
            var backup = _project.Resolve(_project.Build.Backup, _paths);
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            CopyDir(_paths.AkTerrainDir, Path.Combine(backup, "raw_terrain"));
            if (Directory.Exists(terrainOut)) CopyDir(terrainOut, Path.Combine(backup, "working_terrain"));
            Log("compile:backup", $"terrain backup in {backup}");
            return ([], 0);
        })) return false;

        foreach (var c in _project.Build.Clean)
        {
            var dir = Path.GetFullPath(Path.Combine(terrainOut, c));
            if (!dir.StartsWith(terrainOut, StringComparison.OrdinalIgnoreCase)) continue;   // never outside the map's folder
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); Write("compile", $"cleaned {dir}"); }
        }

        var ctx = Context();
        var started = new System.Collections.Concurrent.ConcurrentDictionary<string, Stopwatch>();
        var pipeline = new CampaignBuildPipeline
        {
            Progress = e =>
            {
                var id = "compile:" + e.Step;
                switch (e.Kind)
                {
                    case CampaignBuildPipeline.StepEventKind.Started:
                        started[id] = Stopwatch.StartNew();
                        _events(new Event(id, EventKind.Started));
                        break;
                    case CampaignBuildPipeline.StepEventKind.Log: Log(id, e.Message!); break;
                    case CampaignBuildPipeline.StepEventKind.Finished:
                        var o = e.Outcome!;
                        var seconds = o.Result?.Elapsed.TotalSeconds ?? (started.TryGetValue(id, out var w) ? w.Elapsed.TotalSeconds : 0);
                        var r = new ItemResult(id, o.Status, seconds, o.Problems, o.Result?.Notes ?? [], o.Result?.Written.Count ?? 0);
                        lock (_items) _items.Add(r);
                        Write(id, $"{o.Status}{(o.Result is { } res ? $" {res.Elapsed.TotalSeconds:F1} s, {res.Written.Count} files" : "")}");
                        foreach (var p in o.Problems) Write(id, "! " + p);
                        foreach (var n in r.Notes) Write(id, NoteLine(n));
                        _events(new Event(id, EventKind.Finished, Result: r));
                        break;
                }
            },
        };
        var outcomes = pipeline.Run(ctx, selected);
        return outcomes.All(o => o.Status == "ok");
    }

    private bool RunCustom(CustomStepStage stage, Request request)
    {
        foreach (var step in _project.Build.CustomSteps.Where(s => s.RunAt == stage))
        {
            if (request.CustomSteps is { } only ? !only.Contains(step.Name, StringComparer.OrdinalIgnoreCase) : !step.Enabled) continue;
            var id = "custom:" + step.Name;
            if (!Item(id, notes => RunCommand(id, step, notes))) return false;
        }
        return true;
    }

    private (List<string> Problems, int Files) RunCommand(string id, CustomStep step, List<string> notes)
    {
        var cwd = _project.Resolve(step.WorkingDir.Length > 0 ? step.WorkingDir : "{project}", _paths);
        var psi = new ProcessStartInfo(_project.Expand(step.Command, _paths), _project.Expand(step.Arguments, _paths))
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["ATLASWH3_AK"] = _paths.AssemblyKitRoot;
        psi.Environment["ATLASWH3_MAP"] = _project.Map;
        psi.Environment["ATLASWH3_OUT"] = _project.OutputDir(_paths);
        psi.Environment["ATLASWH3_GAME"] = _paths.GameDataDir;
        psi.Environment["ATLASWH3_PROJECT"] = _project.Folder;
        if (_project.Build.Pack.Output.Length > 0) psi.Environment["ATLASWH3_PACK"] = _project.Resolve(_project.Build.Pack.Output, _paths);
        if (CliExe() is { } cli) psi.Environment["ATLASWH3_CLI"] = cli;
        Log(id, $"> {psi.FileName} {psi.Arguments}  (in {cwd})");

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(id, e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(id, e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var deadline = Stopwatch.StartNew();
        while (!proc.WaitForExit(250))
        {
            if (_cancel.IsCancellationRequested || deadline.Elapsed.TotalSeconds > step.TimeoutSeconds)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                proc.WaitForExit();
                _cancel.ThrowIfCancellationRequested();
                return ([$"timed out after {step.TimeoutSeconds} s"], 0);
            }
        }
        proc.WaitForExit();   // drains the async output readers
        if (proc.ExitCode == 0) return ([], 0);
        if (step.ContinueOnError) { notes.Add($"{WarningPrefix}exit code {proc.ExitCode} (continue on error)"); return ([], 0); }
        return ([$"exit code {proc.ExitCode}"], 0);
    }

    private bool Pack() => Item("pack", notes =>
    {
        var settings = _project.Build.Pack;
        if (settings.Output.Length == 0) return (["no pack output set"], 0);
        var output = _project.Resolve(settings.Output, _paths);
        if (IsInGameData(output) && GameRunning()) return ([$"{GameProcess}.exe is running; close the game to write {Path.GetFileName(output)}"], 0);

        var problems = new List<string>();
        var contents = settings.Contents.Select(c => (Source: _project.Resolve(c.Source, _paths), Path: _project.Expand(c.Path, _paths), c.Optional)).ToList();
        var files = PackBuilder.Collect(contents.Select(c => (c.Source, c.Path)), missing =>
        {
            var optional = contents.Any(c => c.Optional && c.Source.Equals(missing, StringComparison.OrdinalIgnoreCase));
            (optional ? notes : problems).Add($"{(optional ? WarningPrefix : "")}missing {missing}");
        });
        if (problems.Count > 0) return (problems, 0);
        if (files.Count == 0) return (["nothing to pack"], 0);
        _cancel.ThrowIfCancellationRequested();

        PackBuilder.Summary summary;
        if (settings.Mode == PackMode.Merge)
        {
            var basePack = settings.Base.Length > 0 ? _project.Resolve(settings.Base, _paths) : output;
            if (!File.Exists(basePack)) return ([$"merge base pack {basePack} not found"], 0);
            Log("pack", $"merging {files.Count} files into {Path.GetFileName(basePack)} -> {output}");
            summary = PackBuilder.Merge(basePack, output, files, settings.ReplaceDirs.Select(d => _project.Expand(d, _paths)));
            notes.Add($"{summary.Kept} kept, {summary.Replaced} replaced, {summary.Added} added, {summary.Dropped} stale dropped");
        }
        else
        {
            Log("pack", $"packing {files.Count} files -> {output}");
            summary = PackBuilder.New(output, files);
        }
        notes.Add($"{output}: {summary.Files} files, {summary.Bytes / 1048576.0:F1} MB");
        return (summary.Verified ? [] : ["re-read check failed: files missing from the written pack"], summary.Files);
    });

    private bool Install() => Item("install", notes =>
    {
        var pack = _project.Build.Pack.Output.Length > 0 ? _project.Resolve(_project.Build.Pack.Output, _paths) : "";
        if (!File.Exists(pack)) return ([$"no pack to install ({pack}); run Pack first"], 0);
        if (!Directory.Exists(_paths.GameDataDir)) return ([$"game data folder {_paths.GameDataDir} not found"], 0);
        if (IsInGameData(pack)) { notes.Add($"{Path.GetFileName(pack)} is already in the game's data folder"); return ([], 0); }
        if (GameRunning()) return ([$"{GameProcess}.exe is running; close the game to install"], 0);
        var target = Path.Combine(_paths.GameDataDir, Path.GetFileName(pack));
        if (File.Exists(target) && _project.Build.Install.Backup)
        {
            var backupDir = Path.Combine(_paths.OutputRoot, "pack_backups");
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir, Path.GetFileName(target));   // one rolling copy per pack name
            File.Copy(target, backup, overwrite: true);
            notes.Add($"previous pack backed up to {backup}");
        }
        File.Copy(pack, target, overwrite: true);
        Log("install", $"installed {target}");
        return ([], 1);
    });

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs one item with Started/Finished events, timing, logging and exception → failed.</summary>
    private bool Item(string id, Func<List<string>, (List<string> Problems, int Files)> body)
    {
        var sw = Stopwatch.StartNew();
        _events(new Event(id, EventKind.Started));
        Write(id, "start");
        var notes = new List<string>();
        ItemResult result;
        try
        {
            _cancel.ThrowIfCancellationRequested();
            var (problems, files) = body(notes);
            var status = problems.Count > 0 ? "failed" : notes.Any(n => n.StartsWith(WarningPrefix)) ? "warning" : "ok";
            result = new ItemResult(id, status, sw.Elapsed.TotalSeconds, problems, notes, files);
        }
        catch (OperationCanceledException)
        {
            result = new ItemResult(id, "cancelled", sw.Elapsed.TotalSeconds, ["build cancelled"], notes, 0);
        }
        catch (Exception e)
        {
            result = new ItemResult(id, "failed", sw.Elapsed.TotalSeconds, [e.Message], notes, 0);
        }
        lock (_items) _items.Add(result);
        Write(id, $"{result.Status} {result.Seconds:F1} s");
        foreach (var p in result.Problems) Write(id, "! " + p);
        foreach (var n in result.Notes) Write(id, NoteLine(n));
        _events(new Event(id, EventKind.Finished, Result: result));
        return result.Succeeded;
    }

    private CampaignBuildContext Context() => new(_paths, _project.OutputDir(_paths), _ => { })
    {
        AcceptedTileMapIssues = _project.Build.AcceptTileMap.ToHashSet(),
        Cancel = _cancel,
    };

    private IReadOnlyList<string> SelectedSteps(IReadOnlyList<string>? steps) =>
        steps is { Count: > 0 } ? steps
        : _project.Build.Steps.Count > 0 ? _project.Build.Steps
        : CampaignBuildPipeline.NativeSteps.Select(s => s.Name).ToList();

    private void Log(string id, string message)
    {
        Write(id, message);
        _events(new Event(id, EventKind.Log, message));
    }

    private void Write(string id, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {(id.Length > 0 ? $"[{id}] " : "")}{message}";
        lock (this) _log?.WriteLine(line);
    }

    private bool IsInGameData(string file) =>
        Path.GetDirectoryName(Path.GetFullPath(file))!.TrimEnd('\\').Equals(Path.GetFullPath(_paths.GameDataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool GameRunning() => Process.GetProcessesByName(GameProcess).Length > 0;

    /// <summary>The AtlasWH3 CLI next to the running app, for custom steps that call it.</summary>
    private static string? CliExe()
    {
        var here = AppContext.BaseDirectory;
        return new[] { Path.Combine(here, "AtlasWH3.Cli.exe") }.FirstOrDefault(File.Exists);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }

    /// <summary>A note as a log line: "warning: …" as is, else "note: …".</summary>
    public static string NoteLine(string note) => note.StartsWith(WarningPrefix) ? note : "note: " + note;

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
