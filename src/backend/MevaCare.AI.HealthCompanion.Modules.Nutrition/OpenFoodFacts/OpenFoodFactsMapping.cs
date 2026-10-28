using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;

/// <summary>
/// One product as Open Food Facts publishes it (a CSV row of the bulk export or an API result), before any validation.
/// Values are per 100 g. Everything may be missing or wrong: the data is contributed by volunteers.
/// </summary>
public sealed record OffProduct(
    string? Code,
    string? ProductName,
    string? Brands,
    string? CategoryTag,
    string? Quantity,
    string? ServingSize,
    decimal? ServingQuantityG,
    decimal? EnergyKcal,
    decimal? EnergyKj,
    decimal? Protein,
    decimal? Carbohydrates,
    decimal? Fat,
    decimal? Fiber);

/// <summary>
/// Turns an Open Food Facts product into a <see cref="Food"/> or says why it is not trusted. Shared by the bulk importer and the online
/// lookup so both apply the same rules. The checks are sanity checks (impossible values), not a guarantee that the label was typed correctly.
/// </summary>
public static partial class OpenFoodFactsMapping
{
    public const decimal KilojoulesPerKilocalorie = 4.184m;

    public static bool TryCreateFood(OffProduct product, out Food? food, out string? rejection)
    {
        food = null;
        rejection = null;

        var code = product.Code?.Trim();
        if (code is null || code.Length is < 8 or > 14 || !code.All(char.IsAsciiDigit))
        {
            rejection = "invalid_barcode";
            return false;
        }

        var name = Clean(product.ProductName, 300);
        if (name is null || name.Length < 2)
        {
            rejection = "missing_name";
            return false;
        }

        var protein = Round(product.Protein);
        var carbohydrates = Round(product.Carbohydrates);
        var fat = Round(product.Fat);
        var fiber = Round(product.Fiber);

        var kcal = product.EnergyKcal ?? (product.EnergyKj is { } kj ? Math.Round(kj / KilojoulesPerKilocalorie, 1) : null);
        var derived = false;
        if (kcal is null && protein is not null && carbohydrates is not null && fat is not null)
        {
            // Many products have the macronutrients but no energy. Calories are then calculated (general Atwater factors; fibre 2 kcal/g) and the
            // food is flagged, so meal items made from it are shown as estimated instead of looking like label values.
            kcal = Math.Round(4 * protein.Value + 4 * carbohydrates.Value + 9 * fat.Value + 2 * (fiber ?? 0), 1);
            derived = true;
        }

        if (kcal is null)
        {
            rejection = "missing_energy";
            return false;
        }

        if (kcal < 0 || kcal > 900)
        {
            rejection = "implausible_energy";
            return false;
        }

        if (protein is null && carbohydrates is null && fat is null)
        {
            rejection = "no_macronutrients";
            return false;
        }

        if (new[] { protein, carbohydrates, fat, fiber }.Any(v => v is < 0 or > 100)
            || (protein ?? 0) + (carbohydrates ?? 0) + (fat ?? 0) > 102)
        {
            rejection = "implausible_macronutrients";
            return false;
        }

        // Energy cannot be (much) lower than what the macronutrients provide. Higher is allowed: alcohol and polyols add energy that is not tracked here.
        if (!derived && protein is not null && carbohydrates is not null && fat is not null
            && kcal < 0.6m * (4 * protein.Value + 4 * carbohydrates.Value + 9 * fat.Value) - 20)
        {
            rejection = "inconsistent_energy";
            return false;
        }

        var brand = FirstBrand(product.Brands);
        food = new Food(FoodSource.OpenFoodFacts, code, name, CategoryName(product.CategoryTag), PreparationState.Unspecified, brand, code, derived);
        food.AddNutrient(NutrientType.Calories, Math.Round(kcal.Value, 1));
        AddIfPresent(food, NutrientType.Protein, protein);
        AddIfPresent(food, NutrientType.Carbohydrates, carbohydrates);
        AddIfPresent(food, NutrientType.Fat, fat);
        AddIfPresent(food, NutrientType.Fiber, fiber);

        if (product.ServingQuantityG is > 0 and <= 2000)
        {
            food.AddPortion(1, "serving", Clean(product.ServingSize, 200), Math.Round(product.ServingQuantityG.Value, 1));
        }

        if (PackageGrams(product.Quantity) is { } packageGrams)
        {
            // Volunteers write "175 g," and "250g."; the stray punctuation is not part of the quantity.
            food.AddPortion(1, "package", Clean(product.Quantity, 200)?.TrimEnd(',', ';', '.', ' '), packageGrams);
        }

        return true;
    }

    /// <summary>"175 g" → 175, "0.5 kg" → 500; anything else (multipacks, millilitres, free text) → null: weights are never guessed.</summary>
    public static decimal? PackageGrams(string? quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
        {
            return null;
        }

        var match = PackagePattern().Match(quantity);
        if (!match.Success
            || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var grams = match.Groups[2].Value.Equals("kg", StringComparison.OrdinalIgnoreCase) ? amount * 1000m : amount;
        return grams is >= 5 and <= 5000 ? Math.Round(grams, 1) : null;
    }

    /// <summary>"Hochland, Hochland Rumania" → "Hochland".</summary>
    public static string? FirstBrand(string? brands) =>
        brands?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(b => Clean(b, 120)).FirstOrDefault(b => b is not null);

    /// <summary>"en:cottage-cheeses" → "Cottage cheeses" (the last tag of the list is the most specific one).</summary>
    public static string? CategoryName(string? tags)
    {
        var last = tags?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is null)
        {
            return null;
        }

        var name = last[(last.IndexOf(':') + 1)..].Replace('-', ' ').Trim();
        return name.Length == 0 ? null : Clean(char.ToUpperInvariant(name[0]) + name[1..], 120);
    }

    /// <summary>Removes control characters and collapses whitespace; null when nothing is left. Free text from outside is never stored as is.</summary>
    public static string? Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsControl(ch) || char.IsWhiteSpace(ch) ? ' ' : ch);
        }

        var cleaned = WhitespaceRun().Replace(builder.ToString(), " ").Trim();
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    public static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static decimal? Round(decimal? value) => value is null ? null : Math.Round(value.Value, 2);

    private static void AddIfPresent(Food food, NutrientType type, decimal? amount)
    {
        if (amount is not null)
        {
            food.AddNutrient(type, amount.Value);
        }
    }

    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*(g|kg)\s*[,.;]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PackagePattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRun();
}
