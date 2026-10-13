using System.Net.Http;
using OpenPin.Core;
using OpenPin.Services;

namespace OpenPin.Translate;

/// <summary>Result of <see cref="TranslationService.TranslateAsync"/>.</summary>
public sealed record TranslationOutcome(IReadOnlyList<string> Segments, string TargetLanguage, string? DetectedSource, string ProviderId, TimeSpan Elapsed);

/// <summary>
/// Entry point of the Translate module: provider factory, target / fallback resolution, "not configured" guidance.
/// Nothing here touches the network while <see cref="TranslateSettings.Provider"/> is empty.
/// </summary>
public static class TranslationService
{
    public static readonly IReadOnlyList<string> ProviderIds = new[] { "openai", "baidu", "youdao", "deepl" };

    /// <summary>Live settings (a default instance when there is no settings service, e.g. in tests).</summary>
    public static TranslateSettings Settings
    {
        get
        {
            try { return AppServices.Settings?.Current.Translate ?? new TranslateSettings(); }
            catch { return new TranslateSettings(); }
        }
    }

    public static bool IsConfigured(TranslateSettings s) => ProviderIds.Contains(s.Provider);

    public static string ProviderName(string? id) => string.IsNullOrEmpty(id) ? Loc.Get("Translate.Provider.none") : Loc.Get("Translate.Provider." + id);

    /// <summary>Build the configured provider (secrets are decrypted here, in memory only).</summary>
    public static ITranslationProvider CreateProvider(TranslateSettings s, HttpClient http) => s.Provider switch
    {
        "openai" => new OpenAiCompatibleProvider(http, s.OpenAi.BaseUrl, s.OpenAi.Model, SecretProtector.Unprotect(s.OpenAi.ApiKeyProtected), s.OpenAi.Temperature),
        "baidu" => new BaiduTranslateProvider(http, s.Baidu.AppId, SecretProtector.Unprotect(s.Baidu.SecretProtected)),
        "youdao" => new YoudaoTranslateProvider(http, s.Youdao.AppKey, SecretProtector.Unprotect(s.Youdao.SecretProtected)),
        "deepl" => new DeepLTranslateProvider(http, SecretProtector.Unprotect(s.DeepL.ApiKeyProtected)),
        _ => throw new TranslationException(Loc.Get("Translate.NotConfigured")),
    };

    /// <summary>
    /// Translate segments with the configured provider. <paramref name="explicitTarget"/> (a tag chosen in the result
    /// window) is used as is; otherwise the configured target is resolved ("follow app") and the fallback language
    /// applies when the text is already in the target language.
    /// </summary>
    public static async Task<TranslationOutcome> TranslateAsync(IReadOnlyList<string> segments, string? explicitTarget = null,
        TranslateSettings? settings = null, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        var s = settings ?? Settings;
        if (!IsConfigured(s)) throw new TranslationException(Loc.Get("Translate.NotConfigured"));
        if (segments.Count == 0) return new TranslationOutcome(Array.Empty<string>(), "zh-Hans", null, s.Provider, TimeSpan.Zero);

        var text = string.Join("\n", segments);
        var detected = TranslateLanguages.Detect(text);

        string target;
        if (!string.IsNullOrWhiteSpace(explicitTarget) && TranslateLanguages.Find(explicitTarget) is { } exp)
        {
            target = exp.Tag;
        }
        else
        {
            var baseTarget = TranslateLanguages.ResolveTarget(s.TargetLanguage);
            if (TranslateLanguages.IsChinese(baseTarget))
            {
                // Target is Chinese: retain Chinese as target, never invert to fallback
                target = baseTarget;
            }
            else if (!string.IsNullOrEmpty(s.FallbackLanguage) && !string.Equals(s.FallbackLanguage, "none", StringComparison.OrdinalIgnoreCase))
            {
                target = TranslateLanguages.EffectiveTarget(text, baseTarget, s.FallbackLanguage);
            }
            else
            {
                target = baseTarget;
            }
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Selective segment translation:
        // When target is Chinese, Chinese segments are kept untouched; only foreign language segments are translated.
        var toTranslateIndices = new List<int>();
        var toTranslateSegments = new List<string>();

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (TranslateLanguages.ShouldTranslateSegment(seg, target))
            {
                toTranslateIndices.Add(i);
                toTranslateSegments.Add(seg);
            }
        }

        // If all segments already match target language or need no translation (e.g. all Chinese):
        if (toTranslateSegments.Count == 0)
        {
            Log.Info($"Translate: all {segments.Count} segment(s) already in target language ({target}) or untranslatable, preserving original text without network call.");
            return new TranslationOutcome(segments.ToList(), target, detected, s.Provider, clock.Elapsed);
        }

        using var http = handler != null ? new HttpClient(handler, disposeHandler: false) : TranslationHttp.CreateClient(s);
        var provider = CreateProvider(s, http);
        Log.Info($"Translate: {provider.Id}, translating {toTranslateSegments.Count}/{segments.Count} segment(s), detected {detected ?? "?"} → {target}");
        try
        {
            var translatedParts = await provider.TranslateAsync(toTranslateSegments, null, target, ct).ConfigureAwait(false);

            var result = segments.ToArray();
            for (int k = 0; k < toTranslateIndices.Count && k < translatedParts.Count; k++)
            {
                result[toTranslateIndices[k]] = translatedParts[k];
            }

            return new TranslationOutcome(result, target, detected, provider.Id, clock.Elapsed);
        }
        catch (TranslationException ex)
        {
            Log.Warn($"Translate ({provider.Id}) failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// True if a provider is configured. Otherwise shows a toast with a button that opens the translation settings and
    /// returns false (no network use).
    /// </summary>
    public static bool EnsureConfigured()
    {
        if (IsConfigured(Settings)) return true;
        Notify.Show(new NotifyOptions
        {
            Title = Loc.Get("Translate.Title"),
            Message = Loc.Get("Translate.NotConfigured"),
            DurationMs = 8000,
            Actions = new[] { new NotifyAction(Loc.Get("Translate.OpenSettings"), OpenSettings) },
        });
        return false;
    }

    public static void OpenSettings()
    {
        try { AppServices.SettingsUi.Show("translate"); }
        catch (Exception ex)
        {
            Log.Error("Opening translation settings failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }
}
