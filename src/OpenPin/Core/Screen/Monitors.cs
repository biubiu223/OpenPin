using System.Windows;
using OpenPin.Core.Native;
using static OpenPin.Core.Native.NativeMethods;

namespace OpenPin.Core.Screen;

/// <summary>A physical display. All rectangles are PHYSICAL pixels in virtual-screen coordinates.</summary>
public sealed record MonitorInfo(IntPtr Handle, Int32Rect BoundsPx, Int32Rect WorkAreaPx, double Scale, bool IsPrimary, string DeviceName);

public static class Monitors
{
    /// <summary>Bounding rectangle of all monitors, physical pixels.</summary>
    public static Int32Rect VirtualScreenPx => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                double scale = 1.0;
                if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                    scale = dx / 96.0;
                list.Add(new MonitorInfo(h,
                    new Int32Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height),
                    new Int32Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height),
                    scale, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0, mi.szDevice ?? ""));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo Primary => GetAll().FirstOrDefault(m => m.IsPrimary) ?? GetAll().First();

    /// <summary>Monitor containing (or nearest to) a physical-pixel point.</summary>
    public static MonitorInfo FromPointPx(int x, int y)
    {
        var all = GetAll();
        var hit = all.FirstOrDefault(m => x >= m.BoundsPx.X && y >= m.BoundsPx.Y &&
                                          x < m.BoundsPx.X + m.BoundsPx.Width && y < m.BoundsPx.Y + m.BoundsPx.Height);
        if (hit != null) return hit;
        var h = MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);
        return all.FirstOrDefault(m => m.Handle == h) ?? all.First();
    }

    /// <summary>Cursor position in physical pixels.</summary>
    public static Point CursorPx
    {
        get
        {
            GetCursorPos(out var p);
            return new Point(p.X, p.Y);
        }
    }

    public static MonitorInfo UnderCursor
    {
        get
        {
            var p = CursorPx;
            return FromPointPx((int)p.X, (int)p.Y);
        }
    }
}
