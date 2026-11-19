using System.Collections.Concurrent;

namespace AtlasWH3.Core.Campaign;

/// <summary>
/// What one step of a build hands to another step of the same build (<see cref="CampaignBuildPipeline"/> makes one per
/// run): a step publishes a value under its name, and a step that would otherwise redo that work waits for it, when the
/// publishing step is in the build. A step that finishes without publishing (failed, blocked, cancelled) leaves null,
/// and the waiting step does the work itself.
/// </summary>
public sealed class BuildShare
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _slots = new(StringComparer.OrdinalIgnoreCase);

    private TaskCompletionSource<object?> Slot(string step) =>
        _slots.GetOrAdd(step, _ => new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));

    public void Publish(string step, object value) => Slot(step).TrySetResult(value);

    /// <summary>The step has finished: a step still waiting for it gets what it published, else null.</summary>
    internal void Finished(string step) => Slot(step).TrySetResult(null);

    /// <summary>What <paramref name="step"/> publishes (waited for), or null when it publishes none.</summary>
    public T? Take<T>(string step, CancellationToken cancel) where T : class =>
        Slot(step).Task.WaitAsync(cancel).GetAwaiter().GetResult() as T;
}
