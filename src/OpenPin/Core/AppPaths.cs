using System.IO;

namespace OpenPin.Core;

/// <summary>
/// Where OpenPin stores its data. Portable mode: if a file named <c>portable</c> (or <c>portable.txt</c>) sits next to
/// the executable, everything is stored in <c>.\data</c>; otherwise in <c>%AppData%\OpenPin</c>.
/// </summary>
public static class AppPaths
{
    public static string AppDir { get; } = AppContext.BaseDirectory;

    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable")) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    /// <summary>Data root. Override with env var OPENPIN_DATA_DIR (development / isolated test runs).</summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("OPENPIN_DATA_DIR") is { Length: > 0 } overrideDir ? overrideDir
        : IsPortable ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPin");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>Pin windows state, pin groups and pin history (owned by the Pin module).</summary>
    public static string PinsDir => Ensure(Path.Combine(DataDir, "pins"));

    /// <summary>Screenshot history (owned by the Capture module).</summary>
    public static string CaptureHistoryDir => Ensure(Path.Combine(DataDir, "history"));

    public static string LogsDir => Ensure(Path.Combine(DataDir, "logs"));

    /// <summary>Scratch files (e.g. clipboard-as-file images). May be cleaned on startup.</summary>
    public static string TempDir => Ensure(Path.Combine(Path.GetTempPath(), "OpenPin"));

    public static string DefaultPicturesDir => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OpenPin"));

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
