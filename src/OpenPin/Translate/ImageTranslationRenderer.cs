using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core.Imaging;

namespace OpenPin.Translate;

/// <summary>A translated text box to paint: original bounds (image px), translation, original line height.</summary>
public sealed record TranslatedBox(Rect Bounds, string Text, double LineHeight);

/// <summary>How one box is painted (computed by <see cref="ImageTranslationRenderer.Plan"/>).</summary>
public sealed record BoxPlan(Int32Rect Fill, Rect TextArea, Color Background, Color Foreground, double FontSize, string Text);

/// <summary>
/// Image → image translation (PRO-01): paints each translation over its original text box. Per box the background is
/// the median colour of the box border, the text colour comes from the original text pixels (forced to black/white if
/// it does not contrast), and the font size is the largest that fits the box when wrapped. No windows involved — only
/// pixels, <see cref="FormattedText"/> and a <see cref="DrawingVisual"/>; call on an STA thread.
/// </summary>
public sealed class ImageTranslationRenderer
{
    public string FontFamily { get; init; } = "Microsoft YaHei UI, Segoe UI, Arial";
    public double MinFontSize { get; init; } = 6;
    /// <summary>The fill extends this many px beyond the OCR box (OCR boxes are tight around the glyphs).</summary>
    public int Padding { get; init; } = 2;
    public bool RightToLeft { get; init; }

