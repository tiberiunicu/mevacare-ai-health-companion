using System.Text.RegularExpressions;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;

public sealed record GramConversion(decimal? Grams, string? Note);

/// <summary>
/// Converts "2 cups", "6 oz" or "3 slices" to grams. Mass units convert exactly; household measures need a portion
/// recorded for that food in the verified database. Nothing is guessed: if no portion matches, the result has no grams
/// and a note, and the user must enter the weight.
/// </summary>
public static partial class QuantityConverter
{
    public static readonly IReadOnlySet<string> SupportedUnits = new HashSet<string>(StringComparer.Ordinal)
    {
        "g", "kg", "oz", "lb", "cup", "tbsp", "tsp", "slice", "piece",
    };

    private const decimal GramsPerOunce = 28.349523125m;
    private const decimal GramsPerPound = 453.59237m;

    /// <summary>Maps free-text unit spellings to a canonical unit, or null when unknown.</summary>
    public static string? NormalizeUnit(string? unit)
    {
        var u = (unit ?? "").Trim().ToLowerInvariant().TrimEnd('.');
        return u switch
        {
            "g" or "gr" or "gram" or "grams" or "gramme" or "grame" => "g",
            "kg" or "kilogram" or "kilograms" or "kilogramme" => "kg",
            "oz" or "ounce" or "ounces" => "oz",
            "lb" or "lbs" or "pound" or "pounds" => "lb",
            "cup" or "cups" => "cup",
            "tbsp" or "tablespoon" or "tablespoons" or "tbs" => "tbsp",
            "tsp" or "teaspoon" or "teaspoons" => "tsp",
            "slice" or "slices" => "slice",
            "piece" or "pieces" or "pcs" or "pc" or "unit" or "units" or "item" or "items" or "serving" or "servings" or "whole" => "piece",
            _ => null,
        };
    }

    public static GramConversion ToGrams(decimal quantity, string? unit, IEnumerable<FoodPortion> portions)
    {
        if (quantity <= 0)
        {
            return new GramConversion(null, "The quantity must be greater than zero.");
        }

        var canonical = NormalizeUnit(unit);
        switch (canonical)
        {
            case "g":
                return Bounded(quantity);
            case "kg":
                return Bounded(quantity * 1000m);
            case "oz":
                return Bounded(quantity * GramsPerOunce);
            case "lb":
                return Bounded(quantity * GramsPerPound);
            case null:
                return new GramConversion(null, $"The unit '{unit}' is not supported; enter the weight in grams.");
        }

        var portion = portions
            .Where(p => Matches(canonical, p))
            .OrderBy(p => p.Description is null ? 0 : 1)
            .ThenBy(p => p.GramWeight / p.Amount)
            .FirstOrDefault();

        return portion is null
            ? new GramConversion(null, $"No '{canonical}' measure is known for this food; enter the weight in grams.")
            : Bounded(quantity * portion.GramWeight / portion.Amount);
    }

    private static GramConversion Bounded(decimal grams)
    {
        var rounded = Math.Round(grams, 2, MidpointRounding.AwayFromZero);
        return rounded > NutrientCalculator.MaxItemGrams
            ? new GramConversion(null, $"The weight exceeds {NutrientCalculator.MaxItemGrams:0} g; please check the quantity.")
            : new GramConversion(rounded, null);
    }

    private static bool Matches(string canonical, FoodPortion portion)
    {
        var words = Words(portion.Unit + " " + portion.Description);
        return canonical switch
        {
            "cup" => words.Contains("cup"),
            "tbsp" => words.Contains("tablespoon") || words.Contains("tbsp"),
            "tsp" => words.Contains("teaspoon") || words.Contains("tsp"),
            "slice" => words.Contains("slice"),
            "piece" => words.Overlaps(["unit", "piece", "item", "each", "medium", "whole", "serving"]),
            _ => false,
        };
    }

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => Singular(m.Value)).ToHashSet(StringComparer.Ordinal);

    private static string Singular(string word) => word.Length > 3 && word.EndsWith('s') ? word[..^1] : word;

    [GeneratedRegex("[a-z]+")]
    private static partial Regex WordPattern();
}
