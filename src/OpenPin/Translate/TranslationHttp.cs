using System.Net;
using System.Net.Http;
using System.Text.Json;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>HttpClient creation (timeout + proxy) and request helpers shared by the providers.</summary>
internal static class TranslationHttp
{
    /// <summary>
    /// A client honouring <see cref="TranslateSettings.Proxy"/> and <see cref="TranslateSettings.TimeoutSeconds"/>.
    /// The caller owns (and disposes) it.
    /// </summary>
    public static HttpClient CreateClient(TranslateSettings s)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        switch (s.Proxy)
        {
            case TranslateProxyMode.None:
                handler.UseProxy = false;
                break;
            case TranslateProxyMode.Http:
            case TranslateProxyMode.Socks5:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(BuildProxyUri(s.Proxy, s.ProxyAddress));
                break;
            default:
                handler.UseProxy = true; // Proxy == null → system (WinINet / env) proxy
                break;
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 5, 300)),
        };
    }

    /// <summary>"127.0.0.1:7890" → http://127.0.0.1:7890 or socks5://127.0.0.1:7890; full URIs are kept.</summary>
    public static Uri BuildProxyUri(TranslateProxyMode mode, string? address)
    {
        var a = (address ?? "").Trim();
        if (a.Length == 0) throw new TranslationException(Loc.Get("Translate.Error.ProxyAddress"));
        if (!a.Contains("://", StringComparison.Ordinal)) a = (mode == TranslateProxyMode.Socks5 ? "socks5://" : "http://") + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            throw new TranslationException(Loc.Get("Translate.Error.ProxyAddress"));
        return uri;
    }

    /// <summary>
    /// Send and return the response body. Non-success status, network errors and timeouts become
    /// <see cref="TranslationException"/>s; cancellation through <paramref name="ct"/> stays an OperationCanceledException.
    /// </summary>
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, string providerName, CancellationToken ct,
        Func<int, string, string?>? describeError = null)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                var detail = describeError?.Invoke(code, body) ?? Summarize(body);
                throw new TranslationException(Loc.Format("Translate.Error.Http", providerName, code, detail));
            }
            return body;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TranslationException) { throw; }
        catch (TaskCanceledException ex)
        {
            throw new TranslationException(Loc.Format("Translate.Error.Timeout", providerName), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(Loc.Format("Translate.Error.Network", providerName, ex.Message), ex);
        }
    }

    /// <summary>Short single-line description of an error body: a JSON "message"/"error_msg" if present, else the text.</summary>
    public static string Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (FindMessage(doc.RootElement) is { Length: > 0 } m) return Truncate(m);
        }
        catch (JsonException) { /* not JSON */ }
        return Truncate(body);
    }

    private static string? FindMessage(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "message", "error_msg", "msg", "detail" })
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        if (e.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String) return err.GetString();
            return FindMessage(err);
        }
        return null;
    }

    private static string Truncate(string s)
    {
        s = string.Join(' ', s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    /// <summary>Lowercase hex of a hash.</summary>
    public static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>Split a segment into lines (\r\n / \n), used by line-based providers.</summary>
    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