    private Typeface Face => new(new FontFamily(FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>The image with every box repainted (Bgra32, 96 DPI, same size, frozen).</summary>
    public BitmapSource Render(BitmapSource image, IReadOnlyList<TranslatedBox> boxes)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var px = BitmapUtil.GetPixels(image);
        var plans = boxes.Select(b => Plan(px, w, h, b)).Where(p => p != null).Select(p => p!).ToList();

        var dv = new DrawingVisual();
        TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(dv, TextFormattingMode.Ideal);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, w, h));
            // All backgrounds first, so a padded fill never covers a neighbour's text.
            foreach (var p in plans)
            {
                var bg = new SolidColorBrush(p.Background);
                bg.Freeze();
                dc.DrawRectangle(bg, null, new Rect(p.Fill.X, p.Fill.Y, p.Fill.Width, p.Fill.Height));
            }
            foreach (var p in plans)
            {
                var fg = new SolidColorBrush(p.Foreground);
                fg.Freeze();
                var ft = Format(p.Text, p.FontSize, p.TextArea.Width, fg);
                double y = p.TextArea.Y + Math.Max(0, (p.TextArea.Height - ft.Height) / 2);
                dc.PushClip(new RectangleGeometry(new Rect(p.Fill.X, p.Fill.Y, p.Fill.Width, p.Fill.Height)));
                dc.DrawText(ft, new Point(p.TextArea.X, y));
                dc.Pop();
            }
        }
        return BitmapUtil.ToBgra32(BitmapUtil.Render(dv, w, h));
    }

    /// <summary>Colours, area and font size for one box, or null when the box is outside the image / the text is empty.</summary>
    public BoxPlan? Plan(byte[] px, int w, int h, TranslatedBox box)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        var inner = ToInt(box.Bounds, w, h, 0);
        if (inner.Width < 2 || inner.Height < 2) return null;
        var fill = ToInt(box.Bounds, w, h, Padding);
        var bg = MedianBorderColor(px, w, h, fill);
        var fg = TextColor(px, w, h, inner, bg);
        var area = new Rect(fill.X + 1, fill.Y, Math.Max(1, fill.Width - 2), fill.Height);
        double max = Math.Max(MinFontSize, Math.Min(box.LineHeight * 0.95, area.Height));
        var text = box.Text.Trim();
        double size = FitFontSize(area.Size, MinFontSize, max, s =>
        {
            var ft = Format(text, s, area.Width, Brushes.Black);
            return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
        });
        return new BoxPlan(fill, area, bg, fg, size, text);
    }

    private FormattedText Format(string text, double size, double maxWidth, Brush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Face, size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.None,
            TextAlignment = TextAlignment.Left,
        };
        return ft;
    }

    private static Int32Rect ToInt(Rect r, int w, int h, int pad)
    {
        int x0 = (int)Math.Floor(r.Left) - pad, y0 = (int)Math.Floor(r.Top) - pad;
        int x1 = (int)Math.Ceiling(r.Right) + pad, y1 = (int)Math.Ceiling(r.Bottom) + pad;
        return BitmapUtil.Clamp(new Int32Rect(x0, y0, x1 - x0, y1 - y0), w, h);
    }

    // ------------------------------------------------------------------ pure helpers (unit tested)

    /// <summary>
    /// Largest font size in [min, max] whose wrapped text fits <paramref name="box"/> (binary search on the measured
    /// size; 0.25 px precision). Returns <paramref name="min"/> when even that does not fit.
    /// </summary>
    public static double FitFontSize(Size box, double min, double max, Func<double, Size> measure)
    {
        if (max < min) max = min;
        bool Fits(double s)
        {
            var m = measure(s);
            return m.Height <= box.Height + 0.01 && m.Width <= box.Width + 0.5;
        }
        if (Fits(max)) return max;
        if (!Fits(min)) return min;
        double lo = min, hi = max;
        while (hi - lo > 0.25)
        {
            double mid = (lo + hi) / 2;
            if (Fits(mid)) lo = mid; else hi = mid;
        }
        return Math.Floor(lo * 4) / 4;
    }

    /// <summary>Per-channel median of the pixels on the border of <paramref name="r"/> (the background around the text).</summary>
    public static Color MedianBorderColor(byte[] px, int w, int h, Int32Rect r)
    {
        var b = new List<byte>(); var g = new List<byte>(); var rr = new List<byte>(); var a = new List<byte>();
        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = (y * w + x) * 4;
            b.Add(px[i]); g.Add(px[i + 1]); rr.Add(px[i + 2]); a.Add(px[i + 3]);
        }
        int x0 = r.X, y0 = r.Y, x1 = r.X + r.Width - 1, y1 = r.Y + r.Height - 1;
        for (int x = x0; x <= x1; x++) { Add(x, y0); if (y1 != y0) Add(x, y1); }
        for (int y = y0 + 1; y < y1; y++) { Add(x0, y); if (x1 != x0) Add(x1, y); }
        if (b.Count == 0) return Colors.White;
        return Color.FromArgb(Median(a), Median(rr), Median(g), Median(b));
    }

    private static byte Median(List<byte> v)
    {
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>
    /// Text colour: the average of the pixels inside <paramref name="inner"/> that differ most from the background
    /// (the glyph cores). Falls back to black / white when that colour does not contrast enough (WCAG ratio &lt; 3).
    /// </summary>
    public static Color TextColor(byte[] px, int w, int h, Int32Rect inner, Color bg)
    {
        var samples = new List<(int Dist, int I)>();
        for (int y = inner.Y; y < inner.Y + inner.Height && y < h; y++)
        for (int x = inner.X; x < inner.X + inner.Width && x < w; x++)
        {
            int i = (y * w + x) * 4;
            int d = Math.Abs(px[i] - bg.B) + Math.Abs(px[i + 1] - bg.G) + Math.Abs(px[i + 2] - bg.R);
            if (d > 60) samples.Add((d, i));
        }
        Color candidate;
        if (samples.Count == 0) candidate = bg;
        else
        {
            samples.Sort((p, q) => q.Dist.CompareTo(p.Dist));
            int n = Math.Max(1, samples.Count * 3 / 10);
            long sb = 0, sg = 0, sr = 0;
            for (int k = 0; k < n; k++)
            {
                int i = samples[k].I;
                sb += px[i]; sg += px[i + 1]; sr += px[i + 2];
            }
            candidate = Color.FromRgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
        }
        if (ContrastRatio(candidate, bg) >= 3) return candidate;
        return ContrastRatio(Colors.Black, bg) >= ContrastRatio(Colors.White, bg) ? Colors.Black : Colors.White;
    }

    /// <summary>WCAG 2 contrast ratio (1–21).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
