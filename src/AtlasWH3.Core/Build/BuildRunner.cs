using System.Diagnostics;
using System.Text.Json;
using AtlasWH3.Core.Campaign;
using AtlasWH3.Core.Campaign.TileMapCheck;

namespace AtlasWH3.Core.Build;

/// <summary>Build segments, in run order.</summary>
public enum BuildSegment { Validate, Compile, Custom, Pack }

/// <summary>
/// Runs a <see cref="BuildProject"/>'s build: Validate → custom "beforeCompile" → Compile (native steps) → "afterCompile"
/// → Pack (the compiled files into the project's pack) → "afterPack". Stops at the first failure. Used by the GUI's Build window, the CLI
/// <c>build</c> command and the MCP <c>build_project</c> tool.
/// </summary>
public sealed class BuildRunner
{
    /// <summary>Item ids: <c>validate</c>, <c>compile:&lt;step&gt;</c>, <c>custom:&lt;name&gt;</c>, <c>pack</c>.</summary>
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
            BuildSegment.Pack => profile.WritePack,
            _ => true,
        };

        Directory.CreateDirectory(LogDir);
        var logFile = Path.Combine(LogDir, $"build_{Safe(_project.Name.Length > 0 ? _project.Name : _project.Map)}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        using (_log = new StreamWriter(logFile) { AutoFlush = true })
        {
            Write("", $"AtlasWH3 build: {_project.FilePath ?? _project.Name}  map {_project.Map}  output {_project.OutputDir(_paths)}");
            var ok = true;
            if (ok && Want(BuildSegment.Validate)) ok = Validate(request.Steps);
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.BeforeCompile, request);
            if (ok && Want(BuildSegment.Compile)) ok = Compile(request.Steps);
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.AfterCompile, request);
            if (ok && Want(BuildSegment.Pack)) ok = Pack();
            if (ok && Want(BuildSegment.Custom)) ok = RunCustom(CustomStepStage.AfterPack, request);
            var report = new Report(_project.Name, _project.Map, sw.Elapsed.TotalSeconds, ok && !_cancel.IsCancellationRequested, _items, logFile);
            Write("", $"{(report.Succeeded ? "build succeeded" : _cancel.IsCancellationRequested ? "build cancelled" : "build FAILED")} in {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(Path.ChangeExtension(logFile, ".json"), JsonSerializer.Serialize(report, BuildProject.Json));
            return report;
        }
    }

    // ------------------------------------------------------------------ segments

    /// <summary>The input checks of the steps Compile will run (<paramref name="steps"/>, else the profile's).</summary>
    private bool Validate(IReadOnlyList<string>? steps) => Item("validate", notes =>
    {
        var problems = new List<string>();
        var ctx = Context();
        // Steps that read another selected step's output are checked when they run (their inputs don't exist yet).
        var selected = SelectedSteps(steps).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in selected)
        {
            var step = CampaignBuildPipeline.Find(name);
            if (step.DependsOn.Any(selected.Contains)) continue;
            problems.AddRange(step.CheckInputs(ctx).Select(p => $"{name}: {p}"));
        }
        _cancel.ThrowIfCancellationRequested();
        if (selected.Contains("tile_list") && File.Exists(_paths.TileMapPng))
        {
            var report = TileMapValidator.Run(_paths, new() { Log = m => Log("validate", m) });
            foreach (var f in report.Findings.Where(f => f.Severity == TileMapFinding.Warning).Take(20))
                notes.Add($"{WarningPrefix}tile map {f.Code}: {f.Message}");
            foreach (var f in report.Findings.Where(f => f.Severity == TileMapFinding.Error))
                if (_project.Build.AcceptTileMap.Contains(f.Code)) notes.Add($"accepted tile map {f.Code}: {f.Message}");
                else problems.Add($"tile map {f.Code}: {f.Message} (fix it, or tick it under Project settings > Accepted tile-map errors)");
        }
        return (problems, 0);
    });

    private bool Compile(IReadOnlyList<string>? steps)
    {
        var selected = SelectedSteps(steps);
        var outDir = _project.OutputDir(_paths);

        var ctx = Context();
        var manifestFile = BuildManifest.FileFor(_paths, _project.Map, outDir);
        var manifest = BuildManifest.Load(manifestFile, outDir);
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
                        if (o.Status == "ok" && o.Result is { } done)
                            lock (manifest) { manifest.Set(e.Step, done.Written, done.Owns); manifest.Save(manifestFile); }
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
        if (_project.PackOutput(_paths) is { } packOut) psi.Environment["ATLASWH3_PACK"] = packOut;
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

    /// <summary>Writes the compiled files into the project's pack, and the event-area pieces into the pieces pack when
    /// the project has one (<see cref="BuildProfile.PiecesPackFile"/>).</summary>
    private bool Pack() => Item("pack", notes =>
    {
        if (_project.PackOutput(_paths) is not { } target) return (["no pack set (Project settings > Pack)"], 0);
        var piecesTarget = _project.PiecesPackOutput(_paths);
        var outDir = _project.OutputDir(_paths);
        var manifest = BuildManifest.Load(BuildManifest.FileFor(_paths, _project.Map, outDir), outDir);
        if (manifest.Steps.Count == 0) return ([$"nothing compiled into {manifest.Output} yet; run Compile first"], 0);
        var files = new Dictionary<string, (string Rel, string Disk)>();
        var problems = new List<string>();
        foreach (var rel in manifest.Files)
        {
            var disk = Path.Combine(manifest.Output, rel.Replace('/', '\\'));
            if (File.Exists(disk)) files[Formats.Packs.PackFile.Normalize(rel)] = (rel, disk);
            else problems.Add($"compiled file {disk} is gone; run Compile again");
        }
        if (problems.Count > 0) return (problems.Take(20).ToList(), 0);
        if (files.Count == 0) return (["nothing to pack"], 0);
        if (GameRunning() && new[] { target, piecesTarget }.Any(t => t is not null && IsInGameData(t)))
            return ([$"{GameProcess}.exe is running; close the game to write {Path.GetFileName(target)}"], 0);

        var owned = manifest.Owned.ToList();
        if (piecesTarget is null) return ([], WritePack(target, files, owned, notes));
        var pieces = files.Where(f => IsPieces(f.Value.Rel)).ToDictionary();
        var main = files.Where(f => !IsPieces(f.Value.Rel)).ToDictionary();
        var written = WritePack(target, main, owned.Where(o => !IsPieces(o)), notes);
        if (pieces.Count == 0) { notes.Add($"{WarningPrefix}no event-area pieces to write into {Path.GetFileName(piecesTarget)}"); return ([], written); }
        return ([], written + WritePack(piecesTarget, pieces, owned.Where(IsPieces), notes));
    });

    /// <summary>The event-area pieces: the map's pieces\ and everything in the devastated map's folder (the compiled
    /// output's other terrain\campaigns folder, and its campaign_maps folder, which the devastation step owns).</summary>
    private bool IsPieces(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/');
        if (parts.Length >= 3 && parts[0].Equals("campaign_maps", StringComparison.OrdinalIgnoreCase))
            return !parts[1].Equals(_project.Map, StringComparison.OrdinalIgnoreCase);
        if (parts.Length < 4 || !parts[0].Equals("terrain", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("campaigns", StringComparison.OrdinalIgnoreCase)) return false;
        return !parts[2].Equals(_project.Map, StringComparison.OrdinalIgnoreCase) || parts[3].Equals("pieces", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Merges <paramref name="files"/> into <paramref name="target"/> (its other files kept, the steps' stale
    /// <paramref name="owned"/> files dropped), or makes a new pack when there is none yet. Written beside it and swapped
    /// in, after the backup. Returns the pack's file count.</summary>
    private int WritePack(string target, IReadOnlyDictionary<string, (string Rel, string Disk)> files, IEnumerable<string> owned, List<string> notes)
    {
        _cancel.ThrowIfCancellationRequested();
        var name = Path.GetFileName(target);
        // written beside the target, then swapped, so a failed write never leaves a half pack
        var temp = target + ".tmp";
        PackBuilder.Summary summary;
        if (File.Exists(target))
        {
            if (_project.Build.BackupPack)
            {
                var backupDir = Path.Combine(_paths.OutputRoot, "pack_backups");
                Directory.CreateDirectory(backupDir);
                var backup = Path.Combine(backupDir, name);   // one rolling copy per pack name
                File.Copy(target, backup, overwrite: true);
                notes.Add($"{name} as it was: {backup}");
            }
            Log("pack", $"writing {files.Count} files into {target}");
            summary = PackBuilder.Merge(target, temp, files, owned: owned);
            notes.Add($"{name}: {summary.Kept} kept, {summary.Replaced} replaced, {summary.Added} added, {summary.Dropped} stale dropped");
        }
        else
        {
            Log("pack", $"new pack {target} with {files.Count} files");
            summary = PackBuilder.New(temp, files);
        }
        if (!summary.Verified)
        {
            File.Delete(temp);
            throw new InvalidDataException($"re-read check failed: files missing from the written pack; {name} was not changed");
        }
        File.Move(temp, target, overwrite: true);
        notes.Add($"{target}: {summary.Files} files, {summary.Bytes / 1048576.0:F1} MB");
        return summary.Files;
    }

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
        PatchMask = _project.Build.PatchMask,
        LookupLastRegions = _project.Build.LookupLast.Select(e => LookupStep.IsFile(e) ? _project.Resolve(e, _paths) : e).ToList(),
        Cancel = _cancel,
    };

    private IReadOnlyList<string> SelectedSteps(IReadOnlyList<string>? steps) =>
        steps is { Count: > 0 } ? steps
        : _project.Build.Steps.Count > 0 ? _project.Build.Steps
        : CampaignBuildPipeline.AllSteps.Select(s => s.Name).ToList();

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

    private bool IsInGameData(string file) => PathEquals(Path.GetDirectoryName(Path.GetFullPath(file))!, _paths.GameDataDir);

    private static bool PathEquals(string a, string b) =>
        Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    internal static bool IsUnder(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var r = Path.GetFullPath(root).TrimEnd('\\', '/');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool GameRunning() => Process.GetProcessesByName(GameProcess).Length > 0;

    /// <summary>The AtlasWH3 CLI next to the running app, for custom steps that call it.</summary>
    private static string? CliExe()
    {
        var here = AppContext.BaseDirectory;
        return new[] { Path.Combine(here, "AtlasWH3.Cli.exe") }.FirstOrDefault(File.Exists);
    }

    /// <summary>A note as a log line: "warning: …" as is, else "note: …".</summary>
    public static string NoteLine(string note) => note.StartsWith(WarningPrefix) ? note : "note: " + note;

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
