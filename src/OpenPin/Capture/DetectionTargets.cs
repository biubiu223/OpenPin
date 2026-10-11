using System.Windows;
using OpenPin.Core.Screen;

namespace OpenPin.Capture;

/// <summary>A window that can be auto-selected. <see cref="RectImg"/> is in snapshot image px, clipped to the image.</summary>
internal sealed record DetectTarget(Int32Rect RectImg, WindowInfo? Window);

/// <summary>
/// CAP-02: windows (z-order, topmost first) that the overlay highlights on hover. Desktop windows (Progman/WorkerW)
/// resolve to the monitor under the point.
/// </summary>
internal sealed class DetectionTargets
{
    private readonly List<DetectTarget> _targets;
    private readonly IReadOnlyList<Int32Rect> _monitorsImg;

    public DetectionTargets(IEnumerable<DetectTarget> targets, IReadOnlyList<Int32Rect> monitorsImg)
    {
        _targets = targets.ToList();
        _monitorsImg = monitorsImg;
    }

    public IReadOnlyList<DetectTarget> All => _targets;

    public static bool IsDesktopClass(string className) => className is "Progman" or "WorkerW";

    /// <summary>
    /// Window classes that are never useful capture targets (invisible input/shell helper windows that would
    /// otherwise swallow the hover).
    /// </summary>
    public static bool IsIgnoredClass(string className) => className is
        "Shell_InputSwitchTopLevelWindow" or "EdgeUiInputTopWndClass" or
        "NativeHWNDHost" or "Internet Explorer_Hidden" or "ForegroundStaging" or "MSCTFIME UI" or "IME" or
        "tooltips_class32" or "SysShadow" or "Shell_TrayWnd_Shadow" or "Progman_Shadow" or "XamlExplorerHostIslandWindow_Overlay";

    /// <summary>The detection target under an image-px point: topmost window, desktop → its monitor, else the monitor.</summary>
    public DetectTarget? HitTest(Point imgPx)
    {
        foreach (var t in _targets)
        {
            if (!SelectionMath.Contains(t.RectImg, imgPx)) continue;
            if (t.Window != null && IsDesktopClass(t.Window.ClassName)) break;
            return t;
        }
        foreach (var m in _monitorsImg)
            if (SelectionMath.Contains(m, imgPx))
                return new DetectTarget(m, null);
        return null;
    }
}

/// <summary>
/// A nested chain of rectangles at the cursor: [window, element, child element, …]. The mouse wheel walks it
/// (CAP-03): up = parent (larger), down = child (smaller). Pure.
/// </summary>
internal sealed class ElementLevels
{
    private readonly List<Int32Rect> _rects;

    public ElementLevels(IEnumerable<Int32Rect> outerToInner)
    {
        _rects = Normalize(outerToInner);
        Index = _rects.Count - 1;
    }

    public IReadOnlyList<Int32Rect> Rects => _rects;
    public int Index { get; private set; }
    public Int32Rect? Current => Index >= 0 && Index < _rects.Count ? _rects[Index] : null;

    public bool Parent()
    {
        if (Index <= 0) return false;
        Index--;
        return true;
    }

    public bool Child()
    {
        if (Index >= _rects.Count - 1) return false;
        Index++;
        return true;
    }

    /// <summary>Keep the user's chosen depth when a refreshed chain has the same prefix.</summary>
    public void TrySelect(Int32Rect rect)
    {
        int i = _rects.IndexOf(rect);
        if (i >= 0) Index = i;
    }

    public bool SameAs(ElementLevels? other) => other != null && other._rects.SequenceEqual(_rects);

    /// <summary>Drop empty rects and consecutive duplicates; clip each rect to its parent.</summary>
    internal static List<Int32Rect> Normalize(IEnumerable<Int32Rect> outerToInner)
    {
        var list = new List<Int32Rect>();
        foreach (var raw in outerToInner)
        {
            var r = list.Count == 0 ? raw : SelectionMath.Intersect(raw, list[^1]);
            if (SelectionMath.IsEmpty(r) || r.Width < 2 || r.Height < 2) continue;
            if (list.Count > 0 && list[^1] == r) continue;
            list.Add(r);
        }
        return list;
    }
}
