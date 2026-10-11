using System.Runtime.InteropServices;
using OpenPin.Core;

namespace OpenPin.Recording.Capture;

/// <summary>
/// Low-level mouse / keyboard hooks (WH_MOUSE_LL / WH_KEYBOARD_LL) on a dedicated thread with its own message loop.
/// Observes only — every event is passed on with CallNextHookEx and nothing is ever injected. Callbacks run on the hook
/// thread and must be fast (they just append to a list). Installed only while a recording is running.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int X, Y; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    /// <summary>Mouse button pressed: screen position (physical px), button (0 left, 1 right, 2 middle).</summary>
    public event Action<int, int, int>? MouseDown;
    /// <summary>Wheel: screen position, delta (+120 up/right), horizontal.</summary>
    public event Action<int, int, int, bool>? Wheel;
    /// <summary>Key: virtual key, down.</summary>
    public event Action<int, bool>? Key;

    private readonly bool _mouse, _keyboard;
    private readonly HookProc _mouseProc, _keyProc; // keep delegates alive while hooked
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook, _keyHook;

    public InputHooks(bool mouse, bool keyboard)
    {
        _mouse = mouse;
        _keyboard = keyboard;
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
    }

    public bool MouseHooked => _mouseHook != IntPtr.Zero;
    public bool KeyboardHooked => _keyHook != IntPtr.Zero;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "OpenPin input hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        if (_mouse)
        {
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero) Log.Warn($"WH_MOUSE_LL failed: {Marshal.GetLastWin32Error()}");
        }
        if (_keyboard)
        {
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            if (_keyHook == IntPtr.Zero) Log.Warn($"WH_KEYBOARD_LL failed: {Marshal.GetLastWin32Error()}");
        }
        _ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { /* hooks are called from inside GetMessage */ }
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
            _mouseHook = _keyHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                int msg = (int)wParam;
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
                {
                    var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    switch (msg)
                    {
                        case WM_LBUTTONDOWN: MouseDown?.Invoke(s.X, s.Y, 0); break;
                        case WM_RBUTTONDOWN: MouseDown?.Invoke(s.X, s.Y, 1); break;
                        case WM_MBUTTONDOWN: MouseDown?.Invoke(s.X, s.Y, 2); break;
                        default: Wheel?.Invoke(s.X, s.Y, (short)(s.mouseData >> 16), msg == WM_MOUSEHWHEEL); break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Mouse hook handler failed", ex); }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                int msg = (int)wParam;
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                {
                    var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    Key?.Invoke((int)s.vkCode, msg is WM_KEYDOWN or WM_SYSKEYDOWN);
                }
            }
            catch (Exception ex) { Log.Error("Keyboard hook handler failed", ex); }
        }
        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _thread = null;
        _ready.Dispose();
    }
}
