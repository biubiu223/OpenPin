using System.Windows;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Recording;

/// <summary>SCREEN RECORDING MODULE entry point.</summary>
public sealed class ScreenRecorder : IScreenRecorder
{
    public bool IsActive => false;

    public void Start(Int32Rect regionPx) => Notify.Info(Loc.Get("Common.NotImplemented"));
}
