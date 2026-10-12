using System.Windows.Media;
using System.Xml.Linq;
using OpenPin.Recognition;

namespace OpenPin.Tests.Recognition;

public class LatexRendererTests
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
    public void RenderToDrawing_SimpleFormula_ProducesValidDrawingGroup()
    {
        RunSta(() =>
        {
            var drawing = LatexRenderer.RenderToDrawing(@"E = mc^2");
            Assert.NotNull(drawing);
            Assert.True(drawing.IsFrozen);
            Assert.True(drawing.Bounds.Width > 0);
            Assert.True(drawing.Bounds.Height > 0);
        });
    }

    [Fact]
    public void RenderToDrawing_FractionAndRoot_RendersChildren()
    {
        RunSta(() =>
        {
            var drawing = LatexRenderer.RenderToDrawing(@"\frac{-b \pm \sqrt{b^2 - 4ac}}{2a}");
            Assert.NotNull(drawing);
            Assert.True(drawing.Bounds.Width > 20);
            Assert.True(drawing.Bounds.Height > 20);
        });
    }

    [Fact]
    public void RenderToDrawing_BigOperatorAndSum_RendersCorrectly()
    {
        RunSta(() =>
        {
            var drawing = LatexRenderer.RenderToDrawing(@"\sum_{i=1}^n i = \frac{n(n+1)}{2}");
            Assert.NotNull(drawing);
            Assert.True(drawing.Bounds.Width > 30);
            Assert.True(drawing.Bounds.Height > 20);
        });
    }

    [Fact]
    public void RenderToBitmap_ProducesNonEmptyBitmapSource()
    {
        RunSta(() =>
        {
            var bmp = LatexRenderer.RenderToBitmap(@"\int_0^\infty e^{-x} dx = 1");
            Assert.NotNull(bmp);
            Assert.True(bmp.PixelWidth > 10);
            Assert.True(bmp.PixelHeight > 10);
            Assert.True(bmp.IsFrozen);
        });
    }

    [Fact]
    public void RenderToBitmap_WithOptions_AppliesScaleAndBackground()
    {
        RunSta(() =>
        {
            var options = new LatexRenderOptions
            {
                FontSize = 30.0,
                Scale = 2.0,
                Foreground = Brushes.Navy,
                Background = Brushes.White,
            };
            var bmp = LatexRenderer.RenderToBitmap(@"x^2 + y^2 = r^2", options);
            Assert.NotNull(bmp);
            Assert.True(bmp.PixelWidth > 50);
            Assert.True(bmp.PixelHeight > 20);
        });
    }

    [Fact]
    public void RenderToSvg_ProducesValidXmlDocument()
    {
        RunSta(() =>
        {
            string svg = LatexRenderer.RenderToSvg(@"\frac{\alpha}{\beta} + \sqrt{x}");
            Assert.NotNull(svg);
            Assert.StartsWith("<svg", svg.TrimStart());
            Assert.EndsWith("</svg>", svg.TrimEnd());

            // Validate that the SVG output parses as well-formed XML
            var doc = XDocument.Parse(svg);
            Assert.NotNull(doc.Root);
            Assert.Equal("svg", doc.Root.Name.LocalName);

            // Check that it contains text and line elements
            XNamespace ns = "http://www.w3.org/2000/svg";
            var textElements = doc.Descendants(ns + "text").ToList();
            var lineElements = doc.Descendants(ns + "line").ToList();
            Assert.NotEmpty(textElements);
            Assert.NotEmpty(lineElements);
        });
    }

    [Fact]
    public void RenderToSvg_EscapesSpecialCharacters()
    {
        RunSta(() =>
        {
            string svg = LatexRenderer.RenderToSvg(@"a < b \text{ and } c > d");
            var doc = XDocument.Parse(svg);
            Assert.NotNull(doc);
            Assert.Contains("&lt;", svg);
            Assert.Contains("&gt;", svg);
        });
    }
}
