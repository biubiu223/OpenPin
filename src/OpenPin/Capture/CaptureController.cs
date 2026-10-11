using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Capture;

/// <summary>
/// CAPTURE MODULE entry point (implements <see cref="ICaptureController"/>).
/// PHASE-0 STUB — the Capture module implements the overlay (see docs/SPEC.md §1).
/// </summary>
public sealed class CaptureController : ICaptureController
{
    public bool IsActive => false;

    public void Start(CaptureMode mode = CaptureMode.Edit) => Notify.Info(Loc.Get("Common.NotImplemented"));

    public void StartCustom() => Notify.Info(Loc.Get("Common.NotImplemented"));

    public void CaptureFullScreenToClipboard() => Notify.Info(Loc.Get("Common.NotImplemented"));

    public void CaptureLastRegionToClipboard() => Notify.Info(Loc.Get("Common.NotImplemented"));
}
