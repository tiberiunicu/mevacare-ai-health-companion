using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;

/// <summary>Values typed by the user for an item that is not in the food database. Always stored as "estimated".</summary>
public sealed record EstimatedNutrientsInput(decimal? Calories, decimal? ProteinG, decimal? CarbohydratesG, decimal? FatG, decimal? FiberG);

/// <summary>
/// One line of a meal. With a <see cref="FoodId"/> the server calculates the nutrients from the database
/// (client-supplied numbers are not accepted); without one the item is custom and needs <see cref="Estimated"/>.
/// </summary>
public sealed record MealItemInput(
    Guid? FoodId, string Name, decimal QuantityGrams, PreparationState Preparation, EstimatedNutrientsInput? Estimated);

/// <param name="Type">Optional; inferred from the local time of day when omitted.</param>
/// <param name="EatenAt">With the user's UTC offset. Defaults to the current time at UTC.</param>
/// <param name="ProposalId">Set when the meal was prepared by the assistant and is being confirmed by the user.</param>
public sealed record SaveMealRequest(
    MealType? Type, DateTimeOffset? EatenAt, string? Notes, IReadOnlyList<MealItemInput> Items, Guid? ProposalId);

public sealed record SaveMealResult(MealDto Meal, bool Created);

public sealed record FoodSearchItemDto(
    Guid FoodId, string Description, string Preparation, string? Category, string SourceLabel, NutrientValues Per100g,
    bool Ambiguous, bool PreparationAssumed, string? Brand = null, string? Barcode = null, bool EnergyDerived = false);

/// <summary>Body of <c>POST /api/nutrition/foods/online/add</c>.</summary>
public sealed record AddOnlineFoodRequest(string Barcode);

public sealed class SaveMealRequestValidator : AbstractValidator<SaveMealRequest>
{
    public const int MaxItems = 30;

    public SaveMealRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type is not null);
        RuleFor(x => x.Notes).MaximumLength(500);
        RuleFor(x => x.EatenAt).Must(t =>
            {
                var now = clock.GetUtcNow();
                return t is null || (t.Value <= now.AddDays(1) && t.Value >= now.AddYears(-5));
            })
            .WithMessage("The time must be within the last 5 years and not more than a day in the future.");
        RuleFor(x => x.Items).NotEmpty().Must(i => i.Count <= MaxItems).WithMessage($"A meal can have at most {MaxItems} items.");
        RuleForEach(x => x.Items).SetValidator(new MealItemInputValidator());
    }
}

public sealed class MealItemInputValidator : AbstractValidator<MealItemInput>
{
    public MealItemInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300);
        RuleFor(x => x.QuantityGrams).GreaterThan(0).LessThanOrEqualTo(10_000m);
        RuleFor(x => x.Preparation).IsInEnum();
        RuleFor(x => x.Estimated).NotNull().When(x => x.FoodId is null)
            .WithMessage("Custom items need estimated nutrient values.");
        RuleFor(x => x.Estimated).Null().When(x => x.FoodId is not null)
            .WithMessage("Nutrients of database foods are calculated by the server and cannot be supplied.");
        When(x => x.Estimated is not null, () =>
        {
            RuleFor(x => x.Estimated!.Calories).InclusiveBetween(0, 10_000m).When(x => x.Estimated!.Calories is not null);
            RuleFor(x => x.Estimated!.ProteinG).InclusiveBetween(0, 1_000m).When(x => x.Estimated!.ProteinG is not null);
            RuleFor(x => x.Estimated!.CarbohydratesG).InclusiveBetween(0, 1_000m).When(x => x.Estimated!.CarbohydratesG is not null);
            RuleFor(x => x.Estimated!.FatG).InclusiveBetween(0, 1_000m).When(x => x.Estimated!.FatG is not null);
            RuleFor(x => x.Estimated!.FiberG).InclusiveBetween(0, 1_000m).When(x => x.Estimated!.FiberG is not null);
            RuleFor(x => x.Estimated!).Must(e => e.Calories is not null || e.ProteinG is not null || e.CarbohydratesG is not null || e.FatG is not null || e.FiberG is not null)
                .WithMessage("Enter at least one nutrient value.");
        });
    }
}

public sealed class NutritionTargetValidator : AbstractValidator<NutritionTargetDto>
{
    public NutritionTargetValidator()
    {
        RuleFor(x => x.Calories).InclusiveBetween(0, 20_000m).When(x => x.Calories is not null);
        RuleFor(x => x.ProteinG).InclusiveBetween(0, 1_000m).When(x => x.ProteinG is not null);
        RuleFor(x => x.CarbohydratesG).InclusiveBetween(0, 2_000m).When(x => x.CarbohydratesG is not null);
        RuleFor(x => x.FatG).InclusiveBetween(0, 1_000m).When(x => x.FatG is not null);
        RuleFor(x => x.FiberG).InclusiveBetween(0, 300m).When(x => x.FiberG is not null);
    }
}
