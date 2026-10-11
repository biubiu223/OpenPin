using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Annotation;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;
using OpenPin.Core.Imaging;

namespace OpenPin.Tests.Annotation;

/// <summary>WPF objects need an STA thread; xUnit runs tests on MTA threads.</summary>
internal static class Sta
{
    public static void Run(Action action)
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
}

public class SurfaceTests
{
    private static BitmapSource Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)(x * 255 / Math.Max(1, w - 1));
            px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
            px[i + 2] = (byte)((x + y) % 256);
            px[i + 3] = 255;
        }
        return BitmapUtil.FromPixels(px, w, h);
    }

    private static Color Pixel(BitmapSource b, int x, int y) => BitmapUtil.GetPixel(b, x, y);

    [Fact]
    public void SurfaceTakesImageSizeAndToolControlsHitTesting() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(64, 32));
        Assert.Equal(64, s.Width);
        Assert.Equal(32, s.Height);
        Assert.False(s.IsHitTestVisible);
        int toolChanged = 0;
        s.ToolChanged += (_, _) => toolChanged++;
        s.Tool = AnnotationTool.Rectangle;
        Assert.True(s.IsHitTestVisible);
        s.Tool = AnnotationTool.None;
        Assert.False(s.IsHitTestVisible);
        Assert.Equal(2, toolChanged);
    });

    [Fact]
    public void RenderWithoutAnnotationsEqualsBase() => Sta.Run(() =>
    {
        var img = Gradient(40, 30);
        var s = new AnnotationSurface(img);
        var full = s.Render();
        Assert.Equal(40, full.PixelWidth);
        Assert.Equal(30, full.PixelHeight);
        Assert.Equal(PixelFormats.Bgra32, full.Format);
        Assert.Equal(96, full.DpiX);
        Assert.Equal(BitmapUtil.GetPixels(img), BitmapUtil.GetPixels(full));

        var crop = s.Render(new Int32Rect(5, 6, 10, 8));
        Assert.Equal(10, crop.PixelWidth);
        Assert.Equal(8, crop.PixelHeight);
        Assert.Equal(Pixel(img, 5, 6), Pixel(crop, 0, 0));
        Assert.Equal(Pixel(img, 14, 13), Pixel(crop, 9, 7));
    });

    [Fact]
    public void RenderDrawsAnnotationsAtImagePixels() => Sta.Run(() =>
    {
        var img = Gradient(100, 80);
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new ShapeItem { Rect = new Rect(10, 10, 40, 30), StrokeWidth = 4, Fill = true, Color = Colors.Red });
        var outImg = s.Render();
        Assert.Equal(Colors.Red, Pixel(outImg, 30, 25));        // inside the filled rect
        Assert.Equal(Pixel(img, 80, 70), Pixel(outImg, 80, 70)); // untouched elsewhere

        // Crop is the same pixels as the full render.
        var crop = s.Render(new Int32Rect(20, 20, 30, 30));
        Assert.Equal(Pixel(outImg, 25, 25), Pixel(crop, 5, 5));
        Assert.Equal(Pixel(outImg, 49, 49), Pixel(crop, 29, 29));
    });

    [Fact]
    public void RenderMatchesOnScreenComposition() => Sta.Run(() =>
    {
        var img = Gradient(160, 120);
        var s = new AnnotationSurface(img);
        var items = s.Document.Items;
        items.Add(new ShapeItem { Rect = new Rect(10, 10, 60, 40), StrokeWidth = 3, Color = Colors.Blue, Dash = LineDash.Dash });
        items.Add(new LineItem { IsArrow = true, Points = { new Point(20, 100), new Point(140, 20) }, Width = 5, Color = Colors.Red });
        items.Add(new TextItem { Position = new Point(80, 60), Text = "Hi 你好", FontSize = 18, Color = Colors.Black, Outline = true });
        items.Add(new MosaicItem { Mode = DrawMode.Rect, Rect = new Rect(90, 10, 50, 30), Strength = 2 });
        items.Add(new HighlighterItem { Mode = DrawMode.Brush, Points = { new Point(10, 70), new Point(70, 70) }, Width = 12, Color = Colors.Yellow });
        items.Add(new SerialItem { Center = new Point(130, 100), Value = 7, Size = 24, Color = Colors.Green });
        items.Add(new SpotlightItem { Rect = new Rect(5, 5, 150, 110), Opacity = 0.3 });
        s.SyncVisuals();

        var exported = s.Render();

        var grid = new Grid { Width = 160, Height = 120 };
        grid.Children.Add(new Image { Source = img, Stretch = Stretch.None });
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
        // Same drawing code; only compositing rounding may differ.
        Assert.True(maxDiff <= 2, $"max channel difference {maxDiff}");
    });

    [Fact]
    public void MultiplyHighlighterWithWhiteKeepsBase() => Sta.Run(() =>
    {
        var img = Gradient(50, 50);
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new HighlighterItem { Mode = DrawMode.Rect, Rect = new Rect(0, 0, 50, 50), Color = Colors.White });
        Assert.Equal(BitmapUtil.GetPixels(img), BitmapUtil.GetPixels(s.Render()));
    });

    [Fact]
    public void EditBoundsClipsRendering() => Sta.Run(() =>
    {
        var img = Gradient(100, 100);
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new ShapeItem { Rect = new Rect(0, 0, 100, 100), Fill = true, Color = Colors.Red });
        s.EditBounds = new Rect(20, 20, 30, 30);
        var outImg = s.Render();
        Assert.Equal(Colors.Red, Pixel(outImg, 30, 30));
        Assert.Equal(Pixel(img, 10, 10), Pixel(outImg, 10, 10));
        Assert.Equal(Pixel(img, 60, 60), Pixel(outImg, 60, 60));
    });

    [Fact]
    public void ReplacingBaseImageKeepsAnnotations() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(40, 40));
        s.Document.Items.Add(new MosaicItem { Mode = DrawMode.Rect, Rect = new Rect(0, 0, 20, 20) });
        s.BaseImage = Gradient(60, 30);
        Assert.Equal(60, s.Width);
        Assert.True(s.HasAnnotations);
        var r = s.Render();
        Assert.Equal(60, r.PixelWidth);
    });

    [Fact]
    public void DrawRectangleWithGestureThenUndoRedo() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(200, 200));
        int changed = 0;
        s.Changed += (_, _) => changed++;
        s.Tool = AnnotationTool.Rectangle;
        Assert.True(s.PointerDown(new Point(10, 10), ModifierKeys.None));
        Assert.True(s.IsBusy);
        s.PointerMove(new Point(50, 30), ModifierKeys.None);
        s.PointerMove(new Point(60, 40), ModifierKeys.Shift); // Shift → square
        s.PointerUp(new Point(60, 40), ModifierKeys.Shift);
        Assert.False(s.IsBusy);
        Assert.True(s.HasAnnotations);
        var rect = Assert.IsType<ShapeItem>(Assert.Single(s.Document.Items));
        Assert.Equal(new Rect(10, 10, 50, 50), rect.Rect);
        Assert.True(changed > 0);

        Assert.True(s.CanUndo);
        s.Undo();
        Assert.False(s.HasAnnotations);
        Assert.True(s.CanRedo);
        s.Redo();
        Assert.True(s.HasAnnotations);
    });

    [Fact]
    public void GesturesStartOnlyInsideEditBounds() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(200, 200)) { EditBounds = new Rect(50, 50, 100, 100) };
        s.Tool = AnnotationTool.Rectangle;
        Assert.False(s.PointerDown(new Point(10, 10), ModifierKeys.None));
        Assert.False(s.IsBusy);
        Assert.True(s.PointerDown(new Point(60, 60), ModifierKeys.None));
        s.PointerMove(new Point(500, 500), ModifierKeys.None);
        s.PointerUp(new Point(500, 500), ModifierKeys.None);
        var rect = Assert.IsType<ShapeItem>(Assert.Single(s.Document.Items));
        Assert.Equal(new Rect(60, 60, 90, 90), rect.Rect); // clamped to the edit bounds
    });

    [Fact]
    public void ArrowWithShiftSnapsTo45Degrees() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(300, 300));
        s.Tool = AnnotationTool.Arrow;
        s.PointerDown(new Point(100, 100), ModifierKeys.None);
        s.PointerMove(new Point(200, 110), ModifierKeys.Shift);
        s.PointerUp(new Point(200, 110), ModifierKeys.Shift);
        var arrow = Assert.IsType<LineItem>(Assert.Single(s.Document.Items));
        Assert.True(arrow.IsArrow);
        Assert.Equal(100, arrow.Points[1].Y, 6);
    });

    [Fact]
    public void PolylineByClicks() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(300, 300));
        s.Tool = AnnotationTool.Line;
        // Click without drag starts polyline mode.
        s.PointerDown(new Point(10, 10), ModifierKeys.None);
        s.PointerUp(new Point(10, 10), ModifierKeys.None);
        Assert.True(s.IsBusy);
        s.PointerMove(new Point(100, 10), ModifierKeys.None, leftPressed: false);
        s.PointerDown(new Point(100, 10), ModifierKeys.None, 1);
        s.PointerMove(new Point(100, 100), ModifierKeys.None, leftPressed: false);
        s.PointerDown(new Point(100, 100), ModifierKeys.None, 1);
        s.PointerDown(new Point(100, 100), ModifierKeys.None, 2); // double-click finishes
        Assert.False(s.IsBusy);
        var line = Assert.IsType<LineItem>(Assert.Single(s.Document.Items));
        Assert.Equal(new[] { new Point(10, 10), new Point(100, 10), new Point(100, 100) }, line.Points);
    });

    [Fact]
    public void SerialNumbersIncrementAndRenumberAfterDelete() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(300, 300));
        s.Tool = AnnotationTool.Serial;
        for (int i = 0; i < 3; i++)
        {
            s.PointerDown(new Point(20 + i * 60, 20), ModifierKeys.None);
            s.PointerUp(new Point(20 + i * 60, 20), ModifierKeys.None);
        }
        var serials = s.Document.Items.OfType<SerialItem>().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, serials.Select(x => x.Value));
        Assert.Equal(4, s.NextSerialNumber);

        // Select the second marker by clicking it, then delete.
        s.PointerDown(new Point(80, 20), ModifierKeys.None);
        s.PointerUp(new Point(80, 20), ModifierKeys.None);
        Assert.True(s.HasSelection);
        Assert.True(s.DeleteSelected());
        Assert.Equal(new[] { 1, 2 }, s.Document.Items.OfType<SerialItem>().Select(x => x.Value));
        Assert.Equal(3, s.NextSerialNumber);

        // Toolbar override of the next number.
        s.NextSerialNumber = 10;
        s.PointerDown(new Point(200, 200), ModifierKeys.None);
        s.PointerUp(new Point(200, 200), ModifierKeys.None);
        Assert.Equal(10, s.Document.Items.OfType<SerialItem>().Last().Value);
        Assert.Equal(11, s.NextSerialNumber);
    });

    [Fact]
    public void SecondaryEditMovesItemWithMatchingTool() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(300, 300));
        var rect = new ShapeItem { Rect = new Rect(50, 50, 100, 60), StrokeWidth = 4 };
        s.Document.Items.Add(rect);
        s.SyncVisuals();

        // A non-matching tool draws a new item instead of editing.
        s.Tool = AnnotationTool.Pencil;
        s.PointerDown(new Point(50, 80), ModifierKeys.None);
        s.PointerUp(new Point(50, 80), ModifierKeys.None);
        Assert.Equal(2, s.Document.Items.Count);
        s.Undo();

        s.Tool = AnnotationTool.Rectangle;
        s.PointerDown(new Point(50, 80), ModifierKeys.None); // on the left edge
        s.PointerMove(new Point(60, 90), ModifierKeys.None);
        s.PointerUp(new Point(60, 90), ModifierKeys.None);
        var moved = Assert.IsType<ShapeItem>(Assert.Single(s.Document.Items));
        Assert.Equal(new Rect(60, 60, 100, 60), moved.Rect);
        s.Undo();
        Assert.Equal(new Rect(50, 50, 100, 60), ((ShapeItem)s.Document.Items[0]).Rect);
    });

    [Fact]
    public void EraserBrushDeletesTouchedItems() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(300, 300));
        s.Document.Items.Add(new PencilItem { Points = { new Point(100, 0), new Point(100, 300) }, Width = 3 });
        s.Document.Items.Add(new PencilItem { Points = { new Point(250, 0), new Point(250, 300) }, Width = 3 });
        s.Tool = AnnotationTool.Eraser;
        s.Options.Get(AnnotationTool.Eraser).Mode = DrawMode.Brush;
        s.PointerDown(new Point(50, 150), ModifierKeys.None);
        s.PointerMove(new Point(150, 150), ModifierKeys.None);
        s.PointerUp(new Point(150, 150), ModifierKeys.None);
        var left = Assert.IsType<PencilItem>(Assert.Single(s.Document.Items));
        Assert.Equal(250, left.Points[0].X);
        s.Undo();
        Assert.Equal(2, s.Document.Items.Count);
    });

    [Fact]
    public void PartialEraserCutsAnnotation() => Sta.Run(() =>
    {
        var img = Gradient(200, 100);
        var s = new AnnotationSurface(img);
        s.Document.Items.Add(new PencilItem { Points = { new Point(10, 50), new Point(190, 50) }, Width = 6, Color = Colors.Red });
        s.Tool = AnnotationTool.Eraser;
        var o = s.Options.Get(AnnotationTool.Eraser);
        o.Mode = DrawMode.Partial;
        o.Size = 20;
        s.PointerDown(new Point(100, 30), ModifierKeys.None);
        s.PointerMove(new Point(100, 70), ModifierKeys.None);
        s.PointerUp(new Point(100, 70), ModifierKeys.None);
        var line = Assert.Single(s.Document.Items);
        Assert.NotNull(line.EraseMask);
        var r = s.Render();
        Assert.Equal(Colors.Red, Pixel(r, 30, 50));
        Assert.Equal(Pixel(img, 100, 50), Pixel(r, 100, 50));
        s.Undo();
        Assert.Null(s.Document.Items[0].EraseMask);
        o.Mode = DrawMode.Brush;
    });

    [Fact]
    public void SnapRectProviderCreatesRectangleOnRightClick() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(300, 300)) { SnapRectProvider = _ => new Rect(20, 30, 100, 50) };
        s.Tool = AnnotationTool.Mosaic;
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent };
        s.RaiseEvent(args);
        var m = Assert.IsType<MosaicItem>(Assert.Single(s.Document.Items));
        Assert.Equal(new Rect(20, 30, 100, 50), m.Rect);
        Assert.True(args.Handled);
    });

    [Fact]
    public void ClearAllIsUndoable() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(50, 50));
        s.Document.Items.Add(new ShapeItem { Rect = new Rect(1, 1, 10, 10) });
        s.Document.Items.Add(new ShapeItem { Rect = new Rect(5, 5, 10, 10) });
        s.ClearAll();
        Assert.False(s.HasAnnotations);
        s.Undo();
        Assert.Equal(2, s.Document.Items.Count);
    });

    [Fact]
    public void WheelAdjustsToolSizeAndAlpha() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(50, 50));
        var o = s.Options.Get(AnnotationTool.Pencil);
        o.Size = 4;
        o.ColorValue = Colors.Red;
        s.AdjustByWheel(AnnotationTool.Pencil, 1, ctrl: false);
        Assert.Equal(5, o.Size);
        s.AdjustByWheel(AnnotationTool.Pencil, -1, ctrl: true);
        Assert.True(o.ColorValue.A < 255);
    });

    [Fact]
    public void ToolShortcutMapping()
    {
        Assert.Equal(AnnotationTool.Rectangle, AnnotationSurface.ToolForDigit(Key.D1));
        Assert.Equal(AnnotationTool.Mosaic, AnnotationSurface.ToolForDigit(Key.D7));
        Assert.Equal(AnnotationTool.Spotlight, AnnotationSurface.ToolForDigit(Key.D0));
        Assert.Null(AnnotationSurface.ToolForDigit(Key.A));
    }

    [Fact]
    public void ShiftOneTogglesRectangleAndEllipse() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(20, 20));
        s.Tool = AnnotationTool.Rectangle;
        s.ToggleTool(AnnotationTool.Rectangle);
        Assert.Equal(AnnotationTool.Ellipse, s.Tool);
        s.ToggleTool(AnnotationTool.Rectangle);
        Assert.Equal(AnnotationTool.Rectangle, s.Tool);
        s.ToggleTool(AnnotationTool.Arrow);
        Assert.Equal(AnnotationTool.Arrow, s.Tool);
        s.ToggleTool(AnnotationTool.Arrow);
        Assert.Equal(AnnotationTool.None, s.Tool);
    });

    [Fact]
    public void ToolbarBindsAndShowsOptionsRow() => Sta.Run(() =>
    {
        var s = new AnnotationSurface(Gradient(20, 20));
        var tb = new AnnotationToolbar { Surface = s };
        tb.HostActions.Add(new ToolbarAction("x", "\uE711", "close", () => { }));
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var h0 = tb.DesiredSize.Height;
        s.Tool = AnnotationTool.Text;
        tb.UpdateLayout();
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(tb.DesiredSize.Height > h0, "options row should appear when a tool is active");
        tb.Surface = null;
        tb.Surface = s;
    });
}
