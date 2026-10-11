using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using OpenPin.Core;

namespace OpenPin.LongCapture;

/// <summary>Win32 bits used only by the long-capture module.</summary>
internal static class LongCaptureNative
{
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x01000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public int mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
        // KEYBDINPUT / HARDWAREINPUT are smaller than MOUSEINPUT: the union size is MOUSEINPUT's.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    /// <summary>Synthetic mouse wheel at the current cursor position. Vertical: negative = scroll down; horizontal: positive = right.</summary>
    public static bool SendWheel(int delta, bool horizontal)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { mouseData = delta, dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL },
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
    }

    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    public static (int X, int Y) CursorPx
    {
        get
        {
            GetCursorPos(out var p);
            return (p.X, p.Y);
        }
    }

    /// <summary>Title and process name of the top-level window at a screen point (for file-name templates).</summary>
    public static (string? Title, string? Process) WindowInfoAt(int x, int y)
    {
        try
        {
            var hwnd = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */);
            if (hwnd == IntPtr.Zero) return (null, null);
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hwnd, out int pid);
            string? process = null;
            try { using var p = Process.GetProcessById(pid); process = p.ProcessName; }
            catch (Exception ex) { Log.Warn($"Long capture: process name unavailable: {ex.Message}"); }
            return (sb.ToString(), process);
        }
        catch (Exception ex)
        {
            Log.Error("Long capture: window info failed", ex);
            return (null, null);
        }
    }

    /// <summary>
    /// A global hotkey (e.g. Esc) registered on a message-only window for the lifetime of the object. The long-capture
    /// windows never take focus (the user keeps scrolling the app below), so Esc must be caught globally.
    /// </summary>
    public sealed class GlobalKey : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;
        private readonly HwndSource _source;
        private readonly int _id;
        private readonly Action _pressed;

        public GlobalKey(uint vk, int id, Action pressed)
        {
            _id = id;
            _pressed = pressed;
            _source = new HwndSource(new HwndSourceParameters("OpenPin.LongCapture.Keys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
            _source.AddHook(Hook);
            IsRegistered = RegisterHotKey(_source.Handle, id, MOD_NOREPEAT, vk);
            if (!IsRegistered) Log.Warn($"Long capture: could not register key 0x{vk:X2} (error {Marshal.GetLastWin32Error()})");
        }

        public bool IsRegistered { get; }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
            {
                handled = true;
                try { _pressed(); }
                catch (Exception ex) { Log.Error("Long capture key handler failed", ex); }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (IsRegistered) UnregisterHotKey(_source.Handle, _id);
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
    }
}
