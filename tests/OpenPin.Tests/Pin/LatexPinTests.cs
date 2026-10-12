using System.Windows;
using System.Windows.Controls;
using OpenPin.Core;
using OpenPin.Pin.Model;
using OpenPin.Pin.Views;
using OpenPin.Recognition;
using OpenPin.Services;

namespace OpenPin.Tests.Pin;

public class LatexPinTests
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
    public void LatexBody_MeasuresContentAndRenders()
    {
        RunSta(() =>
        {
            var body = new LatexBody(@"\frac{1}{2} + \sqrt{x}");
            Assert.Equal(PinKind.Latex, body.Kind);
            Assert.Equal(@"\frac{1}{2} + \sqrt{x}", body.Latex);
            Assert.True(body.ContentSize.Width >= 40);
            Assert.True(body.ContentSize.Height >= 28);

            // Render current bitmap
            var bmp = body.RenderCurrent(1.0, true);
            Assert.NotNull(bmp);
            Assert.True(bmp.PixelWidth > 0);
            Assert.True(bmp.PixelHeight > 0);

            // Update formula
            var prevSize = body.ContentSize;
            body.UpdateLatex(@"\sum_{i=1}^{100} i^2 = \frac{100 \times 101 \times 201}{6}");
            Assert.Contains(@"\sum", body.Latex);
            Assert.True(body.ContentSize.Width > 0);

            // SaveTo manifest
            var manifest = new PinManifest();
            body.SaveTo(manifest);
            Assert.Equal(body.Latex, manifest.Latex);

            // Content hash
            Assert.False(string.IsNullOrEmpty(body.ContentHash()));
        });
    }

    [Fact]
    public void LatexBody_HasExpectedMenuItems()
    {
        RunSta(() =>
        {
            var body = new LatexBody(@"E = mc^2");
            var f = new PinMenuFactory(null!);
            var items = body.MenuItems(new Point(0, 0), f).OfType<MenuItem>().ToList();

            // Menu items should contain: Copy LaTeX, Copy Image, Edit, Export SVG
            Assert.Equal(4, items.Count);
        });
    }

    [Fact]
    public void PinManifest_LatexKind_RoundTripsThroughJson()
    {
        var m = new PinManifest
        {
            Id = "latex-pin-1",
            Kind = PinKind.Latex,
            Latex = @"\int_0^1 x^2 dx = \frac{1}{3}",
            OriginX = 100,
            OriginY = 200,
            Zoom = 1.0,
            BaseScale = 1.0,
        };

        string json = m.ToJson();
        var back = PinManifest.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(PinKind.Latex, back!.Kind);
        Assert.Equal(m.Latex, back.Latex);
        Assert.Equal(m.Id, back.Id);
    }

    [Fact]
    public void LatexPinContent_RecordCreation()
    {
        var content = new LatexPinContent(@"\alpha + \beta");
        Assert.Equal(@"\alpha + \beta", content.Latex);
        Assert.IsAssignableFrom<PinContent>(content);
    }
}
