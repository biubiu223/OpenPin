using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using OpenPin.Translate;

namespace OpenPin.Tests.Translate;

public class BatchingTests
{
    [Fact]
    public void Split_KeepsOrder_AndRespectsLimits()
    {
        var segs = new[] { "aaaa", "bb", "cccccc", "d", "eeeeeeeeeeee", "f" };
        var batches = TranslationBatcher.Split(segs, maxItems: 2, maxChars: 8);
        Assert.Equal(Enumerable.Range(0, segs.Length), batches.SelectMany(b => b));
        Assert.All(batches, b => Assert.True(b.Count <= 2));
        // An oversized segment forms its own batch.
        Assert.Contains(batches, b => b.Count == 1 && b[0] == 4);
        Assert.All(batches.Where(b => b.Count > 1), b => Assert.True(b.Sum(i => segs[i].Length) <= 8));
    }

    [Fact]
    public void Split_Empty() => Assert.Empty(TranslationBatcher.Split(Array.Empty<string>(), 10, 100));

    private sealed class EchoProvider : TranslationProviderBase
    {
        public readonly List<IReadOnlyList<string>> Calls = new();
        public override string Id => "echo";
        protected override int MaxBatchItems => 2;
        protected override Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> segments, string? s, string t, CancellationToken ct)
        {
            Calls.Add(segments);
            return Task.FromResult<IReadOnlyList<string>>(segments.Select(x => $"[{x}]").ToList());
        }
    }

    [Fact]
    public async Task Base_SkipsBlankSegments_AndReassemblesInOrder()
    {
        var p = new EchoProvider();
        var result = await p.TranslateAsync(new[] { "a", "", "b", "  ", "c", "d", "e" }, null, "en", default);
        Assert.Equal(new[] { "[a]", "", "[b]", "  ", "[c]", "[d]", "[e]" }, result);
        Assert.Equal(3, p.Calls.Count); // 5 non-blank segments, 2 per batch
        Assert.DoesNotContain(p.Calls.SelectMany(c => c), string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void LineFlattener_RebuildsSegmentsWithBlankLines()
    {
        var flat = new LineFlattener(new[] { "one\n\ntwo", "three", "four\r\nfive" });
        Assert.Equal(new[] { "one", "two", "three", "four", "five" }, flat.Lines);
        var rebuilt = flat.Rebuild(new[] { "1", "2", "3", "4", "5" });
        Assert.Equal(new[] { "1\n\n2", "3", "4\n5" }, rebuilt);
        Assert.Throws<TranslationException>(() => flat.Rebuild(new[] { "1" }));
    }
}

public class OpenAiProviderTests
{
    private static string Completion(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { role = "assistant", content } } } });

    [Theory]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://x.example/v1/chat/completions", "https://x.example/v1/chat/completions")]
    [InlineData("open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    public void ChatEndpoint(string input, string expected) => Assert.Equal(expected, OpenAiCompatibleProvider.ChatEndpoint(input).ToString());

    [Fact]
    public void ChatEndpoint_RejectsEmpty() => Assert.Throws<TranslationException>(() => OpenAiCompatibleProvider.ChatEndpoint(" "));

    [Fact]
    public async Task SendsJsonArray_AndParsesJsonArrayReply()
    {
        var h = new FakeHandler((req, body) => FakeHandler.Json(Completion("```json\n[\"你好\", \"世界\"]\n```")));
        var p = new OpenAiCompatibleProvider(h.Client(), "https://api.example.com/v1", "m1", "sk-test");
        var result = await p.TranslateAsync(new[] { "Hello", "World" }, null, "zh-Hans", default);

        Assert.Equal(new[] { "你好", "世界" }, result);
        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", r.Uri.ToString());
        Assert.Equal("Bearer sk-test", r.Auth);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal("m1", doc.RootElement.GetProperty("model").GetString());
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Contains("Simplified Chinese", messages[0].GetProperty("content").GetString());
        var user = messages[1].GetProperty("content").GetString()!;
        Assert.Equal(new[] { "Hello", "World" }, JsonSerializer.Deserialize<string[]>(user));
    }

    [Fact]
    public async Task WrongLengthReply_FallsBackToOneRequestPerSegment()
    {
        var h = new FakeHandler((req, body, n) => FakeHandler.Json(Completion(n == 1 ? "[\"only one\"]" : n == 2 ? "\"A\"" : "<think>hmm</think>B")));
        var p = new OpenAiCompatibleProvider(h.Client(), "http://localhost:11434/v1", "qwen", apiKey: null);
        var result = await p.TranslateAsync(new[] { "a", "b" }, null, "en", default);
        Assert.Equal(new[] { "A", "B" }, result);
        Assert.Equal(3, h.Requests.Count);
        Assert.All(h.Requests, r => Assert.Null(r.Auth)); // local server: no key, no header
        // fallback requests carry the plain segment text
        Assert.Equal("a", JsonDocument.Parse(h.Requests[1].Body).RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task HttpError_BecomesTranslationException()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json("{\"error\":{\"message\":\"Invalid API key\"}}", HttpStatusCode.Unauthorized));
        var p = new OpenAiCompatibleProvider(h.Client(), "https://api.example.com/v1", "m", "bad");
        var ex = await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(new[] { "x" }, null, "en", default));
        Assert.Single(h.Requests); // no per-segment retry storm on HTTP errors
        Assert.DoesNotContain("bad", ex.Message);
    }

    [Theory]
    [InlineData("[\"a\",\"b\"]", 2, true)]
    [InlineData("Here you go:\n[\"a\", \"b\"]\nDone.", 2, true)]
    [InlineData("[\"a\"]", 2, false)]
    [InlineData("not json", 1, false)]
    [InlineData("[1, \"b\"]", 2, true)]
    public void TryParseArray(string content, int count, bool ok) => Assert.Equal(ok, OpenAiCompatibleProvider.TryParseArray(content, count, out _));

    [Fact]
    public void CleanSingle_StripsFencesAndQuotes()
    {
        Assert.Equal("你好", OpenAiCompatibleProvider.CleanSingle("\"你好\""));
        Assert.Equal("\"quoted\"", OpenAiCompatibleProvider.CleanSingle("\"quoted\"", "\"zitat\""));
        Assert.Equal("x", OpenAiCompatibleProvider.CleanSingle("```\nx\n```"));
        Assert.Equal("ok", OpenAiCompatibleProvider.StripThinking("<think>\nreasoning\n</think>\nok"));
    }
}

