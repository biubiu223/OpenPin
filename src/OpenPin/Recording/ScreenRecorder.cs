using System.Windows;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Recording;

/// <summary>SCREEN RECORDING MODULE entry point. PHASE-0 STUB.</summary>
public sealed class ScreenRecorder : IScreenRecorder
{
    public bool IsActive => false;

    public void Start(Int32Rect regionPx) => Notify.Info(Loc.Get("Common.NotImplemented"));
}

/// <summary>settings.json → "Recording". Owned by the Recording module.</summary>
public sealed class RecordingSettings
{
    /// <summary>5 / 16 / 24 / 30 / 60.</summary>
    public int Fps { get; set; } = 16;
    public int StartDelaySeconds { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool HighlightClicks { get; set; } = true;
    public bool ShowKeys { get; set; }
    /// <summary>gif | mp4 | webp</summary>
    public string DefaultFormat { get; set; } = "gif";
}
