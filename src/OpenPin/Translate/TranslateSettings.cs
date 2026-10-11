namespace OpenPin.Translate;

/// <summary>What image translation produces.</summary>
public enum TranslateMode
{
    /// <summary>OCR + translate, then show source / translation side by side in a window.</summary>
    ImageToText,
    /// <summary>Paint the translation over the original text boxes and pin the new image over the source.</summary>
    ImageToImage,
}

/// <summary>Network proxy used for translation requests.</summary>
public enum TranslateProxyMode { None, System, Http, Socks5 }

/// <summary>settings.json → "Translate". Owned by the Translate module (PRO-01).</summary>
public sealed class TranslateSettings
{
    /// <summary>Provider id: "openai" | "baidu" | "youdao" | "deepl". Empty = not configured (no network use at all).</summary>
    public string Provider { get; set; } = "";
    /// <summary>Target language tag ("" = follow app language).</summary>
    public string TargetLanguage { get; set; } = "";
    /// <summary>Used when the source text is already in the target language.</summary>
    public string FallbackLanguage { get; set; } = "en";

    public TranslateMode Mode { get; set; } = TranslateMode.ImageToText;

    public TranslateProxyMode Proxy { get; set; } = TranslateProxyMode.System;
    /// <summary>"host:port" (or a full URI) for <see cref="TranslateProxyMode.Http"/> / <see cref="TranslateProxyMode.Socks5"/>.</summary>
    public string ProxyAddress { get; set; } = "127.0.0.1:7890";

    /// <summary>Per-request timeout, seconds (5-300).</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public OpenAiTranslateSettings OpenAi { get; set; } = new();
    public BaiduTranslateSettings Baidu { get; set; } = new();
    public YoudaoTranslateSettings Youdao { get; set; } = new();
    public DeepLTranslateSettings DeepL { get; set; } = new();
}

/// <summary>
/// OpenAI-compatible chat completions (DeepSeek, Qwen/DashScope, Moonshot/Kimi, Zhipu, Ollama, LM Studio…).
/// Secrets are stored DPAPI-encrypted (see <see cref="SecretProtector"/>); never log them.
/// </summary>
public sealed class OpenAiTranslateSettings
{
    /// <summary>Preset id the base URL / model came from ("deepseek", "qwen", "kimi", "zhipu", "ollama", "lmstudio", "custom").</summary>
    public string Preset { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-chat";
    /// <summary>DPAPI-protected API key ("dpapi:…"). Empty for local servers without a key.</summary>
    public string ApiKeyProtected { get; set; } = "";
    public double Temperature { get; set; } = 0.2;
}

/// <summary>Baidu Translate (fanyi-api.baidu.com): APP ID + secret key (MD5 sign).</summary>
public sealed class BaiduTranslateSettings
{
    public string AppId { get; set; } = "";
    /// <summary>DPAPI-protected secret key.</summary>
    public string SecretProtected { get; set; } = "";
}

/// <summary>Youdao Zhiyun (openapi.youdao.com): app key + app secret (v3 SHA-256 sign).</summary>
public sealed class YoudaoTranslateSettings
{
    public string AppKey { get; set; } = "";
    /// <summary>DPAPI-protected app secret.</summary>
    public string SecretProtected { get; set; } = "";
}

/// <summary>DeepL API: the free endpoint is used for keys ending in ":fx".</summary>
public sealed class DeepLTranslateSettings
{
    /// <summary>DPAPI-protected auth key.</summary>
    public string ApiKeyProtected { get; set; } = "";
}