public class BaiduProviderTests
{
    private static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Fact]
    public void Sign_IsMd5OfAppIdQuerySaltKey()
    {
        // Example from the Baidu Translate API documentation.
        Assert.Equal("f89f9594663708c1605f3d736d01d2d4", BaiduTranslateProvider.Sign("2015063000000001", "apple", "1435660288", "12345678"));
        Assert.Equal(Md5("id你好\n世界42secret"), BaiduTranslateProvider.Sign("id", "你好\n世界", "42", "secret"));
    }

    [Fact]
    public async Task SendsLinesInOneRequest_WithDeterministicSign()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json(
            "{\"from\":\"en\",\"to\":\"zh\",\"trans_result\":[{\"src\":\"Hello\",\"dst\":\"你好\"},{\"src\":\"big\",\"dst\":\"大\"},{\"src\":\"world\",\"dst\":\"世界\"}]}"));
        var p = new BaiduTranslateProvider(h.Client(), "appid1", "key1", salt: () => "777");
        var result = await p.TranslateAsync(new[] { "Hello", "big\nworld" }, null, "zh-Hans", default);

        Assert.Equal(new[] { "你好", "大\n世界" }, result);
        var r = Assert.Single(h.Requests);
        Assert.Equal(BaiduTranslateProvider.Endpoint, r.Uri.ToString());
        var form = HttpUtility.ParseQueryString(r.Body);
        Assert.Equal("Hello\nbig\nworld", form["q"]);
        Assert.Equal("auto", form["from"]);
        Assert.Equal("zh", form["to"]);
        Assert.Equal("appid1", form["appid"]);
        Assert.Equal("777", form["salt"]);
        Assert.Equal(Md5("appid1" + "Hello\nbig\nworld" + "777" + "key1"), form["sign"]);
        Assert.DoesNotContain("key1", r.Body);
    }

    [Fact]
    public async Task ErrorCode_BecomesException_AndRateLimitIsRetried()
    {
        int delays = 0;
        var h = new FakeHandler((_, _, n) => FakeHandler.Json(n == 1
            ? "{\"error_code\":\"54003\",\"error_msg\":\"Invalid Access Limit\"}"
            : "{\"trans_result\":[{\"src\":\"a\",\"dst\":\"A\"}]}"));
        var p = new BaiduTranslateProvider(h.Client(), "id", "k", () => "1", (_, _) => { delays++; return Task.CompletedTask; });
        Assert.Equal(new[] { "A" }, await p.TranslateAsync(new[] { "a" }, null, "en", default));
        Assert.Equal(1, delays);

        var bad = new FakeHandler((_, _) => FakeHandler.Json("{\"error_code\":\"54001\",\"error_msg\":\"Invalid Sign\"}"));
        var p2 = new BaiduTranslateProvider(bad.Client(), "id", "k", () => "1");
        var ex = await Assert.ThrowsAsync<TranslationException>(() => p2.TranslateAsync(new[] { "a" }, null, "en", default));
        Assert.False(string.IsNullOrEmpty(ex.Message)); // localized text (code included) needs the app resources
    }

    [Theory]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-Hant", "cht")]
    [InlineData("ja", "jp")]
    [InlineData("ko", "kor")]
    [InlineData("fr", "fra")]
    [InlineData("es", "spa")]
    [InlineData("vi", "vie")]
    [InlineData("ar", "ara")]
    [InlineData("ms", "may")]
    public void LanguageCodes(string tag, string code) => Assert.Equal(code, BaiduTranslateProvider.LanguageCode(tag));
}

