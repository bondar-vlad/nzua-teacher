using NzuaTeacher.Core.Abstractions;

namespace NzuaTeacher.Core.AI;

public enum AiProvider
{
    Gemini = 0,
    OpenAi = 1,
    Anthropic = 2,
}

public sealed record AiProviderConfig(AiProvider Provider, string Model, string ApiKey);

/// <summary>Налаштування AI: ключі в захищеному сховищі, вибір провайдера/моделі — у преференсах.</summary>
public sealed class AiSettingsService(ISecretStore secrets, IAppPrefs prefs)
{
    public const string GeminiOpenAiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public static readonly IReadOnlyDictionary<AiProvider, string> DefaultModels = new Dictionary<AiProvider, string>
    {
        [AiProvider.Gemini] = "gemini-3.5-flash",
        [AiProvider.OpenAi] = "gpt-5.6-luna",
        [AiProvider.Anthropic] = "claude-sonnet-5",
    };

    /// <summary>Перевірені актуальні моделі (вересень 2026) для вибору в налаштуваннях.</summary>
    public static readonly IReadOnlyDictionary<AiProvider, IReadOnlyList<(string Id, string Label)>> KnownModels =
        new Dictionary<AiProvider, IReadOnlyList<(string, string)>>
        {
            [AiProvider.Gemini] =
            [
                ("gemini-3.5-flash", "Gemini 3.5 Flash — рекомендована, є безоплатний тариф"),
                ("gemini-3.7-flash", "Gemini 3.7 Flash — найновіша Flash, є безоплатний тариф"),
                ("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite — найшвидша й найдешевша"),
                ("gemini-3.1-pro-preview", "Gemini 3.1 Pro — найрозумніша (лише платно)"),
                ("gemini-2.5-flash", "Gemini 2.5 Flash — стабільна попередня"),
            ],
            [AiProvider.OpenAi] =
            [
                ("gpt-5.6-luna", "GPT-5.6 Luna — швидка й недорога (рекомендована)"),
                ("gpt-5.6-terra", "GPT-5.6 Terra — збалансована"),
                ("gpt-5.6-sol", "GPT-5.6 Sol — найпотужніша"),
                ("gpt-5.4-mini", "GPT-5.4 mini — бюджетна"),
                ("gpt-5-mini", "GPT-5 mini — старіша бюджетна"),
            ],
            [AiProvider.Anthropic] =
            [
                ("claude-sonnet-5", "Claude Sonnet 5 — швидкість + якість (рекомендована)"),
                ("claude-haiku-4-5", "Claude Haiku 4.5 — найшвидша й найдешевша"),
                ("claude-opus-5", "Claude Opus 5 — для складних задач"),
                ("claude-fable-5", "Claude Fable 5 — найпотужніша"),
            ],
        };

    /// <summary>Сторінки, де копіювати API-ключі.</summary>
    public static readonly IReadOnlyDictionary<AiProvider, string> KeyPageUrls = new Dictionary<AiProvider, string>
    {
        [AiProvider.Gemini] = "https://aistudio.google.com/apikey",
        [AiProvider.OpenAi] = "https://platform.openai.com/api-keys",
        [AiProvider.Anthropic] = "https://console.anthropic.com/settings/keys",
    };

    public static readonly IReadOnlyDictionary<AiProvider, string> ProviderNames = new Dictionary<AiProvider, string>
    {
        [AiProvider.Gemini] = "Google Gemini — є безоплатний тариф",
        [AiProvider.OpenAi] = "OpenAI — платно",
        [AiProvider.Anthropic] = "Anthropic Claude — платно",
    };

    private static string KeyName(AiProvider p) => $"apikey:{p}";

    public AiProvider ActiveProvider
    {
        get => Enum.TryParse<AiProvider>(prefs.Get("ai.provider", nameof(AiProvider.Gemini)), out var p) ? p : AiProvider.Gemini;
        set => prefs.Set("ai.provider", value.ToString());
    }

    public string GetModel(AiProvider provider) =>
        prefs.Get($"ai.model.{provider}", DefaultModels[provider]);

    public void SetModel(AiProvider provider, string model) =>
        prefs.Set($"ai.model.{provider}", string.IsNullOrWhiteSpace(model) ? DefaultModels[provider] : model.Trim());

    public Task<string?> GetApiKeyAsync(AiProvider provider) => secrets.GetAsync(KeyName(provider));

    public Task SetApiKeyAsync(AiProvider provider, string key) => secrets.SetAsync(KeyName(provider), key.Trim());

    public void RemoveApiKey(AiProvider provider) => secrets.Remove(KeyName(provider));

    /// <summary>Показувати реальні імена учнів у даних для LLM (за замовчуванням — псевдоніми).</summary>
    public bool ShowRealNamesInChat
    {
        get => prefs.GetBool("ai.showRealNames", false);
        set
        {
            prefs.SetBool("ai.showRealNames", value);
            // NzuaPrivacy читає env-змінну динамічно — діє одразу на MCP-тули in-process.
            Environment.SetEnvironmentVariable("NZUA_SHOW_REAL_NAMES", value ? "true" : "false");
        }
    }

    public void ApplyPrivacyEnv() =>
        Environment.SetEnvironmentVariable("NZUA_SHOW_REAL_NAMES", ShowRealNamesInChat ? "true" : "false");

    /// <summary>Провайдер для розшифровки голосу (Anthropic не має ASR — беремо Gemini/OpenAI).</summary>
    public AiProvider TranscriptionProvider
    {
        get => Enum.TryParse<AiProvider>(prefs.Get("ai.transcription", nameof(AiProvider.Gemini)), out var p) ? p : AiProvider.Gemini;
        set => prefs.Set("ai.transcription", value.ToString());
    }

    public async Task<AiProviderConfig?> GetActiveConfigAsync()
    {
        var provider = ActiveProvider;
        var key = await GetApiKeyAsync(provider);
        if (string.IsNullOrWhiteSpace(key)) return null;
        return new AiProviderConfig(provider, GetModel(provider), key);
    }
}
