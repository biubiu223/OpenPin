using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;

namespace OpenPin.Services;

/// <summary>
/// PRO-08 "save as" window (Win11 style): large preview of the ACTUALLY encoded result (zoom fit / 100 %, pan),
/// size estimate from a real background encode (debounced), format chips, JPEG quality, scaling, PDF page options,
/// file name (Enter saves), folder + browse, overwrite confirmation, "copy after save". Fully keyboard operable.
/// Modal, owned by the caller's window, opened on the owner's / cursor's monitor. UI thread.
/// </summary>
internal sealed class SaveWindow : Window
{
    private enum ScaleMode { Full, P75, P50, Custom }

    private sealed record Request(ImageFileFormat Format, int Quality, int Width, int Height, PdfOptions Pdf);

    private sealed record Encoded(Request Request, byte[] Bytes, BitmapSource Scaled, ImageSource Preview, bool IsPdf, int Pages, string PageInfo);

    private readonly BitmapSource _image;
    private readonly SaveSettings _settings;
    private readonly Window? _owner;

    // Options.
    private ImageFileFormat _format;
    private int _quality;
    private ScaleMode _scale = ScaleMode.Full;
    private int _customW, _customH;
    private PdfPaperSize _paper;
    private PdfOrientation _orientation;
    private double _marginMm;
    private bool _fitToPage, _paginate, _pdfJpeg;
    private double _overlapMm;

    // Controls.
    private readonly TextBox _nameBox;
    private readonly TextBox _folderBox;
    private readonly TextBlock _extText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 13 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _previewStatus = new() { FontSize = 12, Margin = new Thickness(10, 6, 10, 6) };
    private readonly ChipGroup<bool> _zoom;
    private readonly ChipGroup<ImageFileFormat> _formatChips;
    private readonly ChipGroup<ScaleMode> _scaleChips;
    private readonly ChipGroup<PdfPaperSize> _paperChips;
    private readonly ChipGroup<PdfOrientation> _orientationChips;
    private readonly ChipGroup<bool> _fitChips;
    private readonly Slider _qualitySlider;
    private readonly TextBlock _qualityValue = new() { Width = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly FrameworkElement _qualityRow;
    private readonly FrameworkElement _customRow;
    private readonly TextBox _customWBox, _customHBox;
    private readonly FrameworkElement _pdfSection;
    private readonly TextBox _marginBox, _overlapBox;
    private readonly CheckBox _paginateBox, _pdfJpegBox, _copyBox;
    private readonly FrameworkElement _overlapRow, _pdfLayoutRows;
    private readonly TextBlock _infoFormat = Value(), _infoDims = Value(), _infoPages = Value(), _infoEstimate = Value(), _infoCompression = Value();
    private readonly FrameworkElement _infoPagesRow;
    private readonly Button _saveButton, _cancelButton, _replaceButton, _backButton;
    private readonly FrameworkElement _buttonsPanel, _confirmPanel;
    private readonly TextBlock _confirmText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private bool _syncingCustom;
    private bool _fit = true;
    private string? _pendingOverwritePath;

    // Background encoding.
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _requestId;
    private Encoded? _latest;
    private readonly object _scaledGate = new();
    private (int W, int H, BitmapSource Image)? _scaled;

    /// <summary>The saved file, or null if the user cancelled.</summary>
    public string? SavedPath { get; private set; }

    /// <summary>Show modally; returns the saved path or null.</summary>
    public static string? ShowSave(BitmapSource image, SaveContext context, Window? owner)
    {
        var w = new SaveWindow(ImageEncoding.EnsureFrozen(image), context, owner);
        w.ShowDialog();
        return w.SavedPath;
    }

    private SaveWindow(BitmapSource image, SaveContext context, Window? owner)
    {
        _image = image;
        _settings = ServicesConfig.Save;
        _owner = owner != null && PresentationSource.FromVisual(owner) != null ? owner : null;
        var s = _settings;

        _format = SaveService.DialogFormat(s);
        _quality = Math.Clamp(s.Quality, 1, 100);
        _customW = image.PixelWidth;
        _customH = image.PixelHeight;
        _paper = s.PdfPaper;
        _orientation = s.PdfOrientation;
        _marginMm = Math.Clamp(s.PdfMarginMm, 0, 100);
        _fitToPage = s.PdfFitToPage;
        _paginate = s.PdfAutoPaginate;
        _overlapMm = Math.Clamp(s.PdfPageOverlapMm, 0, 50);
        _pdfJpeg = s.PdfUseJpeg;

        SetResourceReference(TitleProperty, "Services.SaveDialogTitle");
        SetResourceReference(BackgroundProperty, "Brush.SurfaceAlt");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        if (Application.Current?.TryFindResource("AppIcon") is ImageSource icon) Icon = icon;
        Width = 1060;
        Height = 720;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.CanResize;
        UseLayoutRounding = true;
        // Above the (topmost) capture overlay and pins; owned so it closes / minimises with the owner.
        Topmost = true;
        if (_owner != null) Owner = _owner;
        ShowInTaskbar = _owner == null;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        _previewStatus.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) => StartEncode();

        // ---------------------------------------------------------------- bottom: file name / folder
        _nameBox = Input(SaveService.DialogFileName(s, context, image.PixelWidth, image.PixelHeight));
        _nameBox.TextChanged += (_, _) => CancelConfirm();
        _folderBox = Input(SaveService.DialogFolder(s));
        _folderBox.TextChanged += (_, _) => CancelConfirm();
        var browse = DialogButton("Services.SaveWin.Browse", accent: false, (_, _) => Browse());
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.MinWidth = 0;

        var fields = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition());
        fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        fields.RowDefinitions.Add(new RowDefinition());
        AddCell(fields, FieldLabel("Services.SaveWin.FileName", _nameBox, new Thickness(0, 0, 12, 0)), 0, 0);
        AddCell(fields, _nameBox, 0, 1);
        AddCell(fields, _extText, 0, 2);
        AddCell(fields, FieldLabel("Services.SaveWin.Folder", _folderBox, new Thickness(0, 0, 12, 0)), 2, 0);
        AddCell(fields, _folderBox, 2, 1);
        AddCell(fields, browse, 2, 2);

