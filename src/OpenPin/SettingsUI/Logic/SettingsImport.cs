using System.Text.Json;
using OpenPin.Core.Settings;

namespace OpenPin.SettingsUI.Logic;

/// <summary>Import / export of settings.json (SET-05).</summary>
internal static class SettingsImport
{
    /// <summary>Parses and validates a settings file. Missing sections get defaults; missing default hotkeys are added.</summary>
    public static bool TryRead(string json, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty";
            return false;
        }
        try
        {
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "not an object";
                    return false;
                }
            }
            var s = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions);
            if (s is null)
            {
                error = "null";
                return false;
            }
            Normalize(s);
            settings = s;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Replaces null sections/collections (e.g. <c>"Capture": null</c>) by defaults.</summary>
    public static void Normalize(AppSettings s)
    {
        s.General ??= new();
        s.Hotkeys ??= new();
        s.Hotkeys.Bindings ??= HotkeySettings.DefaultBindings();
        foreach (var (k, v) in HotkeySettings.DefaultBindings()) s.Hotkeys.Bindings.TryAdd(k, v);
        s.Capture ??= new();
        s.Capture.CustomPresets ??= new();
        s.Capture.LastCustom ??= new() { Width = 0, Height = 0 };
        s.Annotation ??= new();
        s.Annotation.Palette ??= new OpenPin.Annotation.AnnotationSettings().Palette;
        s.Annotation.ToolState ??= new();
        s.Pin ??= new();
        s.Save ??= new();
        s.Ocr ??= new();
        s.LongCapture ??= new();
        s.Recording ??= new();
    }

    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, SettingsService.JsonOptions);

}
