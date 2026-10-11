using System.Windows;
using System.Windows.Media;
using OpenPin.Core.Imaging;
using OpenPin.Pin;
using OpenPin.Pin.Model;
using OpenPin.Pin.Views;

namespace OpenPin.Tests.Pin;

public class PinViewsTests
{
    private static void RunSta(Action action)
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
        if (error != null) throw new Xunit.Sdk.XunitException("STA action failed: " + error);
    }

    [Fact]
    public void Bodies_MeasureTheirContent()
    {
        RunSta(() =>
        {
            var px = new byte[40 * 30 * 4];
            var img = new ImageBody(BitmapUtil.FromPixels(px, 40, 30), null, new ImageAdjustments());
            Assert.Equal(new Size(40, 30), img.ContentSize);
            Assert.False(img.HasEdits);
            img.Adjust.RotateRight();
            img.Recompute();
            Assert.Equal(new Size(30, 40), img.ContentSize);
            img.Bake(BitmapUtil.FromPixels(new byte[10 * 10 * 4], 10, 10));
            Assert.True(img.HasEdits);
            Assert.True(img.Adjust.IsIdentity);
            Assert.Equal(40, img.RenderOriginal(1).PixelWidth);
            Assert.Equal(20, img.RenderCurrent(2, false).PixelWidth);

            var text = new TextBody("hello\nworld", "<b>hello</b><br>world", false);
            Assert.True(text.ContentSize.Width > 24 && text.ContentSize.Height > 16);
            var before = text.ContentSize;
            text.SetIgnoreFormat(true);
            Assert.True(text.IgnoreFormat);
            Assert.True(text.ContentSize.Height > 0 && before.Height > 0);

            var color = new ColorBody(Color.FromRgb(41, 184, 219), "#29B8DB");
            Assert.Equal("#29B8DB", color.Format(ColorFormat.Hex));
            Assert.True(color.ContentSize.Width >= 168);
            var m = new PinManifest();
            color.SaveTo(m);
            Assert.Equal("#FF29B8DB", m.Color);

            var files = new FilesBody(new[] { @"C:\Windows", @"C:\Windows", @"Z:\does\not\exist.txt" });
            Assert.Equal(2, files.Paths.Count);
            Assert.True(files.ContentSize.Height >= 40);
        });
    }

    [Fact]
    public void Dialogs_Construct()
    {
        RunSta(() =>
        {
            var book = new PinGroupBook();
            book.EnsureValid("默认组");
            var g = book.Create("工作", "#1677FF")!;
            _ = new NewGroupDialog(book);
            _ = new DeleteGroupDialog(book, g, 3);
            _ = new InputDialog("t", "p", "v");
            _ = new ManageGroupsDialog(new PinManager());
            _ = new MessageDialog("t", "m", "ok");
        });
    }
}
