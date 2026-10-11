namespace OpenPin.LongCapture.Stitching;

/// <summary>Outcome of pushing one frame into the <see cref="ScrollStitcher"/>.</summary>
internal enum StitchStatus
{
    /// <summary>The first frame: it becomes the reference, nothing to match yet.</summary>
    First,
    /// <summary>Same pixels (profile) as the previously pushed frame: skipped cheaply.</summary>
    Unchanged,
    /// <summary>The content did not scroll (offset 0, or only small local changes such as a blinking caret).</summary>
    NoMovement,
    /// <summary>Matched and new lines were appended.</summary>
    Appended,
    /// <summary>Matched, the content moved but nothing new was revealed (scrolling backwards / inside known content).</summary>
    Moved,
    /// <summary>Matched, the content moved against the stitch direction and the result was trimmed (auto-crop).</summary>
    Trimmed,
    /// <summary>Matched, but the result reached <see cref="StitchOptions.MaxLength"/>; further content is ignored.</summary>
    LimitReached,
    /// <summary>No reliable match with the reference frame: the state is unchanged (last good state is kept).</summary>
    Failed,
}

/// <summary>Result of <see cref="ScrollStitcher.Push"/>. Lines = rows (vertical) or columns (horizontal).</summary>
internal readonly record struct StitchResult(
    StitchStatus Status,
    // Scroll offset of the new frame relative to the reference, in lines (positive = content moved forward).
    int Offset,
    // Lines appended by this frame.
    int Appended,
    // Total result length (lines along the scroll axis) after this frame.
    int Length,
    // Fixed header / footer lines detected for this frame pair.
    int Header,
    int Footer)
{
    /// <summary>True when the frame was matched (or needed no match). False only for <see cref="StitchStatus.Failed"/>.</summary>
    public bool IsGood => Status != StitchStatus.Failed;

    /// <summary>True if the content did not move (used by auto-scroll to detect the end of the page).</summary>
    public bool IsIdle => Status is StitchStatus.Unchanged or StitchStatus.NoMovement;
}

/// <summary>Tuning knobs of the matcher. Defaults work for typical desktop content at 100–200 % scaling.</summary>
internal sealed class StitchOptions
{
    /// <summary>Maximum result length along the scroll axis (lines); appending stops there.</summary>
    public int MaxLength { get; set; } = 30000;

    /// <summary>
    /// Auto-crop: when the content moves against the stitch direction (set by the first real movement), the result is
    /// trimmed from its far end so it ends at the currently visible content. Off = scrolling back changes nothing.
    /// May be changed between frames.
    /// </summary>
    public bool AutoCrop { get; set; } = true;

    /// <summary>Largest scroll step between two frames, as a fraction of the moving band (0.92 = 92 %).</summary>
    public double MaxOffsetFraction { get; set; } = 0.92;

    /// <summary>Absolute minimum overlap (lines) between two frames.</summary>
    public int MinOverlapLines { get; set; } = 8;

    /// <summary>Number of bins across each line in the line profile (more = more discriminative, slower).</summary>
    public int MaxBins { get; set; } = 64;

    /// <summary>At most this many pixels are sampled per line for the profile.</summary>
    public int MaxSamplesPerLine { get; set; } = 1280;

    /// <summary>Gray-level tolerance for "same pixel area at the same position" (fixed header / footer / column detection).</summary>
    public float BinTolerance { get; set; } = 3f;

    /// <summary>Truncation of a single feature difference (robust cost).</summary>
    public float FeatureCap { get; set; } = 48f;

    /// <summary>A line pair whose summed feature cost exceeds this does not match.</summary>
    public float RowTolerance { get; set; } = 24f;

    /// <summary>Maximum fraction of informative overlap lines that may mismatch (animations, caret, hover…).</summary>
    public double MaxBadRowFraction { get; set; } = 0.12;

    /// <summary>Per-channel difference above which two pixels are considered different (pixel verification).</summary>
    public int PixelTolerance { get; set; } = 40;

    /// <summary>A sampled line fails pixel verification if more than this fraction of its pixels differ.</summary>
    public double MaxBadPixelFraction { get; set; } = 0.06;

    /// <summary>Maximum fraction of informative sampled lines that may fail pixel verification.</summary>
    public double MaxBadPixelRowFraction { get; set; } = 0.12;
}