public class YoudaoProviderTests
{
    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("12345678901234567890", "12345678901234567890")]                       // exactly 20 → unchanged
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghij26qrstuvwxyz")]
    public void SignInput(string q, string expected) => Assert.Equal(expected, YoudaoTranslateProvider.SignInput(q));

    [Fact]
    public async Task V3Signature_AndResponseParsing()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json("{\"errorCode\":\"0\",\"translation\":[\"Good morning\\nThanks\"]}"));
        var p = new YoudaoTranslateProvider(h.Client(), "appKey1", "secret1", salt: () => "salt-1", unixSeconds: () => 1700000000);
        var result = await p.TranslateAsync(new[] { "早上好", "谢谢" }, null, "en", default);

        Assert.Equal(new[] { "Good morning", "Thanks" }, result);
        var form = HttpUtility.ParseQueryString(Assert.Single(h.Requests).Body);
        Assert.Equal("早上好\n谢谢", form["q"]);
        Assert.Equal("v3", form["signType"]);
        Assert.Equal("1700000000", form["curtime"]);
        Assert.Equal("salt-1", form["salt"]);
        Assert.Equal("en", form["to"]);
        Assert.Equal(Sha256("appKey1" + "早上好\n谢谢" + "salt-1" + "1700000000" + "secret1"), form["sign"]);
        Assert.DoesNotContain("secret1", h.Requests[0].Body);
    }

    [Fact]
    public async Task LineCountMismatch_FallsBackToPerLineRequests()
    {
        var h = new FakeHandler((_, body, n) =>
        {
            var q = HttpUtility.ParseQueryString(body)["q"];
            return FakeHandler.Json(n == 1
                ? "{\"errorCode\":\"0\",\"translation\":[\"merged into one line\"]}"
                : "{\"errorCode\":\"0\",\"translation\":[\"T:" + q + "\"]}");
        });
        var p = new YoudaoTranslateProvider(h.Client(), "k", "s", () => "x", () => 1);
        var result = await p.TranslateAsync(new[] { "a", "b" }, null, "zh-Hans", default);
        Assert.Equal(new[] { "T:a", "T:b" }, result);
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal("zh-CHS", HttpUtility.ParseQueryString(h.Requests[0].Body)["to"]);
    }

    [Fact]
    public async Task ErrorCode_BecomesException()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json("{\"errorCode\":\"202\"}"));
        var p = new YoudaoTranslateProvider(h.Client(), "k", "s");
        var ex = await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(new[] { "a" }, null, "en", default));
        Assert.False(string.IsNullOrEmpty(ex.Message));
    }
}

public class DeepLProviderTests
{
    [Theory]
    [InlineData("abc:fx", "https://api-free.deepl.com/v2/translate")]
    [InlineData("abc", "https://api.deepl.com/v2/translate")]
    public void Endpoint_ByKeySuffix(string key, string url) => Assert.Equal(url, DeepLTranslateProvider.EndpointFor(key).ToString());

    [Fact]
    public async Task SendsTextArray_WithAuthHeader()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json(
            "{\"translations\":[{\"detected_source_language\":\"EN\",\"text\":\"你好\"},{\"detected_source_language\":\"EN\",\"text\":\"世界\"}]}"));
        var p = new DeepLTranslateProvider(h.Client(), "key-123:fx");
        var result = await p.TranslateAsync(new[] { "Hello", "World" }, null, "zh-Hans", default);

        Assert.Equal(new[] { "你好", "世界" }, result);
        var r = Assert.Single(h.Requests);
        Assert.Equal("https://api-free.deepl.com/v2/translate", r.Uri.ToString());
        Assert.Equal("DeepL-Auth-Key key-123:fx", r.Auth);
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal("ZH-HANS", doc.RootElement.GetProperty("target_lang").GetString());
        Assert.Equal(new[] { "Hello", "World" }, doc.RootElement.GetProperty("text").EnumerateArray().Select(e => e.GetString()));
        Assert.False(doc.RootElement.TryGetProperty("source_lang", out _));
    }

    [Fact]
    public async Task UnsupportedTarget_FailsWithoutRequest()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json("{}"));
        var p = new DeepLTranslateProvider(h.Client(), "k");
        await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(new[] { "a" }, null, "ms", default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task QuotaExceeded_BecomesException()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json("{\"message\":\"Quota exceeded\"}", (HttpStatusCode)456));
        var p = new DeepLTranslateProvider(h.Client(), "k");
        await Assert.ThrowsAsync<TranslationException>(() => p.TranslateAsync(new[] { "a" }, null, "en", default));
    }
}

