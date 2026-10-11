using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Recording.Codec;

namespace OpenPin.Recording.UI;

/// <summary>
/// Renders keystroke labels ("Ctrl + C") as Win11-style pills (dark translucent rounded rectangle, light text) for the
/// video overlay. WPF text rendering needs the UI thread, so all labels of a recording are rendered once up front and the
/// background composer just alpha-blends the sprites.
/// </summary>
internal static class KeyPillRenderer
{
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    static KeyPillRenderer()
    {
        // OEM keys: ask the active keyboard layout for the character
        FrameComposer.KeyNameFallback = vk =>
        {
            uint ch = MapVirtualKey((uint)vk, 2 /* MAPVK_VK_TO_CHAR */) & 0x7FFFFFFF;
            return ch is > 32 and < 0xFFFF ? char.ToUpperInvariant((char)ch).ToString() : null;
        };
    }

    /// <summary>Make sure the OEM key-name fallback is installed.</summary>
    public static void EnsureInitialized() { }

    /// <summary>All labels the keystroke overlay can show for this recording (both filters).</summary>
    public static IEnumerable<string> Labels(InputLog input)
    {
        if (input.Keys.Count == 0) return Array.Empty<string>();
        return Keystrokes.BuildTimeline(input.Keys, KeystrokeFilter.All, FrameComposer.KeyNameFallback)
            .Concat(Keystrokes.BuildTimeline(input.Keys, KeystrokeFilter.CombosOnly, FrameComposer.KeyNameFallback))
            .Select(s => s.Label).Distinct();
    }

    /// <summary>Render pills sized for a video of the given size. UI thread.</summary>
    public static Dictionary<string, CursorShape> Render(IEnumerable<string> labels, int videoWidth, int videoHeight)
    {
        var result = new Dictionary<string, CursorShape>();
        var app = Application.Current;
        double unit = Math.Clamp(Math.Min(videoWidth, videoHeight) / 720.0, 0.6, 2.5);
        double fontSize = 22 * unit, padX = 16 * unit, padY = 8 * unit, radius = 8 * unit;
        var fontFamily = app?.TryFindResource("Font.UI") as FontFamily ?? new FontFamily("Segoe UI");
        var bgColor = app?.TryFindResource("Color.Text") is Color c ? c : Color.FromRgb(0x1F, 0x1F, 0x1F);
        var fgColor = app?.TryFindResource("Color.Surface") is Color f ? f : Colors.White;
        var bg = new SolidColorBrush(Color.FromArgb(0xD9, bgColor.R, bgColor.G, bgColor.B));
        var fg = new SolidColorBrush(fgColor);
        var face = new Typeface(fontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        foreach (var label in labels)
        {
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, fontSize, fg, 1.0);
            int w = Math.Min(videoWidth, (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace + 2 * padX));
            int h = (int)Math.Ceiling(ft.Height + 2 * padY);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), radius, radius);
                dc.DrawText(ft, new Point((w - ft.Width) / 2, padY));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            // premultiplied → straight alpha
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (a is 0 or 255) continue;
                px[i] = (byte)Math.Min(255, px[i] * 255 / a);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
                px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
            }
            result[label] = new CursorShape(w, h, px, null, 0, 0);
        }
        return result;
    }
}