        // ---------------------------------------------------------------- right column: options + info
        var options = new StackPanel();

        _formatChips = new ChipGroup<ImageFileFormat>(Enum.GetValues<ImageFileFormat>().Select(f => (f, (object)f.ToString().ToUpperInvariant())), _format,
            v => { _format = v; CancelConfirm(); OptionsChanged(); });
        options.Children.Add(_formatChips.Label = SectionLabel("Services.SaveWin.Format", _formatChips.Checked, top: false));
        options.Children.Add(_formatChips.Panel);

        _qualitySlider = new Slider { Minimum = 1, Maximum = 100, Value = _quality, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1 };
        _qualitySlider.SetResourceReference(StyleProperty, "Style.Services.Slider");
        _qualitySlider.SetResourceReference(ToolTipProperty, "Services.SaveWin.QualityTip");
        _qualitySlider.ValueChanged += (_, e) => { _quality = (int)Math.Round(e.NewValue); _qualityValue.Text = _quality.ToString(CultureInfo.InvariantCulture); OptionsChanged(); };
        _qualityValue.Text = _quality.ToString(CultureInfo.InvariantCulture);
        var qualityGrid = new DockPanel();
        DockPanel.SetDock(_qualityValue, Dock.Right);
        qualityGrid.Children.Add(_qualityValue);
        qualityGrid.Children.Add(_qualitySlider);
        _qualityRow = new StackPanel { Children = { SectionLabel("Services.SaveWin.Quality", _qualitySlider), qualityGrid } };
        options.Children.Add(_qualityRow);

