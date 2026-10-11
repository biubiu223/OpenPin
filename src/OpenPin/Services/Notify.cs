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
    public BitmapSource? Thumbnail { get; init; }
    public IReadOnlyList<NotifyAction> Actions { get; init; } = Array.Empty<NotifyAction>();
    public int DurationMs { get; init; } = 3000;
}

/// <summary>
/// User notifications (toasts). Thread-safe: may be called from any thread.
/// Phase-0 implementation uses tray balloons; the Services module replaces it with a toast window
/// (bottom-right, auto-hide, optional thumbnail + action buttons) without changing this API.
/// </summary>
public static class Notify
{
    private static Action<NotifyOptions>? _backend;

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
        if (app == null || _backend == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            try { _backend(options); }
            catch (Exception ex) { Log.Error("Notify backend failed", ex); }
        });
    }
}
