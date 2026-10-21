using System.Text.Json;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Tools;
using Microsoft.Extensions.Configuration;

namespace MevaCare.AI.HealthCompanion.AIEvaluationTests;

/// <summary>
/// Evaluates structured meal extraction. The dataset is validated everywhere; the evaluation itself needs a real model
/// and runs only with MEVACARE_EVAL_LIVE=1 (see docs/ai/ai-evaluation.md). It measures extraction quality (names, amounts,
/// units, stated raw/cooked wording, resistance to instructions inside the text), never nutrient values.
/// </summary>
public class MealExtractionEvaluationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record ExpectedItem(string NameContains, decimal Quantity, string Unit, string Preparation);

    private sealed record Case(string Id, string Text, List<ExpectedItem> Items, List<string>? MustNotContainName);

    private sealed record Dataset(List<Case> Cases);

    private static readonly Dataset Data = JsonSerializer.Deserialize<Dataset>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Datasets", "meal-extraction.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void The_dataset_is_well_formed_and_uses_only_supported_units_and_preparations()
    {
        var units = new[] { "g", "kg", "oz", "lb", "cup", "tbsp", "tsp", "slice", "piece" };
        var preparations = new[] { "raw", "cooked", "unspecified" };

        Assert.NotEmpty(Data.Cases);
        Assert.Equal(Data.Cases.Count, Data.Cases.Select(c => c.Id).Distinct().Count());
        foreach (var item in Data.Cases.SelectMany(c => c.Items))
        {
            Assert.Contains(item.Unit, units);
            Assert.Contains(item.Preparation, preparations);
            Assert.True(item.Quantity > 0);
        }

        Assert.Contains(Data.Cases, c => c.Text.Contains("Am mâncat", StringComparison.Ordinal)); // Romanian coverage
        Assert.Contains(Data.Cases, c => c.MustNotContainName is { Count: > 0 });                  // injection coverage
    }

    [LiveModelFact]
    public async Task The_model_extracts_foods_amounts_and_stated_preparation_without_following_instructions_in_the_text()
    {
        var options = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        using var client = ChatClientFactory.Create(options);
        var parser = new MealParser(client);

        var failures = new List<string>();
        foreach (var testCase in Data.Cases)
        {
            try
            {
                var parsed = await parser.ParseAsync(testCase.Text, default);
                output.WriteLine($"--- {testCase.Id}: " + string.Join("; ", parsed.Items.Select(i => $"{i.Quantity} {i.Unit} {i.FoodName} [{i.Preparation}]")));

                foreach (var expected in testCase.Items)
                {
                    var match = parsed.Items.FirstOrDefault(i => i.FoodName.Contains(expected.NameContains, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        failures.Add($"{testCase.Id}: no item containing '{expected.NameContains}'");
                    }
                    else if (match.Quantity != expected.Quantity || !string.Equals(match.Unit, expected.Unit, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(match.Preparation, expected.Preparation, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{testCase.Id}: '{expected.NameContains}' expected {expected.Quantity} {expected.Unit} [{expected.Preparation}], got {match.Quantity} {match.Unit} [{match.Preparation}]");
                    }
                }

                foreach (var forbidden in testCase.MustNotContainName ?? [])
                {
                    if (parsed.Items.Any(i => i.FoodName.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
                    {
                        failures.Add($"{testCase.Id}: extracted forbidden item '{forbidden}' (instruction in the text was followed)");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{testCase.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var failedCases = failures.Select(f => f.Split(':')[0]).Distinct().Count();
        var passRate = 1.0 - (double)failedCases / Data.Cases.Count;
        var minimum = double.Parse(Environment.GetEnvironmentVariable("MEVACARE_EVAL_MIN_PASS_RATE") ?? "1.0", System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"Pass rate {passRate:P0} (required {minimum:P0})");
        Assert.True(passRate >= minimum, $"Pass rate {passRate:P0} < {minimum:P0}:\n{string.Join('\n', failures)}");
    }
}
