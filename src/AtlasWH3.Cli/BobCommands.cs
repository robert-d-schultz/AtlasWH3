using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtlasWH3.Core;
using AtlasWH3.Core.Bob;

/// <summary>
/// bob-* commands: BOB headless, always in an isolated scratch kit (never the user's kit).
///  - bob-actions: the WH3 campaign actions and the step each belongs to
///  - bob-scratch --maps a,b [--scratch dir] [--outputs]: create or refresh the scratch kit from the kit (--ak)
///  - bob-run --maps a,b [--scratch dir] [--timeout min] [--config-only] [--fresh]: one silent BOB run (the default
///    actions: masks, Terry file, Devastation pieces) of the maps in the scratch kit (--fresh: clear their outputs first)
/// </summary>
static class BobCommands
{
    public static readonly HashSet<string> Names = ["bob-actions", "bob-scratch", "bob-run"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(ProjectPaths paths, string command, string[] a)
    {
        try
        {
            JsonNode result = command switch
            {
                "bob-actions" => new JsonArray(BobActions.All.Select(x => (JsonNode)new JsonObject
                    { ["action"] = x.Name, ["step"] = x.Step, ["headless"] = x.Default, ["processor"] = x.Processor, ["writes"] = x.Writes }).ToArray()),
                "bob-scratch" => Scratch(paths, a),
                "bob-run" => RunBob(paths, a).GetAwaiter().GetResult(),
                _ => throw new ArgumentException(command),
            };
            Console.WriteLine(result.ToJsonString(Indented));
            return result["ok"]?.GetValue<bool>() == false ? 1 : 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.WriteLine(new JsonObject { ["error"] = ex.Message, ["type"] = ex.GetType().Name }.ToJsonString(Indented));
            return 1;
        }
    }

    private static List<string> Maps(string[] a) =>
        (Option(a, "--maps") ?? throw new ArgumentException("--maps a,b needed")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string ScratchRoot(ProjectPaths paths, string[] a) => Option(a, "--scratch") ?? ScratchKit.DefaultRoot(paths.AssemblyKitRoot);

    private static JsonNode Scratch(ProjectPaths paths, string[] a)
    {
        var r = ScratchKit.Create(paths.AssemblyKitRoot, ScratchRoot(paths, a),
            new ScratchKit.Options { Maps = Maps(a), IncludeOutputs = a.Contains("--outputs") }, m => Console.Error.WriteLine(m));
        return new JsonObject { ["scratch"] = r.Root, ["copied"] = r.Copied, ["unchanged"] = r.Unchanged, ["gb_copied"] = Math.Round(r.BytesCopied / 1e9, 2) };
    }

    private static async Task<JsonNode> RunBob(ProjectPaths paths, string[] a)
    {
        var root = ScratchRoot(paths, a);
        if (!ScratchKit.IsScratch(root)) throw new InvalidOperationException($"{root} is not a scratch kit: run bob-scratch first");
        var maps = Maps(a);
        if (a.Contains("--fresh")) foreach (var m in maps) ScratchKit.ClearOutputs(root, m);
        var request = BobActions.DefaultRun(maps, TimeSpan.FromMinutes(double.Parse(Option(a, "--timeout") ?? "120", CultureInfo.InvariantCulture)));
        var runner = new BobRunner(root);
        if (a.Contains("--config-only"))
            return new JsonObject { ["config"] = runner.WriteConfig(request), ["exe"] = runner.Exe, ["config_name"] = request.ConfigName };
        var result = await runner.RunAsync(request, m => Console.Error.WriteLine(m));
        return new JsonObject
        {
            ["ok"] = result.Ok, ["exit_code"] = result.ExitCode, ["timed_out"] = result.TimedOut, ["guarded"] = result.Guarded,
            ["selected"] = result.Selected, ["seconds"] = Math.Round(result.Duration.TotalSeconds, 1),
            ["actions"] = new JsonArray(result.Statuses.Select(s => (JsonNode)new JsonObject { ["action"] = s.Action, ["input"] = s.Input, ["status"] = s.Status }).ToArray()),
            ["error_log"] = result.ErrorLog.Length > 4000 ? result.ErrorLog[..4000] : result.ErrorLog,
        };
    }

    private static string? Option(string[] a, string name)
    {
        var i = Array.FindIndex(a, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
