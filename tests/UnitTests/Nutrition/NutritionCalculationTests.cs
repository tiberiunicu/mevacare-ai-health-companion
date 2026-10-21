using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

namespace MevaCare.AI.HealthCompanion.UnitTests.Nutrition;

/// <summary>Foods with real USDA SR Legacy values (per 100 g), used across the nutrition tests.</summary>
internal static class SampleFoods
{
    public static Food ChickenBreastRoasted() => Build("171477", "Chicken, broilers or fryers, breast, meat only, cooked, roasted", "Poultry Products", PreparationState.Cooked,
        calories: 165, protein: 31.02m, carbs: 0, fat: 3.57m, fiber: 0, ("cup, chopped or diced", 140m));

    public static Food RiceCooked() => Build("168878", "Rice, white, long-grain, regular, enriched, cooked", "Cereal Grains and Pasta", PreparationState.Cooked,
        calories: 130, protein: 2.69m, carbs: 28.17m, fat: 0.28m, fiber: 0.4m, ("cup", 158m));

    public static Food RiceRaw() => Build("168877", "Rice, white, long-grain, regular, raw, enriched", "Cereal Grains and Pasta", PreparationState.Raw,
        calories: 365, protein: 7.13m, carbs: 79.95m, fat: 0.66m, fiber: 1.3m);

    /// <summary>A food whose source does not report fibre (unknown, not zero).</summary>
    public static Food WithoutFiber() => Build("999001", "Test food without fibre data", null, PreparationState.Unspecified,
        calories: 100, protein: 10, carbs: 10, fat: 1, fiber: null);

    private static Food Build(
        string id, string description, string? category, PreparationState preparation,
        decimal calories, decimal protein, decimal carbs, decimal fat, decimal? fiber, params (string Label, decimal Grams)[] portions)
    {
        var food = new Food(FoodSource.UsdaSrLegacy, id, description, category, preparation);
        food.AddNutrient(NutrientType.Calories, calories);
        food.AddNutrient(NutrientType.Protein, protein);
        food.AddNutrient(NutrientType.Carbohydrates, carbs);
        food.AddNutrient(NutrientType.Fat, fat);
        if (fiber is { } f)
        {
            food.AddNutrient(NutrientType.Fiber, f);
        }

        foreach (var (label, grams) in portions)
        {
            food.AddPortion(1, "undetermined", label, grams);
        }

        return food;
    }
}

public class NutrientCalculatorTests
{
    [Fact]
    public void Two_hundred_grams_of_roasted_chicken_breast_are_calculated_from_per_100g_data()
    {
        var values = NutrientCalculator.ForQuantity(SampleFoods.ChickenBreastRoasted(), 200m);

        Assert.Equal(330m, values.Calories);
        Assert.Equal(62.04m, values.ProteinG);
        Assert.Equal(0m, values.CarbohydratesG);
        Assert.Equal(7.14m, values.FatG);
        Assert.Equal(0m, values.FiberG);
    }

    [Fact]
    public void One_hundred_fifty_grams_of_cooked_rice_are_rounded_away_from_zero_at_two_decimals()
    {
        var values = NutrientCalculator.ForQuantity(SampleFoods.RiceCooked(), 150m);

        Assert.Equal(195m, values.Calories);
        Assert.Equal(4.04m, values.ProteinG);   // 4.035
        Assert.Equal(42.26m, values.CarbohydratesG); // 42.255
        Assert.Equal(0.42m, values.FatG);
        Assert.Equal(0.6m, values.FiberG);
    }

    [Fact]
    public void Raw_and_cooked_rice_differ_enormously_for_the_same_weight()
    {
        var raw = NutrientCalculator.ForQuantity(SampleFoods.RiceRaw(), 150m);
        var cooked = NutrientCalculator.ForQuantity(SampleFoods.RiceCooked(), 150m);

        Assert.Equal(547.5m, raw.Calories);
        Assert.True(raw.Calories > cooked.Calories * 2.5m);
    }

    [Fact]
    public void A_nutrient_the_food_does_not_report_stays_unknown_instead_of_becoming_zero()
    {
        var values = NutrientCalculator.ForQuantity(SampleFoods.WithoutFiber(), 50m);

        Assert.Equal(50m, values.Calories);
        Assert.Null(values.FiberG);
    }

