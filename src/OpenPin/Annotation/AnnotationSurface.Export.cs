using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Annotation.Rendering;
using OpenPin.Core.Imaging;

namespace OpenPin.Annotation;

public partial class AnnotationSurface
{
    /// <summary>
    /// Export (ANN-16): annotations are drawn with the SAME code as on screen into a transparent 96-DPI target of the
    /// crop size, then composited over the untouched base pixels on the CPU ("over" operator, like WPF composition).
    /// Independent of host zoom / DPI because nothing here depends on the visual tree.
    /// </summary>
    private BitmapSource RenderCore(Int32Rect? cropPx)
    {
        int w = _ctx.Width, h = _ctx.Height;
        var r = cropPx is { } c ? BitmapUtil.Clamp(c, w, h) : new Int32Rect(0, 0, w, h);
        if (r.Width <= 0 || r.Height <= 0) throw new ArgumentException("Crop rectangle outside image", nameof(cropPx));

        var basePx = PixelEffects.ReadRegion(BgraBase(), r);
        if (_doc.Items.Count == 0) return BitmapUtil.FromPixels(basePx, r.Width, r.Height);

        var dv = new DrawingVisual();
        TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Grayscale);
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-r.X, -r.Y));
            if (_editBounds is { } eb) dc.PushClip(new RectangleGeometry(eb));
            // Pending background effects (smart erase) are computed inline, with the same code as on screen.
            bool sync = _ctx.SynchronousEffects;
            _ctx.SynchronousEffects = true;
            try { AnnotationLayer.DrawAll(dc, _doc.Items, _ctx); }
            finally { _ctx.SynchronousEffects = sync; }
            if (_editBounds != null) dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(r.Width, r.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var ann = new byte[r.Width * r.Height * 4];
        rtb.CopyPixels(ann, r.Width * 4, 0);
        Composite(ann, basePx);
        return BitmapUtil.FromPixels(basePx, r.Width, r.Height);
    }

    private BitmapSource BgraBase()
    {
        var b = _ctx.BaseImage;
        return b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Bgr32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
    }

    /// <summary>dst (straight BGRA) = premultiplied src OVER dst; result stored back into dst as straight BGRA.</summary>
    internal static void Composite(byte[] srcPremul, byte[] dst)
    {
        for (int i = 0; i < dst.Length; i += 4)
        {
            int sa = srcPremul[i + 3];
            if (sa == 0) continue;
            if (sa == 255)
            {
                dst[i] = srcPremul[i]; dst[i + 1] = srcPremul[i + 1]; dst[i + 2] = srcPremul[i + 2]; dst[i + 3] = 255;
                continue;
            }
            int da = dst[i + 3];
            int inv = 255 - sa;
            // Destination premultiplied, then OVER.
            int outA = sa + (da * inv + 127) / 255;
            for (int k = 0; k < 3; k++)
            {
                int dp = (dst[i + k] * da + 127) / 255;
                int o = srcPremul[i + k] + (dp * inv + 127) / 255;
                dst[i + k] = (byte)Math.Min(255, outA == 0 ? 0 : (o * 255 + outA / 2) / outA);
            }
            dst[i + 3] = (byte)outA;
        }
    }
}