        _scaleChips = new ChipGroup<ScaleMode>(new[]
        {
            (ScaleMode.Full, (object)"100%"), (ScaleMode.P75, "75%"), (ScaleMode.P50, "50%"), (ScaleMode.Custom, Loc.Get("Services.SaveWin.Custom")),
        }, _scale, v => { _scale = v; OptionsChanged(); });
        options.Children.Add(_scaleChips.Label = SectionLabel("Services.SaveWin.Size", _scaleChips.Checked));
        options.Children.Add(_scaleChips.Panel);
        _customWBox = NumberBox(_customW, 1, 32767, 1, "0", v => CustomSizeChanged(width: (int)v, height: null));
        _customHBox = NumberBox(_customH, 1, 32767, 1, "0", v => CustomSizeChanged(width: null, height: (int)v));
        _customWBox.Width = _customHBox.Width = 86;
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        customRow.SetResourceReference(ToolTipProperty, "Services.SaveWin.CustomTip");
        customRow.Children.Add(SmallLabel("Services.SaveWin.Width", _customWBox));
        customRow.Children.Add(_customWBox);
        customRow.Children.Add(new TextBlock { Text = "×", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        customRow.Children.Add(SmallLabel("Services.SaveWin.Height", _customHBox));
        customRow.Children.Add(_customHBox);
        _customRow = customRow;
        options.Children.Add(_customRow);

        // PDF
        _paperChips = new ChipGroup<PdfPaperSize>(new[]
        {
            (PdfPaperSize.Original, (object)Loc.Get("Services.SaveWin.PaperOriginal")), (PdfPaperSize.A4, "A4"), (PdfPaperSize.Letter, "Letter"),
        }, _paper, v =>
        {
            // Paper sizes without margins look broken when printed: start with 10 mm.
            if (_paper == PdfPaperSize.Original && v != PdfPaperSize.Original && _marginMm == 0) SetNumber(_marginBox!, 10);
            _paper = v;
            OptionsChanged();
        });
        _orientationChips = new ChipGroup<PdfOrientation>(new[]
        {
            (PdfOrientation.Auto, (object)Loc.Get("Services.SaveWin.OrientationAuto")),
            (PdfOrientation.Portrait, Loc.Get("Services.SaveWin.Portrait")),
            (PdfOrientation.Landscape, Loc.Get("Services.SaveWin.Landscape")),
        }, _orientation, v => { _orientation = v; OptionsChanged(); });
        _fitChips = new ChipGroup<bool>(new[]
        {
            (true, (object)Loc.Get("Services.SaveWin.FitToPage")), (false, Loc.Get("Services.SaveWin.ActualSize")),
        }, _fitToPage, v => { _fitToPage = v; OptionsChanged(); });
        _marginBox = NumberBox(_marginMm, 0, 100, 1, "0.#", v => { _marginMm = v; OptionsChanged(); });
        _marginBox.Width = 86;
        _marginBox.HorizontalAlignment = HorizontalAlignment.Left;
        _overlapBox = NumberBox(_overlapMm, 0, 50, 1, "0.#", v => { _overlapMm = v; OptionsChanged(); });
        _overlapBox.Width = 86;
        _overlapBox.HorizontalAlignment = HorizontalAlignment.Left;
        _paginateBox = CheckBox("Services.SaveWin.Paginate", _paginate, v => { _paginate = v; OptionsChanged(); });
        _pdfJpegBox = CheckBox("Services.SaveWin.PdfJpeg", _pdfJpeg, v => { _pdfJpeg = v; OptionsChanged(); });
        _overlapRow = new StackPanel { Children = { SmallCaption("Services.SaveWin.Overlap", _overlapBox), _overlapBox } };
        _pdfLayoutRows = new StackPanel
        {
            Children =
            {
                (_orientationChips.Label = SmallCaption("Services.SaveWin.Orientation", _orientationChips.Checked)), _orientationChips.Panel,
                (_fitChips.Label = SmallCaption("Services.SaveWin.Placement", _fitChips.Checked)), _fitChips.Panel,
            },
        };
        _pdfSection = new StackPanel
        {
            Children =
            {
                (_paperChips.Label = SectionLabel("Services.SaveWin.Paper", _paperChips.Checked)), _paperChips.Panel,
                _pdfLayoutRows,
                SmallCaption("Services.SaveWin.Margin", _marginBox), _marginBox,
                Spacer(10), _paginateBox, _overlapRow, Spacer(6), _pdfJpegBox,
            },
        };
        options.Children.Add(_pdfSection);

        // Info
        var info = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void InfoRow(string key, TextBlock value, out FrameworkElement rowLabel)
        {
            info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 3, 14, 3) };
            l.SetResourceReference(TextBlock.TextProperty, key);
            l.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Grid.SetRow(l, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            info.Children.Add(l); info.Children.Add(value);
            rowLabel = l;
            row++;
        }
        InfoRow("Services.SaveWin.InfoFormat", _infoFormat, out _);
        InfoRow("Services.SaveWin.InfoDimensions", _infoDims, out _);
        InfoRow("Services.SaveWin.InfoPages", _infoPages, out var pagesLabel);
        InfoRow("Services.SaveWin.InfoEstimate", _infoEstimate, out _);
        InfoRow("Services.SaveWin.InfoOriginal", _infoCompression, out _);
        _infoEstimate.FontWeight = FontWeights.SemiBold;
        _infoPagesRow = pagesLabel;
        var infoHeader = new Label();
        infoHeader.SetResourceReference(ContentProperty, "Services.SaveWin.Info");
        infoHeader.SetResourceReference(StyleProperty, "Style.Services.SectionLabel");
        var infoPanel = new StackPanel { Children = { infoHeader, ServicesUi.Card(info) } };

        var optionsScroll = new ScrollViewer
        {
            Content = options,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0, 0, 6, 0),
        };

