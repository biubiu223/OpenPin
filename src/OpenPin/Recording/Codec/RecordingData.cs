namespace OpenPin.Recording.Codec;

/// <summary>Everything one recording produced: screen frames, input events, audio tracks, webcam frames (all temp files).</summary>
internal sealed class RecordingData
{
    public RecordingData(FrameStore video, InputLog input, AudioTrack? microphone = null, AudioTrack? systemAudio = null, FrameStore? webcam = null)
    {
        Video = video; Input = input; Microphone = microphone; SystemAudio = systemAudio; Webcam = webcam;
    }

    public FrameStore Video { get; }
    public InputLog Input { get; }
    public AudioTrack? Microphone { get; }
    public AudioTrack? SystemAudio { get; }
    public FrameStore? Webcam { get; }

    public double DurationMs => Video.DurationMs;
    public bool HasAudio => Microphone != null || SystemAudio != null;

    /// <summary>Delete all temp files (best effort).</summary>
    public void Delete()
    {
        Video.Delete();
        Microphone?.Delete();
        SystemAudio?.Delete();
        Webcam?.Delete();
    }
}

/// <summary>Which overlays to draw (playback toggles / export options).</summary>
internal sealed record OverlayOptions
{
    public bool ShowCursor { get; init; } = true;
    public bool ShowClicks { get; init; } = true;
    public bool ShowWheel { get; init; } = true;
    /// <summary>Length of the click ripple animation (ms); smaller = faster.</summary>
    public double ClickAnimationMs { get; init; } = 500;
    public bool ShowKeys { get; init; }
    public KeystrokeFilter KeyFilter { get; init; } = KeystrokeFilter.All;
    public bool ShowWebcam { get; init; } = true;
    public WebcamShape WebcamShape { get; init; } = WebcamShape.Circle;
    public WebcamCorner WebcamCorner { get; init; } = WebcamCorner.BottomRight;
    /// <summary>Webcam overlay height as a fraction of the video height.</summary>
    public double WebcamSize { get; init; } = 0.25;
}
