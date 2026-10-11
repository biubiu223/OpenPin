using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Core.Hotkeys;
using OpenPin.SettingsUI.Logic;

namespace OpenPin.SettingsUI.Controls;

/// <summary>
/// Hotkey recorder (SET-02). Click (or Enter/Space) → press a combination → shows "Ctrl+Shift+A" as key chips.
/// Backspace/Delete clears, Esc cancels. Global hotkeys are suspended while recording (they would swallow the keys).
/// </summary>
internal sealed class HotkeyBox : Border
{
    private readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Func<IDisposable> _suspendHotkeys;
    private IDisposable? _suspension;
    private string _hotkeyText = "";
    private bool _recording;

    /// <summary>Raised with the new hotkey text ("" = cleared).</summary>
    public event Action<string>? HotkeyChanged;

    public HotkeyBox(Func<IDisposable> suspendHotkeys)
    {
        _suspendHotkeys = suspendHotkeys;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        MinWidth = 180;
        MinHeight = 32;
        Padding = new Thickness(8, 3, 8, 3);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1, 1, 1, 1);
        SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Child = _chips;
        ToolTipService.SetInitialShowDelay(this, 500);
        SetResourceReference(ToolTipProperty, "SettingsUI.Hotkey.Tip");
        Refresh();

        MouseLeftButtonDown += (_, e) => { Focus(); StartRecording(); e.Handled = true; };
        MouseEnter += (_, _) => { if (!_recording) SetResourceReference(BackgroundProperty, "ControlFillColorSecondaryBrush"); };
        MouseLeave += (_, _) => { if (!_recording) SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush"); };
        LostKeyboardFocus += (_, _) => StopRecording();
        Unloaded += (_, _) => StopRecording();
    }

    public string HotkeyText
    {
        get => _hotkeyText;
        set { _hotkeyText = value ?? ""; Refresh(); }
    }

    public bool IsRecording => _recording;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (!_recording)
        {
            if (key is Key.Enter or Key.Space) { StartRecording(); e.Handled = true; }
            else if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { Commit(""); e.Handled = true; }
            return;
        }
        e.Handled = true;
        if (key == Key.Snapshot) return; // handled on key up (no key-down for PrintScreen)
        Handle(key, Keyboard.Modifiers);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!_recording) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Snapshot) Handle(key, Keyboard.Modifiers);
        else if (Keyboard.Modifiers == ModifierKeys.None) ShowRecordingHint(null);
        else ShowPreview(HotkeyRecorderLogic.ModifierPreview(Keyboard.Modifiers));
    }

    private void Handle(Key key, ModifierKeys mods)
    {
        var r = HotkeyRecorderLogic.Interpret(key, mods);
        switch (r.Outcome)
        {
            case RecorderOutcome.Pending: ShowPreview(r.Preview); break;
            case RecorderOutcome.Cancel: StopRecording(); break;
            case RecorderOutcome.Clear: StopRecording(); Commit(""); break;
            case RecorderOutcome.Set: StopRecording(); Commit(r.Hotkey.ToString()); break;
            case RecorderOutcome.Rejected: ShowRecordingHint("SettingsUI.Hotkey.NeedModifier", r.Preview); break;
        }
    }

    private void Commit(string text)
    {
        if (text == _hotkeyText) { Refresh(); return; }
        _hotkeyText = text;
        Refresh();
        HotkeyChanged?.Invoke(text);
    }

    private void StartRecording()
    {
        if (_recording) return;
        _recording = true;
        try { _suspension = _suspendHotkeys(); } catch (Exception ex) { Core.Log.Error("Suspending hotkeys failed", ex); }
        BorderThickness = new Thickness(1, 1, 1, 2);
        SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
        SetResourceReference(BackgroundProperty, "ControlFillColorInputActiveBrush");
        ShowRecordingHint(null);
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _suspension?.Dispose();
        _suspension = null;
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
        Refresh();
    }

    private void ShowRecordingHint(string? errorKey, string preview = "")
    {
        _chips.Children.Clear();
        if (preview.Length > 0) AddChips(preview, errorKey != null);
        _chips.Children.Add(HintText(errorKey ?? "SettingsUI.Hotkey.Recording",
            errorKey != null ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush", preview.Length > 0 ? 4 : 0));
    }

    private void ShowPreview(string preview)
    {
        if (preview.Length == 0) { ShowRecordingHint(null); return; }
        _chips.Children.Clear();
        AddChips(preview.TrimEnd('+'), false);
        var dots = new TextBlock { Text = "+ …", VerticalAlignment = VerticalAlignment.Center };
        dots.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _chips.Children.Add(dots);
    }

    private void Refresh()
    {
        _chips.Children.Clear();
        if (string.IsNullOrEmpty(_hotkeyText))
        {
            _chips.Children.Add(HintText("SettingsUI.Hotkey.None", "TextFillColorTertiaryBrush", 0));
            return;
        }
        AddChips(Hotkey.TryParse(_hotkeyText, out var hk) ? hk.ToString() : _hotkeyText, false);
    }

    private static TextBlock HintText(string key, string brushKey, double left)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(left, 0, 0, 0) };
        t.SetResourceReference(TextBlock.TextProperty, key);
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }
    private void AddChips(string text, bool error)
    {
        // "Ctrl++" style texts: split keeping a trailing "+" key
        var parts = text.Replace("++", "+\u0001").Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p == "\u0001" ? "+" : p);
        foreach (var part in parts)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(0, 1, 4, 1),
                MinWidth = 24,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = part, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
            };
            if (error)
            {
                chip.SetResourceReference(BackgroundProperty, "SystemFillColorCriticalBackgroundBrush");
                chip.SetResourceReference(BorderBrushProperty, "SystemFillColorCriticalBrush");
            }
            else
            {
                chip.SetResourceReference(BackgroundProperty, "AccentFillColorDefaultBrush");
                chip.SetResourceReference(BorderBrushProperty, "AccentFillColorDefaultBrush");
                ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            }
            _chips.Children.Add(chip);
        }
    }
}
