using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using OpenPin.Core;

namespace OpenPin.Capture;

/// <summary>
/// CAP-03: UI element detection through UI Automation. Runs on ONE dedicated MTA background thread (a hung target
/// app can never block the overlay), processes only the latest request, caches the element tree per window for the
/// lifetime of the capture session (the screen is frozen anyway) and gives up after a time budget.
/// Results are raised on the UI thread. Rectangles are screen px.
/// </summary>
internal sealed class ElementDetector : IDisposable
{
    private sealed class Node
    {
        public required AutomationElement Element { get; init; }
        public required Int32Rect RectPx { get; init; }
        public List<Node>? Children;
    }

    private readonly Dispatcher _dispatcher;
    private readonly int _budgetMs;
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Dictionary<IntPtr, Node?> _roots = new();
    private (IntPtr Hwnd, int X, int Y, int Seq)? _pending;
    private volatile bool _disposed;
    private readonly CacheRequest _cache;

    /// <summary>(sequence, hwnd, chain outer→inner in screen px, excluding the window itself).</summary>
    public event Action<int, IntPtr, IReadOnlyList<Int32Rect>>? Completed;

    public ElementDetector(Dispatcher dispatcher, int budgetMs)
    {
        _dispatcher = dispatcher;
        _budgetMs = Math.Clamp(budgetMs, 50, 5000);
        _cache = new CacheRequest { TreeScope = TreeScope.Element };
        _cache.Add(AutomationElement.BoundingRectangleProperty);
        _cache.Add(AutomationElement.IsOffscreenProperty);
        var t = new Thread(Run) { IsBackground = true, Name = "OpenPin UIA detector" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    /// <summary>Queue a lookup; older queued requests are dropped.</summary>
    public void Request(IntPtr hwnd, int xPx, int yPx, int seq)
    {
        lock (_gate) _pending = (hwnd, xPx, yPx, seq);
        _signal.Set();
    }

    private void Run()
    {
        while (!_disposed)
        {
            _signal.WaitOne();
            if (_disposed) break;
            (IntPtr Hwnd, int X, int Y, int Seq)? req;
            lock (_gate) { req = _pending; _pending = null; }
            if (req is not { } r) continue;
            IReadOnlyList<Int32Rect> chain;
            try { chain = Walk(r.Hwnd, r.X, r.Y); }
            catch (Exception ex)
            {
                Log.Warn($"UIA detection failed: {ex.GetType().Name}: {ex.Message}");
                chain = Array.Empty<Int32Rect>();
            }
            if (_disposed) break;
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (!_disposed) Completed?.Invoke(r.Seq, r.Hwnd, chain);
                });
            }
            catch { /* dispatcher shut down */ }
        }
    }

    private IReadOnlyList<Int32Rect> Walk(IntPtr hwnd, int x, int y)
    {
        var sw = Stopwatch.StartNew();
        var chain = new List<Int32Rect>();
        if (!_roots.TryGetValue(hwnd, out var node))
        {
            node = null;
            using (_cache.Activate())
            {
                try
                {
                    var el = AutomationElement.FromHandle(hwnd);
                    if (el != null) node = MakeNode(el);
                }
                catch (Exception ex) { Log.Warn($"UIA FromHandle failed: {ex.Message}"); }
            }
            _roots[hwnd] = node;
        }

        for (int depth = 0; node != null && depth < 40; depth++)
        {
            if (_disposed || sw.ElapsedMilliseconds > _budgetMs || PendingNewer()) break;
            if (node.Children == null)
            {
                node.Children = new List<Node>();
                using (_cache.Activate())
                {
                    try
                    {
                        var found = node.Element.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                        foreach (AutomationElement c in found)
                            if (MakeNode(c) is { } n) node.Children.Add(n);
                    }
                    catch (Exception) { /* element vanished / provider failed: treat as leaf */ }
                }
            }
            Node? best = null;
            long bestArea = long.MaxValue;
            foreach (var c in node.Children)
            {
                var b = c.RectPx;
                if (x < b.X || y < b.Y || x >= b.X + b.Width || y >= b.Y + b.Height) continue;
                long area = (long)b.Width * b.Height;
                if (area < bestArea) { bestArea = area; best = c; }
            }
            if (best == null) break;
            chain.Add(best.RectPx);
            node = best;
        }
        return chain;
    }

    private bool PendingNewer()
    {
        lock (_gate) return _pending != null;
    }

    private static Node? MakeNode(AutomationElement el)
    {
        try
        {
            if (el.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty) is true) return null;
            if (el.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty) is not Rect r || r.IsEmpty ||
                r.Width < 2 || r.Height < 2 || double.IsInfinity(r.Width)) return null;
            return new Node
            {
                Element = el,
                RectPx = new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)),
            };
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        Completed = null;
        _signal.Set();
    }
}
