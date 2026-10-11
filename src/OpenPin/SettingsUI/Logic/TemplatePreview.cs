using System.IO;
using OpenPin.Services;

namespace OpenPin.SettingsUI.Logic;

/// <summary>Live previews of file-name / path templates on the Save page (uses <see cref="PathTemplate"/>).</summary>
internal static class TemplatePreview
{
    public const int SampleWidth = 1920;
    public const int SampleHeight = 1080;

    /// <summary>Window title used by the sample context (the UI sets a localized one).</summary>
    public static string SampleTitle { get; set; } = "Untitled - Notepad";

    /// <summary>Sample context: a Notepad window, captured "now".</summary>
    public static SaveContext SampleContext(DateTime timestamp) => new()
    {
        Timestamp = timestamp,
        WindowTitle = SampleTitle,
        ProcessName = "notepad.exe",
        FromCapture = true,
    };

    /// <summary>Default folder of manual / quick save (Pictures\OpenPin), without creating it.</summary>
    public static string DefaultPicturesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OpenPin");

    /// <summary>Default auto-save folder (Pictures\OpenPin\AutoSave).</summary>
    public static string DefaultAutoSaveFolder => Path.Combine(DefaultPicturesFolder, "AutoSave");

    /// <summary>Configured folder with environment variables expanded, or <paramref name="fallback"/> when empty.</summary>
    public static string ResolveFolder(string? folder, string fallback)
    {
        if (string.IsNullOrWhiteSpace(folder)) return fallback;
        try { return Environment.ExpandEnvironmentVariables(folder.Trim()); }
        catch { return folder.Trim(); }
    }

    /// <summary>Manual save: file name shown in the save dialog (template without extension + extension).</summary>
    public static string FileName(string? template, string? extension, DateTime timestamp)
    {
        var t = string.IsNullOrWhiteSpace(template) ? "OpenPin_$yyyy-MM-dd_HH-mm-ss$" : template;
        var name = Path.GetFileName(PathTemplate.Expand(t, SampleContext(timestamp), SampleWidth, SampleHeight).TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(name)) name = "OpenPin";
        var ext = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.StartsWith('.') ? extension : "." + extension;
        return name + ext.ToLowerInvariant();
    }

    /// <summary>Quick / auto save: full path the next file would get.</summary>
    public static string FullPath(string? folder, string fallbackFolder, string? template, DateTime timestamp)
    {
        try
        {
            return PathTemplate.ExpandToFullPath(ResolveFolder(folder, fallbackFolder), template ?? "",
                SampleContext(timestamp), SampleWidth, SampleHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    /// <summary>Template variables for the help list: (token, description resource key).</summary>
    public static readonly IReadOnlyList<(string Token, string DescKey)> Variables = new[]
    {
        ("$yyyy-MM-dd_HH-mm-ss$", "SettingsUI.Var.Date"),
        ("$yyyy$/$MM$/", "SettingsUI.Var.DateFolder"),
        ("%title%", "SettingsUI.Var.Title"),
        ("%process%", "SettingsUI.Var.Process"),
        ("%width% %height%", "SettingsUI.Var.Size"),
        ("%uuid%", "SettingsUI.Var.Uuid"),
        ("%timestamp%", "SettingsUI.Var.Timestamp"),
        ("%computername% %username% %os%", "SettingsUI.Var.Machine"),
        ("%NAME%", "SettingsUI.Var.Env"),
        ("%title#20%", "SettingsUI.Var.Truncate"),
        ("%process!%", "SettingsUI.Var.StripExe"),
        ("%OneDrive&%\\", "SettingsUI.Var.KeepSeparators"),
    };
}
