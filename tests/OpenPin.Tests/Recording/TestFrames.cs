using System.IO;
using System.Runtime.ExceptionServices;

namespace OpenPin.Tests.Recording;

/// <summary>Synthetic BGRA frames for the codec tests.</summary>
internal static class TestFrames
{
    public static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return px;
    }

    /// <summary>Smooth gradient (many colors → forces quantization).</summary>
    public static byte[] Gradient(int w, int h, int phase = 0)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)((x * 255 / Math.Max(1, w - 1) + phase) & 255);
            px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
            px[i + 2] = (byte)((x + y + phase) * 255 / Math.Max(1, w + h - 2) & 255);
            px[i + 3] = 255;
        }
        return px;
    }

    /// <summary>Fill a rectangle with a color (in place).</summary>
    public static byte[] FillRect(byte[] px, int w, int x0, int y0, int rw, int rh, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + rh; y++)
        for (int x = x0; x < x0 + rw; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return px;
    }

    public static (byte R, byte G, byte B) At(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return (px[i + 2], px[i + 1], px[i]);
    }

    /// <summary>WPF imaging objects are happiest on an STA thread; xUnit runs tests on MTA threads.</summary>
    public static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static string TempFile(string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OpenPin.Tests", "Recording");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
    }
}
