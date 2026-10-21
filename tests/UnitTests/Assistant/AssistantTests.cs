using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Features;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Safety;

namespace MevaCare.AI.HealthCompanion.UnitTests.Assistant;

public class ChatClientFactoryTests
{
    [Fact]
    public void OpenAI_without_key_or_model_reports_a_configuration_problem_instead_of_crashing()
    {
        var ex = Assert.Throws<ServiceUnavailableException>(() =>
            ChatClientFactory.Create(new AiOptions { Provider = AiProvider.OpenAI }));

        Assert.Equal("ai_not_configured", ex.Code);
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public void OpenAI_error_message_names_the_missing_settings_but_never_contains_a_key()
    {
        var ex = Assert.Throws<ServiceUnavailableException>(() =>
            ChatClientFactory.Create(new AiOptions { Provider = AiProvider.OpenAI, OpenAI = new OpenAiOptions { ApiKey = "sk-test-key" } }));

        Assert.Contains("Ai:OpenAI:Model", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ollama_without_a_model_reports_a_configuration_problem()
    {
        var ex = Assert.Throws<ServiceUnavailableException>(() =>
            ChatClientFactory.Create(new AiOptions { Provider = AiProvider.Ollama }));

        Assert.Equal("ai_not_configured", ex.Code);
    }

    [Theory]
    [InlineData(AiProvider.OpenAI)]
    [InlineData(AiProvider.Ollama)]
    public void A_fully_configured_provider_produces_a_client_without_contacting_the_network(AiProvider provider)
    {
        var options = new AiOptions
        {
            Provider = provider,
            OpenAI = new OpenAiOptions { ApiKey = "test-key", Model = "test-model" },
            Ollama = new OllamaOptions { Endpoint = "http://localhost:1", Model = "test-model" },
        };

        using var client = ChatClientFactory.Create(options);

        Assert.NotNull(client);
        Assert.Equal("test-model", ChatClientFactory.ModelOf(options));
    }
}

public class SendMessageRequestValidatorTests
{
    private readonly SendMessageRequestValidator _validator = new();

    [Fact]
    public void A_normal_message_is_valid() =>
        Assert.True(_validator.Validate(new SendMessageRequest(null, "Hello", null)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    public void Blank_messages_are_invalid(string text) =>
        Assert.False(_validator.Validate(new SendMessageRequest(null, text, null)).IsValid);

    [Fact]
    public void The_length_limit_is_enforced()
    {
        Assert.True(_validator.Validate(new SendMessageRequest(null, new string('a', SendMessageRequestValidator.MaxMessageLength), null)).IsValid);
        Assert.False(_validator.Validate(new SendMessageRequest(null, new string('a', SendMessageRequestValidator.MaxMessageLength + 1), null)).IsValid);
    }
}

public class SystemPromptTests
{
    [Fact]
    public void The_system_prompt_states_the_non_negotiable_safety_rules()
    {
        var prompt = SystemPrompts.Companion;

        Assert.Contains("NOT a doctor", prompt, StringComparison.Ordinal);
        Assert.Contains("112", prompt, StringComparison.Ordinal);
        Assert.Contains("untrusted", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO access to the user's personal data", prompt, StringComparison.Ordinal);
        Assert.Contains(SystemPrompts.Canary, prompt, StringComparison.Ordinal);
    }
}
