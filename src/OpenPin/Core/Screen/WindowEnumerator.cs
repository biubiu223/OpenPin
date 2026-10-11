using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Core.Screen;

/// <summary>A top-level window. Bounds are the visible frame (DWM extended frame bounds) in screen px.</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public Int32Rect BoundsPx { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public int ProcessId { get; init; }

    private string? _processName;

    /// <summary>Process name without ".exe" (lazy; empty if inaccessible).</summary>
    public string ProcessName
    {
        get
        {
            if (_processName != null) return _processName;
            try { _processName = Process.GetProcessById(ProcessId).ProcessName; }
            catch { _processName = ""; }
            return _processName;
        }
    }

    public override string ToString() => $"{Title} [{ClassName}] {BoundsPx}";
}

public static class WindowEnumerator
{
    /// <summary>
    /// Visible, non-cloaked, non-minimized top-level windows in z-order (topmost first).
    /// Windows belonging to the current process are excluded unless <paramref name="includeOwnProcess"/> is set.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetTopLevelWindows(bool includeOwnProcess = false)
    {
        var result = new List<WindowInfo>();
        int ownPid = Environment.ProcessId;
        var sb = new StringBuilder(512);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (!includeOwnProcess && pid == ownPid) return true;

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);
            if (r.Width <= 1 || r.Height <= 1) return true;

            sb.Clear();
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            sb.Clear();
            GetClassName(hwnd, sb, sb.Capacity);

            result.Add(new WindowInfo
            {
                Handle = hwnd,
                BoundsPx = new Int32Rect(r.Left, r.Top, r.Width, r.Height),
                Title = title,
                ClassName = sb.ToString(),
                ProcessId = pid,
            });
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>The topmost window in <paramref name="windows"/> containing the screen-px point, or null.</summary>
    public static WindowInfo? HitTest(IReadOnlyList<WindowInfo> windows, Point screenPx)
    {
        foreach (var w in windows)
        {
            var b = w.BoundsPx;
            if (screenPx.X >= b.X && screenPx.Y >= b.Y && screenPx.X < b.X + b.Width && screenPx.Y < b.Y + b.Height)
                return w;
        }
        return null;
    }
}
