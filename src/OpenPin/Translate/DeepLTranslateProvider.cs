using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// DeepL API v2 (<c>POST /v2/translate</c>, JSON, header <c>Authorization: DeepL-Auth-Key …</c>). Keys ending in ":fx"
/// belong to the free plan and use api-free.deepl.com.
/// </summary>
public sealed class DeepLTranslateProvider : TranslationProviderBase
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly string _key;

    public DeepLTranslateProvider(HttpClient http, string authKey)
    {
        if (string.IsNullOrWhiteSpace(authKey))
            throw new TranslationException(Loc.Format("Translate.Error.NoKey", Loc.Get("Translate.Provider.deepl")));
        _http = http;
        _key = authKey.Trim();
    }

    public override string Id => "deepl";

    protected override int MaxBatchItems => 50;
    protected override int MaxBatchChars => 20000;

    public static bool IsFreeKey(string key) => key.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase);

    public static Uri EndpointFor(string key) =>
        new(IsFreeKey(key) ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate");

    /// <summary>Target code, or null when DeepL does not support the language.</summary>
    public static string? TargetCode(string tag) => TranslateLanguages.Find(tag)?.DeepL;

    /// <summary>Source codes have no regional variants ("ZH", "EN", "PT").</summary>
    public static string? SourceCode(string tag) => TargetCode(tag)?.Split('-')[0];

    protected override async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct)
    {
        var target = TargetCode(targetLang)
                     ?? throw new TranslationException(Loc.Format("Translate.Error.TargetUnsupported", Loc.Get("Translate.Provider.deepl"), TranslateLanguages.Find(targetLang)?.DisplayName ?? targetLang));
        var payload = new Dictionary<string, object> { ["text"] = segments, ["target_lang"] = target };
        if (sourceLang != null && SourceCode(sourceLang) is { } src) payload["source_lang"] = src;

        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointFor(_key))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _key);
        var body = await TranslationHttp.SendAsync(_http, request, Loc.Get("Translate.Provider.deepl"), ct, DescribeHttpError).ConfigureAwait(false);
        return Parse(body);
    }

    internal static IReadOnlyList<string> Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array)
                return tr.EnumerateArray().Select(e => e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").ToList();
        }
        catch (JsonException) { /* reported below */ }
        throw new TranslationException(Loc.Format("Translate.Error.BadResponse", Loc.Get("Translate.Provider.deepl"), TranslationHttp.Summarize(body)));
    }

    private static string? DescribeHttpError(int status, string body) => status switch
    {
        401 or 403 => Loc.Get("Translate.Error.BadCredentials"),
        456 => Loc.Get("Translate.Error.NoBalance"),
        429 => Loc.Get("Translate.Error.RateLimited"),
        _ => null,
    };
}
