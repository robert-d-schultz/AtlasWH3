namespace AtlasWH3.Core.Editing;

public interface IUndoable
{
    string Description { get; }
    void Undo();
    void Redo();
}

public sealed class UndoStack
{
    private readonly Stack<IUndoable> _undo = new();
    private readonly Stack<IUndoable> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int Count => _undo.Count;

    public void Push(IUndoable action)
    {
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke();
    }

    public IUndoable? Undo()
    {
        if (!_undo.TryPop(out var action)) return null;
        action.Undo();
        _redo.Push(action);
        Changed?.Invoke();
        return action;
    }

    public IUndoable? Redo()
    {
        if (!_redo.TryPop(out var action)) return null;
        action.Redo();
        _undo.Push(action);
        Changed?.Invoke();
        return action;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
