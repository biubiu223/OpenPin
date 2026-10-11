using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenPin.Core;
using OpenPin.Core.Native;
using OpenPin.Core.Screen;
using OpenPin.SettingsUI.Logic;
using OpenPin.SettingsUI.Views;

namespace OpenPin.SettingsUI;

internal enum StatusKind { Info, Success, Error }

internal enum DialogChoice { None, Primary, Secondary }

/// <summary>
/// The settings window (SET-01): Windows 11 look via WPF's built-in Fluent theme, applied to THIS window only
/// (<see cref="Window.ThemeMode"/> merges the Fluent dictionary into the window's resources — Application.Resources,
/// and therefore the capture overlay, pins and toolbars, stay untouched). Mica backdrop on Windows 11, solid on Windows 10.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly string StateFile = Path.Combine(AppPaths.DataDir, "settings-window.json");

    private readonly SettingsSession _session = new();
    private readonly PageBuilder _builder;
    private readonly ListBox _nav = new();
    private readonly ContentControl _pageHost = new() { Focusable = false };
    private readonly TextBlock _pageTitle;
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly TextBlock _statusIcon;
    private readonly TextBlock _statusText;
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };
    private readonly Button _applyButton;
    private SettingsWindowState _state = new();
    private bool _closeConfirmed;
    private bool? _dark;

    /// <summary>Expand all expander cards initially (OPENPIN_SETTINGSUI_EXPAND=1, used for screenshot checks).</summary>
    public bool ExpandGroups { get; } = Environment.GetEnvironmentVariable("OPENPIN_SETTINGSUI_EXPAND") == "1";

    public string CurrentPage { get; private set; } = "general";

    public SettingsWindow()
    {
        _builder = new PageBuilder(_session, this);
        SetResourceReference(TitleProperty, "SettingsUI.WindowTitle");
        Width = 1040;
        Height = 740;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SetResourceReference(FontFamilyProperty, "Font.UI");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/AppIcon.ico")); }
        catch (Exception ex) { Log.Warn($"Settings icon: {ex.Message}"); }
        TemplatePreview.SampleTitle = Loc.Get("SettingsUI.SampleWindowTitle");

        ApplyTheme();

        // ---------------------------------------------------------------- layout
        var root = new Grid();
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(264) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(main);

        // Navigation pane
        var navPanel = new DockPanel { Margin = new Thickness(8, 8, 4, 8) };
        var appTitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 0, 14) };
        if (Icon != null) appTitle.Children.Add(new Image { Source = Icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
        appTitle.Children.Add(Kit.Text("SettingsUI.AppTitle", 13, "TextFillColorPrimaryBrush"));
        DockPanel.SetDock(appTitle, Dock.Top);
        navPanel.Children.Add(appTitle);

        _nav.SetResourceReference(StyleProperty, "Style.SettingsUI.NavList");
        _nav.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Style.SettingsUI.NavItem");
        foreach (var page in SettingsCatalog.Pages)
        {
            var item = new ListBoxItem { Tag = page.Id };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Kit.Icon(page.Icon, 16);
            ic.Margin = new Thickness(0, 0, 14, 0);
            sp.Children.Add(ic);
            sp.Children.Add(Kit.Text(page.TitleKey, 14));
            item.Content = sp;
            if (page.Id == "about")
            {
                // About sits at the bottom like Windows' own settings footer items.
                item.Margin = new Thickness(0, 0, 0, 0);
            }
            _nav.Items.Add(item);
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: string id }) ShowPage(id);
        };
        navPanel.Children.Add(_nav);
        main.Children.Add(navPanel);

        // Content
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(content, 1);
        main.Children.Add(content);

        _pageTitle = Kit.Text("", 28, "TextFillColorPrimaryBrush", FontWeights.SemiBold);
        _pageTitle.Margin = new Thickness(36, 22, 36, 14);
        content.Children.Add(_pageTitle);

        _pageHost.Margin = new Thickness(36, 0, 36, 28);
        _pageHost.MaxWidth = 1000;
        _pageHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scroll = new ScrollViewer
        {
            Content = _pageHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);

        // Footer: status + 确定 / 取消 / 应用
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(36, 12, 24, 12) };
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        footer.SetResourceReference(Border.BackgroundProperty, "LayerOnMicaBaseAltFillColorTransparentBrush");
        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _statusIcon = Kit.Icon("", 14);
        _statusIcon.Margin = new Thickness(0, 0, 8, 0);
        _statusText = Kit.Text("", 13, "TextFillColorSecondaryBrush");
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusText.TextWrapping = TextWrapping.NoWrap;
        status.Children.Add(_statusIcon);
        status.Children.Add(_statusText);
        footerGrid.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = Kit.Button("Common.OK", (_, _) => { if (ApplyNow()) CloseConfirmed(); }, accent: true);
        var cancel = Kit.Button("Common.Cancel", (_, _) => CloseConfirmed());
        _applyButton = Kit.Button("Common.Apply", (_, _) => ApplyNow());
        foreach (var b in new[] { ok, cancel, _applyButton })
        {
            b.MinWidth = 104;
            b.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(b);
        }
        _applyButton.SetBinding(IsEnabledProperty, new Binding(nameof(SettingsSession.IsDirty)) { Source = _session });
        Grid.SetColumn(buttons, 1);
        footerGrid.Children.Add(buttons);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);

        // Dialog overlay (ContentDialog-like, themed with the window)
        _overlay.SetResourceReference(Panel.BackgroundProperty, "SmokeFillColorDefaultBrush");
        root.Children.Add(_overlay);

        Content = root;

        // Any binding update → recompute dirty state.
        AddHandler(Binding.SourceUpdatedEvent, new EventHandler<DataTransferEventArgs>((_, _) => _session.NotifyEdited()));
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsSession.IsDirty)) UpdateDirtyStatus();
        };

        LoadState();
        SourceInitialized += (_, _) => { RestoreSavedBounds(); ApplyDwm(); };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _session.Dispose();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { ApplyNow(); e.Handled = true; }
            else if (e.Key == Key.Escape && _overlay.Visibility == Visibility.Visible) { CloseDialog(DialogChoice.None); e.Handled = true; }
        };

        Navigate(SettingsCatalog.FindPage(_state.LastPage)?.Id ?? "general");
    }

    // ------------------------------------------------------------------ navigation

    public void Navigate(string? pageId)
    {
        var page = SettingsCatalog.FindPage(pageId) ?? SettingsCatalog.Pages[0];
        var item = _nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == page.Id);
        if (item != null && !ReferenceEquals(_nav.SelectedItem, item)) _nav.SelectedItem = item; // → ShowPage
        else ShowPage(page.Id);
    }

    private void ShowPage(string id)
    {
        var page = SettingsCatalog.FindPage(id);
        if (page is null) return;
        CurrentPage = page.Id;
        _pageTitle.SetResourceReference(TextBlock.TextProperty, page.TitleKey);
        if (!_pages.TryGetValue(page.Id, out var view))
        {
            try { view = _builder.Build(page); }
            catch (Exception ex)
            {
                Log.Error($"Building settings page '{page.Id}' failed", ex);
                view = Kit.Hint(Loc.Format("Common.ActionFailed", ex.Message), "SystemFillColorCriticalBrush");
            }
            _pages[page.Id] = view;
        }
        _pageHost.Content = view;
        _scroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ apply / close

    /// <summary>Applies pending changes. Returns false if the user should stay (nothing blocks at the moment).</summary>
    private bool ApplyNow()
    {
        if (!_session.IsDirty) return true;
        try
        {
            var failed = _session.Apply();
            ApplyTheme();
            if (failed.Count > 0)
            {
                var names = string.Join("、", failed.Select(id => AppServices.Actions?.Get(id)?.DisplayName ?? id));
                SetStatus(Loc.Format("SettingsUI.Status.HotkeysFailed", names), StatusKind.Error);
                Navigate("hotkeys");
                return false;
            }
            SetStatus("SettingsUI.Status.Applied", StatusKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Applying settings failed", ex);
            SetStatus(Loc.Format("Common.ActionFailed", ex.Message), StatusKind.Error);
            return false;
        }
    }

    private void CloseConfirmed()
    {
        _closeConfirmed = true;
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed && _session.IsDirty)
        {
            e.Cancel = true;
            var choice = await ShowDialogAsync("SettingsUI.Unsaved.Title", Loc.Get("SettingsUI.Unsaved.Message"),
                "SettingsUI.Unsaved.Apply", "SettingsUI.Unsaved.Discard", "Common.Cancel");
            if (choice == DialogChoice.None) return;
            if (choice == DialogChoice.Primary && !ApplyNow()) return;
            CloseConfirmed();
            return;
        }
        SaveState();
    }

    // ------------------------------------------------------------------ status

    public void SetStatus(string keyOrText, StatusKind kind)
    {
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, kind == StatusKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
        Kit.SetText(_statusText, keyOrText);
        _statusText.ToolTip = Kit.Label(keyOrText);
        _statusIcon.Text = kind switch { StatusKind.Success => "", StatusKind.Error => "", _ => "" };
        _statusIcon.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            StatusKind.Success => "SystemFillColorSuccessBrush",
            StatusKind.Error => "SystemFillColorCriticalBrush",
            _ => "AccentTextFillColorPrimaryBrush",
        });
    }

    private void UpdateDirtyStatus()
    {
        if (_session.IsDirty) SetStatus("SettingsUI.Status.Pending", StatusKind.Info);
        else if (_statusText.Text == Loc.Get("SettingsUI.Status.Pending")) { _statusText.Text = ""; _statusIcon.Text = ""; }
    }

    // ------------------------------------------------------------------ dialogs

    private TaskCompletionSource<DialogChoice>? _dialog;

    public Task ShowMessageAsync(string titleKey, string message) => ShowDialogAsync(titleKey, message, "Common.OK", null, null);

    /// <summary>Shows an in-window confirmation dialog. Esc / close button → <see cref="DialogChoice.None"/>.</summary>
    public Task<DialogChoice> ShowDialogAsync(string titleKey, string message, string primaryKey, string? secondaryKey, string? closeKey)
    {
        _dialog?.TrySetResult(DialogChoice.None);
        var tcs = new TaskCompletionSource<DialogChoice>();
        _dialog = tcs;

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 22) };
        var title = Kit.Text(titleKey, 20, "TextFillColorPrimaryBrush", FontWeights.SemiBold);
        title.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(title);
        body.Children.Add(Kit.Text(message, 14));

        var footer = new Border { Padding = new Thickness(24), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 8, 8) };
        footer.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var buttons = new UniformGrid0();
        void Add(string? key, DialogChoice choice, bool accent)
        {
            if (key is null) return;
            var b = Kit.Button(key, (_, _) => CloseDialog(choice), accent);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(buttons.Children.Count > 0 ? 8 : 0, 0, 0, 0);
            buttons.Children.Add(b);
            if (accent) b.Loaded += (_, _) => b.Focus();
        }
        Add(primaryKey, DialogChoice.Primary, true);
        Add(secondaryKey, DialogChoice.Secondary, false);
        Add(closeKey, DialogChoice.None, false);
        footer.Child = buttons;

        var stack = new StackPanel();
        stack.Children.Add(body);
        stack.Children.Add(footer);
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            MinWidth = 320,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.3 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        _overlay.Children.Clear();
        _overlay.Children.Add(card);
        _overlay.Visibility = Visibility.Visible;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return tcs.Task;
    }

    private void CloseDialog(DialogChoice choice)
    {
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.Children.Clear();
        var d = _dialog;
        _dialog = null;
        d?.TrySetResult(choice);
    }

    /// <summary>Horizontal panel whose children share the width equally (dialog buttons).</summary>
    private sealed class UniformGrid0 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid0() { Rows = 1; }
    }

    // ------------------------------------------------------------------ theme

