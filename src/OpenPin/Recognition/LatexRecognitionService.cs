using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using OpenPin.Core;
using OpenPin.Services;
using OpenPin.Translate;

namespace OpenPin.Recognition;

/// <summary>
/// Result of a LaTeX formula recognition attempt.
/// </summary>
public sealed record LatexRecognitionResult(
    string Latex,
    string Source, // "ai_vision", "ai_chat", or "ocr"
    bool Success,
    string? ErrorMessage = null
);

/// <summary>
/// Recognition service for mathematical formulas (SPEC PRO-05).
/// If an AI service (DeepSeek / OpenAI compatible) is configured, extracts LaTeX via visual multimodal
/// or AI text correction. If not configured or offline, falls back to Windows system OCR + heuristic post-processing.
/// </summary>
public static class LatexRecognitionService
{
    public static async Task<LatexRecognitionResult> RecognizeAsync(
        BitmapSource image,
        string? ocrLanguage = null,
        CancellationToken ct = default,
        HttpMessageHandler? testHttpHandler = null,
        TranslateSettings? settings = null)
    {
        // 1. Try AI recognition if an OpenAI-compatible service is configured
        var s = settings ?? AppServices.Settings?.Current.Translate ?? TranslationService.Settings;
        if (s != null && s.Provider == "openai" && !string.IsNullOrWhiteSpace(s.OpenAi.BaseUrl))
        {
            try
            {
                var aiResult = await TryAiRecognitionAsync(image, s, ocrLanguage, ct, testHttpHandler).ConfigureAwait(false);
                if (aiResult is { Success: true } && !string.IsNullOrWhiteSpace(aiResult.Latex))
                {
                    Log.Info($"LaTeX recognized via {aiResult.Source}: {aiResult.Latex}");
                    return aiResult;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"AI LaTeX recognition attempt failed: {ex.Message}. Falling back to OCR.");
            }
        }

        // 2. Fall back to offline System OCR + post-processing
        return await RecognizeWithOcrAsync(image, ocrLanguage, ct).ConfigureAwait(false);
    }

    private static async Task<LatexRecognitionResult?> TryAiRecognitionAsync(
        BitmapSource image,
        TranslateSettings s,
        string? ocrLanguage,
        CancellationToken ct,
        HttpMessageHandler? testHttpHandler)
    {
        using var http = testHttpHandler != null
            ? new HttpClient(testHttpHandler, disposeHandler: false)
            : TranslationHttp.CreateClient(s);

        var endpoint = OpenAiCompatibleProvider.ChatEndpoint(s.OpenAi.BaseUrl);
        string? apiKey = SecretProtector.Unprotect(s.OpenAi.ApiKeyProtected);
        string model = string.IsNullOrWhiteSpace(s.OpenAi.Model) ? "deepseek-chat" : s.OpenAi.Model.Trim();

        // --- Mode A: Visual multimodal extraction ---
        try
        {
            var pngBytes = ImageEncoding.Encode(image, ImageFileFormat.Png, 95);
            var base64 = Convert.ToBase64String(pngBytes);

            var visionPayload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = new object[]
                {
                    new Dictionary<string, string>
                    {
                        ["role"] = "system",
                        ["content"] = "You are an expert mathematical formula recognizer. Convert the formula shown in the image to clean, standard LaTeX. Output ONLY the raw LaTeX formula code, with no markdown code blocks, no surrounding dollar signs, and no extra text or explanations."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, string> { ["type"] = "text", ["text"] = "Convert the mathematical formula in this image to LaTeX:" },
                            new Dictionary<string, object> { ["type"] = "image_url", ["image_url"] = new Dictionary<string, string> { ["url"] = $"data:image/png;base64,{base64}" } }
                        }
                    }
                },
                ["temperature"] = 0.1,
                ["stream"] = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(visionPayload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var content = OpenAiCompatibleProvider.ParseContent(body);
                var clean = LatexCleaner.Clean(content);
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    return new LatexRecognitionResult(clean, "ai_vision", Success: true);
                }
            }
            else
            {
                Log.Info($"Vision multimodal returned {(int)resp.StatusCode}, falling back to OCR + AI chat");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Vision multimodal failed: {ex.Message}, falling back to OCR + AI chat");
        }

        // --- Mode B: OCR + AI text chat correction ---
        var ocr = await OcrService.RecognizeAsync(image, ocrLanguage, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ocr.Text))
        {
            try
            {
                var chatPayload = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, string>
                        {
                            ["role"] = "system",
                            ["content"] = "You are an expert mathematical formula converter. The user provides OCR recognized text representing a math formula or equation. Correct any OCR mistakes, reconstruct the mathematical structure, and convert it into clean, standard LaTeX formula. Output ONLY the raw LaTeX formula. Do not include markdown formatting or explanations."
                        },
                        new Dictionary<string, string>
                        {
                            ["role"] = "user",
                            ["content"] = ocr.Text
                        }
                    },
                    ["temperature"] = 0.1,
                    ["stream"] = false
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(chatPayload), Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var content = OpenAiCompatibleProvider.ParseContent(body);
                    var clean = LatexCleaner.Clean(content);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        return new LatexRecognitionResult(clean, "ai_chat", Success: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"AI text chat completion failed: {ex.Message}");
            }
        }

        return null;
    }

    private static async Task<LatexRecognitionResult> RecognizeWithOcrAsync(
        BitmapSource image,
        string? ocrLanguage,
        CancellationToken ct)
    {
        var ocr = await OcrService.RecognizeAsync(image, ocrLanguage, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(ocr.Error))
        {
            return new LatexRecognitionResult("", "ocr", Success: false, ErrorMessage: ocr.Error);
        }
        if (string.IsNullOrWhiteSpace(ocr.Text))
        {
            return new LatexRecognitionResult("", "ocr", Success: false, ErrorMessage: Loc.Get("Recognition.Latex.NoFormula"));
        }

        var converted = OcrToLatexConverter.Convert(ocr.Text);
        var clean = LatexCleaner.Clean(converted);
        return new LatexRecognitionResult(clean, "ocr", Success: !string.IsNullOrWhiteSpace(clean));
    }
}
