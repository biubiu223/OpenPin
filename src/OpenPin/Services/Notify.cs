using System.Windows;
using System.Windows.Media.Imaging;
using OpenPin.Core;

namespace OpenPin.Services;

public sealed record NotifyAction(string Text, Action Execute);

public sealed class NotifyOptions
{
    public string Title { get; init; } = "OpenPin";
    public string Message { get; init; } = "";
    public bool IsError { get; init; }
    /// <summary>Optional thumbnail. Must be frozen when <see cref="Notify.Show"/> is called from a background thread.</summary>
    public BitmapSource? Thumbnail { get; init; }
    /// <summary>Buttons; the toast closes after a button's action ran (on the UI thread).</summary>
    public IReadOnlyList<NotifyAction> Actions { get; init; } = Array.Empty<NotifyAction>();
    /// <summary>Auto-hide delay; &lt;= 0 = stays until closed. Errors stay at least 5 s.</summary>
    public int DurationMs { get; init; } = 3000;
}

/// <summary>
/// User notifications (toasts). Thread-safe: may be called from any thread.
/// Shows a Win11-style toast window bottom-right on the monitor under the cursor (stacked, auto-hide, hover pauses,
/// thumbnail + action buttons, error style). Falls back to the backend given to <see cref="Initialize"/> (tray balloon)
/// if the toast window cannot be shown.
/// </summary>
public static class Notify
{
    private static Action<NotifyOptions>? _backend;

    /// <summary>False = always use the fallback backend (tray balloons).</summary>
    public static bool UseToastWindow { get; set; } = true;

    /// <summary>Called by App at startup.</summary>
    internal static void Initialize(Action<NotifyOptions> fallbackBackend) => _backend = fallbackBackend;

    public static void Info(string message, string? title = null) =>
        Show(new NotifyOptions { Title = title ?? "OpenPin", Message = message });

    public static void Error(string message, string? title = null) =>
        Show(new NotifyOptions { Title = title ?? "OpenPin", Message = message, IsError = true });

    public static void Show(NotifyOptions options)
    {
        Log.Info($"Notify: {options.Title}: {options.Message}");
        var app = Application.Current;
        if (app == null) return;
        if (options.Thumbnail is { IsFrozen: false } thumb && thumb.CheckAccess())
            options = WithFrozenThumbnail(options, thumb);
        app.Dispatcher.BeginInvoke(() =>
        {
            if (UseToastWindow)
            {
                try
                {
                    ToastHost.Show(options);
                    return;
                }
                catch (Exception ex) { Log.Error("Toast window failed, falling back", ex); }
            }
            try { _backend?.Invoke(options); }
            catch (Exception ex) { Log.Error("Notify backend failed", ex); }
        });
    }

    /// <summary>Close all visible toasts.</summary>
    public static void DismissAll()
    {
        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(() =>
        {
            try { ToastHost.CloseAll(); }
            catch (Exception ex) { Log.Error("Closing toasts failed", ex); }
        });
    }

    private static NotifyOptions WithFrozenThumbnail(NotifyOptions o, BitmapSource thumb) => new()
    {
        Title = o.Title,
        Message = o.Message,
        IsError = o.IsError,
        Thumbnail = ImageEncoding.EnsureFrozen(thumb),
        Actions = o.Actions,
        DurationMs = o.DurationMs,
    };
}
