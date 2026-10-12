using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Capture;
using OpenPin.Pin;
using OpenPin.Pin.Model;
using OpenPin.Pin.Views;

namespace OpenPin.Tests.Pin;

public class PinPlacementTests
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

    private static BitmapSource CreateTestBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void Place_WithExplicitRect_SetsOriginToRect()
    {
        RunSta(() =>
        {
            var mgr = new PinManager();
            var m = new PinManifest { Id = "test-1", Kind = PinKind.Image };
            var body = new ImageBody(CreateTestBitmap(200, 150), null, m.Adjust);
            var rect = new Int32Rect(320, 180, 200, 150);

            mgr.Place(m, body, rect);

            Assert.Equal(320, m.OriginX);
            Assert.Equal(180, m.OriginY);
        });
    }

    [Fact]
    public void Place_CapturePosition_MatchesLastCaptureRegion()
    {
        RunSta(() =>
        {
            var mgr = new PinManager();
            mgr.Settings.DefaultPosition = PinDefaultPosition.CapturePosition;

            var capturedRegion = new Int32Rect(450, 220, 340, 260);
            CaptureController.History.AddRegion(capturedRegion, 10);

            var m = new PinManifest { Id = "test-2", Kind = PinKind.Image };
            var body = new ImageBody(CreateTestBitmap(340, 260), null, m.Adjust);

            mgr.Place(m, body, null);

            Assert.Equal(450, m.OriginX);
            Assert.Equal(220, m.OriginY);
        });
    }

    [Fact]
    public void Place_CapturePosition_CentersDifferentSizeOverCaptureRegion()
    {
        RunSta(() =>
        {
            var mgr = new PinManager();
            mgr.Settings.DefaultPosition = PinDefaultPosition.CapturePosition;

            var capturedRegion = new Int32Rect(400, 200, 300, 200); // Center is (550, 300)
            CaptureController.History.AddRegion(capturedRegion, 10);

            var m = new PinManifest { Id = "test-3", Kind = PinKind.Text };
            var body = new TextBody("Hello LaTeX & Pin", null, false);

            mgr.Place(m, body, null);

            // Center of placed rect should align with center of captured region (550, 300)
            double expectedCenterX = 550;
            double expectedCenterY = 300;
            double actualCenterX = m.OriginX + (body.ContentSize.Width * m.BaseScale * m.Zoom) / 2.0;
            double actualCenterY = m.OriginY + (body.ContentSize.Height * m.BaseScale * m.Zoom) / 2.0;

            Assert.InRange(actualCenterX, expectedCenterX - 2, expectedCenterX + 2);
            Assert.InRange(actualCenterY, expectedCenterY - 2, expectedCenterY + 2);
        });
    }
}
