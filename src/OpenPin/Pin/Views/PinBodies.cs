using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Pin.Logic;
using OpenPin.Pin.Model;
using OpenPin.Pin.Native;
using OpenPin.Services;

namespace OpenPin.Pin.Views;

/// <summary>
/// The content of a pin window. Content is laid out in "content units"; the window scales it by
/// <c>BaseScale × Zoom</c> physical pixels per unit (images: 1 unit = 1 image pixel).
/// </summary>
internal abstract class PinBody
{
    public abstract PinKind Kind { get; }

    /// <summary>The element shown inside the pin (unscaled, content units).</summary>
    public abstract FrameworkElement View { get; }

    /// <summary>Size of <see cref="View"/> in content units.</summary>
    public abstract Size ContentSize { get; }

    /// <summary>Raised when <see cref="ContentSize"/> or the displayed pixels change.</summary>
    public event EventHandler? Changed;

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>What is shown, rendered at <paramref name="scale"/> physical px per content unit.</summary>
    public virtual BitmapSource RenderCurrent(double scale, bool smooth)
    {
        var size = ContentSize;
        int w = Math.Max(1, (int)Math.Round(size.Width * scale)), h = Math.Max(1, (int)Math.Round(size.Height * scale));
        var brush = new VisualBrush(View)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size.Width, size.Height),
        };
        return BitmapUtil.ToBgra32(BitmapUtil.Render(w, h, dc => dc.DrawRectangle(brush, null, new Rect(0, 0, w, h))));
    }

    /// <summary>The untouched source as an image (non-image pins: same as current at 100%).</summary>
    public virtual BitmapSource RenderOriginal(double baseScale) => RenderCurrent(baseScale, true);

    /// <summary>Puts the content on the clipboard (default Ctrl+C for non-image pins).</summary>
    public virtual void CopyToClipboard() { }

    /// <summary>Clipboard / drag-out data (text, files, color). Image pins are handled by the window.</summary>
    public virtual void FillDataObject(DataObject data) { }

    /// <summary>Extra context-menu items; <paramref name="contentPos"/> is where the menu was opened (content units).</summary>
    public virtual IEnumerable<object> MenuItems(Point contentPos, PinMenuFactory f) => Array.Empty<object>();

    /// <summary>Return true to suppress "double-click closes" (e.g. a file row opens instead).</summary>
    public virtual bool HandleDoubleClick(Point contentPos) => false;

    /// <summary>Write content fields into the manifest.</summary>
    public virtual void SaveTo(PinManifest m) { }

    public virtual string ContentHash() => "";

    protected static Brush BrushFrom(string? text, Brush fallback)
    {
        if (ColorText.TryParse(text, out var c)) return BitmapUtil.Frozen(new SolidColorBrush(c));
        return fallback;
    }
}

/// <summary>Image pin: original pixels, edited pixels (annotations / crop baked in) and non-destructive adjustments.</summary>
internal sealed class ImageBody : PinBody
{
    private readonly Image _image;
    private readonly ImageAdjustments _adjust;

    public ImageBody(BitmapSource original, BitmapSource? edited, ImageAdjustments adjust)
    {
        Original = BitmapUtil.Normalize96Dpi(original);
        Edited = edited != null ? BitmapUtil.Normalize96Dpi(edited) : Original;
        _adjust = adjust;
        _image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        Display = Edited;
        Recompute();
    }

    public override PinKind Kind => PinKind.Image;
    public override FrameworkElement View => _image;
    public override Size ContentSize => new(Display.PixelWidth, Display.PixelHeight);

    /// <summary>Untouched pixels ("原始图像").</summary>
    public BitmapSource Original { get; private set; }

    /// <summary>Original + baked annotations / crops.</summary>
    public BitmapSource Edited { get; private set; }

    /// <summary>Edited + adjustments: what is on screen at 100%.</summary>
    public BitmapSource Display { get; private set; }

    public bool HasEdits => !ReferenceEquals(Edited, Original);

    public ImageAdjustments Adjust => _adjust;

    /// <summary>Recompute <see cref="Display"/> after <see cref="Adjust"/> changed.</summary>
    public void Recompute()
    {
        Display = _adjust.IsIdentity ? Edited : PinImageOps.Apply(Edited, _adjust);
        _image.Source = Display;
        _image.Width = Display.PixelWidth;
        _image.Height = Display.PixelHeight;
        RaiseChanged();
    }

