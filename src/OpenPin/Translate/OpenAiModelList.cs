using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>
/// Lists the models of an OpenAI-compatible server: <c>GET {base}/models</c> → <c>{"data":[{"id":"…"}]}</c>.
/// Works with DeepSeek, Qwen (DashScope compatible mode), Moonshot/Kimi, Ollama (/v1/models), LM Studio, OpenAI…
/// Called only when the user asks for it in the settings window.
/// </summary>
public static class OpenAiModelList
{
    /// <summary>"https://api.deepseek.com/v1" or ".../v1/chat/completions" → ".../v1/models".</summary>
    public static Uri ModelsEndpoint(string? baseUrl)
    {
        var chat = OpenAiCompatibleProvider.ChatEndpoint(baseUrl).ToString(); // validates + normalizes
        var root = chat[..^"/chat/completions".Length];
        return new Uri(root + "/models");
    }

    /// <summary>Model ids sorted and prioritized for chat/translation.</summary>
    public static async Task<IReadOnlyList<string>> FetchAsync(HttpClient http, string? baseUrl, string? apiKey, CancellationToken ct)
    {
        var endpoint = ModelsEndpoint(baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound && (baseUrl ?? "").Contains("11434"))
        {
            // Ollama fallback: try /api/tags if /models returns 404
            var fallbackUri = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/api/tags");
            using var fallbackReq = new HttpRequestMessage(HttpMethod.Get, fallbackUri);
            using var fallbackResp = await http.SendAsync(fallbackReq, ct).ConfigureAwait(false);
            if (fallbackResp.IsSuccessStatusCode)
            {
                var tagBody = await fallbackResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(tagBody);
            }
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TranslationException(Loc.Format("Translate.Settings.ModelsHttpError", (int)response.StatusCode, Shorten(body)));
        return Parse(body);
    }

    /// <summary>Parses the OpenAI list format; also accepts a bare array of objects/strings. Throws on malformed JSON.</summary>
    public static IReadOnlyList<string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement list = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var d) ? d
            : root.TryGetProperty("models", out var m) ? m
            : default;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                string? id = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("id", out var i) => i.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("name", out var n) => n.GetString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
            }
        }
        return SortAndFilter(ids);
    }

    /// <summary>Filters out non-chat models (embeddings, audio, rerankers) when possible, sorting preferred chat models first.</summary>
    public static IReadOnlyList<string> SortAndFilter(IEnumerable<string> models)
    {
        var list = models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var filtered = list.Where(m => !IsNonChatModel(m)).ToList();
        var effective = filtered.Count > 0 ? filtered : list;
        return effective
            .OrderBy(m => ModelPriority(m))
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int ModelPriority(string id)
    {
        var lower = id.ToLowerInvariant();
        if (IsReasoningModel(lower)) return 3;
        if (IsFastChatModel(lower)) return 0;
        if (IsGeneralChatModel(lower)) return 1;
        return 2;
    }

    public static bool IsReasoningModel(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var lower = id.ToLowerInvariant();
        return lower.Contains("reasoner") || lower.Contains("-r1") || lower.Contains("/r1") ||
               lower.Contains("deepseek-r1") || lower.Contains("qwq") || lower.Contains("o1-") ||
               lower.Contains("o3-") || lower.Contains("thinking");
    }

    private static bool IsFastChatModel(string lower)
    {
        return (lower.Contains("deepseek-chat") || lower.Contains("deepseek-v3") || lower.Contains("-v3") || lower.Contains("/v3") ||
                lower.Contains("turbo") || lower.Contains("flash") || lower.Contains("mini") ||
                lower.Contains("speed") || lower.Contains("plus") || lower.Contains("lite")) && !IsReasoningModel(lower);
    }

    private static bool IsGeneralChatModel(string lower)
    {
        return (lower.Contains("chat") || lower.Contains("instruct") || lower.Contains("deepseek") ||
                lower.Contains("qwen") || lower.Contains("glm") || lower.Contains("moonshot") ||
                lower.Contains("doubao") || lower.Contains("claude") || lower.Contains("gpt-4o")) && !IsReasoningModel(lower);
    }

    private static bool IsNonChatModel(string id)
    {
        var lower = id.ToLowerInvariant();
        return lower.Contains("embed") || lower.Contains("rerank") || lower.Contains("whisper")
            || lower.Contains("tts") || lower.Contains("audio") || lower.Contains("dall-e")
            || lower.Contains("flux") || lower.Contains("stable-diffusion") || lower.Contains("bge-")
            || lower.Contains("moderation");
    }

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
