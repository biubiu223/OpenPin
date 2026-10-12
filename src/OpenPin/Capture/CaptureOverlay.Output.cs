using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenPin.Core;
using OpenPin.Core.Imaging;
using OpenPin.Services;

namespace OpenPin.Capture;

internal sealed partial class CaptureOverlay
{
    private Int32Rect SelectionScreenPx => _snap.ImageToScreen(_sel);

    /// <summary>Enter / double-click: Edit mode copies, other modes run their own action.</summary>
    private void ConfirmAction()
    {
        if (_mode == Core.CaptureMode.Edit) DoCopy();
        else ExecuteModeAction();
    }

    /// <summary>CAP-13: the action of a non-Edit capture mode, run as soon as the selection is confirmed.</summary>
    private void ExecuteModeAction()
    {
        if (!HasSel) return;
        switch (_mode)
        {
            case Core.CaptureMode.Copy: DoCopy(); break;
            case Core.CaptureMode.Pin: DoPin(); break;
            case Core.CaptureMode.Ocr: DoOcr(); break;
            case Core.CaptureMode.Save: DoSave(); break;
            case Core.CaptureMode.QuickSave: DoQuickSave(); break;
            case Core.CaptureMode.LongCapture: DoLongCapture(); break;
            case Core.CaptureMode.Record: DoRecord(); break;
            default: DoCopy(); break;
        }
    }

    /// <summary>Selection pixels + annotations, with the CAP-11 output style applied.</summary>
    private BitmapSource RenderSelection(bool forPin)
    {
        _surface.CommitPendingEdits();
        var bmp = _surface.Render(_sel);
        var fx = CurrentEffects(forPin);
        if (fx.IsNone) return bmp;
        var (pixels, w, h, _) = ImageEffects.Apply(BitmapUtil.GetPixels(bmp), bmp.PixelWidth, bmp.PixelHeight, fx);
        return BitmapUtil.FromPixels(pixels, w, h);
    }

    private OutputEffects CurrentEffects(bool forPin)
    {
        var borderColor = ColorText.TryParse(_cfg.OutputBorderColor, out var bc) ? bc
            : ColorText.TryParse(AppServices.Settings?.Current.General.AccentColor, out var ac) ? ac
            : Color.FromRgb(0x16, 0x77, 0xFF);
        return new OutputEffects(
            Math.Max(0, _cfg.OutputCornerRadius),
            _cfg.OutputBorder ? Math.Max(1, _cfg.OutputBorderWidth) : 0,
            borderColor,
            _cfg.OutputShadow && !forPin ? Math.Clamp(_cfg.OutputShadowSize, 2, 64) : 0);
    }

    /// <summary>Title / process of the window under the selection center, for file-name templates.</summary>
    private SaveContext MakeContext()
    {
        var center = new Point(_sel.X + _sel.Width / 2.0, _sel.Y + _sel.Height / 2.0);
        var w = _targets.HitTest(center)?.Window;
        return new SaveContext
        {
            Timestamp = _snap.Timestamp,
            WindowTitle = w?.Title,
            ProcessName = w?.ProcessName,
            FromCapture = true,
        };
    }

    private void Remember(BitmapSource image, Int32Rect rectPx)
    {
        try
        {
            var store = CaptureController.History;
            store.AddRegion(rectPx, _cfg.HistoryRegionCount);
            _ = store.AddImageAsync(image, rectPx, _cfg.HistoryImageCount);
        }
        catch (Exception ex) { Log.Error("Failed to record capture history", ex); }
    }

