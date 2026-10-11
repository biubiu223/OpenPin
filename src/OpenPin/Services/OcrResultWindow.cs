using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core;

namespace OpenPin.Services;

/// <summary>
/// OCR result window (SRV-06): editable text, copy, layout (auto / single line / keep lines) and punctuation toggles,
/// optional source image beside the text. UI thread.
/// </summary>
public sealed class OcrResultWindow : Window
{
    private readonly OcrResult _result;
    private readonly TextBox _text;
    private readonly TextBlock _status;
    private OcrTextLayout _layout;
    private OcrPunctuation _punctuation;

    /// <summary>Open a result window (non-modal, activated).</summary>
    public static OcrResultWindow Show(OcrResult result, BitmapSource? image = null)
    {
        var w = new OcrResultWindow(result, image);
        w.Show();
        w.Activate();
        return w;
    }

    private OcrResultWindow(OcrResult result, BitmapSource? image)
    {
        _result = result;
        var settings = ServicesConfig.Ocr;
        _layout = settings.Layout;
        _punctuation = settings.Punctuation;
        SetResourceReference(TitleProperty, "Services.OcrTitle");
        ServicesUi.SetupDialog(this, image != null ? 820 : 560, 480);

        _text = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
        };
        _text.SetResourceReference(StyleProperty, "Style.Services.TextBox");
        _text.BorderThickness = new Thickness(0);
        _text.TextChanged += (_, _) => UpdateStatus();
        _status = ServicesUi.Label("");

        // Toolbar
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = false };
        var copy = ServicesUi.Button("Services.OcrCopy", accent: true, (_, _) => Copy(), "");
        DockPanel.SetDock(copy, Dock.Right);
        toolbar.Children.Add(copy);

        var layoutGroup = ServicesUi.Segmented(new[]
        {
            (OcrTextLayout.Auto, "Services.OcrLayoutAuto"),
            (OcrTextLayout.SingleLine, "Services.OcrLayoutSingle"),
            (OcrTextLayout.MultiLine, "Services.OcrLayoutMulti"),
        }, _layout, v => { _layout = v; Reformat(); });
        layoutGroup.Margin = new Thickness(0, 0, 12, 0);
        var punctGroup = ServicesUi.Segmented(new[]
        {
            (OcrPunctuation.None, "Services.OcrPunctNone"),
            (OcrPunctuation.ToHalfWidth, "Services.OcrPunctHalf"),
            (OcrPunctuation.ToFullWidth, "Services.OcrPunctFull"),
        }, _punctuation, v => { _punctuation = v; Reformat(); });
        toolbar.Children.Add(layoutGroup);
        toolbar.Children.Add(punctGroup);

        // Body
        var textCard = ServicesUi.Card(_text, new Thickness(2));
        UIElement body = textCard;
        if (image != null)
        {
            var img = new Image { Source = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var imgCard = ServicesUi.Card(new ScrollViewer
            {
                Content = img,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8),
            });
            var split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            Grid.SetColumn(imgCard, 0);
            Grid.SetColumn(textCard, 2);
            split.Children.Add(imgCard);
            split.Children.Add(textCard);
            body = split;
        }

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(toolbar, Dock.Top);
        _status.Margin = new Thickness(2, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        Reformat();
        Loaded += (_, _) => { _text.Focus(); _text.CaretIndex = _text.Text.Length; };
    }

    /// <summary>The (possibly edited) text.</summary>
    public string Text => _text.Text;

    private void Reformat()
    {
        if (_result.IsEmpty)
        {
            _text.Text = "";
            _status.Text = _result.Error ?? Loc.Get("Services.OcrNoText");
            return;
        }
        _text.Text = _result.FormatText(_layout, _punctuation);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_result.IsEmpty) return;
        var lang = string.IsNullOrEmpty(_result.Language) ? "" : Loc.Format("Services.OcrLanguageLabel", _result.Language) + "    ";
        _status.Text = lang + Loc.Format("Services.OcrCharCount", _text.Text.Length);
    }

    private void Copy()
    {
        var text = _text.SelectionLength > 0 ? _text.SelectedText : _text.Text;
        if (ServicesUi.TryCopyText(text)) _status.Text = Loc.Get("Services.Copied");
    }
}
