using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using Microsoft.Extensions.AI;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;

/// <summary>
/// The one place that turns the application's generation settings into <see cref="ChatOptions"/>, so every model call (chat, structured extraction,
/// explanations) respects what the configured model allows: no temperature for reasoning models, extra output budget for hidden reasoning tokens,
/// and an optional reasoning effort.
/// </summary>
public static class ChatOptionsFactory
{
    public static ChatOptions Create(AiOptions ai, float? temperature = null, int? maxOutputTokens = null)
    {
        var options = new ChatOptions { MaxOutputTokens = (maxOutputTokens ?? ai.MaxOutputTokens) + ai.ReasoningTokenAllowance };
        if (ai.SendTemperature)
        {
            options.Temperature = temperature ?? ai.Temperature;
        }

        if (ai.Provider == AiProvider.OpenAI && TryParseEffort(ai.OpenAI.ReasoningEffort, out var effort))
        {
            options.Reasoning = new ReasoningOptions { Effort = effort };
        }

        return options;
    }

    internal static bool TryParseEffort(string? text, out ReasoningEffort effort)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "none":
                effort = ReasoningEffort.None;
                return true;
            case "low":
                effort = ReasoningEffort.Low;
                return true;
            case "medium":
                effort = ReasoningEffort.Medium;
                return true;
            case "high":
                effort = ReasoningEffort.High;
                return true;
            default:
                effort = default;
                return false;
        }
    }
}
