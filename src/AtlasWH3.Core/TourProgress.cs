namespace AtlasWH3.Core;

/// <summary>
/// Where a window walkthrough is: the steps that can be shown (a step whose panel is hidden, e.g. a developer-only menu,
/// is left out up front so "Step n of N" stays right) and the current one. The UI side is the app's Walkthrough.
/// </summary>
public sealed class TourProgress
{
    /// <summary>Indices (into the tour's full step list) of the steps that are shown, in order.</summary>
    public IReadOnlyList<int> Steps { get; }
    public int Position { get; private set; }

    /// <summary><paramref name="available"/>[i]: step i can be shown.</summary>
    public TourProgress(IReadOnlyList<bool> available)
    {
        Steps = Enumerable.Range(0, available.Count).Where(i => available[i]).ToList();
    }

    public int Count => Steps.Count;
    public bool IsEmpty => Steps.Count == 0;
    /// <summary>The current step's index in the full list.</summary>
    public int Current => Steps[Position];
    public bool IsFirst => Position == 0;
    public bool IsLast => Position >= Steps.Count - 1;
    public string Label => $"Step {Position + 1} of {Steps.Count}";

    /// <summary>Moves to the next step; false at the last one (the tour is done).</summary>
    public bool Next()
    {
        if (IsLast) return false;
        Position++;
        return true;
    }

    /// <summary>Moves back one step; false at the first.</summary>
    public bool Back()
    {
        if (IsFirst) return false;
        Position--;
        return true;
    }
}
