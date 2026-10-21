using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

/// <summary>
/// The user's own daily targets. They are configurable and never computed or imposed by the application
/// (no medical advice); an unset value means "no target".
/// </summary>
public sealed class NutritionTarget : AuditableEntity
{
    private NutritionTarget() { }

    public NutritionTarget(Guid userId) => UserId = userId;

    public Guid UserId { get; private set; }
    public decimal? Calories { get; private set; }
    public decimal? ProteinG { get; private set; }
    public decimal? CarbohydratesG { get; private set; }
    public decimal? FatG { get; private set; }
    public decimal? FiberG { get; private set; }

    public void Set(NutritionTargetDto values)
    {
        Calories = values.Calories;
        ProteinG = values.ProteinG;
        CarbohydratesG = values.CarbohydratesG;
        FatG = values.FatG;
        FiberG = values.FiberG;
    }

    public NutritionTargetDto ToDto() => new(Calories, ProteinG, CarbohydratesG, FatG, FiberG);
}
