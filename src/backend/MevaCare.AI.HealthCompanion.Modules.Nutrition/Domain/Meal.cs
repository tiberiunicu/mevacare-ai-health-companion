using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

public enum MealType
{
    Breakfast = 1,
    Lunch = 2,
    Dinner = 3,
    Snack = 4,
}

public enum MealOrigin
{
    Manual = 1,
    Assistant = 2,
}

public sealed class Meal : AuditableEntity
{
    private readonly List<MealItem> _items = [];

    private Meal() { }

    public Meal(
        Guid userId, MealType type, DateTime eatenAtUtc, DateOnly localDate, int utcOffsetMinutes, string? notes,
        MealOrigin origin, Guid? sourceProposalId)
    {
        UserId = userId;
        Type = type;
        EatenAtUtc = eatenAtUtc;
        LocalDate = localDate;
        UtcOffsetMinutes = utcOffsetMinutes;
        Notes = notes;
        Origin = origin;
        SourceProposalId = sourceProposalId;
    }

    public Guid UserId { get; private set; }
    public MealType Type { get; private set; }

    /// <summary>The moment of eating in UTC.</summary>
    public DateTime EatenAtUtc { get; private set; }

    /// <summary>The calendar day the user experienced it (their local date). Daily summaries group by this.</summary>
    public DateOnly LocalDate { get; private set; }

    /// <summary>Offset of the user's clock when the meal was logged; keeps the source time-zone information.</summary>
    public int UtcOffsetMinutes { get; private set; }

    public string? Notes { get; private set; }
    public MealOrigin Origin { get; private set; }

    /// <summary>The assistant proposal this meal came from. Unique: a retried confirmation cannot create a second meal.</summary>
    public Guid? SourceProposalId { get; private set; }

    public IReadOnlyCollection<MealItem> Items => _items;

    public MealItem AddItem(MealItem item)
    {
        _items.Add(item);
        return item;
    }

    public void Update(MealType type, DateTime eatenAtUtc, DateOnly localDate, int utcOffsetMinutes, string? notes)
    {
        Type = type;
        EatenAtUtc = eatenAtUtc;
        LocalDate = localDate;
        UtcOffsetMinutes = utcOffsetMinutes;
        Notes = notes;
    }

    public void ReplaceItems(IEnumerable<MealItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }
}

/// <summary>
/// One food in a meal. The nutrient values are a **snapshot** calculated when the item was logged, so later changes
/// to the food database never rewrite history.
/// </summary>
public sealed class MealItem : Entity
{
    private MealItem() { }

    public MealItem(
        Guid? foodId, string name, decimal quantityGrams, PreparationState preparation, bool isEstimated, string? sourceLabel,
        NutrientValues nutrients)
    {
        FoodId = foodId;
        Name = name;
        QuantityGrams = quantityGrams;
        Preparation = preparation;
        IsEstimated = isEstimated;
        SourceLabel = sourceLabel;
        Calories = nutrients.Calories;
        ProteinG = nutrients.ProteinG;
        CarbohydratesG = nutrients.CarbohydratesG;
        FatG = nutrients.FatG;
        FiberG = nutrients.FiberG;
    }

    public Guid MealId { get; private set; }

    /// <summary>The food it was matched to, or null for a custom (estimated) item. Not a foreign key: history outlives food data.</summary>
    public Guid? FoodId { get; private set; }

    public string Name { get; private set; } = null!;
    public decimal QuantityGrams { get; private set; }
    public PreparationState Preparation { get; private set; }

    /// <summary>True when the values were entered by the user instead of calculated from a verified database.</summary>
    public bool IsEstimated { get; private set; }

    public string? SourceLabel { get; private set; }

    public decimal? Calories { get; private set; }
    public decimal? ProteinG { get; private set; }
    public decimal? CarbohydratesG { get; private set; }
    public decimal? FatG { get; private set; }
    public decimal? FiberG { get; private set; }

    public NutrientValues Nutrients => new(Calories, ProteinG, CarbohydratesG, FatG, FiberG);
}
