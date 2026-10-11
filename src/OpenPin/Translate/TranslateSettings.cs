namespace OpenPin.Translate;

/// <summary>settings.json → "Translate". Owned by the Translate module (PRO-01).</summary>
public sealed class TranslateSettings
{
    /// <summary>Provider id: "openai" | "baidu" | "youdao" | "deepl". Empty = not configured (no network use at all).</summary>
    public string Provider { get; set; } = "";
    /// <summary>Target language tag ("" = follow app language).</summary>
    public string TargetLanguage { get; set; } = "";
    /// <summary>Used when the source text is already in the target language.</summary>
    public string FallbackLanguage { get; set; } = "en";
}
