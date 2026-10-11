using System.IO;
using System.Runtime.CompilerServices;

namespace OpenPin.Core;

/// <summary>Tiny thread-safe file logger: %AppData%\OpenPin\logs\openpin-yyyyMMdd.log (7 days kept).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init()
    {
        try
        {
            _file = Path.Combine(AppPaths.LogsDir, $"openpin-{DateTime.Now:yyyyMMdd}.log");
            foreach (var old in Directory.GetFiles(AppPaths.LogsDir, "openpin-*.log"))
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7)) File.Delete(old);
        }
        catch { /* logging must never crash the app */ }
    }

    public static void Info(string message, [CallerFilePath] string file = "") => Write("INFO", message, file);
    public static void Warn(string message, [CallerFilePath] string file = "") => Write("WARN", message, file);

    public static void Error(string message, Exception? ex = null, [CallerFilePath] string file = "") =>
        Write("ERROR", ex == null ? message : $"{message}\n{ex}", file);

    public static void Error(Exception ex, [CallerFilePath] string file = "") => Write("ERROR", ex.ToString(), file);

    private static void Write(string level, string message, string callerFile)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Path.GetFileNameWithoutExtension(callerFile)}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_file == null) return;
        try
        {
            lock (Gate) File.AppendAllText(_file, line + Environment.NewLine);
        }
        catch { /* ignore */ }
    }
}
