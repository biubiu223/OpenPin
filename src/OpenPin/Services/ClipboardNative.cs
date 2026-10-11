using System.Runtime.InteropServices;

namespace OpenPin.Services;

/// <summary>Raw Win32 clipboard reading (module-private interop).</summary>
internal static class ClipboardNative
{
    public const uint CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIB = 8, CF_DIBV5 = 17, CF_BITMAP = 2;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr hMem);

    private static readonly Lazy<uint> PngFormat = new(() => RegisterClipboardFormat("PNG"));
    private static readonly Lazy<uint> MimePngFormat = new(() => RegisterClipboardFormat("image/png"));
    private static readonly Lazy<uint> HtmlFormat = new(() => RegisterClipboardFormat("HTML Format"));

    public static uint CF_PNG => PngFormat.Value;
    public static uint CF_MIME_PNG => MimePngFormat.Value;
    public static uint CF_HTML => HtmlFormat.Value;

    public static bool IsAvailable(uint format) => IsClipboardFormatAvailable(format);

    /// <summary>
    /// Open the clipboard (retrying while another process holds it), read the given formats' HGLOBAL bytes and close.
    /// Missing formats are absent from the result. Returns null if the clipboard could not be opened.
    /// </summary>
    public static Dictionary<uint, byte[]>? Read(IEnumerable<uint> formats, int attempts = 10)
    {
        bool opened = false;
        for (int i = 0; i < attempts && !opened; i++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened) Thread.Sleep(20 + 15 * i);
        }
        if (!opened) return null;
        try
        {
            var result = new Dictionary<uint, byte[]>();
            var available = new HashSet<uint>();
            for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f)) available.Add(f);
            foreach (var f in formats)
            {
                if (f == 0 || !available.Contains(f) && !IsClipboardFormatAvailable(f)) continue;
                var h = GetClipboardData(f);
                if (h == IntPtr.Zero) continue;
                var bytes = ReadGlobal(h);
                if (bytes != null) result[f] = bytes;
            }
            return result;
        }
        finally { CloseClipboard(); }
    }

    private static byte[]? ReadGlobal(IntPtr h)
    {
        var size = (long)GlobalSize(h).ToUInt64();
        if (size <= 0 || size > int.MaxValue) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, (int)size);
            return bytes;
        }
        finally { GlobalUnlock(h); }
    }
}
