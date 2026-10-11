namespace OpenPin.Recording.Codec;

internal enum MouseButtonKind : byte { Left, Right, Middle }

/// <summary>A mouse button press at <see cref="TimeMs"/> (recording clock); position relative to the recorded region.</summary>
internal readonly record struct ClickEvent(double TimeMs, int X, int Y, MouseButtonKind Button);

/// <summary>A wheel notch (Delta: +120 = away from the user / right) at a position relative to the region.</summary>
internal readonly record struct WheelEvent(double TimeMs, int X, int Y, int Delta, bool Horizontal);

/// <summary>A key going down or up (virtual-key code). Auto-repeat produces repeated downs.</summary>
internal readonly record struct KeyEvent(double TimeMs, int Vk, bool Down);

/// <summary>
/// Input events recorded alongside the frames (REC-03 / PRO-03): drawn at playback/export time, so every overlay can be
/// toggled afterwards. Thread-safe appends (hook thread); <see cref="Snapshot"/> returns immutable sorted copies.
/// </summary>
internal sealed class InputLog
{
    private readonly object _gate = new();
    private readonly List<ClickEvent> _clicks = new();
    private readonly List<WheelEvent> _wheels = new();
    private readonly List<KeyEvent> _keys = new();

    public InputLog() { }

    public InputLog(IEnumerable<ClickEvent> clicks, IEnumerable<WheelEvent>? wheels = null, IEnumerable<KeyEvent>? keys = null)
    {
        _clicks.AddRange(clicks);
        if (wheels != null) _wheels.AddRange(wheels);
        if (keys != null) _keys.AddRange(keys);
    }

    public void Add(ClickEvent e) { lock (_gate) _clicks.Add(e); }
    public void Add(WheelEvent e) { lock (_gate) _wheels.Add(e); }
    public void Add(KeyEvent e) { lock (_gate) _keys.Add(e); }

    public IReadOnlyList<ClickEvent> Clicks { get { lock (_gate) return _clicks.ToArray(); } }
    public IReadOnlyList<WheelEvent> Wheels { get { lock (_gate) return _wheels.ToArray(); } }
    public IReadOnlyList<KeyEvent> Keys { get { lock (_gate) return _keys.ToArray(); } }

    /// <summary>True if keystrokes were recorded (keyboard hook was on).</summary>
    public bool HasKeyboard { get; set; }

    /// <summary>Immutable copy sorted by time.</summary>
    public InputLog Snapshot()
    {
        lock (_gate)
        {
            return new InputLog(_clicks.OrderBy(c => c.TimeMs), _wheels.OrderBy(w => w.TimeMs), _keys.OrderBy(k => k.TimeMs))
            {
                HasKeyboard = HasKeyboard,
            };
        }
    }

    public static InputLog Empty { get; } = new();
}