    /// <summary>Makes <paramref name="image"/> (derived from Display) the new edited image and clears the adjustments.</summary>
    public void Bake(BitmapSource image)
    {
        Edited = BitmapUtil.Normalize96Dpi(image);
        _adjust.Reset();
        Recompute();
    }

    public void RestoreOriginal()
    {
        Edited = Original;
        _adjust.Reset();
        Recompute();
    }

    public void Replace(BitmapSource image)
    {
        _hash = null;
        Original = BitmapUtil.Normalize96Dpi(image);
        Edited = Original;
        _adjust.Reset();
        Recompute();
    }

    public void SetScalingMode(bool smooth, double scale)
    {
        // Pixel art / screenshots look best with nearest neighbour when enlarged; smooth when shrunk.
        var mode = Math.Abs(scale - 1) < 1e-6 ? BitmapScalingMode.NearestNeighbor
            : smooth ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor;
        RenderOptions.SetBitmapScalingMode(_image, mode);
    }

    public override BitmapSource RenderCurrent(double scale, bool smooth) => PinImageOps.Scale(Display, scale, smooth);

    public override BitmapSource RenderOriginal(double baseScale) => Original;

    private string? _hash;

    public override string ContentHash() => _hash ??= PinImageOps.Hash(Original);
}

/// <summary>Text pin: plain text, or simple rich text from the clipboard's HTML fragment (PIN-07).</summary>
internal sealed class TextBody : PinBody
{
    private readonly Border _root;
    private readonly TextBlock _text;
    private Size _size;

    public TextBody(string plain, string? html, bool ignoreFormat)
    {
        PlainText = plain;
        Html = string.IsNullOrWhiteSpace(html) ? null : html;
        IgnoreFormat = ignoreFormat;
        var s = PinManager.CurrentSettings;
        _text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Clamp(s.TextMaxWidth, 80, 4000),
            FontSize = Math.Clamp(s.TextFontSize, 6, 96),
            Foreground = BrushFrom(s.TextForeground, Brushes.Black),
        };
        try { _text.FontFamily = new FontFamily(s.TextFontFamily); }
        catch (Exception ex) { Log.Warn($"Bad text pin font '{s.TextFontFamily}': {ex.Message}"); }
        TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Ideal);
        _root = new Border
        {
            Background = BrushFrom(s.TextBackground, Brushes.White),
            Padding = new Thickness(12, 8, 12, 8),
            Child = _text,
            SnapsToDevicePixels = true,
        };
        Rebuild();
    }

    public string PlainText { get; }
    public string? Html { get; }
    public bool IgnoreFormat { get; private set; }
    public bool HasRichText => Html != null && PinManager.CurrentSettings.TextShowHtml;

    public override PinKind Kind => PinKind.Text;
    public override FrameworkElement View => _root;
    public override Size ContentSize => _size;

    public void SetIgnoreFormat(bool value)
    {
        if (IgnoreFormat == value) return;
        IgnoreFormat = value;
        Rebuild();
    }

    private void Rebuild()
    {
        _text.Inlines.Clear();
        bool rich = false;
        if (HasRichText && !IgnoreFormat)
        {
            var runs = HtmlLite.Parse(Html);
            // mis-decoded CF_HTML (wrong code page) → fall back to the plain text the clipboard also carried
            bool garbled = runs.Any(r => r.Text.Contains('�')) && !PlainText.Contains('�');
            if (runs.Count > 0 && !garbled)
            {
                rich = true;
                double baseSize = _text.FontSize;
                foreach (var r in runs)
                {
                    if (r.IsLineBreak) { _text.Inlines.Add(new LineBreak()); continue; }
                    var run = new Run(r.Text);
                    if (r.Bold) run.FontWeight = FontWeights.SemiBold;
                    if (r.Italic) run.FontStyle = FontStyles.Italic;
                    if (r.Underline) run.TextDecorations = TextDecorations.Underline;
                    if (r.Strike) run.TextDecorations = TextDecorations.Strikethrough;
                    if (Math.Abs(r.SizeScale - 1) > 0.01) run.FontSize = baseSize * r.SizeScale;
                    if (r.Monospace && Application.Current?.TryFindResource("Font.Mono") is FontFamily mono) run.FontFamily = mono;
                    if (r.Color != null && ColorText.TryParse(r.Color, out var c) && c.A > 0) run.Foreground = BitmapUtil.Frozen(new SolidColorBrush(c));
                    else if (r.Href != null) run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent");
                    if (r.Background != null && ColorText.TryParse(r.Background, out var b) && b.A > 0 && !IsWhite(b))
                        run.Background = BitmapUtil.Frozen(new SolidColorBrush(b));
                    _text.Inlines.Add(run);
                }
            }
        }
        if (!rich) _text.Text = PlainText.Replace("\r\n", "\n").TrimEnd('\n');

        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = _root.DesiredSize;
        _size = new Size(Math.Ceiling(Math.Max(24, d.Width)), Math.Ceiling(Math.Max(16, d.Height)));
        _root.Width = _size.Width;
        _root.Height = _size.Height;
        RaiseChanged();
    }

    private static bool IsWhite(Color c) => c.R > 250 && c.G > 250 && c.B > 250;

    public override void CopyToClipboard() => ClipboardService.SetText(PlainText);

    public override void FillDataObject(DataObject data) => data.SetText(PlainText);

    public override IEnumerable<object> MenuItems(Point contentPos, PinMenuFactory f)
    {
        yield return f.Item("Pin.Text.Copy", "", null, CopyToClipboard);
        if (HasRichText)
            yield return f.Check("Pin.Text.IgnoreFormat", IgnoreFormat, () => f.Window.SetTextIgnoreFormat(!IgnoreFormat));
    }

    public override void SaveTo(PinManifest m)
    {
        m.Text = PlainText;
        m.Html = Html;
        m.IgnoreFormat = IgnoreFormat;
    }

    public override string ContentHash() => PinImageOps.Hash("text", PlainText);
}

