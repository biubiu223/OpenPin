namespace OpenPin.Capture;

/// <summary>
/// Most-recent-first list with a fixed capacity. Adding an item that already exists moves it to the front
/// (no duplicates). Pure; not thread-safe.
/// </summary>
internal sealed class RingHistory<T>
{
    private readonly List<T> _items = new();
    private readonly IEqualityComparer<T> _comparer;

    public RingHistory(int capacity, IEqualityComparer<T>? comparer = null)
    {
        Capacity = Math.Max(1, capacity);
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public int Capacity { get; private set; }
    public int Count => _items.Count;
    public IReadOnlyList<T> Items => _items;
    public T this[int index] => _items[index];

    public void Add(T item)
    {
        int i = _items.FindIndex(x => _comparer.Equals(x, item));
        if (i >= 0) _items.RemoveAt(i);
        _items.Insert(0, item);
        Trim();
    }

    public void SetCapacity(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        Trim();
    }

    public void Clear() => _items.Clear();

    private void Trim()
    {
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
    }
}

/// <summary>
/// Browsing position in a most-recent-first history: -1 = "live" (nothing loaded), 0 = newest, Count-1 = oldest.
/// </summary>
internal sealed class HistoryCursor
{
    public int Index { get; private set; } = -1;

    /// <summary>Step to an older entry. Returns false if already at the oldest (or the history is empty).</summary>
    public bool Older(int count)
    {
        if (Index + 1 >= count) return false;
        Index++;
        return true;
    }

    /// <summary>Step to a newer entry; stepping past the newest returns to -1 (live). False if already live.</summary>
    public bool Newer()
    {
        if (Index < 0) return false;
        Index--;
        return true;
    }

    public void Reset() => Index = -1;
}
