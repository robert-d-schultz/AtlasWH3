using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AtlasWH3.Core.Bob;

/// <summary>
/// Runs BOB (the kit's bob.modder.x64.exe) headless, the way Terry's "Process with BOB" does:
/// binaries\BOB\&lt;name&gt;_configuration.xml with &lt;silent&gt;1&lt;/silent&gt; and the input files in
/// &lt;selected_consumers&gt;, started as <c>bob.modder.x64.exe /configuration:&lt;name&gt; /nosplashscreen
/// /dont_stop_on_error</c> from binaries\. WH3's BOB has no action filter: a silent run selects the consumers' default
/// actions (for a campaign .terry, <see cref="BobActions.DefaultGroup"/>; docs/bob_wh3.md). Run it in a
/// <see cref="ScratchKit"/>, never in the user's kit: BOB writes its outputs, logs and configuration there.
/// A guard reads bob.log while BOB runs and kills it as soon as it selected a different number of actions than
/// expected, so a wrong consumer can never start an unrelated (long) build.
/// </summary>
public sealed class BobRunner(string kitRoot)
{
    public const string ExeName = "bob.modder.x64.exe";
    public static readonly string[] LogFiles = ["bob.log", "bob_error.log", "bob_warnings.log", "bob_startup_error.log", "bob_db.log"];

    public string KitRoot { get; } = Path.GetFullPath(kitRoot);
    public string Binaries => Path.Combine(KitRoot, "binaries");
    public string Exe => Path.Combine(Binaries, ExeName);

    /// <summary>One BOB launch on these consumer files.</summary>
    public sealed record Request
    {
        /// <summary>The action names expected to run (checked against bob.log; WH3's BOB takes no action filter).</summary>
        public required IReadOnlyList<string> Actions { get; init; }
        /// <summary>Kit paths in BOB's form, e.g. <c>&lt;raw&gt;/terrain/campaigns/m/m.terry</c>.</summary>
        public required IReadOnlyList<string> Consumers { get; init; }
        /// <summary>Folders BOB scans (<c>&lt;raw&gt;/terrain/campaigns/m/</c>); default: each consumer's folder.</summary>
        public IReadOnlyList<string>? Directories { get; init; }
        public IReadOnlyList<string> Processors { get; init; } = ["Terrain"];
        /// <summary>How many actions BOB must select; default: one per action name.</summary>
        public int? ExpectedSelected { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(2);
        public string ConfigName { get; init; } = "atlaswh3_run";
    }