/// <summary>Files pin: shell icon + name per file (PIN-07).</summary>
internal sealed class FilesBody : PinBody
{
    private readonly Border _root;
    private readonly StackPanel _list = new();
    private Size _size;

    public FilesBody(IReadOnlyList<string> paths)
    {
        Paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in Paths) _list.Children.Add(MakeRow(p));
        _root = new Border
        {
            Padding = new Thickness(6),
            Child = _list,
            SnapsToDevicePixels = true,
        };
        _root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _size = new Size(Math.Ceiling(Math.Max(120, _root.DesiredSize.Width)), Math.Ceiling(Math.Max(40, _root.DesiredSize.Height)));
        _root.Width = _size.Width;
        _root.Height = _size.Height;
    }

    public IReadOnlyList<string> Paths { get; }

    public override PinKind Kind => PinKind.Files;
    public override FrameworkElement View => _root;
    public override Size ContentSize => _size;

    private static FrameworkElement MakeRow(string path)
    {
        bool isDir = Directory.Exists(path);
        bool exists = isDir || File.Exists(path);
        var icon = PinNative.GetFileIcon(path, isDir || path.EndsWith('\\'), exists);
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path;

        var grid = new Grid { Tag = path, Margin = new Thickness(0, 1, 0, 1), MinHeight = 40, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var img = new Image { Source = icon, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        grid.Children.Add(img);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), MaxWidth = 420 };
        var title = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        title.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        if (!exists) title.TextDecorations = TextDecorations.Strikethrough;
        var sub = new TextBlock
        {
            Text = exists ? Path.GetDirectoryName(path) ?? "" : Loc.Get("Pin.Files.Missing"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        sub.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        texts.Children.Add(title);
        texts.Children.Add(sub);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        grid.ToolTip = path;
        grid.MouseEnter += (_, _) => grid.SetResourceReference(Panel.BackgroundProperty, "Brush.Hover");
        grid.MouseLeave += (_, _) => grid.Background = Brushes.Transparent;
        return grid;
    }

    /// <summary>The file row under a content point, or null.</summary>
    public string? PathAt(Point contentPos)
    {
        if (_root.InputHitTest(contentPos) is not DependencyObject hit) return null;
        for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Grid { Tag: string p }) return p;
        return null;
    }

    public override bool HandleDoubleClick(Point contentPos)
    {
        var p = PathAt(contentPos);
        if (p == null) return false;
        Open(p);
        return true;
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error($"Open failed: {path}", ex);
            Notify.Error(Loc.Format("Pin.Files.OpenFailed", ex.Message));
        }
    }

    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error($"Reveal failed: {path}", ex);
            Notify.Error(Loc.Format("Pin.Files.OpenFailed", ex.Message));
        }
    }

    public override void CopyToClipboard() => ClipboardService.SetFiles(Paths);

    public override void FillDataObject(DataObject data)
    {
        var col = new System.Collections.Specialized.StringCollection();
        col.AddRange(Paths.ToArray());
        data.SetFileDropList(col);
        data.SetText(string.Join(Environment.NewLine, Paths));
    }

    public override IEnumerable<object> MenuItems(Point contentPos, PinMenuFactory f)
    {
        var target = PathAt(contentPos);
        var targets = target != null ? new[] { target } : Paths.ToArray();
        if (target != null)
        {
            yield return f.Item("Pin.Files.Open", "", null, () => Open(target));
        }
        else if (Paths.Count > 0)
        {
            var sub = f.Sub("Pin.Files.Open", "");
            foreach (var p in Paths) sub.Items.Add(f.Raw(Path.GetFileName(p.TrimEnd('\\')), null, () => Open(p)));
            yield return sub;
        }
        yield return f.Item("Pin.Files.CopyFiles", "", null, () => ClipboardService.SetFiles(targets));
        yield return f.Item("Pin.Files.CopyPath", "", null, () => ClipboardService.SetText(string.Join(Environment.NewLine, targets)));
        yield return f.Item("Pin.Files.Reveal", "", null, () => Reveal(targets[0]));
    }

    public override void SaveTo(PinManifest m) => m.Files = Paths.ToList();

    public override string ContentHash() => PinImageOps.Hash("files", string.Join("|", Paths).ToLowerInvariant());
}