    /// <summary>Run an output after the overlay closed; errors become notifications, never a stuck overlay.</summary>
    private static void RunOutput(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error($"Capture output '{what}' failed", ex);
            Notify.Error(Loc.Format("Capture.Failed", ex.Message));
        }
    }

    private void DoCopy()
    {
        if (!HasSel) return;
        var bmp = RenderSelection(forPin: false);
        var rect = SelectionScreenPx;
        var ctx = MakeContext();
        CloseOverlay();
        RunOutput("copy", () =>
        {
            ClipboardService.SetImage(bmp);
            SaveService.AutoSave(bmp, ctx);
            Remember(bmp, rect);
        });
    }

    private void DoPin()
    {
        if (!HasSel) return;
        var bmp = RenderSelection(forPin: true);
        var rect = SelectionScreenPx;
        var ctx = MakeContext();
        bool alsoCopy = _cfg.PinAlsoCopies;
        CloseOverlay();
        RunOutput("pin", () =>
        {
            // Same size as the selection (no shadow for pins), so the pin covers exactly where the selection was.
            AppServices.Pins.PinImage(bmp, rect);
            if (alsoCopy)
            {
                ClipboardService.SetImage(bmp);
                SaveService.AutoSave(bmp, ctx);
            }
            Remember(bmp, rect);
        });
    }

    private void DoSave()
    {
        if (!HasSel) return;
        var bmp = RenderSelection(forPin: false);
        var rect = SelectionScreenPx;
        var ctx = MakeContext();
        // The dialog is owned by the (topmost) overlay so it is never hidden behind it; on cancel the user is back
        // in the overlay with the selection and annotations intact.
        HideFlyouts();
        Mouse.OverrideCursor = null;
        string? path = null;
        try { path = SaveService.SaveWithDialog(bmp, ctx, this); }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            Notify.Error(Loc.Format("Capture.Failed", ex.Message));
        }
        if (_closing) return;
        if (path != null)
        {
            CloseOverlay();
            RunOutput("save", () => Remember(bmp, rect));
            return;
        }
        Activate();
        _root.Focus();
        MarkDirty();
    }

    private void DoQuickSave()
    {
        if (!HasSel) return;
        var bmp = RenderSelection(forPin: false);
        var rect = SelectionScreenPx;
        var ctx = MakeContext();
        CloseOverlay();
        RunOutput("quicksave", () =>
        {
            SaveService.QuickSave(bmp, ctx);
            Remember(bmp, rect);
        });
    }

    private void DoOcr()
    {
        if (!HasSel) return;
        _surface.CommitPendingEdits();
        var bmp = _surface.Render(_sel); // OCR on the plain pixels (no rounded corners / shadow)
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        _ = CaptureController.OcrAndCopyAsync(bmp);
    }

    /// <summary>PRO-01 (Ctrl+Q): translate the selection — result window, or a translated pin over the selection.</summary>
    private void DoTranslate()
    {
        if (!HasSel) return;
        _surface.CommitPendingEdits();
        var bmp = _surface.Render(_sel);
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        _ = Translate.TranslateWorkflow.TranslateImageAsync(bmp, img => AppServices.Pins.PinImage(img, rect), Recognition.OcrLanguageChoice.CurrentOrNull);
    }

    /// <summary>DeskMirror (Shift+M): open real-time translucent translation mirror over the selection.</summary>
    private void DoMirror()
    {
        if (!HasSel) return;
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        Translate.TranslateWorkflow.OpenMirror(rect, Recognition.OcrLanguageChoice.CurrentOrNull);
    }

    /// <summary>PRO-02 (Shift+Q): recognize a table in the selection.</summary>
    private void DoTable()
    {
        if (!HasSel) return;
        _surface.CommitPendingEdits();
        var bmp = _surface.Render(_sel);
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        _ = Recognition.TableRecognition.RecognizeAndShowAsync(bmp);
    }

    /// <summary>PRO-05 (Shift+F): recognize a LaTeX mathematical formula in the selection.</summary>
    private void DoLatex()
    {
        if (!HasSel) return;
        _surface.CommitPendingEdits();
        var bmp = _surface.Render(_sel);
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        _ = Recognition.LatexRecognition.RecognizeAndShowAsync(bmp, rect);
    }

    private void DoLongCapture()
    {
        if (!HasSel) return;
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        // Let DWM drop the overlay from the screen before the module starts grabbing frames.
        CaptureController.AfterDelay(150, () => AppServices.LongCapture.Start(rect), "long capture");
    }

    private void DoRecord()
    {
        if (!HasSel) return;
        var rect = SelectionScreenPx;
        CloseOverlay();
        CaptureController.RememberRegion(rect);
        CaptureController.AfterDelay(150, () => AppServices.Recorder.Start(rect), "record");
    }

    // ================================================================================== history (CAP-10)

    private IReadOnlyList<Int32Rect>? _regionHistory;
    private IReadOnlyList<HistoryImage>? _imageHistory;
    private readonly HistoryCursor _regionCursor = new();
    private readonly HistoryCursor _imageCursor = new();

    private void BrowseRegionHistory(bool older)
    {
        _regionHistory ??= CaptureController.History.GetRegions(_cfg.HistoryRegionCount);
        if (_regionHistory.Count == 0) { ShowToast(Loc.Get("Capture.HistoryEmpty")); return; }
        if (!(older ? _regionCursor.Older(_regionHistory.Count) : _regionCursor.Newer())) return;
        if (_regionCursor.Index < 0) { ResetSelection(); return; }
        SetSelection(_snap.ScreenToImage(_regionHistory[_regionCursor.Index]));
        ShowToast(Loc.Format("Capture.HistoryRegion", _regionCursor.Index + 1, _regionHistory.Count));
    }

    private void BrowseImageHistory(bool older)
    {
        _imageHistory ??= CaptureController.History.GetImages();
        if (_imageHistory.Count == 0) { ShowToast(Loc.Get("Capture.HistoryEmpty")); return; }
        if (!(older ? _imageCursor.Older(_imageHistory.Count) : _imageCursor.Newer())) return;

        if (_imageCursor.Index < 0)
        {
            _historyPatch = null;
            RefreshDisplayImage();
            ResetSelection();
            return;
        }
        var entry = _imageHistory[_imageCursor.Index];
        var img = CaptureController.History.LoadImage(entry);
        if (img == null) return;
        // Centre the stored image on its original selection (it may be larger: shadow), keep it on screen.
        var r = _snap.ScreenToImage(entry.RectPx);
        int x = r.X - (img.PixelWidth - r.Width) / 2, y = r.Y - (img.PixelHeight - r.Height) / 2;
        var placed = SelectionMath.Move(new Int32Rect(x, y, img.PixelWidth, img.PixelHeight), 0, 0, _bounds);
        _historyPatch = (img, placed.X, placed.Y);
        RefreshDisplayImage();
        _surface.Tool = Annotation.AnnotationTool.None;
        _surface.ClearAll();
        SetSelection(placed);
        ShowToast(Loc.Format("Capture.HistoryImage", _imageCursor.Index + 1, _imageHistory.Count, entry.Time));
    }

    // ================================================================================== QR (CAP-12)

    private IReadOnlyList<QrCodeResult>? _qrResults;
    private Int32Rect? _qrRegion;
    private int _qrSeq;

    private void ToggleQr()
    {
        if (_cfg.QrDetect == QrDetectMode.Disabled) { ShowToast(Loc.Get("Capture.Qr.Disabled")); return; }
        if (_qrPanel.IsExpanded) { _qrPanel.Hide(); return; }
        var region = HasSel ? _sel : _hover ?? _monitorsImg[MonitorIndexAt(_mousePx)];
        if (_qrResults != null && _qrRegion == region) { ShowQrResults(expanded: true); return; }
        RunQrDetection(manual: true);
    }

    private async void RunQrDetection(bool manual)
    {
        try
        {
            var region = HasSel ? _sel : _hover ?? _monitorsImg[MonitorIndexAt(_mousePx)];
            region = SelectionMath.Clamp(region, _bounds);
            if (SelectionMath.IsEmpty(region)) return;
            int seq = ++_qrSeq;
            var crop = BitmapUtil.Crop(DisplayImage, region);
            if (manual) ShowToast(Loc.Get("Capture.Qr.Detecting"));
            var results = await QrService.DecodeAsync(crop);
            if (_closing || seq != _qrSeq) return;
            _qrRegion = region;
            _qrResults = results
                .Select(r => r with { Bounds = new Rect(r.Bounds.X + region.X, r.Bounds.Y + region.Y, r.Bounds.Width, r.Bounds.Height) })
                .ToList();
            if (_qrResults.Count == 0)
            {
                if (manual) ShowToast(Loc.Get("Capture.Qr.None"));
                return;
            }
            ShowQrResults(expanded: manual || _cfg.QrDetect == QrDetectMode.ShowContent);
        }
        catch (Exception ex)
        {
            Log.Error("QR detection failed", ex);
            if (manual && !_closing) ShowToast(Loc.Format("Capture.Failed", ex.Message));
        }
    }

    private void ShowQrResults(bool expanded)
    {
        if (_qrResults is not { Count: > 0 } results) return;
        if (_stylePanel != null) _stylePanel.Visibility = Visibility.Collapsed;
        if (_recognizePanel != null) _recognizePanel.Visibility = Visibility.Collapsed;
        if (expanded) _qrPanel.ShowFull(results);
        else _qrPanel.ShowCompact(results.Count);

        _qrMarks.Children.Clear();
        var accent = _border.Stroke;
        foreach (var r in results)
        {
            if (r.Bounds.IsEmpty) continue;
            var mark = new Rectangle
            {
                Width = Math.Max(4, r.Bounds.Width), Height = Math.Max(4, r.Bounds.Height),
                Stroke = accent, StrokeThickness = Math.Max(1, Math.Round(2 * _dpi)),
                StrokeDashArray = new DoubleCollection { 3, 2 },
            };
            Canvas.SetLeft(mark, r.Bounds.X);
            Canvas.SetTop(mark, r.Bounds.Y);
            _qrMarks.Children.Add(mark);
        }
        MarkDirty();
    }

    private void UpdateQrPlacement()
    {
        if (_qrPanel.Visibility != Visibility.Visible || _qrRegion is not { } region) return;
        int mon = MonitorIndexFor(region);
        double s = MonitorScale(mon);
        var size = PxSize(_qrPanel, mon);
        // Next to the first code if known, else at the selection's top-right.
        var anchor = _qrResults is { Count: > 0 } list && !list[0].Bounds.IsEmpty ? list[0].Bounds : new Rect(region.X, region.Y, region.Width, region.Height);
        var m = _monitorRects[mon];
        double x = anchor.Right + 8 * s;
        if (x + size.Width > m.Right) x = anchor.Left - 8 * s - size.Width;
        x = Math.Clamp(x, m.Left, Math.Max(m.Left, m.Right - size.Width));
        double y = Math.Clamp(anchor.Top, m.Top, Math.Max(m.Top, m.Bottom - size.Height));
        PlaceUi(_qrPanel, new Point(x, y), mon);
    }

    private void ClearQr()
    {
        _qrSeq++;
        _qrResults = null;
        _qrRegion = null;
        _qrPanel.Hide();
        _qrMarks.Children.Clear();
    }

    private void CopyQrText(QrCodeResult r)
    {
        CloseOverlay();
        RunOutput("qr copy", () =>
        {
            ClipboardService.SetText(r.Text);
            Notify.Info(Loc.Get("Capture.Qr.Copied"));
        });
    }

    private void OpenQrLink(QrCodeResult r)
    {
        if (!r.IsUrl) return;
        CloseOverlay();
        RunOutput("qr open", () => Process.Start(new ProcessStartInfo(r.Text) { UseShellExecute = true }));
    }
}
