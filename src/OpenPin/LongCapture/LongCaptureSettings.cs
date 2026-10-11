namespace OpenPin.LongCapture;

public enum LongCaptureDirection { Vertical, Horizontal }

/// <summary>settings.json → "LongCapture". Owned by the LongCapture module.</summary>
public sealed class LongCaptureSettings
{
    public LongCaptureDirection DefaultDirection { get; set; } = LongCaptureDirection.Vertical;

    /// <summary>Safety limit of the stitched length in pixels.</summary>
    public int MaxLength { get; set; } = 30000;

    /// <summary>
    /// Auto-crop: scrolling back against the stitch direction trims the result so it ends at the visible content
    /// (initial state of the "自动裁剪" toggle).
    /// </summary>
    public bool AutoCrop { get; set; } = true;

    /// <summary>Frame grabs per second while capturing (5–60).</summary>
    public int FramesPerSecond { get; set; } = 20;

    /// <summary>Show the live preview panel next to the region.</summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>Auto-scroll: delay between two synthetic wheel steps (ms, 50–2000).</summary>
    public int AutoScrollIntervalMs { get; set; } = 180;

    /// <summary>Auto-scroll: wheel delta per step (120 = one notch).</summary>
    public int AutoScrollWheelDelta { get; set; } = 120;

    /// <summary>Auto-scroll stops (and capturing ends) when the content has not moved for this long (ms).</summary>
    public int AutoScrollStopAfterIdleMs { get; set; } = 1500;
}
