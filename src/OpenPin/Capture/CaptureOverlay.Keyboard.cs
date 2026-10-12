using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Capture;

internal sealed partial class CaptureOverlay
{
    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    private void OnOverlayKeyDown(KeyEventArgs e)
    {
        var key = RealKey(e);
        var mods = Keyboard.Modifiers;

        // Annotation shortcuts first (Ctrl+Z, Shift+1..0, Delete, Esc = cancel current edit…), only while editing.
        if (HasSel && _mode == Core.CaptureMode.Edit && _drag == DragKind.None && _surface.HandleKeyDown(e))
        {
            e.Handled = true;
            return;
        }
        // Typing into a text box (size input, annotation text, toolbar inputs) never triggers overlay shortcuts.
        if (e.OriginalSource is TextBoxBase || Keyboard.FocusedElement is TextBoxBase || _surface.IsEditingText) return;

        if (key is Key.LeftShift or Key.RightShift)
        {
            if (!e.IsRepeat) _shiftClean = true;
            if (_drag != DragKind.None) { UpdateDrag(); MarkDirty(); }
            return;
        }
        _shiftClean = false;
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            // Handled so Windows does not enter menu mode on Alt release.
            e.Handled = true;
            if (!_altDown) { _altDown = true; MarkDirty(); }
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl) return;
        if (_surface.IsBusy && key is Key.Escape or Key.Enter) return;

        e.Handled = true;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        bool plain = mods == ModifierKeys.None;

        // CAP-18: while the mouse button is held, W/A/S/D nudge the cursor 1 px.
        if (_drag != DragKind.None && plain && key is Key.W or Key.A or Key.S or Key.D)
        {
            CaptureNative.MoveCursorBy(key == Key.A ? -1 : key == Key.D ? 1 : 0, key == Key.W ? -1 : key == Key.S ? 1 : 0);
            return;
        }

        switch (key)
        {
            case Key.Escape:
                if (_stylePanel?.Visibility == Visibility.Visible || _recognizePanel?.Visibility == Visibility.Visible || _qrPanel.IsExpanded)
                {
                    HideFlyouts();
                    return;
                }
                if (_drag != DragKind.None) { CancelDrag(); return; }
                RequestExit();
                return;
            case Key.Enter:
                if (!HasSel && _hover is { } h) _sel = h;
                if (HasSel) ConfirmAction();
                return;
            case Key.C when ctrl && !shift:
                if (HasSel) DoCopy(); else CopyColor();
                return;
            case Key.C when shift && !ctrl:
                if (EnsureSelection()) DoOcr();
                return;
            case Key.C when plain:
                CopyColor();
                return;
            case Key.S when ctrl && shift:
                if (EnsureSelection()) DoQuickSave();
                return;
            case Key.S when ctrl:
                if (EnsureSelection()) DoSave();
                return;
            case Key.T when ctrl:
                if (EnsureSelection()) DoPin();
                return;
            case Key.A when ctrl:
                SelectMonitorUnderCursor();
                return;
            case Key.Oem3 when plain: // ` / ~
                ToggleCursor();
                return;
            case Key.Left or Key.Right or Key.Up or Key.Down when !alt:
                OnArrow(key, ctrl, shift);
                return;
            case Key.R when !ctrl:
                BrowseRegionHistory(older: !shift);
                return;
            case Key.OemComma when !ctrl:
                BrowseImageHistory(older: true);
                return;
            case Key.OemPeriod when !ctrl:
                BrowseImageHistory(older: false);
                return;
            case Key.Q when ctrl && !shift && !alt: // PRO-01 translate
                if (EnsureSelection()) DoTranslate();
                return;
            case Key.Q when shift && !ctrl && !alt: // PRO-02 table recognition
                if (EnsureSelection()) DoTable();
                return;
            case Key.F when shift && !ctrl && !alt: // PRO-05 LaTeX recognition
                if (EnsureSelection()) DoLatex();
                return;
            case Key.Q when plain:
                ToggleQr();
                return;
            default:
                e.Handled = false;
                return;
        }
    }

    private void OnOverlayKeyUp(KeyEventArgs e)
    {
        var key = RealKey(e);
        if (key is Key.LeftAlt or Key.RightAlt)
        {
            e.Handled = true;
            _altDown = false;
            MarkDirty();
            return;
        }
        if (key is Key.LeftShift or Key.RightShift)
        {
            if (_drag != DragKind.None) { UpdateDrag(); MarkDirty(); }
            else if (_shiftClean && !(e.OriginalSource is TextBoxBase))
            {
                // A bare Shift tap toggles the magnifier color format (CAP-05).
                _colorFormat = _colorFormat == ColorFormat.Rgb ? _cfg.MagnifierAltFormat : ColorFormat.Rgb;
                if (_colorFormat == ColorFormat.Rgb && _cfg.MagnifierAltFormat == ColorFormat.Rgb) _colorFormat = ColorFormat.Hex;
                MarkDirty();
            }
            _shiftClean = false;
        }
    }

    private void OnArrow(Key key, bool ctrl, bool shift)
    {
        int dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
        int dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
        if (!HasSel || _drag != DragKind.None)
        {
            // No selection yet: arrows move the mouse 1 px for precise positioning with the magnifier.
            CaptureNative.MoveCursorBy(dx, dy);
            return;
        }
        var edge = key switch { Key.Left => Edge.Left, Key.Right => Edge.Right, Key.Up => Edge.Top, _ => Edge.Bottom };
        if (shift && !ctrl) _sel = SelectionMath.AdjustEdge(_sel, edge, outward: false, _bounds);
        else if (ctrl && !shift) _sel = SelectionMath.AdjustEdge(_sel, edge, outward: true, _bounds);
        else if (!ctrl && !shift) _sel = SelectionMath.Move(_sel, dx, dy, _bounds);
        else return;
        OnSelectionChanged();
    }

    private bool EnsureSelection()
    {
        if (!HasSel && _hover is { } h) _sel = h;
        return HasSel;
    }

    private void SelectMonitorUnderCursor()
    {
        var m = _monitorsImg[MonitorIndexAt(_mousePx)];
        _sel = SelectionMath.Clamp(m, _bounds);
        ConfirmSelection();
    }

    private void ToggleCursor()
    {
        if (_snap.Cursor == null) return;
        _showCursor = !_showCursor;
        RefreshDisplayImage();
        ShowToast(Loc.Get(_showCursor ? "Capture.CursorShown" : "Capture.CursorHidden"));
    }

    private void RequestExit()
    {
        if (_cfg.ConfirmEscClose && (HasSel || _surface.HasAnnotations))
        {
            var r = MessageBox.Show(this, Loc.Get("Capture.ConfirmExit"), "OpenPin", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
        }
        CloseOverlay();
    }

    /// <summary>C: copy the color under the cursor in the current format, then close.</summary>
    private void CopyColor()
    {
        if (!_mouseKnown) return;
        var c = BitmapUtil.GetPixel(DisplayImage, Floor(_mousePx.X), Floor(_mousePx.Y));
        var text = _colorFormat is ColorFormat.Hsl or ColorFormat.Hsv ? ColorText.FormatCss(c, _colorFormat) : ColorText.Format(c, _colorFormat);
        CloseOverlay();
        try
        {
            ClipboardService.SetText(text);
            Notify.Info(Loc.Format("Capture.ColorCopied", text));
        }
        catch (Exception ex)
        {
            Log.Error("Copy color failed", ex);
            Notify.Error(Loc.Format("Capture.Failed", ex.Message));
        }
    }
}
