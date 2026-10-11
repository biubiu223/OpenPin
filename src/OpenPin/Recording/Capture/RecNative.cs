using System.Runtime.InteropServices;

namespace OpenPin.Recording.Capture;

/// <summary>Win32 declarations used only by the recorder (Core.NativeMethods has the common ones).</summary>
internal static class RecNative
{
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int c, out BITMAP pv);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint uPeriod);

    public const int SM_SWAPBUTTON = 23;
}
