using System.ComponentModel.DataAnnotations;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Options;

public enum AiProvider
{
    OpenAI = 1,
    Ollama = 2,
}

/// <summary>
/// Everything provider-specific lives here and in <see cref="Providers.ChatClientFactory"/>. Model names, endpoints
/// and keys are configuration, never code. The OpenAI key must come from user-secrets or an environment variable.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AiProvider Provider { get; init; } = AiProvider.Ollama;
    public OpenAiOptions OpenAI { get; init; } = new();
    public OllamaOptions Ollama { get; init; } = new();

    [Range(64, 8192)] public int MaxOutputTokens { get; init; } = 800;
    [Range(0, 2)] public float Temperature { get; init; } = 0.3f;

    /// <summary>Reasoning models (GPT-5 and GPT-6 families, o-series) reject any temperature other than their default: set false to omit the parameter.</summary>
    public bool SendTemperature { get; init; } = true;

    /// <summary>
    /// Output tokens added to every request's limit for hidden reasoning ("thinking") tokens, which count against it. Leave 0 for models that do not reason
    /// or when ReasoningEffort is "none"; otherwise an answer can be cut off or empty.
    /// </summary>
    [Range(0, 32000)] public int ReasoningTokenAllowance { get; init; }

    /// <summary>Upper bound on previous messages sent with a new question.</summary>
    [Range(0, 100)] public int HistoryMessageLimit { get; init; } = 20;

    /// <summary>Upper bound on the characters of history sent with a new question (keeps cost and context predictable).</summary>
    [Range(0, 100_000)] public int HistoryCharacterBudget { get; init; } = 12_000;

    [Range(5, 600)] public int RequestTimeoutSeconds { get; init; } = 90;

    /// <summary>
    /// Development aid: also logs the provider's error text (for example "Unsupported parameter: ...") when a call fails. Off by default because
    /// provider messages can echo request details; the text is cut to 400 characters and never sent to the client.
    /// </summary>
    public bool LogProviderErrorDetails { get; init; }

    /// <summary>Lets the model call backend tools. Turn off for models without tool-calling support.</summary>
    public bool ToolsEnabled { get; init; } = true;

    /// <summary>Upper bound on model/tool round trips within one answer.</summary>
    [Range(1, 10)] public int MaxToolIterations { get; init; } = 6;
}

public enum OpenAiApi
{
    /// <summary>/chat/completions: widest compatibility (OpenAI-compatible gateways, local test doubles).</summary>
    ChatCompletions = 1,

    /// <summary>/responses: the API newer models are built for; some of them only allow function tools with reasoning here.</summary>
    Responses = 2,
}

public sealed class OpenAiOptions
{
    public OpenAiApi Api { get; init; } = OpenAiApi.ChatCompletions;

    /// <summary>
    /// Optional reasoning effort sent with every request: none, low, medium, high. Empty = the model's default. Some reasoning models refuse function tools
    /// on /chat/completions unless this is "none" (or the Responses API is used).
    /// </summary>
    public string ReasoningEffort { get; init; } = "";

    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";

    /// <summary>Optional override for OpenAI-compatible gateways (Azure OpenAI proxies, local test doubles).</summary>
    public string Endpoint { get; init; } = "";
}

public sealed class OllamaOptions
{
    public string Endpoint { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "";
}
