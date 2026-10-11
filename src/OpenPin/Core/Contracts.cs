using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenPin.Core;

// ============================================================================================
//  Cross-module contracts. Changing a signature here affects several modules — coordinate first.
//  Coordinates: "Px" suffix = PHYSICAL screen pixels (virtual-screen coordinates).
// ============================================================================================

/// <summary>A full-screen modal mode that wants first refusal on global actions/hotkeys.</summary>
public interface IModalSession
{
    /// <summary>Return true if the session handled the action (e.g. "capture.pin" while selecting → pin selection).</summary>
    bool TryHandleAction(string actionId);
}

public enum CaptureMode
{
    /// <summary>Normal: select a region, then annotate / pin / copy / save from the toolbar.</summary>
    Edit,
    /// <summary>After selecting, copy to clipboard and close immediately.</summary>
    Copy,
    /// <summary>After selecting, pin immediately.</summary>
    Pin,
    /// <summary>After selecting, run OCR and copy the text.</summary>
    Ocr,
    /// <summary>After selecting, open the save dialog.</summary>
    Save,
    /// <summary>After selecting, quick-save to the configured folder.</summary>
    QuickSave,
    /// <summary>After selecting, start a scrolling (long) screenshot of that region.</summary>
    LongCapture,
    /// <summary>After selecting, start screen recording of that region.</summary>
    Record,
}

/// <summary>Capture module (Capture/). Owns the full-screen selection overlay.</summary>
public interface ICaptureController
{
    bool IsActive { get; }

    /// <summary>Freeze the screen and open the selection overlay.</summary>
    void Start(CaptureMode mode = CaptureMode.Edit);

    /// <summary>Custom region / delayed capture dialog (Alt+1).</summary>
    void StartCustom();

    /// <summary>Capture the monitor under the cursor and copy it (no UI).</summary>
    void CaptureFullScreenToClipboard();

    /// <summary>Capture the last used region again and copy it (no UI).</summary>
    void CaptureLastRegionToClipboard();
}

/// <summary>Where a pin should appear.</summary>
public enum PinPlacement { Default, AtRect, AtCursor, ScreenCenter }

/// <summary>Pin module (Pin/). Owns pin windows, pin groups, pin history and their persistence.</summary>
public interface IPinManager
{
    /// <summary>Load persisted pins/groups and re-open pins left open at last exit. Called once at startup.</summary>
    void Initialize();

    /// <summary>Persist everything. Called on exit.</summary>
    void Shutdown();

    /// <summary>
    /// Pin an image. If <paramref name="screenRectPx"/> is given the pin is placed so that its image covers exactly that
    /// physical-pixel rectangle (used when pinning a capture selection: the pin must appear exactly where it was).
    /// </summary>
    void PinImage(BitmapSource image, Int32Rect? screenRectPx = null, string? title = null);

    /// <summary>Pin any supported content (image, text/html, files, color).</summary>
    void PinContent(PinContent content, Int32Rect? screenRectPx = null);

    /// <summary>Ctrl+2: pin whatever is on the clipboard.</summary>
    void PinFromClipboard();

    /// <summary>Open a file picker and pin the chosen image(s).</summary>
    void PinFromFile();

    /// <summary>Ctrl+3: re-open the most recently closed pin of the current group.</summary>
    void RestoreLastClosed();

    void CloseAll();
    void DestroyAll();
    void ToggleHideAll();

    /// <summary>Toggle mouse click-through of the pin under the cursor.</summary>
    void ToggleClickThroughUnderCursor();

    void SwitchToNextGroup();

    /// <summary>Number of pins currently on screen in the current group.</summary>
    int VisibleCount { get; }

    /// <summary>Called each time the tray menu opens; add the pin section (counts, groups submenu, batch ops).</summary>
    void PopulateTrayMenu(ItemCollection items);
}

/// <summary>Long (scrolling) screenshot module (LongCapture/).</summary>
public interface ILongCaptureController
{
    bool IsActive { get; }
    void Start(Int32Rect regionPx);
}

/// <summary>Screen recording module (Recording/).</summary>
public interface IScreenRecorder
{
    bool IsActive { get; }
    void Start(Int32Rect regionPx);
}

/// <summary>Settings window module (SettingsUI/).</summary>
public interface ISettingsUi
{
    /// <summary>Open (or focus) the settings window, optionally on a page id ("general", "capture", "pin", "hotkeys"…).</summary>
    void Show(string? page = null);
}

// -------------------------------------------------------------------------------- pin content

/// <summary>Something that can be pinned. Produced by ClipboardService.GetPinContent() or by other modules.</summary>
public abstract record PinContent;

/// <summary>An image. <paramref name="SourcePath"/> is the file it came from, if any.</summary>
public sealed record ImagePinContent(BitmapSource Image, string? SourcePath = null) : PinContent;

/// <summary>Text, optionally with the clipboard's HTML fragment.</summary>
public sealed record TextPinContent(string PlainText, string? Html = null) : PinContent;

/// <summary>One or more files/folders.</summary>
public sealed record FilePinContent(IReadOnlyList<string> Paths) : PinContent;

/// <summary>A color parsed from text like "#29B8DB", "rgb(41,184,219)", "41,184,219" or "orange".</summary>
public sealed record ColorPinContent(Color Color, string OriginalText) : PinContent;
