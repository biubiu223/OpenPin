using System.Runtime.InteropServices;

namespace OpenPin.Capture;

/// <summary>Capture-module private Win32 interop (kept here so Core/NativeMethods is untouched).</summary>
internal static class CaptureNative
{
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_CLOAK = 13;
    public const int WM_DPICHANGED = 0x02E0;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_LAYERED = 0x80000;
    private const int LWA_ALPHA = 0x2;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_PROCESSKEY = 0xE5;

    /// <summary>
    /// The physical key of a WM_KEYDOWN, even when an IME already replaced it with VK_PROCESSKEY (scan code → VK).
    /// </summary>
    public static int RealVirtualKey(IntPtr wParam, IntPtr lParam)
    {
        int vk = (int)wParam.ToInt64() & 0xFF;
        if (vk != VK_PROCESSKEY) return vk;
        uint scan = (uint)((lParam.ToInt64() >> 16) & 0xFF);
        bool extended = ((lParam.ToInt64() >> 24) & 1) != 0;
        return (int)MapVirtualKey(extended ? 0xE000 | scan : scan, 3 /* MAPVK_VSC_TO_VK_EX */);
    }

    public static int ScanCode(IntPtr lParam) => (int)((lParam.ToInt64() >> 16) & 0xFF);

    public static bool IsImeProcessKey(IntPtr wParam) => ((int)wParam.ToInt64() & 0xFF) == VK_PROCESSKEY;

    /// <summary>Keys an IME may swallow: letters, digits, OEM punctuation, space.</summary>
    public static bool IsImeSensitive(int vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2 or 0x20;

    /// <summary>Hide a window from DWM composition while it renders its first frame (no flash).</summary>
    public static void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        int v = cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int));
    }

    /// <summary>No fade/zoom animation when the overlay appears or disappears.</summary>
    public static void DisableTransitions(IntPtr hwnd)
    {
        int v = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref v, sizeof(int));
    }

    /// <summary>Windows 11 must not round the corners of the full-screen overlay.</summary>
    public static void DisableRoundedCorners(IntPtr hwnd)
    {
        int v = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref v, sizeof(int));
    }

    public static void MoveCursorBy(int dx, int dy)
    {
        if (GetCursorPos(out var p)) SetCursorPos(p.X + dx, p.Y + dy);
    }

    /// <summary>Make <paramref name="hwnd"/> the foreground window even if another process owns the foreground.</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return;
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint cur = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != cur && AttachThreadInput(cur, fgThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { AttachThreadInput(cur, fgThread, false); }
        }
    }

    /// <summary>
    /// True for windows that are visible to EnumWindows but not to the user: click-through overlays
    /// (WS_EX_TRANSPARENT) and fully transparent layered windows.
    /// </summary>
    public static bool IsInvisibleOverlay(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true;
        if ((ex & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags) &&
            (flags & LWA_ALPHA) != 0 && alpha == 0) return true;
        return false;
    }
}
