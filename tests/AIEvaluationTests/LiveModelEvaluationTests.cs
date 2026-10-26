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
/// human review. Run it with scripts/run-live-eval.ps1 (uses the API's user-secrets from scripts/setup-ai.ps1) or set environment variables, e.g. Ai__Provider=Ollama, Ai__Ollama__Model=...
/// </summary>
public class LiveModelEvaluationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record Case(string Id, string Prompt, List<string>? MustNotContain, List<string>? MustNotMatch, List<string>? MustMatchAny);

    private sealed record Dataset(List<Case> Cases);

    [LiveModelFact]
    public async Task Adversarial_prompts_do_not_break_the_safety_rules()
    {
        var options = LiveConfiguration.Load();
        using var client = ChatClientFactory.Create(options);
        var dataset = JsonSerializer.Deserialize<Dataset>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Datasets", "adversarial-prompts.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var failures = new List<string>();
        var blocked = new List<string>();
        foreach (var testCase in dataset.Cases)
        {
            string answer;
            try
            {
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, SystemPrompts.Companion), new ChatMessage(ChatRole.User, testCase.Prompt)],
                    ChatOptionsFactory.Create(options, temperature: 0, maxOutputTokens: 600));
                // Models answer with typographic apostrophes (can’t); the dataset patterns use plain ones (can't).
                answer = response.Text.Replace('’', '\'').Replace('‘', '\'');
            }
            catch (Exception ex) when (ProviderFailure.IsContentFilter(ex))
            {
                // The provider's own filter (for example Azure's jailbreak shield) refused the prompt before the model saw it. That is a safe outcome,
                // but it does not test OUR system prompt, so it is counted and reported separately.
                blocked.Add(testCase.Id);
                output.WriteLine($"--- {testCase.Id}\n[blocked by the provider's content filter]\n");
                continue;
            }

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

        output.WriteLine($"Blocked by the provider's content filter before reaching the model: {blocked.Count} of {dataset.Cases.Count} ({string.Join(", ", blocked)})");
        Assert.True(blocked.Count < dataset.Cases.Count, "Every prompt was blocked by the provider's filter, so the system prompt was not evaluated at all.");
        var passRate = 1.0 - (double)failures.Select(f => f.Split(':')[0]).Distinct().Count() / dataset.Cases.Count;
        var minimum = double.Parse(Environment.GetEnvironmentVariable("MEVACARE_EVAL_MIN_PASS_RATE") ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"Pass rate {passRate:P0} (required {minimum:P0})");
        Assert.True(passRate >= minimum, $"Pass rate {passRate:P0} < {minimum:P0}:\n{string.Join('\n', failures)}");
    }
}
