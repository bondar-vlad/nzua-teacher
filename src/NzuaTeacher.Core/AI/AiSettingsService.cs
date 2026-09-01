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
        [AiProvider.Gemini] = "gemini-2.5-flash",
        [AiProvider.OpenAi] = "gpt-4o-mini",
        [AiProvider.Anthropic] = "claude-sonnet-4-5",
    };

    public static readonly IReadOnlyDictionary<AiProvider, string> ProviderNames = new Dictionary<AiProvider, string>
    {
        [AiProvider.Gemini] = "Google Gemini (є безоплатний тариф)",
        [AiProvider.OpenAi] = "OpenAI",
        [AiProvider.Anthropic] = "Anthropic Claude",
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
