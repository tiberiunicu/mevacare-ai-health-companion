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

    /// <summary>Upper bound on previous messages sent with a new question.</summary>
    [Range(0, 100)] public int HistoryMessageLimit { get; init; } = 20;

    /// <summary>Upper bound on the characters of history sent with a new question (keeps cost and context predictable).</summary>
    [Range(0, 100_000)] public int HistoryCharacterBudget { get; init; } = 12_000;

    [Range(5, 600)] public int RequestTimeoutSeconds { get; init; } = 90;

    /// <summary>Lets the model call backend tools. Turn off for models without tool-calling support.</summary>
    public bool ToolsEnabled { get; init; } = true;

    /// <summary>Upper bound on model/tool round trips within one answer.</summary>
    [Range(1, 10)] public int MaxToolIterations { get; init; } = 6;
}

public sealed class OpenAiOptions
{
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
