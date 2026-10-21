using System.Text.Json;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;

namespace MevaCare.AI.HealthCompanion.UnitTests.Nutrition;

/// <summary>
/// Measures the food-matching heuristic on real USDA SR Legacy descriptions (public domain). For each query the fixture
/// holds the 60 shortest matching descriptions, which are the hardest competition for the generic food.
/// </summary>
public class FoodRankingTests
{
    private sealed record Row(string Id, string D, string? C);

    private static readonly Dictionary<string, List<Row>> Fixture = JsonSerializer.Deserialize<Dictionary<string, List<Row>>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Nutrition", "Fixtures", "usda-sr-legacy-candidates.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string Best(string fixtureKey, string query, PreparationState preparation)
    {
        var (tokens, wanted) = FoodRanking.SplitPreparation(query, preparation);
        return Fixture[fixtureKey]
            .Select(r => new Food(FoodSource.UsdaSrLegacy, r.Id, r.D, r.C, FoodRanking.InferPreparation(r.D)))
            .Where(f => FoodRanking.MatchesAll(f.Description, tokens))
            .Select(f => (f.Description, Score: FoodRanking.Score(f, tokens, wanted)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Description.Length)
            .First().Description;
    }

    [Theory]
    [InlineData("chicken breast", "chicken breast", PreparationState.Cooked, "Chicken, broilers or fryers, breast, meat only, cooked, roasted")]
    [InlineData("chicken breast", "chicken breast", PreparationState.Unspecified, "Chicken, broilers or fryers, breast, meat only, cooked, roasted")]
    [InlineData("rice", "rice", PreparationState.Cooked, "Rice, white, long-grain, regular, enriched, cooked")]
    [InlineData("rice", "cooked rice", PreparationState.Unspecified, "Rice, white, long-grain, regular, enriched, cooked")]
    [InlineData("rice", "rice", PreparationState.Raw, "Rice, white, long-grain, regular, raw, enriched")]
    [InlineData("egg", "egg", PreparationState.Cooked, "Egg, whole, cooked, hard-boiled")]
    [InlineData("egg", "egg", PreparationState.Unspecified, "Egg, whole, raw, fresh")]
    [InlineData("banana", "banana", PreparationState.Unspecified, "Bananas, raw")]
    [InlineData("olive oil", "olive oil", PreparationState.Unspecified, "Oil, olive, salad or cooking")]
    [InlineData("whole milk", "whole milk", PreparationState.Unspecified, "Milk, whole, 3.25% milkfat, with added vitamin D")]
    [InlineData("tomato", "tomato", PreparationState.Unspecified, "Tomatoes, red, ripe, raw, year round average")]
    [InlineData("carrot", "carrot", PreparationState.Unspecified, "Carrots, raw")]
    [InlineData("butter", "butter", PreparationState.Unspecified, "Butter, salted")]
    [InlineData("almond", "almonds", PreparationState.Unspecified, "Nuts, almonds")]
    [InlineData("honey", "honey", PreparationState.Unspecified, "Honey")]
    [InlineData("yogurt", "yogurt", PreparationState.Unspecified, "Yogurt, plain, whole milk")]
    public void The_generic_food_wins_over_processed_variants(string key, string query, PreparationState preparation, string expected) =>
        Assert.Equal(expected, Best(key, query, preparation));

    [Theory]
    [InlineData("salmon", "salmon", PreparationState.Cooked, "Fish, salmon", PreparationState.Cooked)]
    [InlineData("salmon", "salmon", PreparationState.Raw, "Fish, salmon", PreparationState.Raw)]
    [InlineData("potato", "potato", PreparationState.Cooked, "Potatoes,", PreparationState.Cooked)]
    [InlineData("pasta", "pasta", PreparationState.Cooked, "Pasta, cooked", PreparationState.Cooked)]
    [InlineData("ground beef", "ground beef", PreparationState.Cooked, "Beef, ground", PreparationState.Cooked)]
    [InlineData("broccoli", "broccoli", PreparationState.Cooked, "Broccoli, cooked", PreparationState.Cooked)]
    [InlineData("turkey breast", "turkey breast", PreparationState.Cooked, "Turkey", PreparationState.Cooked)]
    [InlineData("orange", "orange", PreparationState.Unspecified, "Oranges, raw", PreparationState.Raw)]
    [InlineData("bread", "bread", PreparationState.Unspecified, "Bread, white", PreparationState.Unspecified)]
    [InlineData("oats", "oats", PreparationState.Unspecified, "Cereals, oats", PreparationState.Unspecified)]
    public void The_best_match_starts_with_the_base_food_and_has_the_requested_state(
        string key, string query, PreparationState preparation, string prefix, PreparationState expectedState)
    {
        var best = Best(key, query, preparation);

        Assert.StartsWith(prefix, best, StringComparison.Ordinal);
        if (expectedState != PreparationState.Unspecified)
        {
            Assert.Equal(expectedState, FoodRanking.InferPreparation(best));
        }
    }

    [Fact]
    public void An_explicit_state_changes_the_choice()
    {
        var raw = Best("chicken breast", "chicken breast", PreparationState.Raw);
        var cooked = Best("chicken breast", "chicken breast", PreparationState.Cooked);

        Assert.Contains("raw", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cooked", cooked, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Chicken, broilers or fryers, breast, meat only, cooked, roasted", PreparationState.Cooked)]
    [InlineData("Rice, white, long-grain, regular, raw, enriched", PreparationState.Raw)]
    [InlineData("Pasta, dry, enriched", PreparationState.Raw)]
    [InlineData("Egg, whole, cooked, hard-boiled", PreparationState.Cooked)]
    [InlineData("Butter, salted", PreparationState.Unspecified)]
    public void The_preparation_state_is_inferred_from_the_description(string description, PreparationState expected) =>
        Assert.Equal(expected, FoodRanking.InferPreparation(description));

    [Fact]
    public void Preparation_words_are_taken_out_of_the_search_phrase()
    {
        var (tokens, preparation) = FoodRanking.SplitPreparation("2 boiled Eggs", PreparationState.Unspecified);

        Assert.Equal(["egg"], tokens);
        Assert.Equal(PreparationState.Cooked, preparation);
    }

    [Fact]
    public void Plural_and_common_synonyms_are_normalised()
    {
        Assert.Equal(["tomato"], FoodRanking.Tokenize("Tomatoes"));
        Assert.Equal(["oat"], FoodRanking.Tokenize("oatmeal"));
        Assert.Equal(["berry"], FoodRanking.Tokenize("berries"));
    }

    private static (Food Food, double Score)[] Top2(string key, string query, PreparationState preparation)
    {
        var (tokens, wanted) = FoodRanking.SplitPreparation(query, preparation);
        return Fixture[key]
            .Select(r => new Food(FoodSource.UsdaSrLegacy, r.Id, r.D, r.C, FoodRanking.InferPreparation(r.D)))
            .Where(f => FoodRanking.MatchesAll(f.Description, tokens))
            .Select(f => (f, FoodRanking.Score(f, tokens, wanted)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.f.Description.Length).Take(2).ToArray();
    }

    [Theory]
    [InlineData("chicken breast", "chicken breast", PreparationState.Cooked)]
    [InlineData("rice", "rice", PreparationState.Cooked)]
    [InlineData("whole milk", "whole milk", PreparationState.Unspecified)]
    public void Near_ties_between_variants_of_the_same_food_are_not_reported_as_ambiguous(string key, string query, PreparationState preparation)
    {
        var (tokens, _) = FoodRanking.SplitPreparation(query, preparation);
        var top = Top2(key, query, preparation);

        Assert.False(FoodRanking.IsAmbiguous(top[0].Food, top[0].Score, top[1].Food, top[1].Score, tokens));
    }

    [Fact]
    public void Different_kinds_of_food_with_near_equal_scores_are_reported_as_ambiguous()
    {
        var tokens = FoodRanking.Tokenize("yogurt");
        var dairy = new Food(FoodSource.UsdaSrLegacy, "1", "Yogurt, plain, whole milk", "Dairy and Egg Products", PreparationState.Unspecified);
        var soy = new Food(FoodSource.UsdaSrLegacy, "2", "Yogurt, soy, plain", "Legumes and Legume Products", PreparationState.Unspecified);

        Assert.True(FoodRanking.IsAmbiguous(dairy, 5, soy, 4.5, tokens));
        Assert.False(FoodRanking.IsAmbiguous(dairy, 12, soy, 4.5, tokens)); // a clear winner is not ambiguous
    }

    [Fact]
    public void Whole_words_are_required_so_rice_does_not_match_licorice()
    {
        Assert.True(FoodRanking.MatchesAll("Rice, white, cooked", ["rice"]));
        Assert.False(FoodRanking.MatchesAll("Candies, licorice", ["rice"]));
    }
}
