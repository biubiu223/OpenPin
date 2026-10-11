using System.Windows;
using System.Windows.Interop;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Core.Native;

/// <summary>
/// Helpers to manipulate WPF windows in PHYSICAL pixel coordinates.
/// WPF's Left/Top/Width/Height are DIPs and become ambiguous on mixed-DPI setups,
/// so screen-anchored windows (overlay, pins, toolbars) should use these helpers.
/// </summary>
public static class WindowHelper
{
    public static IntPtr GetHandle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    /// <summary>Moves/resizes a window to an exact physical-pixel rectangle (virtual-screen coordinates).</summary>
    public static void SetBoundsPx(Window window, Int32Rect boundsPx, bool topmost = false)
    {
        var hwnd = GetHandle(window);
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero, boundsPx.X, boundsPx.Y, boundsPx.Width, boundsPx.Height,
            SWP_NOACTIVATE | (topmost ? 0 : SWP_NOZORDER));
    }

    /// <summary>Moves a window (keeping its size) so its top-left is at the given physical-pixel point.</summary>
    public static void SetPositionPx(Window window, int x, int y)
    {
        var hwnd = GetHandle(window);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE);
    }

    /// <summary>Gets the window rectangle in physical pixels.</summary>
    public static Int32Rect GetBoundsPx(Window window)
    {
        GetWindowRect(GetHandle(window), out var r);
        return new Int32Rect(r.Left, r.Top, r.Width, r.Height);
    }

    public static void SetTopmost(Window window, bool topmost)
    {
        SetWindowPos(GetHandle(window), topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Mouse click-through (WS_EX_TRANSPARENT + WS_EX_LAYERED).</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = GetHandle(window);
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = enabled ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Hide from Alt+Tab (tool window style).</summary>
    public static void SetToolWindow(Window window, bool enabled)
    {
        var hwnd = GetHandle(window);
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = enabled ? (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW : ex & ~WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Window does not take focus when clicked.</summary>
    public static void SetNoActivate(Window window, bool enabled)
    {
        var hwnd = GetHandle(window);
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = enabled ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>DPI scale of the monitor the window currently lives on (1.0 = 96 DPI).</summary>
    public static double GetDpiScale(Window window)
    {
        var dpi = GetDpiForWindow(GetHandle(window));
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>Brings a window to the foreground even when the app is in the background (best effort).</summary>
    public static void ForceForeground(Window window)
    {
        var hwnd = GetHandle(window);
        window.Activate();
        SetForegroundWindow(hwnd);
    }
}
