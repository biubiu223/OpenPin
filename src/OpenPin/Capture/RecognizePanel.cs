using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenPin.Core;
using OpenPin.Recognition;

namespace OpenPin.Capture;

/// <summary>
/// "识别" flyout of the capture toolbar: text (OCR), translate (PRO-01), table (PRO-02), QR / barcode (PRO-10), plus the
/// OCR language quick switch with "add language…" (PRO-09).
/// </summary>
internal sealed class RecognizePanel : Border
{
    private readonly WrapPanel _languages = new();

    public event Action? OcrRequested;
    public event Action? LatexRequested;
    public event Action? TranslateRequested;
    public event Action? MirrorRequested;
    public event Action? TableRequested;
    public event Action? QrRequested;

    public RecognizePanel()
    {
        SetResourceReference(StyleProperty, "Style.Capture.Flyout");
        Padding = new Thickness(6, 6, 6, 10);
        Visibility = Visibility.Collapsed;
        var stack = new StackPanel { Width = 268 };
        stack.Children.Add(Row("", "Capture.Recognize.Ocr", "Shift+C", () => OcrRequested?.Invoke()));
        stack.Children.Add(Row("", "Capture.Recognize.Latex", "Shift+F", () => LatexRequested?.Invoke()));
        stack.Children.Add(Row("", "Capture.Recognize.Translate", "Ctrl+Q", () => TranslateRequested?.Invoke()));
        stack.Children.Add(Row("🪞", "Capture.Recognize.Mirror", "Shift+M", () => MirrorRequested?.Invoke()));
        stack.Children.Add(Row("", "Capture.Recognize.Table", "Shift+Q", () => TableRequested?.Invoke()));
        stack.Children.Add(Row("", "Capture.Recognize.Qr", "Q", () => QrRequested?.Invoke()));
        var separator = new Separator { Margin = new Thickness(6, 6, 6, 6) };
        separator.SetResourceReference(BackgroundProperty, "Brush.Border");
        stack.Children.Add(separator);
        var caption = new TextBlock { Text = Loc.Get("Capture.Recognize.OcrLanguage"), Margin = new Thickness(8, 0, 0, 6) };
        caption.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        stack.Children.Add(caption);
        _languages.Margin = new Thickness(8, 0, 4, 0);
        stack.Children.Add(_languages);
        Child = stack;
    }

    /// <summary>Rebuild the language chips (installed OCR languages may change while the app runs).</summary>
    public void RefreshLanguages()
    {
        _languages.Children.Clear();
        var chips = new List<ToggleButton>();
        void AddChip(string text, string tag)
        {
            var chip = Ui.Chip(text, OcrLanguageChoice.IsCurrent(tag), _ =>
            {
                OcrLanguageChoice.Set(tag);
                foreach (var c in chips) c.IsChecked = Equals(c.Tag, tag);
            });
            chip.Tag = tag;
            chips.Add(chip);
            _languages.Children.Add(chip);
        }
        var available = OcrLanguageChoice.Available;
        AddChip(Loc.Get("Recognition.OcrLang.Auto"), "");
        foreach (var l in available) AddChip(l.DisplayName, l.Tag);
        // A remembered language that is no longer installed: "auto" is what will be used.
        if (!chips.Any(c => c.IsChecked == true)) chips[0].IsChecked = true;
        var add = Ui.Button(Loc.Get("Recognition.OcrLang.Add"), OcrLanguageChoice.OpenLanguageSettings);
        add.SetResourceReference(StyleProperty, "Style.Capture.Chip");
        _languages.Children.Add(add);
    }

    private static Button Row(string glyph, string textKey, string shortcut, Action onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        var text = new TextBlock { Text = Loc.Get(textKey), VerticalAlignment = VerticalAlignment.Center };
        var keys = new TextBlock { Text = shortcut, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        keys.SetResourceReference(StyleProperty, "Style.Capture.Caption");
        Grid.SetColumn(text, 1);
        Grid.SetColumn(keys, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(keys);
        var b = new Button { Content = grid };
        b.SetResourceReference(StyleProperty, "Style.Capture.MenuRow");
        b.Click += (_, _) => onClick();
        return b;
    }
}