    public sealed record Result(IReadOnlyList<string> Actions, int? ExitCode, bool TimedOut, bool Guarded, int? Selected,
                                TimeSpan Duration, string Log, string ErrorLog, IReadOnlyList<ActionStatus> Statuses)
    {
        /// <summary>Exit code 0, every expected action finished (warnings allowed), nothing in bob_error.log.</summary>
        public bool Ok => !TimedOut && !Guarded && ExitCode == 0 && ErrorLog.Trim().Length == 0
                          && Statuses.Count > 0 && Statuses.All(s => s.Status is "Finished" or "FinishedWithWarnings")
                          && Actions.All(a => Statuses.Any(s => s.Action.StartsWith(a, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>An action header of bob.log: "=== Terrain / Campaign Trees (from …) (STATUS: Finished) ===".</summary>
    public sealed record ActionStatus(string Action, string Input, string Status);

    /// <summary>The kit has a BOB.</summary>
    public bool Exists => File.Exists(Exe);

    /// <summary>bob.modder.x64.exe processes running from this kit (BOB refuses to share a kit; so do we).</summary>
    public IReadOnlyList<Process> Running() =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))
            .Where(p => { try { return string.Equals(p.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
            .ToList();

    public string WriteConfig(Request request)
    {
        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;");
        var dirs = request.Directories ?? request.Consumers.Select(c => c[..(c.LastIndexOf('/') + 1)]).Distinct().ToList();
        var sb = new StringBuilder("<bob_configuration>\n    <processors>\n");
        foreach (var p in request.Processors) sb.Append($"        <processor>{Esc(p)}</processor>\n");
        sb.Append("    </processors>\n    <directories>\n");
        foreach (var d in dirs) sb.Append($"        <directory>{Esc(d)}</directory>\n");
        sb.Append("    </directories>\n    <global_rules/>\n    <retail>1</retail>\n    <silent>1</silent>\n    <show_errors>0</show_errors>\n")
          .Append("    <no_progress>1</no_progress>\n    <fail_on_assert>0</fail_on_assert>\n    <scan_perforce>0</scan_perforce>\n")
          .Append("    <merge_for_checkin_mode>3</merge_for_checkin_mode>\n    <keep_output>1</keep_output>\n    <load_asset_graph>0</load_asset_graph>\n")
          .Append("    <clean_asset_graph>0</clean_asset_graph>\n    <get_latest>0</get_latest>\n    <selected_providers/>\n    <selected_consumers>\n");
        foreach (var c in request.Consumers) sb.Append($"        <entry>{Esc(c)}</entry>\n");
        sb.Append("    </selected_consumers>\n</bob_configuration>\n");
        var path = Path.Combine(Binaries, "BOB", request.ConfigName + "_configuration.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static readonly Regex SelectedLine = new(@"(\d+) action\(s\) were selected for execution", RegexOptions.Compiled);
    // GUI runs log "(from <input>)", silent runs "(<input>)"; action names may hold parentheses ("Color Overlay (Sea)")
    // or a progress note ("Devastation pieces: BMDs: 254/254")
    private static readonly Regex HeaderLine = new(@"^=== (?<proc>[^/=]+?) / (?<action>.+?) \((?:from )?(?<input>[a-zA-Z]:/[^)]*)\) \(STATUS: (?<status>\w+)\) ===",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static int? SelectedCount(string log) => SelectedLine.Match(log) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    public static IReadOnlyList<ActionStatus> Statuses(string log) =>
        HeaderLine.Matches(log).Select(m => new ActionStatus(m.Groups["action"].Value.Trim(), m.Groups["input"].Value, m.Groups["status"].Value)).ToList();

    /// <summary>Starts BOB with <paramref name="request"/> and waits for it. <paramref name="started"/> gets the process
    /// as soon as it runs (for a Frida attach or a progress display).</summary>
    public async Task<Result> RunAsync(Request request, Action<string>? log = null, Action<Process>? started = null, CancellationToken cancel = default)
    {
        if (!Exists) throw new FileNotFoundException($"BOB not found: {Exe}");
        if (Running().Count > 0) throw new InvalidOperationException($"BOB is already running from {Binaries}");
        WriteConfig(request);
        foreach (var f in LogFiles)
            try { File.Delete(Path.Combine(Binaries, f)); } catch (IOException) { }
        var expected = request.ExpectedSelected ?? request.Actions.Count;
        var sw = Stopwatch.StartNew();
        using var process = Process.Start(new ProcessStartInfo(Exe, [$"/configuration:{request.ConfigName}", "/nosplashscreen", "/dont_stop_on_error"])
        {
            WorkingDirectory = Binaries, UseShellExecute = false, CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("BOB did not start");
        started?.Invoke(process);
        log?.Invoke($"BOB started (pid {process.Id}): {string.Join(", ", request.Actions)}");

        var guarded = false;
        int? selected = null;
        var timedOut = false;
        var lastLength = 0L;
        while (!process.HasExited)
        {
            try { await process.WaitForExitAsync(cancel).WaitAsync(TimeSpan.FromSeconds(2), cancel); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { Kill(process); throw; }
            var text = ReadLog("bob.log");
            if (selected is null && SelectedCount(text) is { } n)
            {
                selected = n;
                log?.Invoke($"{n} action(s) selected");
                if (n != expected)
                {
                    guarded = true;
                    log?.Invoke($"guard: BOB selected {n} action(s), {expected} were asked for; stopping it");
                    Kill(process);
                    break;
                }
            }
            if (text.Length != lastLength)
            {
                foreach (var s in Statuses(text[(int)Math.Min(lastLength, text.Length)..]))
                    log?.Invoke($"{s.Action}: {s.Status}");
                lastLength = text.Length;
            }
            if (sw.Elapsed > request.Timeout)
            {
                timedOut = true;
                log?.Invoke($"timeout after {request.Timeout}: stopping BOB");
                Kill(process);
                break;
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        var finalLog = ReadLog("bob.log");
        return new Result(request.Actions, process.HasExited ? process.ExitCode : null, timedOut, guarded, selected ?? SelectedCount(finalLog),
            sw.Elapsed, finalLog, ReadLog("bob_error.log"), Statuses(finalLog));
    }

    private string ReadLog(string name)
    {
        var path = Path.Combine(Binaries, name);
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    /// <summary>A kit file in BOB's notation: <c>&lt;raw&gt;/…</c> or <c>&lt;working&gt;/…</c>, forward slashes.</summary>
    public static string KitPath(string tree, params string[] parts) => $"<{tree}>/" + string.Join('/', parts);
}
