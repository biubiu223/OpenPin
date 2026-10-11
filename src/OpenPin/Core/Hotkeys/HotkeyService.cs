using System.Windows.Input;
using System.Windows.Interop;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Core.Hotkeys;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window. Must be created on the UI thread.
/// Note: while a hotkey is registered, the key combination is swallowed system-wide — including inside
/// OpenPin's own windows. Modules that want e.g. Ctrl+1/Ctrl+2 inside the capture overlay receive them through
/// <see cref="Actions.ActionRegistry"/> → <c>IModalSession.TryHandleAction</c>.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _idToAction = new();
    private readonly Dictionary<string, Hotkey> _bindings = new();
    private int _nextId = 0x1000;
    private bool _suspended;

    /// <summary>Raised on the UI thread with the action id.</summary>
    public event Action<string>? Triggered;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("OpenPinHotkeySink")
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Currently applied (action → hotkey) map, including ones that failed to register.</summary>
    public IReadOnlyDictionary<string, Hotkey> Bindings => _bindings;

    /// <summary>When true all hotkeys are unregistered (tray "Disable hotkeys").</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            if (value) UnregisterAll(); else RegisterAll();
        }
    }

    /// <summary>Replaces all bindings. Returns the action ids whose hotkey could not be registered (conflict).</summary>
    public IReadOnlyList<string> ApplyBindings(IReadOnlyDictionary<string, string> bindings)
    {
        UnregisterAll();
        _bindings.Clear();
        foreach (var (action, text) in bindings)
            if (Hotkey.TryParse(text, out var hk)) _bindings[action] = hk;
        return _suspended ? Array.Empty<string>() : RegisterAll();
    }

    /// <summary>Checks whether a hotkey is currently free (not used by another app). Does not keep it registered.</summary>
    public bool IsAvailable(Hotkey hk)
    {
        if (_bindings.ContainsValue(hk)) return true; // ours
        const int probeId = 0x0FFF;
        if (!RegisterHotKey(_source.Handle, probeId, ToNative(hk.Modifiers), (uint)KeyInterop.VirtualKeyFromKey(hk.Key)))
            return false;
        UnregisterHotKey(_source.Handle, probeId);
        return true;
    }

    private List<string> RegisterAll()
    {
        var failed = new List<string>();
        foreach (var (action, hk) in _bindings)
        {
            if (hk.IsEmpty) continue;
            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, ToNative(hk.Modifiers) | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(hk.Key)))
                _idToAction[id] = action;
            else
            {
                failed.Add(action);
                Log.Warn($"Hotkey {hk} for '{action}' could not be registered (in use by another program?)");
            }
        }
        return failed;
    }

    private void UnregisterAll()
    {
        foreach (var id in _idToAction.Keys) UnregisterHotKey(_source.Handle, id);
        _idToAction.Clear();
    }

    private static uint ToNative(ModifierKeys m)
    {
        uint r = 0;
        if (m.HasFlag(ModifierKeys.Alt)) r |= MOD_ALT;
        if (m.HasFlag(ModifierKeys.Control)) r |= MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Shift)) r |= MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) r |= MOD_WIN;
        return r;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _idToAction.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { Triggered?.Invoke(action); }
            catch (Exception ex) { Log.Error($"Hotkey action '{action}' failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
