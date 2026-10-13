using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// OpenAI-compatible chat completions (<c>POST {base}/chat/completions</c>): DeepSeek, Qwen (DashScope compatible mode),
/// Moonshot/Kimi, Zhipu, Ollama / LM Studio (no key)… A batch is sent as a JSON array and must come back as a JSON array
/// of the same length; otherwise each segment is translated with its own request.
/// </summary>
public sealed class OpenAiCompatibleProvider : TranslationProviderBase
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly double _temperature;

    public OpenAiCompatibleProvider(HttpClient http, string baseUrl, string model, string? apiKey, double temperature = 0.2)
    {
        _http = http;
        _endpoint = ChatEndpoint(baseUrl);
        _model = string.IsNullOrWhiteSpace(model) ? throw new TranslationException(Loc.Get("Translate.Error.NoModel")) : model.Trim();
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _temperature = Math.Clamp(temperature, 0, 2);
    }

    public override string Id => "openai";

    protected override int MaxBatchItems => 30;
    protected override int MaxBatchChars => 2500;

    /// <summary>"https://api.deepseek.com/v1" → ".../v1/chat/completions"; a full ".../chat/completions" URL is kept.</summary>
    public static Uri ChatEndpoint(string? baseUrl)
    {
        var b = (baseUrl ?? "").Trim();
        if (b.Length == 0) throw new TranslationException(Loc.Get("Translate.Error.NoBaseUrl"));
        if (!b.Contains("://", StringComparison.Ordinal)) b = "https://" + b;
        b = b.TrimEnd('/');
        if (!b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) b += "/chat/completions";
        if (!Uri.TryCreate(b, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new TranslationException(Loc.Get("Translate.Error.NoBaseUrl"));
        return uri;
    }

    protected override async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct)
    {
        var target = LanguageName(targetLang);
        var source = sourceLang != null ? LanguageName(sourceLang) : null;
        var content = await ChatAsync(ArraySystemPrompt(target, source, segments.Count), JsonSerializer.Serialize(segments, Json), ct).ConfigureAwait(false);
        if (TryParseArray(content, segments.Count, out var parsed)) return parsed;
        if (TryParseLines(content, segments.Count, out var lineParsed)) return lineParsed;

        Log.Warn($"Translate (openai, {_model}): reply was not a {segments.Count}-element JSON array; translating segment by segment");
        var list = new List<string>(segments.Count);
        foreach (var s in segments)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(CleanSingle(await ChatAsync(SingleSystemPrompt(target, source), s, ct).ConfigureAwait(false), s));
        }
        return list;
    }

    private static string LanguageName(string tag) => TranslateLanguages.Find(tag)?.EnglishName ?? tag;

    internal static string ArraySystemPrompt(string target, string? source, int count) =>
        "You are a professional translation engine. The user message is a JSON array of " + count + " text segments" +
        (source != null ? " written in " + source : "") + ". Translate every segment into " + target + ". " +
        "Reply with ONLY a JSON array of exactly " + count + " strings, in the same order: element i is the translation of segment i. " +
        "Do not merge, split, skip, explain or add anything. Keep line breaks, numbers, URLs, code, e-mail addresses and placeholders unchanged. " +
        "If a segment needs no translation (e.g. it is already in " + target + " or is a number), copy it unchanged. " +
        "Do not output any thoughts, reasoning, analysis, or <think> tags. Directly output the JSON array immediately.";

    internal static string SingleSystemPrompt(string target, string? source) =>
        "You are a professional translation engine. Translate the user's text" + (source != null ? " from " + source : "") +
        " into " + target + ". Output only the translation — no quotes, notes or explanations. Keep line breaks, numbers, URLs and code unchanged. " +
        "Do not output any thoughts, reasoning, analysis, or <think> tags. Directly output the translation immediately.";

    public static bool ShouldDisableThinking(string model, Uri endpoint)
    {
        if (endpoint.Host.Contains("siliconflow", StringComparison.OrdinalIgnoreCase)) return true;
        if (endpoint.Host.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) return true;
        var lower = model.ToLowerInvariant();
        return lower.Contains("r1") || lower.Contains("reasoner") || lower.Contains("thinking") ||
               lower.Contains("qwq") || lower.Contains("deepseek") || lower.Contains("flash");
    }

    private async Task<string> ChatAsync(string system, string user, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
            },
            ["temperature"] = _temperature,
            ["stream"] = false,
        };
        if (ShouldDisableThinking(_model, _endpoint))
        {
            payload["enable_thinking"] = false;
            payload["reasoning_effort"] = "none";
            payload["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" };
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
        if (_apiKey != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var body = await TranslationHttp.SendAsync(_http, request, Loc.Get("Translate.Provider.openai"), ct).ConfigureAwait(false);
        return ParseContent(body);
    }

    /// <summary>choices[0].message.content of a chat completion response.</summary>
    internal static string ParseContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    return StripThinking(content.GetString() ?? "");
                if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return StripThinking(text.GetString() ?? "");
            }
            var err = TranslationHttp.Summarize(body);
            throw new TranslationException(Loc.Format("Translate.Error.BadResponse", Loc.Get("Translate.Provider.openai"), err));
        }
        catch (JsonException ex)
        {
            throw new TranslationException(Loc.Format("Translate.Error.BadResponse", Loc.Get("Translate.Provider.openai"), TranslationHttp.Summarize(body)), ex);
        }
    }

    /// <summary>Reasoning models (DeepSeek-R1, Qwen3…) may put their reasoning in &lt;think&gt; tags.</summary>
    internal static string StripThinking(string s) =>
        Regex.Replace(s, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();

    /// <summary>Extract a JSON string array with exactly <paramref name="count"/> elements (code fences / prose around it are tolerated).</summary>
    internal static bool TryParseArray(string content, int count, out IReadOnlyList<string> result)
    {
        result = Array.Empty<string>();
        var s = content.Trim();
        int start = s.IndexOf('['), end = s.LastIndexOf(']');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(s.Substring(start, end - start + 1));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != count) return false;
            var list = new List<string>(count);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                list.Add(e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString() ?? "",
                    JsonValueKind.Null => "",
                    JsonValueKind.Array or JsonValueKind.Object => "",
                    _ => e.GetRawText(),
                });
            }
            if (list.Count(string.IsNullOrEmpty) == count && count > 0) return false;
            result = list;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Extract line-by-line translation when the model returns plain lines instead of a JSON array.</summary>
    internal static bool TryParseLines(string content, int count, out IReadOnlyList<string> result)
    {
        result = Array.Empty<string>();
        if (count <= 1) return false;
        var clean = StripThinking(content).Trim();
        var lines = clean.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("```") && !l.StartsWith("<think>") && !l.EndsWith("</think>"))
            .ToList();
        if (lines.Count == count)
        {
            result = lines.Select(l => CleanSingle(l)).ToList();
            return true;
        }
        return false;
    }

    /// <summary>A single translation: remove code fences and wrapping quotes some models add.</summary>
    internal static string CleanSingle(string content, string? original = null)
    {
        var s = content.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            s = nl >= 0 ? s[(nl + 1)..] : s.TrimStart('`');
            if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];
            s = s.Trim();
        }
        bool quotedSource = original != null && original.TrimStart().Length > 0 && original.TrimStart()[0] is '"' or '“';
        if (!quotedSource && s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '“' && s[^1] == '”'))) s = s[1..^1];
        return s;
    }
}
