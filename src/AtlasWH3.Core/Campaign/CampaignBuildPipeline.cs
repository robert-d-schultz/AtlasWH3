using System.Collections.Concurrent;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// Runs the native replacements for BOB's campaign actions, straight from disk (no pack import between steps).
/// Steps wait only for the dependencies that were selected; independent steps run in parallel.
/// </summary>
public sealed class CampaignBuildPipeline
{
    public sealed record StepOutcome(string Step, string Status, StepResult? Result, IReadOnlyList<string> Problems);

    /// <summary>All WH3 steps, in dependency order (docs/atlaswh3_plan.md §2 and Phases 3 and 6). A
    /// <see cref="PendingStep"/> is not native yet (none is left).</summary>
    public static IReadOnlyList<ICampaignBuildStep> AllSteps { get; } =
    [
        new HeightmapsStep(),
        new TileListStep(),
        new Trees.TreesStep(),
        new GlobalMapStep(),
        new MasksStep(),
        new Rivers.RiversStep(),
        new Props.GlobalPropsStep(),
        new DevastationPiecesStep(),
        new LookupStep(),
        new CameraHeightmapStep(),
        new EnvironmentStep(),
        new AiPathfinding.AiPathfindingStep(),
    ];

    /// <summary>Steps that run when none are named: every one that is native.</summary>
    public static IEnumerable<ICampaignBuildStep> NativeSteps => AllSteps.Where(s => s is not PendingStep);

    public static ICampaignBuildStep Find(string name) =>
        AllSteps.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown step '{name}'. Steps: {string.Join(", ", AllSteps.Select(s => s.Name))}");

    /// <summary>Progress for a UI: a step started, logged a line, or finished (<see cref="StepOutcome"/> set).</summary>
    public sealed record StepEvent(string Step, StepEventKind Kind, string? Message = null, StepOutcome? Outcome = null);
    public enum StepEventKind { Started, Log, Finished }

    /// <summary>Raised from worker threads (steps run in parallel); handlers must marshal to their own thread.</summary>
    public Action<StepEvent>? Progress { get; init; }

    public IReadOnlyList<StepOutcome> Run(CampaignBuildContext ctx, IEnumerable<string>? stepNames = null)
    {
        var steps = stepNames is null ? NativeSteps.ToList() : stepNames.Select(Find).Distinct().ToList();
        var selected = steps.Select(s => s.Name).ToHashSet();
        var outcomes = new ConcurrentDictionary<string, StepOutcome>();
        var tasks = new Dictionary<string, Task>();

        void Finish(StepOutcome o)
        {
            outcomes[o.Step] = o;
            Progress?.Invoke(new StepEvent(o.Step, StepEventKind.Finished, Outcome: o));
        }

        // AllSteps is in dependency order, so every dependency's task exists before its dependents are created.
        foreach (var step in AllSteps.Where(s => selected.Contains(s.Name)))
        {
            var deps = step.DependsOn.Where(selected.Contains).Select(d => tasks[d]).ToArray();
            tasks[step.Name] = Task.WhenAll(deps).ContinueWith(_ =>
            {
                if (ctx.Cancel.IsCancellationRequested)
                {
                    Finish(new StepOutcome(step.Name, "cancelled", null, ["build cancelled"]));
                    return;
                }
                var failedDeps = step.DependsOn.Where(d => outcomes.TryGetValue(d, out var o) && o.Status != "ok").ToList();
                if (failedDeps.Count > 0)
                {
                    Finish(new StepOutcome(step.Name, "skipped", null, [$"dependency failed: {string.Join(", ", failedDeps)}"]));
                    return;
                }
                Progress?.Invoke(new StepEvent(step.Name, StepEventKind.Started));
                void Log(string m)
                {
                    ctx.Log($"[{step.Name}] {m}");
                    Progress?.Invoke(new StepEvent(step.Name, StepEventKind.Log, m));
                }
                var stepCtx = new CampaignBuildContext(ctx.Paths, ctx.TargetRoot, Log)
                {
                    AcceptedTileMapIssues = ctx.AcceptedTileMapIssues, Cancel = ctx.Cancel,
                    PatchMask = ctx.PatchMask, CampaignMapName = ctx.CampaignMapName, HeightMapBlocks = ctx.HeightMapBlocks,
                    DevastatedMap = ctx.DevastatedMap, LookupLastRegions = ctx.LookupLastRegions,
                };
                var problems = step.CheckInputs(stepCtx);
                if (problems.Count > 0)
                {
                    Finish(new StepOutcome(step.Name, "blocked", null, problems));
                    return;
                }
                try
                {
                    Log("start");
                    Finish(new StepOutcome(step.Name, "ok", step.Run(stepCtx), []));
                }
                catch (Exception e) when (ctx.Cancel.IsCancellationRequested && e is OperationCanceledException or AggregateException)
                {
                    Finish(new StepOutcome(step.Name, "cancelled", null, ["build cancelled"]));
                }
                catch (Exception e)
                {
                    Finish(new StepOutcome(step.Name, "failed", null, [e.Message]));
                }
            }, TaskScheduler.Default);
        }
        Task.WaitAll([.. tasks.Values]);
        return AllSteps.Where(s => outcomes.ContainsKey(s.Name)).Select(s => outcomes[s.Name]).ToList();
    }

    /// <summary><paramref name="names"/> plus every step they depend on (transitively), in build order.</summary>
    public static IReadOnlyList<string> WithDependencies(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string n)
        {
            if (!wanted.Add(n)) return;
            foreach (var d in Find(n).DependsOn) Add(d);
        }
        foreach (var n in names) Add(n);
        return AllSteps.Where(s => wanted.Contains(s.Name)).Select(s => s.Name).ToList();
    }

    /// <summary>Pre-flight report for every step (what <c>bob_diagnose</c> used to check).</summary>
    public static IReadOnlyList<(string Step, bool Native, IReadOnlyList<string> Problems)> Diagnose(CampaignBuildContext ctx) =>
        AllSteps.Select(s => (s.Name, s is not PendingStep, s.CheckInputs(ctx))).ToList();
}
