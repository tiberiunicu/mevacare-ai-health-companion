using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;

/// <summary>
/// The read/prepare surface the AI assistant is allowed to use. It receives a meal that an LLM *extracted* from free
/// text ("200 g chicken breast"), matches each item against the verified food database and calculates the nutrients
/// with deterministic code. It writes nothing: the result is a proposal the user must confirm.
/// </summary>
public sealed class NutritionAssistantApi(
    IFoodCatalog catalog, NutritionSummaryService summaries, IConsentService consents) : INutritionAssistantApi
{
    public const int MaxItems = 15;

    public Task<DailyNutritionSummaryDto> GetDailySummaryAsync(Guid userId, DateOnly date, CancellationToken ct) =>
        summaries.GetDailyAsync(userId, date, ct);

    public Task<NutritionRangeSummaryDto> GetRangeSummaryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct) =>
        summaries.GetRangeAsync(userId, from, to, ct);

    public async Task<MealProposalDto> PrepareMealAsync(Guid userId, ParsedMeal meal, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        if (meal.Items.Count == 0)
        {
            throw new BadRequestException("no_items", "No foods were found in the description.");
        }

        if (meal.Items.Count > MaxItems)
        {
            throw new BadRequestException("too_many_items", $"At most {MaxItems} foods can be prepared at once.");
        }

        var items = new List<ProposedItemDto>();
        var warnings = new List<string>();
        for (var index = 0; index < meal.Items.Count; index++)
        {
            items.Add(await BuildItemAsync($"i{index + 1}", meal.Items[index], warnings, ct));
        }

        var matched = items.Where(i => i.Status == "Matched").ToList();
        var totals = NutrientCalculator.Sum(matched.Select(i => (i.Nutrients, false)));
        if (totals.IncompleteData && matched.Count > 0)
        {
            warnings.Add("Some nutrient values are not reported for these foods, so the totals may be incomplete.");
        }

        return new MealProposalDto(NormalizeMealType(meal.MealType), items, totals, warnings);
    }

    private async Task<ProposedItemDto> BuildItemAsync(string clientId, ParsedFoodItem parsed, List<string> warnings, CancellationToken ct)
    {
        var input = string.IsNullOrWhiteSpace(parsed.OriginalText) ? $"{parsed.Quantity} {parsed.Unit} {parsed.FoodName}" : parsed.OriginalText.Trim();
        var wanted = parsed.Preparation?.Trim().ToLowerInvariant() switch
        {
            "raw" => PreparationState.Raw,
            "cooked" => PreparationState.Cooked,
            _ => PreparationState.Unspecified,
        };

        if (string.IsNullOrWhiteSpace(parsed.FoodName) || parsed.Quantity <= 0)
        {
            return Unmatched(clientId, input, wanted, "The food name or quantity could not be understood.");
        }

        var result = await catalog.SearchAsync(new FoodQuery(parsed.FoodName, wanted, 5), ct);
        if (!string.IsNullOrWhiteSpace(parsed.Brand))
        {
            // A named brand: try that product first; when it is not in the database fall back to the generic food and say so.
            var branded = await catalog.SearchAsync(new FoodQuery($"{parsed.FoodName} {parsed.Brand}", wanted, 5), ct);
            if (branded.Matches.Count > 0)
            {
                result = branded;
            }
            else if (result.Matches.Count > 0)
            {
                warnings.Add($"'{parsed.FoodName}': the brand '{parsed.Brand}' was not found in the database; a generic food was used. Search for the product to use its exact values.");
            }
        }

        if (result.Matches.Count == 0)
        {
            warnings.Add($"No food matching '{parsed.FoodName}' was found in the database; it was not guessed.");
            return Unmatched(clientId, input, wanted, "Not found in the food database.");
        }

        var top = await catalog.GetAsync(result.Matches[0].Food.Id, ct) ?? throw new InvalidOperationException("Matched food disappeared.");
        if (top.IsBranded && string.IsNullOrWhiteSpace(parsed.Brand))
        {
            warnings.Add($"'{parsed.FoodName}': no generic food matched, so a packaged product ('{top.Description}') was used. Please check it.");
        }

        var conversion = QuantityConverter.ToGrams(parsed.Quantity, parsed.Unit, top.Portions);
        var alternatives = result.Matches.Skip(1).Select(m => ToCandidate(m.Food)).ToList();

        if (conversion.Grams is not { } grams)
        {
            warnings.Add($"'{parsed.FoodName}': {conversion.Note}");
            return new ProposedItemDto(
                clientId, input, "NeedsQuantity", ToCandidate(top), alternatives, null, top.Preparation.ToString(), false,
                result.Ambiguous, NutrientValues.None, conversion.Note);
        }

        if (result.PreparationAssumed)
        {
            warnings.Add($"'{parsed.FoodName}': raw or cooked was not stated; the {top.Preparation.ToString().ToLowerInvariant()} version was assumed. Weights of raw and cooked food are not interchangeable.");
        }

        if (result.Ambiguous)
        {
            warnings.Add($"'{parsed.FoodName}': several foods match about equally; please check the choice.");
        }

        return new ProposedItemDto(
            clientId, input, "Matched", ToCandidate(top), alternatives, grams, top.Preparation.ToString(), result.PreparationAssumed,
            result.Ambiguous, NutrientCalculator.ForQuantity(top, grams), null);
    }

    private static ProposedItemDto Unmatched(string clientId, string input, PreparationState wanted, string note) =>
        new(clientId, input, "Unmatched", null, [], null, wanted.ToString(), false, false, NutrientValues.None, note);

    private static FoodCandidateDto ToCandidate(Food food) =>
        new(food.Id, food.Description, food.Preparation.ToString(), food.Category, food.SourceLabel);

    private static string? NormalizeMealType(string? value) =>
        Enum.TryParse<MealType>(value?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed.ToString() : null;
}