        // ---------------------------------------------------------------- left: preview
        _scroller = new ScrollViewer
        {
            Content = _preview,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            FocusVisualStyle = null,
            Padding = new Thickness(12),
        };
        _scroller.SetResourceReference(BackgroundProperty, "Brush.Services.PreviewBackdrop");
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        HookPan();
        _zoom = new ChipGroup<bool>(new[]
        {
            (true, (object)Loc.Get("Services.SaveWin.PreviewFit")), (false, Loc.Get("Services.SaveWin.PreviewActual")),
        }, true, v => SetZoom(v));
        _zoom.Buttons[0].SetResourceReference(ToolTipProperty, "Services.SaveWin.PreviewFitTip");
        _zoom.Buttons[1].SetResourceReference(ToolTipProperty, "Services.SaveWin.PreviewActualTip");
        _zoom.Panel.Margin = new Thickness(10, 10, 4, 4);
        _zoom.Panel.HorizontalAlignment = HorizontalAlignment.Right;
        _zoom.Panel.VerticalAlignment = VerticalAlignment.Top;
        var statusBorder = new Border { Child = _previewStatus, CornerRadius = new CornerRadius(4), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
        statusBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        statusBorder.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text))
        {
            Source = _previewStatus,
            Converter = new EmptyToCollapsed(),
        });
        var previewGrid = new Grid { ClipToBounds = true };
        previewGrid.Children.Add(_scroller);
        previewGrid.Children.Add(statusBorder);
        var previewCard = ServicesUi.Card(previewGrid);
        previewCard.ClipToBounds = true;

        // ---------------------------------------------------------------- actions
        _copyBox = CheckBox("Services.SaveWin.CopyAfterSave", s.CopyAfterSave, _ => { });
        _copyBox.Margin = new Thickness(0, 0, 16, 0);
        _copyBox.VerticalAlignment = VerticalAlignment.Center;
        _saveButton = DialogButton("Services.SaveWin.Save", accent: true, (_, _) => Save());
        _saveButton.IsDefault = true;
        _cancelButton = DialogButton("Services.SaveWin.Cancel", accent: false, (_, _) => Close());
        _cancelButton.IsCancel = true;
        _cancelButton.Margin = new Thickness(8, 0, 0, 0);
        _buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, Children = { _saveButton, _cancelButton } };
        _replaceButton = DialogButton("Services.SaveWin.Replace", accent: true, (_, _) => ConfirmOverwrite());
        _backButton = DialogButton("Services.SaveWin.Cancel", accent: false, (_, _) => CancelConfirm(focusSave: true));
        _backButton.Margin = new Thickness(8, 0, 0, 0);
        _confirmText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _confirmPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
            Children = { _replaceButton, _backButton },
        };
        var actions = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
        DockPanel.SetDock(_buttonsPanel, Dock.Right);
        DockPanel.SetDock(_confirmPanel, Dock.Right);
        DockPanel.SetDock(_copyBox, Dock.Left);
        actions.Children.Add(_buttonsPanel);
        actions.Children.Add(_confirmPanel);
        actions.Children.Add(_copyBox);
        var messages = new Grid();
        messages.Children.Add(_error);
        var warn = new TextBlock { Text = "", FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        warn.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        warn.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        warn.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = _confirmText, Converter = new EmptyToCollapsed() });
        messages.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { warn, _confirmText } });
        actions.Children.Add(messages);

        // ---------------------------------------------------------------- root (child order = Tab order)
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        AddCell(root, fields, 1, 0, colSpan: 3);
        var right = new DockPanel();
        DockPanel.SetDock(infoPanel, Dock.Bottom);
        right.Children.Add(infoPanel);
        right.Children.Add(optionsScroll);
        AddCell(root, right, 0, 2);
        AddCell(root, actions, 2, 0, colSpan: 3);
        var previewHost = new Grid { Children = { previewCard, _zoom.Panel } };
        AddCell(root, previewHost, 0, 0);
        Content = root;

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => PlaceOnMonitor();
        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
            StartEncode();
        };
        Closed += (_, _) => { _debounce.Stop(); _cts?.Cancel(); };
        UpdateVisibility();
        UpdateStaticInfo();
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Centre on the monitor of the owner (or of the cursor when it is over the owner / there is no owner).</summary>
    private void PlaceOnMonitor()
    {
        try
        {
            var cursor = Monitors.CursorPx;
            MonitorInfo mon;
            if (_owner != null)
            {
                var ob = WindowHelper.GetBoundsPx(_owner);
                bool inside = cursor.X >= ob.X && cursor.Y >= ob.Y && cursor.X < ob.X + ob.Width && cursor.Y < ob.Y + ob.Height;
                mon = inside ? Monitors.FromPointPx((int)cursor.X, (int)cursor.Y) : Monitors.FromPointPx(ob.X + ob.Width / 2, ob.Y + ob.Height / 2);
            }
            else mon = Monitors.FromPointPx((int)cursor.X, (int)cursor.Y);
            var wa = mon.WorkAreaPx;
            // Move onto the monitor first so WPF adapts the size to its DPI, then centre (clamped to the work area).
            WindowHelper.SetPositionPx(this, wa.X, wa.Y);
            var b = WindowHelper.GetBoundsPx(this);
            int w = Math.Min(b.Width, (int)(wa.Width * 0.94)), h = Math.Min(b.Height, (int)(wa.Height * 0.94));
            WindowHelper.SetBoundsPx(this, new Int32Rect(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h));
        }
        catch (Exception ex) { Log.Warn($"Placing the save window failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ options

    private (int W, int H) TargetSize() => _scale switch
    {
        ScaleMode.P75 => (Math.Max(1, (int)Math.Round(_image.PixelWidth * 0.75)), Math.Max(1, (int)Math.Round(_image.PixelHeight * 0.75))),
        ScaleMode.P50 => (Math.Max(1, (int)Math.Round(_image.PixelWidth * 0.5)), Math.Max(1, (int)Math.Round(_image.PixelHeight * 0.5))),
        ScaleMode.Custom => (Math.Clamp(_customW, 1, 32767), Math.Clamp(_customH, 1, 32767)),
        _ => (_image.PixelWidth, _image.PixelHeight),
    };

    private PdfOptions CurrentPdf() => new()
    {
        Paper = _paper,
        Orientation = _orientation,
        MarginMm = _marginMm,
        FitToPage = _fitToPage,
        AutoPaginate = _paginate,
        OverlapMm = _overlapMm,
        UseJpeg = _pdfJpeg,
        JpegQuality = _pdfJpeg ? _quality : 92,
    };

    private Request CurrentRequest()
    {
        var (w, h) = TargetSize();
        // Irrelevant options are normalised so they do not trigger a re-encode.
        bool usesQuality = _format == ImageFileFormat.Jpg || (_format == ImageFileFormat.Pdf && _pdfJpeg);
        var pdf = _format == ImageFileFormat.Pdf ? CurrentPdf() : new PdfOptions();
        return new Request(_format, usesQuality ? _quality : 0, w, h, pdf);
    }

    private void CustomSizeChanged(int? width, int? height)
    {
        if (_syncingCustom) return;
        _syncingCustom = true;
        try
        {
            double ratio = (double)_image.PixelHeight / _image.PixelWidth;
            if (width is { } w)
            {
                _customW = w;
                _customH = Math.Clamp((int)Math.Round(w * ratio), 1, 32767);
                SetNumber(_customHBox, _customH);
            }
            else if (height is { } h)
            {
                _customH = h;
                _customW = Math.Clamp((int)Math.Round(h / ratio), 1, 32767);
                SetNumber(_customWBox, _customW);
            }
        }
        finally { _syncingCustom = false; }
        if (_scale == ScaleMode.Custom) OptionsChanged();
    }

    private void OptionsChanged()
    {
        UpdateVisibility();
        UpdateStaticInfo();
        if (!IsLoaded) return;
        if (_latest?.Request == CurrentRequest())
        {
            ShowResult(_latest);
            return;
        }
        _previewStatus.Text = Loc.Get("Services.SaveWin.Encoding");
        _infoEstimate.Text = "…";
        _debounce.Stop();
        _debounce.Start();
    }

    private void UpdateVisibility()
    {
        _extText.Text = ImageEncoding.ExtensionOf(_format);
        bool pdf = _format == ImageFileFormat.Pdf;
        _qualityRow.Visibility = _format == ImageFileFormat.Jpg || (pdf && _pdfJpeg) ? Visibility.Visible : Visibility.Collapsed;
        _customRow.Visibility = _scale == ScaleMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        _pdfSection.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        _pdfLayoutRows.Visibility = _paper == PdfPaperSize.Original ? Visibility.Collapsed : Visibility.Visible;
        _overlapRow.IsEnabled = _paginate;
        _infoPagesRow.Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        _infoPages.Visibility = _infoPagesRow.Visibility;
    }

    private void UpdateStaticInfo()
    {
        var (w, h) = TargetSize();
        _infoFormat.Text = _format == ImageFileFormat.Pdf && _pdfJpeg ? "PDF (JPEG)" : _format.ToString().ToUpperInvariant();
        _infoDims.Text = Loc.Format("Services.SaveWin.InfoDimensionsValue", w, h);
    }

    // ------------------------------------------------------------------ encoding

    private async void StartEncode()
    {
        _debounce.Stop();
        var req = CurrentRequest();
        if (_latest?.Request == req)
        {
            ShowResult(_latest);
            return;
        }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int id = ++_requestId;
        _previewStatus.Text = Loc.Get("Services.SaveWin.Encoding");
        try
        {
            double aspect = _scroller.ActualHeight > 50 ? Math.Max(0.2, (_scroller.ActualWidth - 24) / (_scroller.ActualHeight - 24)) : 4.0 / 3;
            var result = await Task.Run(() => Encode(req, cts.Token, aspect), cts.Token);
            if (id != _requestId || !IsLoaded) return;
            ShowResult(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (id != _requestId) return;
            Log.Error("Save preview encoding failed", ex);
            _previewStatus.Text = Loc.Format("Services.SaveWin.EncodeFailed", ex.Message);
            _infoEstimate.Text = "—";
            _infoCompression.Text = "—";
        }
    }

    private BitmapSource Scaled(int w, int h)
    {
        lock (_scaledGate)
        {
            if (_scaled is { } c && c.W == w && c.H == h) return c.Image;
        }
        var img = ImageResize.Resize(_image, w, h);
        lock (_scaledGate) _scaled = (w, h, img);
        return img;
    }

    /// <summary>Encode exactly what will be written, and build the preview from the encoded data. Any thread.</summary>
    private Encoded Encode(Request req, CancellationToken ct, double viewAspect = 4.0 / 3)
    {
        var scaled = Scaled(req.Width, req.Height);
        ct.ThrowIfCancellationRequested();
        if (req.Format == ImageFileFormat.Pdf)
        {
            var pages = PdfImageWriter.Build(scaled, req.Pdf);
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            PdfImageWriter.WritePages(ms, pages);
            var preview = PdfPreview(pages, viewAspect);
            var first = pages[0].Plan;
            string paper = req.Pdf.Paper == PdfPaperSize.Original
                ? $"{first.PageWidth / PdfLayout.PtPerMm:0} × {first.PageHeight / PdfLayout.PtPerMm:0} mm"
                : $"{(req.Pdf.Paper == PdfPaperSize.A4 ? "A4" : "Letter")} {Loc.Get(first.PageWidth > first.PageHeight ? "Services.SaveWin.Landscape" : "Services.SaveWin.Portrait")}";
            return new Encoded(req, ms.ToArray(), scaled, preview, true, pages.Count, Loc.Format("Services.SaveWin.InfoPagesValue", pages.Count, paper));
        }
        using (var ms = new MemoryStream())
        {
            ImageEncoding.Encode(scaled, req.Format, ms, Math.Max(1, req.Quality == 0 ? 92 : req.Quality), req.Pdf);
            var bytes = ms.ToArray();
            ct.ThrowIfCancellationRequested();
            // Decode what was written: the preview shows the real JPEG artefacts / flattening.
            var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return new Encoded(req, bytes, scaled, frame, false, 1, "");
        }
    }

    /// <summary>
    /// Pages laid out in a grid whose shape best fits the preview area (white sheets, slice images at their placement;
    /// JPEG slices decoded from the PDF data, so the preview shows the real compression).
    /// </summary>
    private static ImageSource PdfPreview(List<PdfImageWriter.Page> pages, double viewAspect)
    {
        const double k = 96.0 / 72, gap = 18;
        double maxW = pages.Max(p => p.Plan.PageWidth), maxH = pages.Max(p => p.Plan.PageHeight);
        int cols = 1;
        double best = 0;
        for (int c = 1; c <= pages.Count; c++)
        {
            int r = (pages.Count + c - 1) / c;
            double scale = Math.Min(viewAspect / (c * maxW + (c - 1) * gap), 1 / (r * maxH + (r - 1) * gap));
            if (scale > best * 1.0001) { best = scale; cols = c; }
        }
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var paper = new SolidColorBrush(Colors.White);
            paper.Freeze();
            var shadow = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
            shadow.Freeze();
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 0.75);
            edge.Freeze();
            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                var pl = p.Plan;
                double x = (i % cols) * (maxW + gap) + (maxW - pl.PageWidth) / 2, y = (i / cols) * (maxH + gap);
                var sheet = new Rect(x * k, y * k, pl.PageWidth * k, pl.PageHeight * k);
                dc.DrawRectangle(shadow, null, new Rect(sheet.X + 2, sheet.Y + 3, sheet.Width, sheet.Height));
                dc.DrawRectangle(paper, edge, sheet);
                BitmapSource img = p.Slice;
                if (p.Filter == "/DCTDecode")
                {
                    var f = BitmapDecoder.Create(new MemoryStream(p.Data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    f.Freeze();
                    img = f;
                }
                var place = pl.PlacementTopLeft;
                dc.DrawImage(img, new Rect((x + place.X) * k, (y + place.Y) * k, place.Width * k, place.Height * k));
            }
        }
        group.Freeze();
        var di = new DrawingImage(group);
        di.Freeze();
        return di;
    }

    private void ShowResult(Encoded r)
    {
        _latest = r;
        _preview.Source = r.Preview;
        ApplyZoom();
        _previewStatus.Text = "";
        _infoEstimate.Text = FormatBytes(r.Bytes.LongLength);
        long raw = (long)_image.PixelWidth * _image.PixelHeight * 4;
        double pct = raw > 0 ? 100.0 * r.Bytes.LongLength / raw : 0;
        _infoCompression.Text = Loc.Format("Services.SaveWin.InfoOriginalValue", FormatBytes(raw), FormatBytes(r.Bytes.LongLength),
            pct.ToString(pct < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%");
        _infoPages.Text = r.PageInfo;
    }

    internal static string FormatBytes(long bytes)
    {
        var inv = CultureInfo.InvariantCulture;
        if (bytes < 1024) return bytes.ToString(inv) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString(bytes < 10 * 1024 ? "0.0" : "0", inv) + " KB";
        return (bytes / (1024.0 * 1024)).ToString(bytes < 100L * 1024 * 1024 ? "0.00" : "0.0", inv) + " MB";
    }

    // ------------------------------------------------------------------ preview zoom / pan

    private void SetZoom(bool fit)
    {
        _fit = fit;
        if (_zoom.Value != fit) _zoom.Value = fit;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        bool pdf = _latest?.IsPdf == true;
        if (_fit)
        {
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _preview.Stretch = Stretch.Uniform;
            _preview.StretchDirection = StretchDirection.DownOnly;
            _preview.LayoutTransform = Transform.Identity;
            _scroller.Cursor = null;
        }
        else
        {
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _preview.Stretch = Stretch.None;
            // 100 % = one image pixel per physical pixel (PDF: real paper size).
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _preview.LayoutTransform = pdf || Math.Abs(dpi - 1) < 0.01 ? Transform.Identity : new ScaleTransform(1 / dpi, 1 / dpi);
            _scroller.Cursor = Cursors.SizeAll;
        }
    }

    private void HookPan()
    {
        Point? start = null;
        double h0 = 0, v0 = 0;
        _scroller.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _scroller.Focus();
            if (e.ClickCount == 2) { SetZoom(!_fit); e.Handled = true; return; }
            if (_fit) return;
            start = e.GetPosition(_scroller);
            h0 = _scroller.HorizontalOffset;
            v0 = _scroller.VerticalOffset;
            _scroller.CaptureMouse();
            e.Handled = true;
        };
        _scroller.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } s0 || !_scroller.IsMouseCaptured) return;
            var p = e.GetPosition(_scroller);
            _scroller.ScrollToHorizontalOffset(h0 - (p.X - s0.X));
            _scroller.ScrollToVerticalOffset(v0 - (p.Y - s0.Y));
        };
        _scroller.PreviewMouseLeftButtonUp += (_, _) =>
        {
            start = null;
            if (_scroller.IsMouseCaptured) _scroller.ReleaseMouseCapture();
        };
    }

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pendingOverwritePath != null && e.Key == Key.Escape)
        {
            CancelConfirm(focusSave: true);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (e.Key is Key.D0 or Key.NumPad0) { SetZoom(true); e.Handled = true; }
            else if (e.Key is Key.D1 or Key.NumPad1) { SetZoom(false); e.Handled = true; }
        }
    }

    // ------------------------------------------------------------------ save

    private void Browse()
    {
        try
        {
            var dlg = new OpenFolderDialog { Title = Loc.Get("Services.SaveWin.BrowseTitle") };
            var current = ExpandFolder(_folderBox.Text);
            if (current != null && Directory.Exists(current)) dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true) _folderBox.Text = dlg.FolderName;
            _folderBox.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("Folder dialog failed", ex);
            _error.Text = ex.Message;
        }
    }

    private static string? ExpandFolder(string text)
    {
        try
        {
            var t = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
            return t.Length > 0 && Path.IsPathFullyQualified(t) ? Path.GetFullPath(t) : null;
        }
        catch { return null; }
    }

    /// <summary>File name without a trailing image extension; a typed extension selects that format.</summary>
    private string? CleanName()
    {
        var name = _nameBox.Text.Trim();
        var ext = Path.GetExtension(name);
        if (SaveService.IsSupportedExtension(ext))
        {
            var f = ImageEncoding.FormatFromExtension(ext);
            if (f != _format) _formatChips.Value = f; // updates _format through the chip callback
            name = name[..^ext.Length];
        }
        name = PathTemplate.SanitizeFileName(name).TrimEnd('.', ' ');
        return name.Length == 0 ? null : name;
    }

    private void Save()
    {
        _error.Text = "";
        var name = CleanName();
        if (name == null)
        {
            _error.Text = Loc.Get("Services.SaveWin.ErrorName");
            _nameBox.Focus();
            return;
        }
        var folder = ExpandFolder(_folderBox.Text);
        if (folder == null)
        {
            _error.Text = Loc.Get("Services.SaveWin.ErrorFolder");
            _folderBox.Focus();
            _folderBox.SelectAll();
            return;
        }
        var path = Path.Combine(folder, name + ImageEncoding.ExtensionOf(_format));
        if (File.Exists(path))
        {
            ShowConfirm(path);
            return;
        }
        Write(path);
    }

    private void ShowConfirm(string path)
    {
        _pendingOverwritePath = path;
        _confirmText.Text = Loc.Format("Services.SaveWin.Overwrite", Path.GetFileName(path));
        _error.Text = "";
        _buttonsPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Visible;
        _saveButton.IsDefault = false;
        _cancelButton.IsCancel = false;
        _replaceButton.IsDefault = true;
        Dispatcher.BeginInvoke(() => _replaceButton.Focus(), DispatcherPriority.Input);
    }

    private void CancelConfirm(bool focusSave = false)
    {
        if (_pendingOverwritePath == null) return;
        _pendingOverwritePath = null;
        _confirmText.Text = "";
        _confirmPanel.Visibility = Visibility.Collapsed;
        _buttonsPanel.Visibility = Visibility.Visible;
        _replaceButton.IsDefault = false;
        _saveButton.IsDefault = true;
        _cancelButton.IsCancel = true;
        if (focusSave) Dispatcher.BeginInvoke(() => _nameBox.Focus(), DispatcherPriority.Input);
    }

    private void ConfirmOverwrite()
    {
        var path = _pendingOverwritePath;
        CancelConfirm();
        if (path != null) Write(path);
    }

    private void Write(string path)
    {
        var req = CurrentRequest();
        Encoded enc;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            enc = _latest?.Request == req ? _latest : Encode(req, CancellationToken.None);
            SaveService.WriteAtomic(path, s => s.Write(enc.Bytes, 0, enc.Bytes.Length));
            Log.Info($"Saved {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"Saving {path} failed", ex);
            _error.Text = Loc.Format("Services.SaveFailed", ex.Message);
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        var s = _settings;
        if (s.RememberLastExtension) s.LastExtension = ImageEncoding.ExtensionOf(_format);
        s.LastFolder = Path.GetDirectoryName(path) ?? "";
        if (req.Quality > 0) s.Quality = _quality;
        if (_format == ImageFileFormat.Pdf)
        {
            s.PdfPaper = _paper;
            s.PdfOrientation = _orientation;
            s.PdfMarginMm = _marginMm;
            s.PdfFitToPage = _fitToPage;
            s.PdfAutoPaginate = _paginate;
            s.PdfPageOverlapMm = _overlapMm;
            s.PdfUseJpeg = _pdfJpeg;
        }
        s.CopyAfterSave = _copyBox.IsChecked == true;
        ServicesConfig.Persist();

        if (s.CopyAfterSave)
        {
            try { ClipboardService.SetImage(enc.Scaled); }
            catch (Exception ex) { Log.Error("Copy after save failed", ex); }
        }
        SavedPath = path;
        DialogResult = true;
    }

    // ------------------------------------------------------------------ control factories

    private static TextBlock Value() => new() { FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };

    private static void AddCell(Grid g, UIElement e, int row, int col, int colSpan = 1)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        Grid.SetColumnSpan(e, colSpan);
        g.Children.Add(e);
    }

    private static TextBox Input(string text)
    {
        var t = new TextBox { Text = text };
        t.SetResourceReference(StyleProperty, "Style.Services.InputBox");
        return t;
    }

    private static Label FieldLabel(string key, UIElement target, Thickness margin)
    {
        var l = new Label { Target = target, Margin = margin };
        l.SetResourceReference(ContentProperty, key);
        l.SetResourceReference(StyleProperty, "Style.Services.FieldLabel");
        return l;
    }

    private static Label SectionLabel(string key, UIElement target, bool top = true)
    {
        var l = new Label { Target = target };
        l.SetResourceReference(ContentProperty, key);
        l.SetResourceReference(StyleProperty, "Style.Services.SectionLabel");
        if (!top) l.Margin = new Thickness(0, 0, 0, 8);
        return l;
    }

    private static Label SmallCaption(string key, UIElement target)
    {
        var l = FieldLabel(key, target, new Thickness(0, 6, 0, 6));
        l.HorizontalAlignment = HorizontalAlignment.Left;
        return l;
    }

    private static Label SmallLabel(string key, UIElement target) => FieldLabel(key, target, new Thickness(0, 0, 6, 0));

    private static FrameworkElement Spacer(double h) => new Border { Height = h };

    private static Button DialogButton(string key, bool accent, RoutedEventHandler click)
    {
        var b = new Button();
        b.SetResourceReference(ContentProperty, key);
        b.SetResourceReference(StyleProperty, accent ? "Style.Services.DialogAccentButton" : "Style.Services.DialogButton");
        b.Click += click;
        return b;
    }

    private static CheckBox CheckBox(string key, bool value, Action<bool> changed)
    {
        var c = new CheckBox { IsChecked = value };
        c.SetResourceReference(ContentProperty, key);
        c.SetResourceReference(StyleProperty, "Style.Services.CheckBox");
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    /// <summary>Numeric text box: digits (and '.'), Up/Down (Shift = ×10) step, clamped; invalid text is ignored.</summary>
    private static TextBox NumberBox(double value, double min, double max, double step, string format, Action<double> changed)
    {
        var t = Input(value.ToString(format, CultureInfo.InvariantCulture));
        t.HorizontalContentAlignment = HorizontalAlignment.Right;
        t.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(ch => char.IsDigit(ch) || ch == '.');
        DataObject.AddPastingHandler(t, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string s || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double _))
                e.CancelCommand();
        });
        double Parse() => double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : double.NaN;
        t.TextChanged += (_, _) =>
        {
            var v = Parse();
            if (!double.IsNaN(v)) changed(v);
        };
        t.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            var v = Parse();
            if (double.IsNaN(v)) v = min;
            double d = (e.Key == Key.Up ? step : -step) * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1);
            t.Text = Math.Clamp(Math.Round(v + d, 3), min, max).ToString(format, CultureInfo.InvariantCulture);
            t.CaretIndex = t.Text.Length;
            e.Handled = true;
        };
        t.LostKeyboardFocus += (_, _) =>
        {
            var v = Parse();
            t.Text = (double.IsNaN(v) ? min : v).ToString(format, CultureInfo.InvariantCulture);
        };
        return t;
    }

    private static void SetNumber(TextBox t, double v) => t.Text = v.ToString(CultureInfo.InvariantCulture);

    private sealed class EmptyToCollapsed : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// A row of radio "chips". Keyboard: Tab stops on the checked chip only; arrow keys move AND select (like Win32
    /// radio groups); Space / access keys work as usual.
    /// </summary>
    private sealed class ChipGroup<T> where T : struct
    {
        private readonly List<(T Value, RadioButton Button)> _items = new();
        private readonly Action<T> _changed;
        private bool _setting;

        public WrapPanel Panel { get; } = new() { Orientation = Orientation.Horizontal };
        public IReadOnlyList<RadioButton> Buttons => _items.Select(i => i.Button).ToList();

        public ChipGroup(IEnumerable<(T Value, object Content)> items, T selected, Action<T> changed)
        {
            _changed = changed;
            string group = "chips-" + Guid.NewGuid().ToString("N");
            foreach (var (v, content) in items)
            {
                var rb = new RadioButton { Content = content, GroupName = group };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Style.Services.Chip");
                var value = v;
                rb.Checked += (_, _) =>
                {
                    UpdateTabStops();
                    if (!_setting) _changed(value);
                };
                _items.Add((v, rb));
                Panel.Children.Add(rb);
            }
            Select(selected, notify: false);
            Panel.PreviewKeyDown += OnKey;
        }

        /// <summary>The access-key label of the group: its target follows the checked chip.</summary>
        public Label? Label
        {
            get => _label;
            set { _label = value; UpdateTabStops(); }
        }
        private Label? _label;

        public T Value
        {
            get => _items.FirstOrDefault(i => i.Button.IsChecked == true).Value;
            set => Select(value, notify: true);
        }

        private void Select(T value, bool notify)
        {
            var item = _items.FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, value));
            if (item.Button == null || item.Button.IsChecked == true) return;
            _setting = true;
            try { item.Button.IsChecked = true; }
            finally { _setting = false; }
            if (notify) _changed(value);
        }

        public RadioButton Checked => _items.FirstOrDefault(i => i.Button.IsChecked == true).Button ?? _items[0].Button;

        private void UpdateTabStops()
        {
            foreach (var (_, b) in _items) b.IsTabStop = b.IsChecked == true;
            if (_label != null) _label.Target = Checked;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            int dir = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
            if (dir == 0) return;
            int i = _items.FindIndex(x => x.Button.IsKeyboardFocused);
            if (i < 0) return;
            for (int k = 1; k <= _items.Count; k++)
            {
                var next = _items[((i + dir * k) % _items.Count + _items.Count) % _items.Count].Button;
                if (!next.IsEnabled) continue;
                next.IsChecked = true;
                next.Focus();
                break;
            }
            e.Handled = true;
        }
    }
}
