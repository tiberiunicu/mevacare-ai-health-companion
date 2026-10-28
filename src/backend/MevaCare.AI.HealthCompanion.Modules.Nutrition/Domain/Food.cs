using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

/// <summary>Where a food record comes from. New databases (Romanian tables...) add a value here.</summary>
public enum FoodSource
{
    /// <summary>Generic foods, public domain, imported with <c>import-usda</c>.</summary>
    UsdaSrLegacy = 1,

    /// <summary>
    /// Packaged products with brand and barcode, from Open Food Facts (community data under the ODbL licence): imported in bulk with
    /// <c>import-openfoodfacts</c> or added one product at a time after an online lookup.
    /// </summary>
    OpenFoodFacts = 2,
}

/// <summary>
/// Raw and cooked weights are not interchangeable (100 g of dry rice is not 100 g of cooked rice), so every food
/// carries the state its nutrient values describe.
/// </summary>
public enum PreparationState
{
    Unspecified = 0,
    Raw = 1,
    Cooked = 2,
}

public enum NutrientType
{
    Calories = 1,
    Protein = 2,
    Carbohydrates = 3,
    Fat = 4,
    Fiber = 5,
}

/// <summary>A food from a verified database, imported with its provenance. Never created from model output.</summary>
public sealed class Food : Entity
{
    private readonly List<FoodNutrient> _nutrients = [];
    private readonly List<FoodPortion> _portions = [];

    private Food() { }

    public Food(
        FoodSource source, string sourceFoodId, string description, string? category, PreparationState preparation,
        string? brand = null, string? barcode = null, bool energyDerived = false)
    {
        Source = source;
        SourceFoodId = sourceFoodId;
        Description = description;
        Category = category;
        Preparation = preparation;
        Brand = brand;
        Barcode = barcode;
        EnergyDerived = energyDerived;
        SearchText = FoodText.BuildSearchText(description, brand, barcode);
    }

    public FoodSource Source { get; private set; }

    /// <summary>The id in the source database (for USDA: the FDC id; for Open Food Facts: the barcode), used for citation and re-import.</summary>
    public string SourceFoodId { get; private set; } = null!;

    public string Description { get; private set; } = null!;
    public string? Category { get; private set; }
    public PreparationState Preparation { get; private set; }

    /// <summary>Brand of a packaged product ("Hochland"); null for generic foods.</summary>
    public string? Brand { get; private set; }

    /// <summary>EAN/UPC barcode of a packaged product; null for generic foods.</summary>
    public string? Barcode { get; private set; }

    /// <summary>
    /// True when the source gave no energy value and the calories were calculated from protein, carbohydrates and fat (4/4/9 kcal per gram, 2 for fibre).
    /// Meal items made from such a food are marked as estimated.
    /// </summary>
    public bool EnergyDerived { get; private set; }

    /// <summary>Name, brand and barcode without diacritics and in lower case: what the search runs on (see <see cref="FoodText"/>).</summary>
    public string SearchText { get; private set; } = "";

    /// <summary>Packaged and branded products rank after generic foods unless the search names the brand.</summary>
    public bool IsBranded => Source != FoodSource.UsdaSrLegacy;
    public IReadOnlyCollection<FoodNutrient> Nutrients => _nutrients;
    public IReadOnlyCollection<FoodPortion> Portions => _portions;

    public string SourceLabel => $"{Source} #{SourceFoodId}";

    public void AddNutrient(NutrientType type, decimal amountPer100g) => _nutrients.Add(new FoodNutrient(Id, type, amountPer100g));

    public void AddPortion(decimal amount, string unit, string? description, decimal gramWeight) =>
        _portions.Add(new FoodPortion(Id, amount, unit, description, gramWeight));
}

/// <summary>Amount of one nutrient per 100 g. A nutrient the source does not report simply has no row (unknown, not zero).</summary>
public sealed class FoodNutrient
{
    private FoodNutrient() { }

    public FoodNutrient(Guid foodId, NutrientType type, decimal amountPer100g)
    {
        FoodId = foodId;
        Type = type;
        AmountPer100g = amountPer100g;
    }

    public Guid FoodId { get; private set; }
    public NutrientType Type { get; private set; }
    public decimal AmountPer100g { get; private set; }
}

/// <summary>A household measure with its weight in grams (for example "1 cup = 158 g").</summary>
public sealed class FoodPortion : Entity
{
    private FoodPortion() { }

    public FoodPortion(Guid foodId, decimal amount, string unit, string? description, decimal gramWeight)
    {
        FoodId = foodId;
        Amount = amount;
        Unit = unit;
        Description = description;
        GramWeight = gramWeight;
    }

    public Guid FoodId { get; private set; }

    /// <summary>How many <see cref="Unit"/>s the <see cref="GramWeight"/> refers to (often 1).</summary>
    public decimal Amount { get; private set; }

    public string Unit { get; private set; } = null!;
    public string? Description { get; private set; }
    public decimal GramWeight { get; private set; }
}
