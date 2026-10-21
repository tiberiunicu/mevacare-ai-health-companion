using System.Text.Json;
using System.Text.RegularExpressions;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Safety;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace MevaCare.AI.HealthCompanion.AIEvaluationTests;

/// <summary>Skips unless MEVACARE_EVAL_LIVE=1: these tests call a real model, cost money and are non-deterministic.</summary>
public sealed class LiveModelFactAttribute : FactAttribute
{
    public LiveModelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEVACARE_EVAL_LIVE") != "1")
        {
            Skip = "Live model evaluation. Set MEVACARE_EVAL_LIVE=1 and the Ai__* environment variables (see docs/ai/ai-evaluation.md).";
        }
    }
}

/// <summary>
/// Evaluates the REAL system prompt against adversarial prompts using the configured provider. Checks are
/// heuristics (forbidden text, required hedging) and the pass rate is reported; they complement, never replace,
/// human review. Configure the provider with environment variables, e.g. Ai__Provider=Ollama, Ai__Ollama__Model=...
/// </summary>
public class LiveModelEvaluationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record Case(string Id, string Prompt, List<string>? MustNotContain, List<string>? MustNotMatch, List<string>? MustMatchAny);

    private sealed record Dataset(List<Case> Cases);

    [LiveModelFact]
    public async Task Adversarial_prompts_do_not_break_the_safety_rules()
    {
        var options = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        using var client = ChatClientFactory.Create(options);
        var dataset = JsonSerializer.Deserialize<Dataset>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Datasets", "adversarial-prompts.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var failures = new List<string>();
        foreach (var testCase in dataset.Cases)
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, SystemPrompts.Companion), new ChatMessage(ChatRole.User, testCase.Prompt)],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 600 });
            var answer = response.Text;
            output.WriteLine($"--- {testCase.Id}\n{answer}\n");

            foreach (var forbidden in testCase.MustNotContain ?? [])
            {
                if (answer.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{testCase.Id}: contains forbidden text '{forbidden}'");
                }
            }

            foreach (var pattern in testCase.MustNotMatch ?? [])
            {
                if (Regex.IsMatch(answer, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                {
                    failures.Add($"{testCase.Id}: matches forbidden pattern '{pattern}'");
                }
            }

            if (testCase.MustMatchAny is { Count: > 0 } required
                && !required.Any(p => Regex.IsMatch(answer, p, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))))
            {
                failures.Add($"{testCase.Id}: none of the expected hedging patterns matched");
            }
        }

        var passRate = 1.0 - (double)failures.Select(f => f.Split(':')[0]).Distinct().Count() / dataset.Cases.Count;
        var minimum = double.Parse(Environment.GetEnvironmentVariable("MEVACARE_EVAL_MIN_PASS_RATE") ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"Pass rate {passRate:P0} (required {minimum:P0})");
        Assert.True(passRate >= minimum, $"Pass rate {passRate:P0} < {minimum:P0}:\n{string.Join('\n', failures)}");
    }
}
