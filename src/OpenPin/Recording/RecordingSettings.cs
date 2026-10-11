using OpenPin.Recording.Codec;

namespace OpenPin.Recording;

public enum WebcamShape { Circle, RoundedRect }

public enum WebcamCorner { BottomRight, BottomLeft, TopRight, TopLeft }

/// <summary>Export quality preset of the playback window (REC-03). 轻量 / 标准 / 原始.</summary>
public enum RecordingQuality { Light, Standard, Original }

/// <summary>GIF dithering used when a frame has more than 255 colors.</summary>
public enum GifDitherMode { None, Ordered, FloydSteinberg }

/// <summary>settings.json → "Recording". Owned by the Recording module.</summary>
public sealed class RecordingSettings
{
    /// <summary>5 / 16 / 24 / 30 / 60.</summary>
    public int Fps { get; set; } = 16;
    /// <summary>Countdown before recording starts (0 = immediately).</summary>
    public int StartDelaySeconds { get; set; }
    /// <summary>Draw the mouse cursor into the exported video (can also be toggled in the playback window).</summary>
    public bool ShowCursor { get; set; } = true;
    /// <summary>Draw an expanding ring where the mouse was clicked.</summary>
    public bool HighlightClicks { get; set; } = true;
    /// <summary>REC-04 (P2, not implemented yet): show pressed keys.</summary>
    public bool ShowKeys { get; set; }
    /// <summary>gif | mp4 (webp: REC-04, not implemented). The playback window remembers the last choice here.</summary>
    public string DefaultFormat { get; set; } = "gif";

    // ---- added by the Recording module (all optional, sensible defaults)

    /// <summary>Export quality preset; remembered from the playback window.</summary>
    public RecordingQuality Quality { get; set; } = RecordingQuality.Standard;
    /// <summary>GIF dithering for frames with more than 255 colors (photos, gradients). Screen UI rarely needs it.</summary>
    public GifDitherMode GifDither { get; set; } = GifDitherMode.FloydSteinberg;
    /// <summary>Folder for "quick save" of recordings. Empty = Pictures\OpenPin. Environment variables are expanded.</summary>
    public string QuickSaveFolder { get; set; } = "";
    /// <summary>File-name template (no extension) for saved recordings, see Services.PathTemplate.</summary>
    public string FileNameTemplate { get; set; } = "OpenPin_Rec_$yyyy-MM-dd_HH-mm-ss$";
    /// <summary>Folder last used in the recording save dialog.</summary>
    public string LastSaveFolder { get; set; } = "";
    /// <summary>Stop automatically after this many minutes (0 = unlimited). Protects the disk from forgotten sessions.</summary>
    public int MaxDurationMinutes { get; set; } = 60;
    /// <summary>Hide the control bar from the recording even if it overlaps the region (WDA_EXCLUDEFROMCAPTURE).</summary>
    public bool ExcludeControlsFromCapture { get; set; } = true;

    // ---- PRO-03: input visualization
    /// <summary>Duration of the click ripple animation in ms (smaller = faster). 250–1500.</summary>
    public int ClickAnimationMs { get; set; } = 500;
    /// <summary>Show a wheel indicator next to the pointer when scrolling.</summary>
    public bool ShowWheel { get; set; } = true;
    /// <summary>Keystroke overlay filter when <see cref="ShowKeys"/> is on.</summary>
    public KeystrokeFilter KeyFilter { get; set; } = KeystrokeFilter.All;

    // ---- PRO-03: audio (MP4 only)
    public bool RecordMicrophone { get; set; }
    /// <summary>Microphone endpoint id (MMDevice id); empty = default microphone.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    /// <summary>0..2 (1 = unchanged).</summary>
    public double MicrophoneVolume { get; set; } = 1.0;
    public bool RecordSystemAudio { get; set; }
    /// <summary>0..2 (1 = unchanged).</summary>
    public double SystemAudioVolume { get; set; } = 1.0;

    // ---- PRO-03: webcam picture-in-picture
    public bool WebcamEnabled { get; set; }
    /// <summary>Camera device id; empty = first camera.</summary>
    public string WebcamDeviceId { get; set; } = "";
    public WebcamShape WebcamShape { get; set; } = WebcamShape.Circle;
    public WebcamCorner WebcamCorner { get; set; } = WebcamCorner.BottomRight;
    /// <summary>Overlay height as a fraction of the video height (0.1–0.5).</summary>
    public double WebcamSize { get; set; } = 0.25;

    /// <summary>Allowed frame rates (control bar menu).</summary>
    public static IReadOnlyList<int> FpsChoices { get; } = new[] { 5, 16, 24, 30, 60 };

    /// <summary>Allowed countdown lengths in seconds (control bar menu).</summary>
    public static IReadOnlyList<int> DelayChoices { get; } = new[] { 0, 3, 5, 10 };

    /// <summary>Clamp a configured fps to a supported value (1..60).</summary>
    public static int NormalizeFps(int fps) => Math.Clamp(fps <= 0 ? 16 : fps, 1, 60);
}
