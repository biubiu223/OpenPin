namespace OpenPin.Annotation.Model;

/// <summary>
/// Items in z-order plus snapshot-based undo/redo. Items are small, so each undo step stores a deep copy of the
/// list taken BEFORE a change (clones keep Id/Version so unchanged visuals are reused after undo).
/// </summary>
internal sealed class AnnotationDocument
{
    public const int MaxUndo = 100;

    private readonly LinkedList<List<AnnotationItem>> _undo = new();
    private readonly Stack<List<AnnotationItem>> _redo = new();
    private string? _coalesceKey;
    private DateTime _coalesceTime;

    public List<AnnotationItem> Items { get; private set; } = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public List<AnnotationItem> Snapshot() => Items.Select(i => i.Clone()).ToList();

    /// <summary>Record the current state as an undo step (call BEFORE mutating).</summary>
    public void Checkpoint() => Push(Snapshot());

    /// <summary>
    /// Like <see cref="Checkpoint"/>, but consecutive calls with the same key within a short time form ONE undo step
    /// (e.g. repeatedly clicking "+" or turning the wheel on a selected item).
    /// </summary>
    public void CheckpointCoalesced(string key)
    {
        var now = DateTime.UtcNow;
        if (_coalesceKey == key && (now - _coalesceTime).TotalMilliseconds < 1500 && _undo.Count > 0)
        {
            _coalesceTime = now;
            _redo.Clear();
            return;
        }
        Checkpoint();
        _coalesceKey = key;
        _coalesceTime = now;
    }

    /// <summary>Record a snapshot taken earlier (before a gesture started) as an undo step.</summary>
    public void Push(List<AnnotationItem> before)
    {
        _undo.AddLast(before);
        if (_undo.Count > MaxUndo) _undo.RemoveFirst();
        _redo.Clear();
        _coalesceKey = null;
    }

    /// <summary>Put a snapshot back without touching the undo history (gesture cancelled).</summary>
    public void Restore(List<AnnotationItem> snapshot) => Items = snapshot;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var prev = _undo.Last!.Value;
        _undo.RemoveLast();
        _redo.Push(Items);
        Items = prev;
        _coalesceKey = null;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.AddLast(Items);
        Items = _redo.Pop();
        _coalesceKey = null;
        return true;
    }

    public AnnotationItem? Find(Guid id) => Items.FirstOrDefault(i => i.Id == id);
}
