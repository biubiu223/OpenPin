using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// Youdao Zhiyun text translation (<c>openapi.youdao.com/api</c>), v3 signature:
/// sign = SHA256(appKey + input + salt + curtime + appSecret), input = q if ≤ 20 chars else q[..10] + len + q[^10..].
/// A batch is sent as lines joined by '\n'; if the reply does not keep the line count, each segment is sent alone.
/// </summary>
public sealed class YoudaoTranslateProvider : TranslationProviderBase
{
    public const string Endpoint = "https://openapi.youdao.com/api";

    private readonly HttpClient _http;
    private readonly string _appKey;
    private readonly string _secret;
    private readonly Func<string> _salt;
    private readonly Func<long> _now;

    public YoudaoTranslateProvider(HttpClient http, string appKey, string secret, Func<string>? salt = null, Func<long>? unixSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(secret))
            throw new TranslationException(Loc.Format("Translate.Error.NoKey", Loc.Get("Translate.Provider.youdao")));
        _http = http;
        _appKey = appKey.Trim();
        _secret = secret.Trim();
        _salt = salt ?? (() => Guid.NewGuid().ToString());
        _now = unixSeconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public override string Id => "youdao";

    protected override int MaxBatchItems => 60;
    protected override int MaxBatchChars => 1500;

    /// <summary>The "input" part of the v3 signature.</summary>
    public static string SignInput(string q) => q.Length <= 20 ? q : q[..10] + q.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + q[^10..];

    /// <summary>SHA256(appKey + input + salt + curtime + appSecret), lowercase hex.</summary>
    public static string Sign(string appKey, string q, string salt, string curtime, string secret) =>
        TranslationHttp.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(appKey + SignInput(q) + salt + curtime + secret)));

    public static string LanguageCode(string tag) => TranslateLanguages.Find(tag)?.Youdao ?? tag;

    protected override async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string? sourceLang, string targetLang, CancellationToken ct)
    {
        var flat = new LineFlattener(segments);
        if (flat.Lines.Count == 0) return segments.Select(_ => "").ToList();
        var translated = await TranslateTextAsync(string.Join("\n", flat.Lines), sourceLang, targetLang, ct).ConfigureAwait(false);
        var lines = TranslationHttp.Lines(translated).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        if (lines.Count == flat.Lines.Count) return flat.Rebuild(lines);

        // Line structure not preserved: one request per line keeps the alignment.
        Log.Warn($"Translate (youdao): {flat.Lines.Count} lines in, {lines.Count} out; translating line by line");
        var perLine = new List<string>(flat.Lines.Count);
        foreach (var line in flat.Lines)
        {
            ct.ThrowIfCancellationRequested();
            perLine.Add((await TranslateTextAsync(line, sourceLang, targetLang, ct).ConfigureAwait(false)).Replace("\r", "").Replace('\n', ' ').Trim());
        }
        return flat.Rebuild(perLine);
    }

    private async Task<string> TranslateTextAsync(string q, string? sourceLang, string targetLang, CancellationToken ct)
    {
        var salt = _salt();
        var curtime = _now().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = q,
                ["from"] = sourceLang != null ? LanguageCode(sourceLang) : "auto",
                ["to"] = LanguageCode(targetLang),
                ["appKey"] = _appKey,
                ["salt"] = salt,
                ["sign"] = Sign(_appKey, q, salt, curtime, _secret),
                ["signType"] = "v3",
                ["curtime"] = curtime,
            }),
        };
        var body = await TranslationHttp.SendAsync(_http, request, Loc.Get("Translate.Provider.youdao"), ct).ConfigureAwait(false);
        var (text, error) = Parse(body);
        if (text != null) return text;
        throw new TranslationException(Loc.Format("Translate.Error.Provider", Loc.Get("Translate.Provider.youdao"), Describe(error)));
    }

    /// <summary>translation[] joined by '\n', or the errorCode.</summary>
    internal static (string? Text, string? ErrorCode) Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("errorCode", out var c) ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText()) : null;
            if (code != "0") return (null, code ?? "?");
            if (root.TryGetProperty("translation", out var tr) && tr.ValueKind == JsonValueKind.Array)
                return (string.Join("\n", tr.EnumerateArray().Select(e => e.GetString() ?? "")), null);
            return (null, "?");
        }
        catch (JsonException)
        {
            return (null, TranslationHttp.Summarize(body));
        }
    }

    private static string Describe(string? code)
    {
        var hint = code switch
        {
            "101" or "102" => Loc.Get("Translate.Error.LanguageUnsupported"),
            "108" or "110" or "111" or "202" => Loc.Get("Translate.Error.BadCredentials"),
            "401" => Loc.Get("Translate.Error.NoBalance"),
            "411" or "412" => Loc.Get("Translate.Error.RateLimited"),
            _ => null,
        };
        return hint != null ? $"{hint} (errorCode {code})" : $"errorCode {code}";
    }
}
