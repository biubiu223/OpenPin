using System.Windows;
using System.Windows.Media;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;
using OpenPin.Annotation.Rendering;

namespace OpenPin.Tests.Annotation;

public class DocumentTests
{
    private static ShapeItem Rect(double x) => new() { Rect = new Rect(x, 0, 10, 10) };

    [Fact]
    public void UndoRedoRestoresStates()
    {
        var doc = new AnnotationDocument();
        Assert.False(doc.CanUndo);

        doc.Checkpoint();
        doc.Items.Add(Rect(0));
        doc.Checkpoint();
        doc.Items.Add(Rect(20));
        Assert.Equal(2, doc.Items.Count);

        Assert.True(doc.Undo());
        Assert.Single(doc.Items);
        Assert.True(doc.Undo());
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo());

        Assert.True(doc.Redo());
        Assert.Single(doc.Items);
        Assert.True(doc.Redo());
        Assert.Equal(2, doc.Items.Count);
        Assert.False(doc.Redo());
    }

    [Fact]
    public void SnapshotsAreDeepCopies()
    {
        var doc = new AnnotationDocument();
        var line = new LineItem { Points = { new Point(0, 0), new Point(10, 10) } };
        doc.Items.Add(line);
        doc.Checkpoint();
        line.Points[1] = new Point(50, 50);
        line.Touch();
        doc.Undo();
        var restored = (LineItem)doc.Items[0];
        Assert.Equal(new Point(10, 10), restored.Points[1]);
        Assert.Equal(line.Id, restored.Id);
        Assert.NotEqual(line.Version, restored.Version);
    }

    [Fact]
    public void NewChangeClearsRedo()
    {
        var doc = new AnnotationDocument();
        doc.Checkpoint();
        doc.Items.Add(Rect(0));
        doc.Undo();
        Assert.True(doc.CanRedo);
        doc.Checkpoint();
        doc.Items.Add(Rect(5));
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void CoalescedCheckpointsFormOneStep()
    {
        var doc = new AnnotationDocument();
        var r = Rect(0);
        doc.Items.Add(r);
        for (int i = 0; i < 5; i++)
        {
            doc.CheckpointCoalesced("size");
            r.StrokeWidth += 1;
        }
        Assert.True(doc.Undo());
        Assert.Equal(4, ((ShapeItem)doc.Items[0]).StrokeWidth);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void UndoDepthIsLimited()
    {
        var doc = new AnnotationDocument();
        for (int i = 0; i < AnnotationDocument.MaxUndo + 20; i++)
        {
            doc.Checkpoint();
            doc.Items.Add(Rect(i));
        }
        int n = 0;
        while (doc.Undo()) n++;
        Assert.Equal(AnnotationDocument.MaxUndo, n);
    }

    [Fact]
    public void CloneKeepsIdentityDuplicateDoesNot()
    {
        var s = new SerialItem { Value = 3 };
        var c = s.Clone();
        Assert.Equal(s.Id, c.Id);
        Assert.Equal(s.Version, c.Version);
        var d = s.Duplicate();
        Assert.NotEqual(s.Id, d.Id);
    }
}

public class ItemHitTests
{
    [Fact]
    public void RectangleOutlineHit()
    {
        var r = new ShapeItem { Rect = new Rect(10, 10, 100, 50), StrokeWidth = 4 };
        Assert.True(r.HitTest(new Point(10, 30), 2));
        Assert.True(r.HitTest(new Point(60, 61), 2));
        Assert.False(r.HitTest(new Point(60, 35), 2)); // inside but not filled
        r.Fill = true;
        Assert.True(r.HitTest(new Point(60, 35), 2));
        Assert.False(r.HitTest(new Point(200, 35), 2));
    }

    [Fact]
    public void RotatedRectangleHit()
    {
        var r = new ShapeItem { Rect = new Rect(0, 0, 100, 20), StrokeWidth = 2, Angle = 90 };
        // Rotated around its center (50,10): now a vertical bar from y=-40 to y=60 at x=40..60.
        Assert.True(r.HitTest(new Point(40, 0), 2));
        Assert.False(r.HitTest(new Point(0, 0), 2));
    }

    [Fact]
    public void LineAndArrowHit()
    {
        var l = new LineItem { Points = { new Point(0, 0), new Point(100, 0), new Point(100, 100) }, Width = 4 };
        Assert.True(l.HitTest(new Point(50, 3), 2));
        Assert.True(l.HitTest(new Point(102, 50), 2));
        Assert.False(l.HitTest(new Point(50, 50), 2));
        Assert.True(l.IntersectsRect(new Rect(40, -5, 10, 10)));
        Assert.False(l.IntersectsRect(new Rect(40, 40, 10, 10)));
    }

    [Fact]
    public void SerialHitIncludesPointer()
    {
        var s = new SerialItem { Center = new Point(50, 50), Size = 20, Tip = new Point(150, 50) };
        Assert.True(s.HitTest(new Point(55, 55), 1));
        Assert.True(s.HitTest(new Point(120, 51), 1));
        Assert.False(s.HitTest(new Point(120, 80), 1));
    }

    [Fact]
    public void EraserSegmentSweep()
    {
        var p = new PencilItem { Points = { new Point(50, 0), new Point(50, 100) }, Width = 2 };
        // A fast eraser stroke that jumps across the line must still hit it.
        Assert.True(p.HitTestSegment(new Point(0, 50), new Point(100, 50), 5));
        Assert.False(p.HitTestSegment(new Point(0, 150), new Point(100, 150), 5));
    }

    [Fact]
    public void ResizeHandleKeepsOppositeCorner()
    {
        var r = new ShapeItem { Rect = new Rect(10, 10, 100, 50) };
        var start = r.Clone();
        r.DragHandle(4, new Point(200, 100), System.Windows.Input.ModifierKeys.None, start); // bottom-right
        Assert.Equal(new Rect(10, 10, 190, 90), r.Rect);
        r.DragHandle(0, new Point(0, 0), System.Windows.Input.ModifierKeys.None, start); // top-left from the start state
        Assert.Equal(new Rect(0, 0, 110, 60), r.Rect);
    }

    [Fact]
    public void MagnifierConnectorsJoinSourceAndTarget()
    {
        var m = new MagnifierItem { Source = new Rect(0, 0, 10, 10), TargetTopLeft = new Point(50, 0), Zoom = 3 };
        var lines = m.ConnectorLines();
        Assert.Equal(2, lines.Count);
        foreach (var (a, b) in lines)
        {
            bool aSrc = a.X <= 10, bSrc = b.X <= 10;
            Assert.NotEqual(aSrc, bSrc);
        }
    }

    [Fact]
    public void WatermarkTimeVariables()
    {
        var t = new DateTime(2024, 3, 5, 7, 8, 9);
        Assert.Equal("截图 2024-03-05 07:08:09", WatermarkItem.Resolve("截图 $yyyy-MM-dd HH:mm:ss$", t));
        Assert.Equal("no vars", WatermarkItem.Resolve("no vars", t));
    }
}

public class PixelEffectTests
{
    private static byte[] Solid(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return px;
    }

    [Fact]
    public void PixelateAveragesAlignedBlocks()
    {
        int w = 4, h = 2;
        var px = new byte[w * h * 4];
        // Left 2x2 block: values 0 and 200 → average 100; right block: 50.
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            byte v = x < 2 ? (byte)((x + y) % 2 == 0 ? 0 : 200) : (byte)50;
            px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
        }
        var res = PixelEffects.Pixelate(px, w, h, 0, 0, 2);
        Assert.Equal(100, res[0]);
        Assert.Equal(100, res[(1 * w + 1) * 4]);
        Assert.Equal(50, res[(0 * w + 3) * 4]);
    }

    [Fact]
    public void PixelateIsAlignedToImageGrid()
    {
        // A region starting at x=1 with block 2 must split cells at image x=2 (region x=1).
        var px = new byte[4 * 1 * 4];
        byte[] vals = { 10, 20, 30, 40 };
        for (int x = 0; x < 4; x++) { px[x * 4] = vals[x]; px[x * 4 + 3] = 255; }
        var res = PixelEffects.Pixelate(px, 4, 1, 1, 0, 2);
        Assert.Equal(10, res[0]);               // cell [1] alone (image x=1)
        Assert.Equal(25, res[4]);               // cell [2,3] → (20+30)/2
        Assert.Equal(25, res[8]);
        Assert.Equal(40, res[12]);              // cell [4..] alone
    }

    [Fact]
    public void BlurOfConstantImageIsConstant()
    {
        var px = Solid(20, 10, 10, 120, 240);
        var res = PixelEffects.BoxBlur3(px, 20, 10, 3);
        Assert.Equal(px, res);
    }

    [Fact]
    public void BlurSpreadsAnEdge()
    {
        int w = 20, h = 1;
        var px = Solid(w, h, 0, 0, 0);
        for (int x = 10; x < w; x++) px[x * 4] = 255;
        var res = PixelEffects.BoxBlur3(px, w, h, 2);
        Assert.InRange(res[9 * 4], 60, 200);
        Assert.Equal(0, res[0]);
        Assert.Equal(255, res[19 * 4]);
    }

    [Fact]
    public void MultiplyWithWhiteIsIdentityAndWithBlackIsBlack()
    {
        var px = Solid(3, 3, 10, 128, 250);
        Assert.Equal(px, PixelEffects.Multiply(px, Colors.White));
        var black = PixelEffects.Multiply(px, Colors.Black);
        Assert.Equal(0, black[0]); Assert.Equal(0, black[1]); Assert.Equal(0, black[2]); Assert.Equal(255, black[3]);
        var yellow = PixelEffects.Multiply(Solid(1, 1, 200, 200, 200), Color.FromRgb(255, 255, 0));
        Assert.Equal(0, yellow[0]);   // blue removed
        Assert.Equal(200, yellow[1]); // green kept
        Assert.Equal(200, yellow[2]); // red kept
    }

    [Fact]
    public void DownscaleAverages()
    {
        var px = Solid(4, 4, 100, 100, 100);
        var res = PixelEffects.Downscale(px, 4, 4, 2, out int ow, out int oh);
        Assert.Equal(2, ow); Assert.Equal(2, oh);
        Assert.All(res.Where((_, i) => i % 4 == 0), v => Assert.Equal(100, v));
    }
}
