using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Annotation;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;
using OpenPin.Annotation.Rendering;
using OpenPin.Core.Imaging;

namespace OpenPin.Tests.Annotation;

/// <summary>PRO-06 smart erase: inpainting algorithm, mask rasterisation and surface integration.</summary>
public class SmartEraseTests
{
    // ------------------------------------------------------------------ helpers

    private static byte[] Image(int w, int h, Func<int, int, (byte B, byte G, byte R)> color)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (b, g, r) = color(x, y);
            int o = (y * w + x) * 4;
            px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
        }
        return px;
    }

    private static byte[] RectMask(int w, int h, int x0, int y0, int rw, int rh)
    {
        var m = new byte[w * h];
        for (int y = y0; y < y0 + rh; y++)
        for (int x = x0; x < x0 + rw; x++)
            m[y * w + x] = 1;
        return m;
    }

    /// <summary>Paints pseudo "text" (dark strokes) into the masked area, i.e. the content to erase.</summary>
    private static void Scribble(byte[] px, int w, byte[] mask, int seed = 3)
    {
        var r = new Random(seed);
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0 || r.Next(3) != 0) continue;
            px[i * 4] = (byte)r.Next(0, 60); px[i * 4 + 1] = (byte)r.Next(0, 60); px[i * 4 + 2] = (byte)r.Next(0, 60);
        }
    }

    private static int MaxErrorInHole(byte[] actual, byte[] expected, byte[] mask)
    {
        int max = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            for (int c = 0; c < 4; c++) max = Math.Max(max, Math.Abs(actual[i * 4 + c] - expected[i * 4 + c]));
        }
        return max;
    }

    private static void AssertOutsideUnchanged(byte[] actual, byte[] original, byte[] mask)
    {
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] != 0) continue;
            for (int c = 0; c < 4; c++)
                if (actual[i * 4 + c] != original[i * 4 + c]) Assert.Fail($"pixel {i} outside the mask changed");
        }
    }

    // ------------------------------------------------------------------ algorithm

    [Fact]
    public void SolidBackground_IsFilledExactly()
    {
        const int w = 160, h = 90;
        var truth = Image(w, h, (_, _) => (0x69, 0xEC, 0x95)); // WeChat bubble green
        var mask = RectMask(w, h, 30, 30, 90, 24);
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        var before = (byte[])px.Clone();

        Assert.Equal(InpaintMethod.Solid, Inpainter.Fill(px, w, h, mask));
        Assert.Equal(0, MaxErrorInHole(px, truth, mask));
        AssertOutsideUnchanged(px, before, mask);
    }

    [Fact]
    public void SolidBackground_ToleratesAFewAntialiasedGlyphPixelsOnTheBorder()
    {
        const int w = 120, h = 60;
        var truth = Image(w, h, (_, _) => (255, 255, 255));
        var mask = RectMask(w, h, 20, 20, 60, 16);
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        // A glyph tip sticking out of the mask.
        for (int x = 30; x < 34; x++) { int o = (19 * w + x) * 4; px[o] = px[o + 1] = px[o + 2] = 90; }
        Assert.Equal(InpaintMethod.Solid, Inpainter.Fill(px, w, h, mask));
        Assert.Equal(0, MaxErrorInHole(px, truth, mask));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinearGradient_IsReconstructedWithinTolerance(bool horizontal)
    {
        const int w = 200, h = 120;
        var truth = Image(w, h, (x, y) =>
        {
            double t = horizontal ? x / (double)(w - 1) : y / (double)(h - 1);
            return ((byte)Math.Round(40 + 180 * t), (byte)Math.Round(200 - 120 * t), (byte)Math.Round(90 + 60 * t));
        });
        var mask = RectMask(w, h, 40, 35, 110, 40);
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        var before = (byte[])px.Clone();

        var method = Inpainter.Fill(px, w, h, mask);
        Assert.Equal(InpaintMethod.Smooth, method);
        Assert.True(MaxErrorInHole(px, truth, mask) <= 2, $"max error {MaxErrorInHole(px, truth, mask)}");
        AssertOutsideUnchanged(px, before, mask);
    }

    [Fact]
    public void RadialGradient_IsSmoothAndClose()
    {
        const int w = 220, h = 160;
        var truth = Image(w, h, (x, y) =>
        {
            double d = Math.Sqrt((x - 70.0) * (x - 70.0) + (y - 50.0) * (y - 50.0));
            byte v = (byte)Math.Clamp(Math.Round(230 - d * 0.6), 0, 255);
            return (v, (byte)(v / 2), 200);
        });
        var mask = RectMask(w, h, 60, 60, 100, 50);
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        Assert.Equal(InpaintMethod.Smooth, Inpainter.Fill(px, w, h, mask));
        Assert.True(MaxErrorInHole(px, truth, mask) <= 4, $"max error {MaxErrorInHole(px, truth, mask)}");
    }

    [Theory]
    [InlineData(0, 30)]   // touches the left border
    [InlineData(150, 0)]  // touches the top border
    [InlineData(170, 90)] // touches the bottom-right corner
    public void MaskTouchingTheImageBorder(int x0, int y0)
    {
        const int w = 200, h = 120;
        var truth = Image(w, h, (x, y) => ((byte)(30 + x / 2), (byte)(60 + y / 3), 128));
        var mask = RectMask(w, h, x0, y0, Math.Min(30, w - x0), Math.Min(30, h - y0));
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        Inpainter.Fill(px, w, h, mask);
        Assert.True(MaxErrorInHole(px, truth, mask) <= 2, $"max error {MaxErrorInHole(px, truth, mask)}");

        var solid = Image(w, h, (_, _) => (10, 20, 30));
        var sp = (byte[])solid.Clone();
        Scribble(sp, w, mask);
        Assert.Equal(InpaintMethod.Solid, Inpainter.Fill(sp, w, h, mask));
        Assert.Equal(0, MaxErrorInHole(sp, solid, mask));
    }

    [Fact]
    public void Checkerboard_IsContinuedWithItsOwnColours()
    {
        const int w = 160, h = 160, cell = 8;
        var truth = Image(w, h, (x, y) => ((x / cell + y / cell) & 1) == 0 ? ((byte)40, (byte)40, (byte)40) : ((byte)220, (byte)220, (byte)220));
        var mask = RectMask(w, h, 61, 67, 29, 23); // not aligned with the cells
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        var before = (byte[])px.Clone();

        var method = Inpainter.Fill(px, w, h, mask);
        Assert.Equal(InpaintMethod.Structure, method);
        AssertOutsideUnchanged(px, before, mask);
        int dark = 0, light = 0, exact = 0, holes = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            holes++;
            byte v = px[i * 4];
            if (v == 40) dark++; else if (v == 220) light++;
            if (v == truth[i * 4]) exact++;
        }
        // Patches are copied: only the two checker colours appear, in balanced amounts, mostly at the right place.
        Assert.Equal(holes, dark + light);
        Assert.InRange(dark / (double)holes, 0.3, 0.7);
        Assert.True(exact >= holes * 0.8, $"only {exact}/{holes} pixels continue the pattern");
    }

    [Fact]
    public void NoiseTexture_KeepsItsStatistics()
    {
        const int w = 220, h = 160;
        var r = new Random(11);
        var truth = Image(w, h, (_, _) => { byte v = (byte)r.Next(100, 156); return (v, (byte)(v - 20), (byte)(v + 40)); });
        var mask = RectMask(w, h, 80, 60, 60, 30);
        var px = (byte[])truth.Clone();
        Scribble(px, w, mask);
        var method = Inpainter.Fill(px, w, h, mask);
        Assert.True(method is InpaintMethod.Texture or InpaintMethod.Structure, method.ToString());
        var vals = new List<int>();
        for (int i = 0; i < mask.Length; i++) if (mask[i] != 0) vals.Add(px[i * 4]);
        double mean = vals.Average(), sd = Math.Sqrt(vals.Average(v => (v - mean) * (v - mean)));
        Assert.InRange(mean, 118, 138);  // source mean 127.5
        Assert.InRange(sd, 8, 24);       // source sd ≈ 16: not a flat fill, no outliers
        Assert.All(vals, v => Assert.InRange(v, 90, 166));
    }

    [Fact]
    public void Fill_IsDeterministic()
    {
        const int w = 200, h = 140;
        var r = new Random(5);
        var tex = Image(w, h, (x, y) => ((byte)(((x / 5 + y / 7) % 3) * 60 + r.Next(20)), (byte)(100 + (x % 9) * 10), (byte)(y % 13 * 15)));
        var mask = RectMask(w, h, 50, 40, 90, 50);
        var a = (byte[])tex.Clone();
        var b = (byte[])tex.Clone();
        Inpainter.Fill(a, w, h, mask);
        Inpainter.Fill(b, w, h, mask);
        Assert.Equal(a, b);
    }

    [Fact]
    public void EmptyMaskOrNoKnownPixels_DoNothing()
    {
        var px = Image(10, 10, (_, _) => (1, 2, 3));
        var copy = (byte[])px.Clone();
        Assert.Equal(InpaintMethod.None, Inpainter.Fill(px, 10, 10, new byte[100]));
        var all = new byte[100];
        Array.Fill(all, (byte)1);
        Assert.Equal(InpaintMethod.None, Inpainter.Fill(px, 10, 10, all));
        Assert.Equal(copy, px);
    }

    [Fact]
    public void Performance_TextLineHolesOn4K()
    {
        const int w = 3840, h = 2160;
        // Text line on a flat background, on a gradient and on a texture (paper / guilloche-like lines).
        var flat = BitmapUtil.FromPixels(Image(w, h, (_, _) => (245, 245, 245)), w, h);
        var grad = BitmapUtil.FromPixels(Image(w, h, (x, y) => ((byte)(60 + y * 150 / h), (byte)(120 + x * 100 / w), 200)), w, h);
        var tex = BitmapUtil.FromPixels(Image(w, h, (x, y) =>
        {
            double s = Math.Sin(x / 9.0 + 6 * Math.Sin(y / 23.0));
            byte v = (byte)(200 + 30 * s);
            return ((byte)(v - 40), (byte)(v - 10), v);
        }), w, h);
        var shape = new SmartEraseShape(DrawMode.Rect, new Rect(1200, 900, 900, 48), Array.Empty<Point>(), 0);
        foreach (var (img, budget) in new[] { (flat, 300.0), (grad, 600.0), (tex, 1500.0) })
        {
            var area = shape.WorkRect(w, h);
            var buf = PixelEffects.ReadRegion(img, area);
            SmartErasePatch.Compute(shape, (byte[])buf.Clone(), area, w, h); // warm up (JIT)
            var sw = Stopwatch.StartNew();
            var patch = SmartErasePatch.Compute(shape, buf, area, w, h);
            sw.Stop();
            Assert.NotEqual(InpaintMethod.None, patch.Method);
            // Generous budget (CI machines are slow); the target on a desktop is < 150 ms.
            Assert.True(sw.Elapsed.TotalMilliseconds < budget, $"{patch.Method}: {sw.Elapsed.TotalMilliseconds:0} ms");
        }
    }

    // ------------------------------------------------------------------ mask rasterisation

    [Fact]
    public void RectMask_CoversEveryTouchedPixel()
    {
        var m = SmartEraseMask.Rasterize(new SmartEraseShape(DrawMode.Rect, new Rect(10.3, 5, 20.4, 7.5), Array.Empty<Point>(), 0), 100, 100);
        Assert.Equal(new Int32Rect(10, 5, 21, 8), m.Bounds);
        Assert.All(m.Mask, v => Assert.Equal(1, v));
        // Clipped to the image.
        var c = SmartEraseMask.Rasterize(new SmartEraseShape(DrawMode.Rect, new Rect(-5, 95, 20, 20), Array.Empty<Point>(), 0), 100, 100);
        Assert.Equal(new Int32Rect(0, 95, 15, 5), c.Bounds);
        Assert.True(SmartEraseMask.Rasterize(new SmartEraseShape(DrawMode.Rect, new Rect(200, 200, 5, 5), Array.Empty<Point>(), 0), 100, 100).IsEmpty);
    }

    [Fact]
    public void BrushMask_IsAThickStroke()
    {
        var shape = new SmartEraseShape(DrawMode.Brush, Rect.Empty, new[] { new Point(20, 50), new Point(80, 50) }, 10);
        var m = SmartEraseMask.Rasterize(shape, 100, 100);
        bool In(int x, int y) => x >= m.Bounds.X && y >= m.Bounds.Y && x < m.Bounds.X + m.Bounds.Width && y < m.Bounds.Y + m.Bounds.Height
                                 && m.Mask[(y - m.Bounds.Y) * m.Bounds.Width + x - m.Bounds.X] != 0;
        Assert.True(In(50, 50));
        Assert.True(In(50, 45)); // within width/2
        Assert.True(In(16, 50)); // round cap
        Assert.False(In(50, 40));
        Assert.False(In(10, 50));
        Assert.True(shape.WorkRect(100, 100).Width >= m.Bounds.Width);
    }

    // ------------------------------------------------------------------ surface integration

    private static BitmapSource Flat(int w, int h, Color c) => BitmapUtil.FromPixels(Image(w, h, (_, _) => (c.B, c.G, c.R)), w, h);

    private static BitmapSource WithText(BitmapSource bg, Rect area)
    {
        var px = BitmapUtil.GetPixels(bg);
        int w = bg.PixelWidth;
        var r = new Random(1);
        for (int y = (int)area.Y + 2; y < (int)area.Bottom - 2; y++)
        for (int x = (int)area.X + 2; x < (int)area.Right - 2; x++)
            if (r.Next(2) == 0) { int o = (y * w + x) * 4; px[o] = px[o + 1] = px[o + 2] = 20; }
        return BitmapUtil.FromPixels(px, w, bg.PixelHeight);
    }

    [Fact]
    public void Surface_RenderErasesTextOnFlatBackground() => Sta.Run(() =>
    {
        var bg = Flat(160, 100, Color.FromRgb(0xF5, 0xF5, 0xF5));
        var img = WithText(bg, new Rect(20, 30, 100, 24));
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new MosaicItem { Mode = DrawMode.Rect, Rect = new Rect(20, 30, 100, 24), Effect = MosaicEffect.SmartErase });
        Assert.Equal(BitmapUtil.GetPixels(bg), BitmapUtil.GetPixels(s.Render()));
        // A crop is the same pixels.
        var crop = s.Render(new Int32Rect(10, 20, 60, 40));
        Assert.Equal(BitmapUtil.GetPixels(BitmapUtil.Crop(bg, new Int32Rect(10, 20, 60, 40))), BitmapUtil.GetPixels(crop));
    });

    [Fact]
    public void Surface_ScreenEqualsExport() => Sta.Run(() =>
    {
        var bg = BitmapUtil.FromPixels(Image(160, 120, (x, y) => ((byte)(x + 40), (byte)(y + 60), 150)), 160, 120);
        var img = WithText(bg, new Rect(30, 40, 90, 30));
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new MosaicItem { Mode = DrawMode.Brush, Points = { new Point(30, 55), new Point(120, 55) }, Width = 30, Effect = MosaicEffect.SmartErase });
        s.Document.Items.Add(new ShapeItem { Rect = new Rect(10, 10, 60, 40), StrokeWidth = 3, Color = Colors.Blue });
        s.RenderContext.SynchronousEffects = true; // on-screen visuals computed inline for the test
        s.SyncVisuals();
        var exported = s.Render();

        var grid = new Grid { Width = 160, Height = 120 };
        grid.Children.Add(new System.Windows.Controls.Image { Source = img, Stretch = Stretch.None });
        grid.Children.Add(s);
        grid.Measure(new Size(160, 120));
        grid.Arrange(new Rect(0, 0, 160, 120));
        grid.UpdateLayout();
        var rtb = new RenderTargetBitmap(160, 120, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(grid);
        var screen = BitmapUtil.GetPixels(BitmapUtil.ToBgra32(rtb));
        var export = BitmapUtil.GetPixels(exported);
        int maxDiff = 0;
        for (int i = 0; i < screen.Length; i++) maxDiff = Math.Max(maxDiff, Math.Abs(screen[i] - export[i]));
        Assert.True(maxDiff <= 2, $"max channel difference {maxDiff}");
        // And the text is gone: the gradient is back (smooth fill).
        var e = BitmapUtil.GetPixel(exported, 75, 55);
        var t = BitmapUtil.GetPixel(bg, 75, 55);
        Assert.True(Math.Abs(e.B - t.B) <= 3 && Math.Abs(e.G - t.G) <= 3, $"{e} vs {t}");
    });

    [Fact]
    public void Surface_GestureUndoRedoAndSecondaryEdit() => Sta.Run(() =>
    {
        var bg = Flat(200, 120, Colors.White);
        var img = WithText(bg, new Rect(40, 40, 80, 20));
        var s = new AnnotationSurface(img);
        s.Tool = AnnotationTool.Mosaic;
        var o = s.Options.Get(AnnotationTool.Mosaic); // per-surface options (not committed → no global side effects)
        o.Mode = DrawMode.Rect;
        o.Effect = MosaicEffect.SmartErase;
        s.PointerDown(new Point(38, 38), ModifierKeys.None);
        s.PointerMove(new Point(90, 50), ModifierKeys.None);
        s.PointerUp(new Point(122, 62), ModifierKeys.None);
        var item = Assert.IsType<MosaicItem>(Assert.Single(s.Document.Items));
        Assert.Equal(MosaicEffect.SmartErase, item.Effect);
        Assert.Equal(0, item.Layer); // under the other annotations
        Assert.Equal(BitmapUtil.GetPixels(bg), BitmapUtil.GetPixels(s.Render()));

        s.Undo();
        Assert.False(s.HasAnnotations);
        Assert.Equal(BitmapUtil.GetPixels(img), BitmapUtil.GetPixels(s.Render()));
        s.Redo();
        Assert.Equal(BitmapUtil.GetPixels(bg), BitmapUtil.GetPixels(s.Render()));

        // Secondary edit: shrink the rect by dragging its right handle → the right part of the text comes back.
        var sel = Assert.IsType<MosaicItem>(Assert.Single(s.Document.Items));
        var handle = sel.GetHandles().First(h => h.Id == 3);
        s.PointerDown(handle.Position, ModifierKeys.None);
        s.PointerMove(new Point(80, handle.Position.Y), ModifierKeys.None);
        s.PointerUp(new Point(80, handle.Position.Y), ModifierKeys.None);
        var outPx = s.Render();
        Assert.Equal(Colors.White, BitmapUtil.GetPixel(outPx, 60, 50));
        Assert.Equal(BitmapUtil.GetPixel(img, 110, 50), BitmapUtil.GetPixel(outPx, 110, 50));
        s.Undo();
        Assert.Equal(BitmapUtil.GetPixels(bg), BitmapUtil.GetPixels(s.Render()));
    });

    [Fact]
    public void LaterEraseSeesEarlierEraseResult() => Sta.Run(() =>
    {
        // Two overlapping erases: the second one's boundary lies mostly on text the first one erased. Computed on the
        // original image its ring would be ~25 % text; stacked on the first result it is clean → exact solid fill.
        var bg = Flat(200, 100, Color.FromRgb(0x95, 0xEC, 0x69));
        var img = WithText(bg, new Rect(20, 30, 70, 20));
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new MosaicItem { Mode = DrawMode.Rect, Rect = new Rect(20, 30, 70, 20), Effect = MosaicEffect.SmartErase });
        var second = new MosaicItem { Mode = DrawMode.Rect, Rect = new Rect(40, 25, 30, 30), Effect = MosaicEffect.SmartErase };
        s.Document.Items.Add(second);
        Assert.Equal(BitmapUtil.GetPixels(bg), BitmapUtil.GetPixels(s.Render()));

        // On its own (first erase deleted) the second one cannot be a solid fill.
        s.RenderContext.SynchronousEffects = true;
        Assert.Equal(InpaintMethod.Solid, s.RenderContext.GetSmartErase(second)!.Method);
        s.Document.Items.RemoveAt(0);
        Assert.NotEqual(InpaintMethod.Solid, s.RenderContext.GetSmartErase(second)!.Method);
    });

    [Fact]
    public void DraftItemIsNotComputed() => Sta.Run(() =>
    {
        var img = Flat(50, 50, Colors.White);
        var s = new AnnotationSurface(img);
        s.RenderContext.SynchronousEffects = true;
        var draft = new MosaicItem { Mode = DrawMode.Rect, Rect = new Rect(5, 5, 20, 20), Effect = MosaicEffect.SmartErase };
        Assert.Null(s.RenderContext.GetSmartErase(draft));
        s.Document.Items.Add(draft);
        var patch = s.RenderContext.GetSmartErase(draft);
        Assert.NotNull(patch);
        Assert.Equal(new Int32Rect(5, 5, 20, 20), patch!.Bounds);
        // Cached: same object, and a changed item gets a new key.
        Assert.Same(patch, s.RenderContext.GetSmartErase(draft));
        long key = s.RenderContext.SmartEraseKey(draft)!.Value;
        draft.Offset(new Vector(1, 0));
        draft.Touch();
        Assert.NotEqual(key, s.RenderContext.SmartEraseKey(draft));
    });
}
