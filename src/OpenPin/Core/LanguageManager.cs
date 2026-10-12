using System.Windows;
using OpenPin.Core.Settings;

namespace OpenPin.Core;

/// <summary>
/// App-wide language manager (CORE-09). Owns the 10 Strings ResourceDictionaries merged into
/// <c>Application.Resources.MergedDictionaries</c> and hot-swaps them at runtime when the
/// General.Language setting changes, providing seamless runtime language switching without restarting.
/// </summary>
public static class LanguageManager
{
    public const string DefaultLanguage = "zh-CN";
    public const string English = "en-US";

    public static readonly IReadOnlyList<string> SupportedLanguages = new[] { DefaultLanguage, English };

    public static readonly IReadOnlyList<string> Modules = new[]
    {
        "Common",
        "Annotation",
        "Capture",
        "Pin",
        "Services",
        "SettingsUI",
        "LongCapture",
        "Recording",
        "Translate",
        "Recognition",
    };

    private static Application? _app;
    private static SettingsService? _settings;
    private static string? _applied;
    private static readonly Dictionary<string, ResourceDictionary> _currentDicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The currently applied language culture code ("zh-CN" or "en-US").</summary>
    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary>Raised on the UI thread after the language dictionaries have been swapped.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Resets the internal cache of active dictionaries and language state.
    /// </summary>
    public static void Reset()
    {
        _currentDicts.Clear();
        _applied = null;
        CurrentLanguage = DefaultLanguage;
    }

    /// <summary>
    /// Applies the configured language. Call once at startup, after settings are loaded and before any window opens.
    /// </summary>
    public static void Initialize(Application app, SettingsService? settings = null)
    {
        _app = app;
        _settings = settings;
        _currentDicts.Clear();
        _applied = null;

        var targetLang = settings?.Current.General.Language ?? DefaultLanguage;
        Apply(app.Resources.MergedDictionaries, targetLang);

        if (settings != null)
        {
            settings.Changed += (_, _) => OnUi(() =>
            {
                var lang = _settings?.Current.General.Language ?? DefaultLanguage;
                if (!string.Equals(lang, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    Apply(app.Resources.MergedDictionaries, lang);
                }
            });
        }
    }

    /// <summary>
    /// Switches the UI language dynamically using Application.Current.Resources.MergedDictionaries.
    /// </summary>
    public static void Apply(string? language = null)
    {
        var app = _app ?? Application.Current;
        if (app == null) return;
        Apply(app.Resources.MergedDictionaries, language);
    }

    /// <summary>
    /// Switches the UI language dynamically on the specified MergedDictionaries collection.
    /// </summary>
    public static void Apply(IList<ResourceDictionary> merged, string? language = null)
    {
        var lang = NormalizeLanguage(language ?? _settings?.Current.General.Language);

        if (_applied != null && string.Equals(_applied, lang, StringComparison.OrdinalIgnoreCase) && _currentDicts.Count == Modules.Count)
        {
            return;
        }

        try
        {
            foreach (var module in Modules)
            {
                var dict = BuildDictionary(lang, module);
                int index = -1;
                if (_currentDicts.TryGetValue(module, out var oldDict))
                {
                    index = merged.IndexOf(oldDict);
                }

                if (index < 0)
                {
                    index = FindModuleDictionaryIndex(merged, module);
                }

                if (index >= 0)
                {
                    merged[index] = dict;
                }
                else
                {
                    merged.Add(dict);
                }
                _currentDicts[module] = dict;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Applying language '{lang}' failed", ex);
            return;
        }

        bool firstApply = _applied == null;
        _applied = lang;
        CurrentLanguage = lang;
        Log.Info($"Language: {lang}");

        if (firstApply) return;

        try { LanguageChanged?.Invoke(null, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("LanguageChanged handler failed", ex); }
    }

    /// <summary>
    /// Normalizes language tag, defaulting to zh-CN if not recognized.
    /// </summary>
    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return DefaultLanguage;
        var trimmed = language.Trim();
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return DefaultLanguage;
        return DefaultLanguage;
    }

    /// <summary>
    /// Returns true if the language is supported (starts with en or zh).
    /// </summary>
    public static bool IsSupported(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var trimmed = language.Trim();
        return trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the pack URI for the specified language and module.
    /// </summary>
    public static string GetDictionarySource(string language, string module) =>
        $"/OpenPin;component/Resources/Strings/{NormalizeLanguage(language)}/{module}.xaml";

    /// <summary>
    /// Builds a ResourceDictionary for the given language and module.
    /// </summary>
    public static ResourceDictionary BuildDictionary(string language, string module)
    {
        var uri = new Uri(GetDictionarySource(language, module), UriKind.Relative);
        return new ResourceDictionary { Source = uri };
    }

    private static int FindModuleDictionaryIndex(IList<ResourceDictionary> merged, string module)
    {
        string fileName = $"{module}.xaml";
        for (int i = 0; i < merged.Count; i++)
        {
            var src = merged[i].Source?.OriginalString;
            if (string.IsNullOrEmpty(src)) continue;
            if (src.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) &&
                src.Contains("Strings", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static void OnUi(Action a)
    {
        var d = (_app ?? Application.Current)?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }
}
