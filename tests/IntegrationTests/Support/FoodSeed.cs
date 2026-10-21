using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>A few real USDA SR Legacy foods (values per 100 g) for tests; the full dataset is imported by the CLI command.</summary>
public static class FoodSeed
{
    public static async Task EnsureAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Set<Food>().AnyAsync(f => f.SourceFoodId == "171477"))
        {
            return;
        }

        db.AddRange(
            Build("171477", "Chicken, broilers or fryers, breast, meat only, cooked, roasted", "Poultry Products", PreparationState.Cooked, 165, 31.02m, 0, 3.57m, 0, ("cup, chopped or diced", 140m)),
            Build("171534", "Chicken, broilers or fryers, breast, meat only, raw", "Poultry Products", PreparationState.Raw, 120, 22.5m, 0, 2.62m, 0),
            Build("168878", "Rice, white, long-grain, regular, enriched, cooked", "Cereal Grains and Pasta", PreparationState.Cooked, 130, 2.69m, 28.17m, 0.28m, 0.4m, ("cup", 158m)),
            Build("168877", "Rice, white, long-grain, regular, raw, enriched", "Cereal Grains and Pasta", PreparationState.Raw, 365, 7.13m, 79.95m, 0.66m, 1.3m));
        await db.SaveChangesAsync();
    }

    private static Food Build(
        string id, string description, string category, PreparationState state,
        decimal calories, decimal protein, decimal carbs, decimal fat, decimal fiber, params (string Label, decimal Grams)[] portions)
    {
        var food = new Food(FoodSource.UsdaSrLegacy, id, description, category, state);
        food.AddNutrient(NutrientType.Calories, calories);
        food.AddNutrient(NutrientType.Protein, protein);
        food.AddNutrient(NutrientType.Carbohydrates, carbs);
        food.AddNutrient(NutrientType.Fat, fat);
        food.AddNutrient(NutrientType.Fiber, fiber);
        foreach (var (label, grams) in portions)
        {
            food.AddPortion(1, "undetermined", label, grams);
        }

        return food;
    }
}
