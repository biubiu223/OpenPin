using System.Runtime.InteropServices;
using OpenPin.Recording.Capture;

namespace OpenPin.Tests.Recording;

public class CursorTests
{
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr name);

    [Theory]
    [InlineData(32512)] // IDC_ARROW
    [InlineData(32513)] // IDC_IBEAM
    [InlineData(32649)] // IDC_HAND
    public void SystemCursors_AreExtracted_WithVisiblePixels(int id)
    {
        var shape = CursorTracker.Extract(LoadCursor(IntPtr.Zero, id));
        Assert.NotNull(shape);
        Assert.InRange(shape!.Width, 16, 256);
        int visible = 0;
        for (int i = 3; i < shape.Bgra.Length; i += 4) if (shape.Bgra[i] > 0) visible++;
        int inverted = shape.InvertMask?.Count(b => b != 0) ?? 0;
        Assert.True(visible + inverted > 20, $"visible {visible}, inverted {inverted}");
        Assert.InRange(shape.HotspotX, 0, shape.Width);
        Assert.InRange(shape.HotspotY, 0, shape.Height);
    }
}
