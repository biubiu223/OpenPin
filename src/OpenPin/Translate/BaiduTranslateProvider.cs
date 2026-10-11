using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// Baidu Translate general text API (<c>fanyi-api.baidu.com/api/trans/vip/translate</c>), sign = MD5(appid + q + salt + key).
/// Several segments are sent in one request as lines; Baidu returns one result per line.
/// </summary>
public sealed class BaiduTranslateProvider : TranslationProviderBase
{
    public const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    private readonly HttpClient _http;
    private readonly string _appId;
    private readonly string _secret;
    private readonly Func<string> _salt;
    private readonly Func<int, CancellationToken, Task> _delay;

    public BaiduTranslateProvider(HttpClient http, string appId, string secret, Func<string>? salt = null, Func<int, CancellationToken, Task>? delay = null)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
            throw new TranslationException(Loc.Format("Translate.Error.NoKey", Loc.Get("Translate.Provider.baidu")));
        _http = http;
        _appId = appId.Trim();
        _secret = secret.Trim();
        _salt = salt ?? (() => Random.Shared.Next(10000, 99999999).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }

    public override string Id => "baidu";

    // q must stay below 6000 bytes (UTF-8, CJK = 3 bytes).
    protected override int MaxBatchItems => 100;
    protected override int MaxBatchChars => 1800;

    /// <summary>MD5(appid + q + salt + key), lowercase hex.</summary>
    public static string Sign(string appId, string q, string salt, string secret) =>
        TranslationHttp.Hex(MD5.HashData(Encoding.UTF8.GetBytes(appId + q + salt + secret)));

    public static string LanguageCode(string tag) => TranslateLanguages.Find(tag)?.Baidu ?? tag;

    protected override async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct)
    {
        var flat = new LineFlattener(segments);
        if (flat.Lines.Count == 0) return segments.Select(_ => "").ToList();
        var q = string.Join("\n", flat.Lines);
        var from = sourceLang != null ? LanguageCode(sourceLang) : "auto";
        var to = LanguageCode(targetLang);

        for (int attempt = 1; ; attempt++)
        {
            var salt = _salt();
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["q"] = q,
                    ["from"] = from,
                    ["to"] = to,
                    ["appid"] = _appId,
                    ["salt"] = salt,
                    ["sign"] = Sign(_appId, q, salt, _secret),
                }),
            };
            var body = await TranslationHttp.SendAsync(_http, request, Loc.Get("Translate.Provider.baidu"), ct).ConfigureAwait(false);
            var (lines, errorCode, errorMsg) = Parse(body);
            if (lines != null) return flat.Rebuild(lines);
            // 54003: access frequency limited (the free plan allows 1 request per second) → wait and retry.
            if (errorCode == "54003" && attempt < 4)
            {
                await _delay(1100, ct).ConfigureAwait(false);
                continue;
            }
            throw new TranslationException(Loc.Format("Translate.Error.Provider", Loc.Get("Translate.Provider.baidu"), Describe(errorCode, errorMsg)));
        }
    }

    /// <summary>trans_result[].dst, or the error code / message.</summary>
    internal static (IReadOnlyList<string>? Lines, string? ErrorCode, string? ErrorMsg) Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_code", out var code))
            {
                var c = code.ValueKind == JsonValueKind.String ? code.GetString() : code.GetRawText();
                if (c != "52000") // 52000 = success
                    return (null, c, root.TryGetProperty("error_msg", out var m) ? m.GetString() : null);
            }
            if (root.TryGetProperty("trans_result", out var tr) && tr.ValueKind == JsonValueKind.Array)
                return (tr.EnumerateArray().Select(e => e.TryGetProperty("dst", out var d) ? d.GetString() ?? "" : "").ToList(), null, null);
            return (null, "?", TranslationHttp.Summarize(body));
        }
        catch (JsonException)
        {
            return (null, "?", TranslationHttp.Summarize(body));
        }
    }

    private static string Describe(string? code, string? msg)
    {
        var hint = code switch
        {
            "52003" or "54001" => Loc.Get("Translate.Error.BadCredentials"),
            "54003" or "54005" => Loc.Get("Translate.Error.RateLimited"),
            "54004" => Loc.Get("Translate.Error.NoBalance"),
            "58001" => Loc.Get("Translate.Error.LanguageUnsupported"),
            _ => null,
        };
        var text = $"{code} {msg}".Trim();
        return hint != null ? $"{hint} ({text})" : text;
    }
}
