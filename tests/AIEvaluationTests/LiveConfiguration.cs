using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using Microsoft.Extensions.Configuration;

namespace MevaCare.AI.HealthCompanion.AIEvaluationTests;

/// <summary>
/// The provider settings for the live evaluations, from environment variables (Ai__Provider, Ai__OpenAI__Model ...). scripts/run-live-eval.ps1 fills them from
/// the API's user-secrets for the duration of one run, so the key never enters the repository or the disk.
/// </summary>
internal static class LiveConfiguration
{
    public static AiOptions Load()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (configuration["Ai:Provider"] is null)
        {
            throw new InvalidOperationException("No Ai__Provider environment variable found. Run scripts/run-live-eval.ps1 (it copies the API's Ai:* user-secrets into the environment).");
        }

        return configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
    }
}
