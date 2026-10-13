using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.Services;

namespace OpenPin.Translate;

/// <summary>
/// DeskMirror: Real-time draggable and resizable desktop lens that captures the screen
/// beneath it and renders translated text directly in-place.
/// </summary>
public sealed class MirrorWindow : Window
{
    private static MirrorWindow? _activeInstance;
    public static MirrorWindow? ActiveInstance => _activeInstance;

    private readonly Image _translatedImage = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };

    private readonly Border _loadingCard = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0F, 0x17, 0x2A)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(14, 6, 14, 6),
        Visibility = Visibility.Collapsed,
        Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5 }
    };

    private readonly TextBlock _loadingText = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold
    };

    private readonly Border _headerContainer = new()
    {
        Height = 32,
        Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x11, 0x18, 0x27)),
        CornerRadius = new CornerRadius(8, 8, 0, 0),
        VerticalAlignment = VerticalAlignment.Top
    };

    private readonly DockPanel _headerBar = new();

    private readonly ComboBox _targetLangCombo = new()
    {
        Height = 24,
        Width = 100,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0)
    };

    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _targetLanguage;
    private string? _ocrLanguage;
    private double _userOpacity = 1.0;
    private bool _autoRefresh = true;
    private bool _isClickThrough;
    private bool _isTranslating;
    private bool _isPeeking;
    private bool _isLoaded;

    public static MirrorWindow ShowMirror(Int32Rect? initialBoundsPx = null, string? targetLanguage = null, string? ocrLanguage = null)
    {
        if (_activeInstance != null && _activeInstance.IsLoaded)
        {
            if (initialBoundsPx is { } r)
            {
                WindowHelper.SetBoundsPx(_activeInstance, r);
            }
            _activeInstance.Activate();
            _ = _activeInstance.RefreshTranslationAsync();
            return _activeInstance;
        }

        var w = new MirrorWindow(initialBoundsPx, targetLanguage, ocrLanguage);
        _activeInstance = w;
        w.Show();
        w.Activate();
        return w;
    }

    public MirrorWindow(Int32Rect? initialBoundsPx = null, string? targetLanguage = null, string? ocrLanguage = null)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Title = "DeskMirror";
        MinWidth = 200;
        MinHeight = 120;

        _targetLanguage = targetLanguage ?? "";
        _ocrLanguage = ocrLanguage;

        BuildUi();

        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            if (_autoRefresh && _isLoaded) _ = RefreshTranslationAsync();
        };

        LocationChanged += (_, _) => OnGeometryChanged();
        SizeChanged += (_, _) => OnGeometryChanged();

        Loaded += (_, _) =>
        {
            _isLoaded = true;
            if (initialBoundsPx is { } r)
            {
                WindowHelper.SetBoundsPx(this, r, topmost: true);
            }
            else
            {
                Width = 520;
                Height = 340;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            _ = RefreshTranslationAsync();
        };

        Closed += (_, _) =>
        {
            _debounceTimer.Stop();
            if (ReferenceEquals(_activeInstance, this)) _activeInstance = null;
        };

        KeyDown += OnWindowKeyDown;
        KeyUp += OnWindowKeyUp;
        MouseWheel += OnWindowMouseWheel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);

        WindowHelper.SetToolWindow(this, true);
        WindowHelper.SetExcludeFromCapture(this, true);
    }

    private void BuildUi()
    {
        var rootGrid = new Grid();

        // 1. Frame Border
        var outerBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x0F, 0x17, 0x2A)),
            ClipToBounds = true
        };
        outerBorder.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 18,
            ShadowDepth = 4,
            Opacity = 0.6
        };

        // 2. View Area below header
        var viewGrid = new Grid { Margin = new Thickness(0, 32, 0, 0) };
        viewGrid.Children.Add(_translatedImage);

        _loadingText.Text = Loc.Get("Translate.Mirror.Translating");
        _loadingCard.Child = _loadingText;
        viewGrid.Children.Add(_loadingCard);

        outerBorder.Child = viewGrid;
        rootGrid.Children.Add(outerBorder);

        // 3. Header Bar
        _headerContainer.Child = _headerBar;
        _headerContainer.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button and not ComboBox and not ComboBoxItem)
            {
                DragMove();
            }
        };

        // Header Left: Icon + Title + Target Language Switcher
        var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        leftStack.Children.Add(new TextBlock
        {
            Text = "🪞",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        leftStack.Children.Add(new TextBlock
        {
            Text = Loc.Get("Translate.Mirror.Title"),
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });

        // Language items: Auto Detect first, then individual languages
        _targetLangCombo.Items.Add(new ComboBoxItem { Content = Loc.Get("Translate.Mirror.AutoDetect"), Tag = "" });
        foreach (var lang in TranslateLanguages.All)
        {
            _targetLangCombo.Items.Add(new ComboBoxItem { Content = lang.DisplayName, Tag = lang.Tag });
        }
        SelectTargetLanguageInCombo(_targetLanguage);
        _targetLangCombo.SelectionChanged += (_, _) =>
        {
            if (_targetLangCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                _targetLanguage = tag;
                _ = RefreshTranslationAsync();
            }
        };
        leftStack.Children.Add(_targetLangCombo);
        DockPanel.SetDock(leftStack, Dock.Left);
        _headerBar.Children.Add(leftStack);

        // Header Right: Controls
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

        // Peek button: mouse down hides translation, mouse up restores
        var btnPeek = CreateHeaderButton("👁️", Loc.Get("Translate.Mirror.Peek"));
        btnPeek.PreviewMouseLeftButtonDown += (_, _) => SetPeeking(true);
        btnPeek.PreviewMouseLeftButtonUp += (_, _) => SetPeeking(false);
        rightStack.Children.Add(btnPeek);

        // Auto-refresh toggle
        var btnAuto = CreateHeaderToggleButton("⚡", Loc.Get("Translate.Mirror.Auto"), _autoRefresh);
        btnAuto.Click += (_, _) => _autoRefresh = btnAuto.IsChecked == true;
        rightStack.Children.Add(btnAuto);

        // Manual Refresh button
        var btnRefresh = CreateHeaderButton("🔄", Loc.Get("Translate.Mirror.Refresh"));
        btnRefresh.Click += (_, _) => _ = RefreshTranslationAsync();
        rightStack.Children.Add(btnRefresh);

        // Click-through button
        var btnPin = CreateHeaderButton("📌", Loc.Get("Translate.Mirror.ClickThrough"));
        btnPin.Click += (_, _) => ToggleClickThrough();
        rightStack.Children.Add(btnPin);

        // Settings button
        var btnSettings = CreateHeaderButton("⚙️", Loc.Get("Translate.OpenSettings"));
        btnSettings.Click += (_, _) => TranslationService.OpenSettings();
        rightStack.Children.Add(btnSettings);

        // Close button
        var btnClose = CreateHeaderButton("✕", Loc.Get("Translate.Mirror.Close"));
        btnClose.Click += (_, _) => Close();
        rightStack.Children.Add(btnClose);

        DockPanel.SetDock(rightStack, Dock.Right);
        _headerBar.Children.Add(rightStack);

        rootGrid.Children.Add(_headerContainer);

        Content = rootGrid;
    }

    private Button CreateHeaderButton(string text, string tooltip)
    {
        var b = new Button
        {
            Content = text,
            Width = 26,
            Height = 24,
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
            FontSize = 11,
            ToolTip = tooltip,
            Cursor = Cursors.Hand
        };
        return b;
    }

    private ToggleButton CreateHeaderToggleButton(string text, string tooltip, bool initial)
    {
        var b = new ToggleButton
        {
            Content = text,
            Width = 26,
            Height = 24,
            Margin = new Thickness(2, 0, 2, 0),
            Background = initial ? new SolidColorBrush(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8)) : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = initial ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)) : new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
            FontSize = 11,
            ToolTip = tooltip,
            IsChecked = initial,
            Cursor = Cursors.Hand
        };
        b.Checked += (_, _) =>
        {
            b.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x38, 0xBD, 0xF8));
            b.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        };
        b.Unchecked += (_, _) =>
        {
            b.Background = Brushes.Transparent;
            b.Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        };
        return b;
    }

    private void SelectTargetLanguageInCombo(string tag)
    {
        foreach (ComboBoxItem item in _targetLangCombo.Items)
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                _targetLangCombo.SelectedItem = item;
                return;
            }
        }
        if (_targetLangCombo.Items.Count > 0) _targetLangCombo.SelectedIndex = 0;
    }

    public async Task RefreshTranslationAsync()
    {
        if (_isTranslating || !TranslationService.IsConfigured(TranslationService.Settings)) return;
        _isTranslating = true;
        _loadingText.Text = Loc.Get("Translate.Mirror.Translating");
        _loadingCard.Visibility = Visibility.Visible;

        try
        {
            var boundsPx = WindowHelper.GetBoundsPx(this);
            if (boundsPx.Width < 20 || boundsPx.Height < 20) return;

            double dpi = WindowHelper.GetDpiScale(this);
            int headerPx = (int)Math.Round(34 * dpi);

            int x = boundsPx.X + (int)Math.Round(2 * dpi);
            int y = boundsPx.Y + headerPx;
            int w = Math.Max(10, boundsPx.Width - (int)Math.Round(4 * dpi));
            int h = Math.Max(10, boundsPx.Height - headerPx - (int)Math.Round(2 * dpi));

            var captureRectPx = new Int32Rect(x, y, w, h);

            // Hide previous translated overlay momentarily so GDI grabs pure underlying desktop pixels
            _translatedImage.Visibility = Visibility.Hidden;
            var screenBmp = ScreenCapture.CaptureRegion(captureRectPx, includeCursor: false);
            _translatedImage.Visibility = Visibility.Visible;

            var ocr = await OcrService.RecognizeAsync(screenBmp, _ocrLanguage).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(ocr.Error))
            {
                _loadingText.Text = ocr.Error;
                await Task.Delay(2000).ConfigureAwait(true);
                return;
            }
            if (ocr.Lines.Count == 0)
            {
                _loadingText.Text = Loc.Get("Translate.NoText");
                await Task.Delay(1500).ConfigureAwait(true);
                return;
            }

            var segments = TextSegmenter.Paragraphs(ocr.Lines);
            if (segments.Count == 0) return;

            string? explicitTarget = string.IsNullOrWhiteSpace(_targetLanguage) ? null : _targetLanguage;
            var outcome = await TranslationService.TranslateAsync(
                segments.Select(s => s.Text).ToList(),
                explicitTarget: explicitTarget).ConfigureAwait(true);

            var boxes = segments.Zip(outcome.Segments, (s, t) => new TranslatedBox(s.Bounds, t, s.LineHeight)).ToList();
            var renderer = new ImageTranslationRenderer { RightToLeft = outcome.TargetLanguage == "ar" };
            var translated = renderer.Render(screenBmp, boxes);

            _translatedImage.Source = translated;
            _translatedImage.Opacity = _isPeeking ? 0.0 : _userOpacity;
        }
        catch (Exception ex)
        {
            Log.Warn($"DeskMirror capture/translate failed: {ex.Message}");
            _loadingText.Text = Loc.Format("Translate.Failed", ex.Message);
            await Task.Delay(2500).ConfigureAwait(true);
        }
        finally
        {
            _isTranslating = false;
            _loadingCard.Visibility = Visibility.Collapsed;
        }
    }

    private void SetPeeking(bool peeking)
    {
        _isPeeking = peeking;
        _translatedImage.Opacity = peeking ? 0.0 : _userOpacity;
    }

    public void ToggleClickThrough()
    {
        _isClickThrough = !_isClickThrough;
        WindowHelper.SetClickThrough(this, _isClickThrough);
        if (_isClickThrough)
        {
            Notify.Info(Loc.Get("Translate.Mirror.ClickThroughEnabled"));
        }
    }

    private void OnGeometryChanged()
    {
        if (!_isLoaded || !_autoRefresh) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetPeeking(true);
        }
        else if (e.Key is Key.F5 or Key.R && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = RefreshTranslationAsync();
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ToggleClickThrough();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            SetPeeking(false);
        }
    }

    private void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isPeeking)
        {
            e.Handled = true;
            _userOpacity = Math.Clamp(_userOpacity + (e.Delta > 0 ? 0.05 : -0.05), 0.1, 1.0);
            _translatedImage.Opacity = _userOpacity;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        if (msg == WM_NCHITTEST)
        {
            var p = PointFromScreen(new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF)));
            int border = 8;
            bool left = p.X < border;
            bool right = p.X > ActualWidth - border;
            bool top = p.Y < border;
            bool bottom = p.Y > ActualHeight - border;

            if (top && left) { handled = true; return new IntPtr(13); /* HTTOPLEFT */ }
            if (top && right) { handled = true; return new IntPtr(14); /* HTTOPRIGHT */ }
            if (bottom && left) { handled = true; return new IntPtr(16); /* HTBOTTOMLEFT */ }
            if (bottom && right) { handled = true; return new IntPtr(17); /* HTBOTTOMRIGHT */ }
            if (left) { handled = true; return new IntPtr(10); /* HTLEFT */ }
            if (right) { handled = true; return new IntPtr(11); /* HTRIGHT */ }
            if (top) { handled = true; return new IntPtr(12); /* HTTOP */ }
            if (bottom) { handled = true; return new IntPtr(15); /* HTBOTTOM */ }
        }
        return IntPtr.Zero;
    }
}
