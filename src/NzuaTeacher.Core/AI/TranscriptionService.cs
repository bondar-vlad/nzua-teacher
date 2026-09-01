using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace NzuaTeacher.Core.AI;

/// <summary>Розшифровка голосу в текст. Gemini — нативний REST (працює на безоплатному тарифі), OpenAI — Whisper.</summary>
public sealed class TranscriptionService(AiSettingsService settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Повертає розшифрований текст або кидає виняток із зрозумілим повідомленням.</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string mimeType, CancellationToken ct = default)
    {
        var provider = settings.TranscriptionProvider;
        if (provider == AiProvider.Anthropic)
            provider = AiProvider.Gemini; // Anthropic не має ASR

        var key = await settings.GetApiKeyAsync(provider);
        if (string.IsNullOrWhiteSpace(key))
        {
            // Фолбек: будь-який доступний ключ Gemini/OpenAI.
            foreach (var p in new[] { AiProvider.Gemini, AiProvider.OpenAi })
            {
                key = await settings.GetApiKeyAsync(p);
                if (!string.IsNullOrWhiteSpace(key)) { provider = p; break; }
            }
        }
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Для голосового вводу додайте ключ Gemini або OpenAI у Налаштуваннях.");

        return provider switch
        {
            AiProvider.Gemini => await TranscribeGemini(key, audio, mimeType, ct),
            _ => await TranscribeOpenAi(key, audio, mimeType, ct),
        };
    }

    private async Task<string> TranscribeGemini(string key, byte[] audio, string mimeType, CancellationToken ct)
    {
        var model = settings.GetModel(AiProvider.Gemini);
        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = "Розшифруй це аудіо українською мовою. Поверни лише текст без коментарів." },
                        new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(audio) } },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        request.Headers.Add("x-goog-api-key", key);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini: HTTP {(int)response.StatusCode}. {Truncate(json, 300)}");

        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var candidates))
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content) ||
                    !content.TryGetProperty("parts", out var parts)) continue;
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
            }
        }
        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : throw new InvalidOperationException("Gemini не повернув текст розшифровки.");
    }

    private static async Task<string> TranscribeOpenAi(string key, byte[] audio, string mimeType, CancellationToken ct)
    {
        var stt = new OpenAIClient(new ApiKeyCredential(key))
            .GetAudioClient("whisper-1")
            .AsISpeechToTextClient();
        var response = await stt.GetTextAsync(
            new DataContent(audio, mimeType),
            new SpeechToTextOptions { TextLanguage = "uk" },
            cancellationToken: ct);
        var text = response.Text?.Trim();
        return string.IsNullOrEmpty(text)
            ? throw new InvalidOperationException("OpenAI не повернув текст розшифровки.")
            : text;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