public class TranslationServiceTests
{
    [Fact]
    public async Task NotConfigured_NeverTouchesTheNetwork()
    {
        var h = new FakeHandler((_, _) => throw new InvalidOperationException("network must not be used"));
        var s = new TranslateSettings { Provider = "" };
        await Assert.ThrowsAsync<TranslationException>(() => TranslationService.TranslateAsync(new[] { "hello" }, settings: s, handler: h));
        Assert.Empty(h.Requests);
        Assert.False(TranslationService.IsConfigured(s));
    }

    [Fact]
    public async Task FallbackLanguage_IsUsedWhenTextIsAlreadyInTarget()
    {
        string? requestedTo = null;
        var h = new FakeHandler((_, body) =>
        {
            requestedTo = HttpUtility.ParseQueryString(body)["to"];
            return FakeHandler.Json("{\"trans_result\":[{\"src\":\"x\",\"dst\":\"Hello world\"}]}");
        });
        var s = new TranslateSettings
        {
            Provider = "baidu",
            TargetLanguage = "zh-Hans",
            FallbackLanguage = "en",
            Baidu = new BaiduTranslateSettings { AppId = "id", SecretProtected = SecretProtector.Protect("k") },
        };
        var outcome = await TranslationService.TranslateAsync(new[] { "你好世界，这是中文" }, settings: s, handler: h);
        Assert.Equal("en", outcome.TargetLanguage);
        Assert.Equal("en", requestedTo);
        Assert.Equal("zh-Hans", outcome.DetectedSource);
    }

    [Fact]
    public async Task ExplicitTarget_IsUsedAsIs()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Json("{\"translations\":[{\"text\":\"Bonjour\"}]}"));
        var s = new TranslateSettings { Provider = "deepl", DeepL = new DeepLTranslateSettings { ApiKeyProtected = SecretProtector.Protect("k:fx") } };
        var outcome = await TranslationService.TranslateAsync(new[] { "Hello" }, explicitTarget: "fr", settings: s, handler: h);
        Assert.Equal("fr", outcome.TargetLanguage);
        Assert.Equal(new[] { "Bonjour" }, outcome.Segments);
    }

    [Fact]
    public void SecretProtector_RoundTrip_AndGarbage()
    {
        var p = SecretProtector.Protect("sk-secret-123");
        Assert.StartsWith("dpapi:", p);
        Assert.DoesNotContain("sk-secret-123", p);
        Assert.Equal("sk-secret-123", SecretProtector.Unprotect(p));
        Assert.Equal("", SecretProtector.Unprotect("dpapi:AAAA"));
        Assert.Equal("", SecretProtector.Protect(""));
        Assert.Equal("legacy", SecretProtector.Unprotect("legacy"));
    }

    [Theory]
    [InlineData(TranslateProxyMode.Http, "127.0.0.1:7890", "http://127.0.0.1:7890/")]
    [InlineData(TranslateProxyMode.Socks5, "127.0.0.1:1080", "socks5://127.0.0.1:1080/")]
    [InlineData(TranslateProxyMode.Http, "http://proxy.local:8080", "http://proxy.local:8080/")]
    public void ProxyUri(TranslateProxyMode mode, string address, string expected) =>
        Assert.Equal(expected, TranslationHttp.BuildProxyUri(mode, address).ToString());

    [Fact]
    public void ProxyUri_Invalid() => Assert.Throws<TranslationException>(() => TranslationHttp.BuildProxyUri(TranslateProxyMode.Http, ""));

    [Fact]
    public void CreateClient_AppliesTimeout()
    {
        using var c = TranslationHttp.CreateClient(new TranslateSettings { TimeoutSeconds = 12, Proxy = TranslateProxyMode.Socks5, ProxyAddress = "127.0.0.1:1080" });
        Assert.Equal(TimeSpan.FromSeconds(12), c.Timeout);
    }
}