    [Theory]
    [InlineData(0.01, 0.02)]
    [InlineData(1, 1.65)]
    [InlineData(333.33, 549.99)]
    public void Results_scale_linearly_with_the_quantity(double grams, double expectedCalories)
    {
        var values = NutrientCalculator.ForQuantity(SampleFoods.ChickenBreastRoasted(), (decimal)grams);

        Assert.Equal((decimal)expectedCalories, values.Calories);
    }

    [Fact]
    public void Totals_add_item_values_and_flag_incomplete_data_and_estimates()
    {
        var chicken = NutrientCalculator.ForQuantity(SampleFoods.ChickenBreastRoasted(), 200m);
        var rice = NutrientCalculator.ForQuantity(SampleFoods.RiceCooked(), 150m);

        var complete = NutrientCalculator.Sum([(chicken, false), (rice, false)]);
        Assert.Equal(525m, complete.Calories);
        Assert.Equal(66.1m, complete.ProteinG);
        Assert.False(complete.IncompleteData);
        Assert.False(complete.IncludesEstimates);

        var withUnknown = NutrientCalculator.Sum([(chicken, false), (NutrientCalculator.ForQuantity(SampleFoods.WithoutFiber(), 100m), true)]);
        Assert.True(withUnknown.IncompleteData);
        Assert.True(withUnknown.IncludesEstimates);
        Assert.Equal(430m, withUnknown.Calories);
    }

    [Fact]
    public void An_empty_day_has_zero_totals_and_is_not_flagged_incomplete()
    {
        var totals = NutrientCalculator.Sum([]);

        Assert.Equal(0m, totals.Calories);
        Assert.False(totals.IncompleteData);
    }

    [Fact]
    public void Remaining_is_target_minus_consumed_and_goes_negative_when_exceeded_and_is_null_without_target()
    {
        var totals = new NutrientTotals(1800m, 150m, 200m, 70m, 20m, false, false);
        var targets = new NutritionTargetDto(2000m, 120m, null, 60m, null);

        var remaining = NutrientCalculator.Remaining(targets, totals);

        Assert.Equal(200m, remaining.Calories);
        Assert.Equal(-30m, remaining.ProteinG);
        Assert.Null(remaining.CarbohydratesG);
        Assert.Equal(-10m, remaining.FatG);
        Assert.Null(remaining.FiberG);
    }
}

public class QuantityConverterTests
{
    private static readonly IReadOnlyCollection<FoodPortion> RicePortions = SampleFoods.RiceCooked().Portions;

    [Theory]
    [InlineData(150, "g", 150)]
    [InlineData(150, "grams", 150)]
    [InlineData(1.5, "kg", 1500)]
    [InlineData(1, "oz", 28.35)]
    [InlineData(1, "lb", 453.59)]
    public void Mass_units_convert_exactly(double quantity, string unit, double expected)
    {
        var result = QuantityConverter.ToGrams((decimal)quantity, unit, RicePortions);

        Assert.Equal((decimal)expected, result.Grams);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Household_measures_use_the_portion_recorded_for_that_food()
    {
        var result = QuantityConverter.ToGrams(1.5m, "cups", RicePortions);

        Assert.Equal(237m, result.Grams);
    }

    [Fact]
    public void A_household_measure_without_a_recorded_portion_is_not_guessed()
    {
        var result = QuantityConverter.ToGrams(2m, "tbsp", RicePortions);

        Assert.Null(result.Grams);
        Assert.Contains("grams", result.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("handful")]
    [InlineData("ml")]
    [InlineData("")]
    public void Unsupported_units_ask_for_grams(string unit)
    {
        var result = QuantityConverter.ToGrams(1m, unit, RicePortions);

        Assert.Null(result.Grams);
        Assert.NotNull(result.Note);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_quantities_are_rejected(double quantity) =>
        Assert.Null(QuantityConverter.ToGrams((decimal)quantity, "g", RicePortions).Grams);

    [Fact]
    public void Absurdly_large_weights_are_rejected()
    {
        var result = QuantityConverter.ToGrams(20m, "kg", RicePortions);

        Assert.Null(result.Grams);
        Assert.Contains("exceeds", result.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pieces", "piece")]
    [InlineData("Tablespoons", "tbsp")]
    [InlineData("LBS", "lb")]
    [InlineData("slices", "slice")]
    public void Unit_spellings_are_normalised(string input, string expected) =>
        Assert.Equal(expected, QuantityConverter.NormalizeUnit(input));
}
