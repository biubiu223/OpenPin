using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.Pin.Logic;
using OpenPin.Pin.Model;
using OpenPin.Recognition;
using OpenPin.Services;

namespace OpenPin.Pin.Views;

/// <summary>
/// Pinned LaTeX math formula (SPEC PRO-05).
/// Renders formula with <see cref="LatexRenderer"/> into a vector DrawingImage.
/// Right-click menu supports copying LaTeX source, copying as image, editing formula, and SVG export.
/// </summary>
internal sealed class LatexBody : PinBody
{
    private readonly Border _root;
    private readonly Image _image;
    private Size _size;
    private string _latex;

    public LatexBody(string latex)
    {
        _latex = latex;
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _root = new Border
        {
            Child = _image,
            Padding = new Thickness(12, 8, 12, 8),
            SnapsToDevicePixels = true,
        };
        _root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");

        Rebuild();
    }

    public string Latex => _latex;

    public override PinKind Kind => PinKind.Latex;

    public override FrameworkElement View => _root;

    public override Size ContentSize => _size;

    public void UpdateLatex(string newLatex)
    {
        if (string.Equals(_latex, newLatex, StringComparison.Ordinal)) return;
        _latex = newLatex;
        Rebuild();
        RaiseChanged();
    }

    private void Rebuild()
    {
        try
        {
            var drawing = LatexRenderer.RenderToDrawing(_latex, new LatexRenderOptions
            {
                FontSize = 24.0,
                Foreground = (Application.Current?.TryFindResource("Brush.Text") as Brush) ?? Brushes.Black,
            });
            _image.Source = new DrawingImage(drawing);
        }
        catch (Exception ex)
        {
            Log.Warn($"Rendering pinned formula failed: {ex.Message}");
            _image.Source = null;
        }

        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = _root.DesiredSize;
        _size = new Size(Math.Ceiling(Math.Max(40, d.Width)), Math.Ceiling(Math.Max(28, d.Height)));
        _root.Width = _size.Width;
        _root.Height = _size.Height;
    }

    public override BitmapSource RenderCurrent(double scale, bool smooth)
    {
        try
        {
            return LatexRenderer.RenderToBitmap(_latex, new LatexRenderOptions
            {
                FontSize = 24.0,
                Scale = Math.Max(0.1, scale),
                Foreground = Brushes.Black,
                Background = Brushes.White,
            });
        }
        catch
        {
            return base.RenderCurrent(scale, smooth);
        }
    }

    public override void CopyToClipboard() => ClipboardService.SetText(_latex);

    public override void FillDataObject(DataObject data) => data.SetText(_latex);

    public override bool HandleDoubleClick(Point contentPos)
    {
        EditFormula();
        return true;
    }

    public override IEnumerable<object> MenuItems(Point contentPos, PinMenuFactory f)
    {
        yield return f.Item("Pin.Latex.Copy", "", null, CopyToClipboard);
        yield return f.Item("Pin.Latex.CopyImage", "", null, CopyImage);
        yield return f.Item("Pin.Latex.Edit", "", null, EditFormula);
        yield return f.Item("Recognition.Latex.ExportSvg", "", null, ExportSvg);
    }

    private void CopyImage()
    {
        try
        {
            var bmp = LatexRenderer.RenderToBitmap(_latex, new LatexRenderOptions
            {
                FontSize = 32.0,
                Foreground = Brushes.Black,
                Background = Brushes.White,
            });
            ClipboardService.SetImage(bmp);
            Notify.Info(Loc.Get("Recognition.Latex.ImageCopied"));
        }
        catch (Exception ex)
        {
            Log.Error("Copy formula image failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    private void EditFormula()
    {
        LatexResultWindow.Show(_latex, onEdited: updated => UpdateLatex(updated));
    }

    private void ExportSvg()
    {
        try
        {
            string svg = LatexRenderer.RenderToSvg(_latex, new LatexRenderOptions
            {
                FontSize = 28.0,
                Foreground = Brushes.Black,
            });

            var dlg = new SaveFileDialog
            {
                Filter = "SVG (*.svg)|*.svg",
                FileName = "formula.svg",
                DefaultExt = ".svg",
            };

            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, svg, Encoding.UTF8);
                Notify.Info(Loc.Format("Recognition.Table.Exported", dlg.FileName));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Export formula SVG failed", ex);
            Notify.Error(Loc.Format("Recognition.Table.ExportFailed", ex.Message));
        }
    }

    public override void SaveTo(PinManifest m)
    {
        m.Latex = _latex;
    }

    public override string ContentHash() => PinImageOps.Hash("latex", _latex);
}
