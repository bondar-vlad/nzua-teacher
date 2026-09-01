using System.ClientModel;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using OpenAI;

namespace NzuaTeacher.Core.AI;

/// <summary>IChatClient для обраного провайдера. Gemini працює через OpenAI-сумісний ендпоінт.</summary>
public static class ChatClientFactory
{
    public static IChatClient Create(AiProviderConfig config, bool withFunctionInvocation = true)
    {
        IChatClient inner = config.Provider switch
        {
            AiProvider.OpenAi => new OpenAIClient(new ApiKeyCredential(config.ApiKey))
                .GetChatClient(config.Model)
                .AsIChatClient(),

            AiProvider.Gemini => new OpenAIClient(
                    new ApiKeyCredential(config.ApiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(AiSettingsService.GeminiOpenAiEndpoint) })
                .GetChatClient(config.Model)
                .AsIChatClient(),

            AiProvider.Anthropic => new AnthropicClient(new APIAuthentication(config.ApiKey)).Messages,

            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };

        if (!withFunctionInvocation)
            return inner;

        return new ChatClientBuilder(inner)
            .UseFunctionInvocation()
            .Build();
    }
}
