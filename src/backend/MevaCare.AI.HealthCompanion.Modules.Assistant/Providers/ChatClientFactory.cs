using System.ClientModel;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;

/// <summary>
/// The only place that knows about concrete LLM providers. Everything else in the application talks to the
/// provider-neutral <see cref="IChatClient"/> abstraction from Microsoft.Extensions.AI.
/// A missing configuration is reported lazily (HTTP 503 "ai_not_configured") so the rest of the application
/// keeps working without any AI provider.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(AiOptions options) => options.Provider switch
    {
        AiProvider.OpenAI => CreateOpenAi(options.OpenAI, TimeSpan.FromSeconds(options.RequestTimeoutSeconds)),
        AiProvider.Ollama => CreateOllama(options.Ollama, TimeSpan.FromSeconds(options.RequestTimeoutSeconds)),
        _ => throw new ServiceUnavailableException("ai_not_configured", "No AI provider is configured."),
    };

    /// <summary>Name stored with each assistant message so answers stay attributable after a provider switch.</summary>
    public static string ModelOf(AiOptions options) => options.Provider switch
    {
        AiProvider.OpenAI => options.OpenAI.Model,
        AiProvider.Ollama => options.Ollama.Model,
        _ => "",
    };

    private static IChatClient CreateOpenAi(OpenAiOptions options, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ServiceUnavailableException(
                "ai_not_configured", "The OpenAI provider needs Ai:OpenAI:ApiKey and Ai:OpenAI:Model (see README.md).");
        }

        var clientOptions = new OpenAIClientOptions { NetworkTimeout = timeout };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(options.Endpoint);
        }

        return new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions)
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    private static IChatClient CreateOllama(OllamaOptions options, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ServiceUnavailableException(
                "ai_not_configured", "The Ollama provider needs Ai:Ollama:Endpoint and Ai:Ollama:Model (see README.md).");
        }

        return new OllamaApiClient(new HttpClient { BaseAddress = new Uri(options.Endpoint), Timeout = timeout }, options.Model);
    }
}
