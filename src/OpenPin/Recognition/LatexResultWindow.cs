using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.Services;
using OpenPin.Translate;

namespace OpenPin.Recognition;

/// <summary>
/// Fluent tool window displaying recognized LaTeX formula, editable code, live preview,
/// and buttons to copy LaTeX, copy image, pin, or export SVG (SPEC PRO-05).
/// </summary>
public sealed class LatexResultWindow : Window
{
    private readonly TextBox _codeBox;
    private readonly Image _previewImage = new();
    private readonly TextBlock _status = TranslateUi.Caption();
    private readonly Action<string>? _onEdited;
    private readonly string _source;

    public static LatexResultWindow Show(string latex, Action<string>? onEdited = null, string? source = null)
    {
        var w = new LatexResultWindow(latex, onEdited, source);
        w.Show();
        w.Activate();
        return w;
    }

    private LatexResultWindow(string latex, Action<string>? onEdited, string? source)
    {
        _onEdited = onEdited;
        _source = source ?? "ocr";

        SetResourceReference(TitleProperty, "Recognition.Latex.Title");
        TranslateUi.SetupWindow(this, 720, 520);

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 160 });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // splitter gap
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(130), MinHeight = 90 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // bottom bar

        // Top: Formula Preview
        var previewDock = new DockPanel();
        var previewHeader = TranslateUi.Header("Recognition.Latex.Preview");
        previewHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(previewHeader, Dock.Top);
        previewDock.Children.Add(previewHeader);

        _previewImage.Stretch = Stretch.Uniform;
        _previewImage.SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);

        var previewScroll = new ScrollViewer
        {
            Content = _previewImage,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16),
        };
        var previewCard = TranslateUi.Card(previewScroll);
        previewCard.MinHeight = 140;
        previewDock.Children.Add(previewCard);
        Grid.SetRow(previewDock, 0);
        grid.Children.Add(previewDock);

        // Middle: Editable LaTeX Code
        var codeDock = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var codeHeader = TranslateUi.Header("Recognition.Latex.Code");
        codeHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(codeHeader, Dock.Top);
        codeDock.Children.Add(codeHeader);

        _codeBox = TranslateUi.TextArea(readOnly: false);
        _codeBox.Text = latex;
        _codeBox.AcceptsReturn = true;
        _codeBox.TextWrapping = TextWrapping.Wrap;
        _codeBox.Padding = new Thickness(8, 6, 8, 6);
        _codeBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _codeBox.TextChanged += (_, _) => UpdatePreview();
        var codeCard = TranslateUi.Card(_codeBox);
        codeDock.Children.Add(codeCard);
        Grid.SetRow(codeDock, 2);
        grid.Children.Add(codeDock);

        // Bottom: Status and Actions
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var copyBtn = TranslateUi.Button("Recognition.Latex.Copy", accent: true, CopyLatex, "");
        var copyImgBtn = TranslateUi.Button("Recognition.Latex.CopyImage", accent: false, CopyImage, "");
        var pinBtn = TranslateUi.Button("Recognition.Latex.Pin", accent: false, PinFormula, "");
        var svgBtn = TranslateUi.Button("Recognition.Latex.ExportSvg", accent: false, ExportSvg, "");

        copyImgBtn.Margin = pinBtn.Margin = svgBtn.Margin = new Thickness(8, 0, 0, 0);

        buttons.Children.Add(copyBtn);
        buttons.Children.Add(copyImgBtn);
        buttons.Children.Add(pinBtn);
        buttons.Children.Add(svgBtn);

        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);

        _status.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_status);

        Grid.SetRow(bottom, 3);
        grid.Children.Add(bottom);

        Content = grid;

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        string text = _codeBox.Text;
        _onEdited?.Invoke(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            _previewImage.Source = null;
            TranslateUi.SetStatus(_status, Loc.Get("Recognition.Latex.NoFormula"));
            return;
        }

        try
        {
            var drawing = LatexRenderer.RenderToDrawing(text, new LatexRenderOptions
            {
                FontSize = 28.0,
                Foreground = (Application.Current?.TryFindResource("Brush.Text") as Brush) ?? Brushes.Black,
            });
            _previewImage.Source = new DrawingImage(drawing);

            string srcLabel = _source.StartsWith("ai", StringComparison.OrdinalIgnoreCase)
                ? Loc.Get("Recognition.Latex.AiSource")
                : Loc.Get("Recognition.Latex.OcrSource");
            TranslateUi.SetStatus(_status, srcLabel);
        }
        catch (Exception ex)
        {
            _previewImage.Source = null;
            TranslateUi.SetStatus(_status, Loc.Format("Recognition.Latex.Failed", ex.Message));
        }
    }

    private void CopyLatex()
    {
        ClipboardService.SetText(_codeBox.Text);
        Notify.Info(Loc.Get("Recognition.Latex.Copied"));
    }

    private void CopyImage()
    {
        try
        {
            var bmp = LatexRenderer.RenderToBitmap(_codeBox.Text, new LatexRenderOptions
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

    private void PinFormula()
    {
        try
        {
            string code = _codeBox.Text.Trim();
            if (string.IsNullOrEmpty(code)) return;
            AppServices.Pins.PinContent(new LatexPinContent(code));
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Pin formula failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }

    private void ExportSvg()
    {
        try
        {
            string svg = LatexRenderer.RenderToSvg(_codeBox.Text, new LatexRenderOptions
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

            if (dlg.ShowDialog(this) == true)
            {
                File.WriteAllText(dlg.FileName, svg, Encoding.UTF8);
                Notify.Info(Loc.Format("Recognition.Table.Exported", dlg.FileName));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Export SVG failed", ex);
            Notify.Error(Loc.Format("Recognition.Table.ExportFailed", ex.Message));
        }
    }
}
