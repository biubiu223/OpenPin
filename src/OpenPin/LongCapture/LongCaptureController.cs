using System.Windows;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.LongCapture;

/// <summary>LONG CAPTURE MODULE entry point. PHASE-0 STUB.</summary>
public sealed class LongCaptureController : ILongCaptureController
{
    public bool IsActive => false;

    public void Start(Int32Rect regionPx) => Notify.Info(Loc.Get("Common.NotImplemented"));
}

public enum LongCaptureDirection { Vertical, Horizontal }

/// <summary>settings.json → "LongCapture". Owned by the LongCapture module.</summary>
public sealed class LongCaptureSettings
{
    public LongCaptureDirection DefaultDirection { get; set; } = LongCaptureDirection.Vertical;
    /// <summary>Safety limit of the stitched length in pixels.</summary>
    public int MaxLength { get; set; } = 30000;
}