/// <summary>Color pin: big swatch + HEX / RGB / HSL / HSV (PIN-07).</summary>
internal sealed class ColorBody : PinBody
{
    private readonly Border _root;
    private readonly Size _size;

    public ColorBody(Color color, string? originalText)
    {
        Color = color;
        OriginalText = originalText;
        var stack = new StackPanel();

        var swatchBack = new Border { Width = 168, Height = 96, Background = Checker(), CornerRadius = new CornerRadius(4) };
        var swatch = new Border { Background = BitmapUtil.Frozen(new SolidColorBrush(color)), CornerRadius = new CornerRadius(4) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        swatch.BorderThickness = new Thickness(1);
        swatchBack.Child = swatch;
        stack.Children.Add(swatchBack);

        var grid = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        foreach (var f in new[] { ColorFormat.Hex, ColorFormat.Rgb, ColorFormat.Hsl, ColorFormat.Hsv })
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = f.ToString().ToUpperInvariant(), FontSize = 11, Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            var value = new TextBlock { Text = Format(f), FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            value.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            row++;
        }
        if (color.A < 255)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = "A", FontSize = 11, Margin = new Thickness(0, 2, 0, 2) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            var value = new TextBlock { Text = $"{color.A} ({Math.Round(color.A / 2.55)}%)", FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        stack.Children.Add(grid);

        _root = new Border { Padding = new Thickness(8), Child = stack, SnapsToDevicePixels = true };
        _root.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _size = new Size(Math.Ceiling(_root.DesiredSize.Width), Math.Ceiling(_root.DesiredSize.Height));
        _root.Width = _size.Width;
        _root.Height = _size.Height;
    }

    public Color Color { get; }
    public string? OriginalText { get; }

    public override PinKind Kind => PinKind.Color;
    public override FrameworkElement View => _root;
    public override Size ContentSize => _size;

    public string Format(ColorFormat f) => ColorText.FormatCss(Color, f);

    private static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        g.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(Brushes.LightGray, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return BitmapUtil.Frozen(new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute });
    }

    public override void CopyToClipboard() => ClipboardService.SetText(Format(ColorFormat.Hex));

    public override void FillDataObject(DataObject data) => data.SetText(Format(ColorFormat.Hex));

    public override IEnumerable<object> MenuItems(Point contentPos, PinMenuFactory f)
    {
        foreach (var fmt in new[] { ColorFormat.Hex, ColorFormat.Rgb, ColorFormat.Hsl, ColorFormat.Hsv })
        {
            var text = Format(fmt);
            yield return f.Raw(Loc.Format("Pin.Color.CopyAs", fmt.ToString().ToUpperInvariant()) + "  " + text, fmt == ColorFormat.Hex ? "" : null,
                () => ClipboardService.SetText(text));
        }
    }

    public override void SaveTo(PinManifest m)
    {
        m.Color = $"#{Color.A:X2}{Color.R:X2}{Color.G:X2}{Color.B:X2}";
        m.ColorOriginalText = OriginalText;
    }

    public override string ContentHash() => PinImageOps.Hash("color", $"{Color}");
}