#pragma warning disable WPF0001 // ThemeMode is experimental in .NET 9/10; scoped to this window on purpose.
    private void ApplyTheme()
    {
        bool dark = ThemeResolver.IsDark(AppServices.Config.General.Theme, SettingsNative.AppsUseLightTheme());
        if (_dark == dark) return;
        _dark = dark;
        ThemeMode = dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
        if (!SettingsNative.SupportsMica)
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush"); // Windows 10: no Mica
        ApplyDwm();
    }
#pragma warning restore WPF0001

    private void ApplyDwm()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SettingsNative.Apply(hwnd, _dark == true);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    // ------------------------------------------------------------------ bounds persistence (physical px)

    private void LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                _state = JsonSerializer.Deserialize<SettingsWindowState>(File.ReadAllText(StateFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings window state unreadable: {ex.Message}");
            _state = new();
        }
    }

    private void RestoreSavedBounds()
    {
        if (!_state.HasBounds) return;
        try
        {
            var work = Monitors.GetAll().Select(m => m.WorkAreaPx).ToList();
            if (WindowBoundsLogic.Validate(new Int32Rect(_state.X, _state.Y, _state.Width, _state.Height), work) is { } r)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                WindowHelper.SetBoundsPx(this, r);
            }
            if (_state.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            Log.Warn($"Restoring settings window bounds failed: {ex.Message}");
        }
    }

    private void SaveState()
    {
        try
        {
            _state.LastPage = CurrentPage;
            _state.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                var r = WindowHelper.GetBoundsPx(this);
                (_state.X, _state.Y, _state.Width, _state.Height) = (r.X, r.Y, r.Width, r.Height);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving settings window state failed: {ex.Message}");
        }
    }
}
