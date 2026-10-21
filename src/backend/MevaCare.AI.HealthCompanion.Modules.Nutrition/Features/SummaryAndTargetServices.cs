using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;

public sealed class NutritionTargetService(AppDbContext db, IConsentService consents, IAuditLog audit)
{
    public async Task<NutritionTargetDto> GetAsync(Guid userId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        return await LoadAsync(userId, ct);
    }

    public async Task<NutritionTargetDto> SetAsync(Guid userId, NutritionTargetDto values, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var target = await db.Set<NutritionTarget>().SingleOrDefaultAsync(t => t.UserId == userId, ct);
        if (target is null)
        {
            target = new NutritionTarget(userId);
            db.Add(target);
        }

        target.Set(values);
        audit.Record("nutrition_target.updated", userId);
        await db.SaveChangesAsync(ct);
        return target.ToDto();
    }

    internal async Task<NutritionTargetDto> LoadAsync(Guid userId, CancellationToken ct)
    {
        var target = await db.Set<NutritionTarget>().AsNoTracking().SingleOrDefaultAsync(t => t.UserId == userId, ct);
        return target?.ToDto() ?? new NutritionTargetDto(null, null, null, null, null);
    }
}

/// <summary>Daily and range summaries, calculated from the stored item snapshots.</summary>
public sealed class NutritionSummaryService(AppDbContext db, IConsentService consents, NutritionTargetService targets)
{
    public const int MaxRangeDays = 366;

    public async Task<DailyNutritionSummaryDto> GetDailyAsync(Guid userId, DateOnly date, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var meals = await db.Set<Meal>().AsNoTracking().Include(m => m.Items)
            .Where(m => m.UserId == userId && m.LocalDate == date)
            .OrderBy(m => m.EatenAtUtc)
            .ToListAsync(ct);

        var totals = NutrientCalculator.Sum(meals.SelectMany(m => m.Items).Select(i => (i.Nutrients, i.IsEstimated)));
        var target = await targets.LoadAsync(userId, ct);
        return new DailyNutritionSummaryDto(date, totals, target, NutrientCalculator.Remaining(target, totals), meals.Select(m => m.ToDto()).ToList());
    }

    public async Task<NutritionRangeSummaryDto> GetRangeAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        if (to < from)
        {
            throw new BadRequestException("invalid_range", "The end date must not be before the start date.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            throw new BadRequestException("range_too_long", $"The range can span at most {MaxRangeDays} days.");
        }

        var meals = await db.Set<Meal>().AsNoTracking().Include(m => m.Items)
            .Where(m => m.UserId == userId && m.LocalDate >= from && m.LocalDate <= to)
            .ToListAsync(ct);

        var days = meals
            .GroupBy(m => m.LocalDate)
            .OrderBy(g => g.Key)
            .Select(g => new DayTotalsDto(
                g.Key, NutrientCalculator.Sum(g.SelectMany(m => m.Items).Select(i => (i.Nutrients, i.IsEstimated))), g.Count()))
            .ToList();

        // The average covers only days with logged meals: a day without logging is unknown, not a zero-calorie day.
        NutrientValues average = days.Count == 0
            ? NutrientValues.None
            : new NutrientValues(
                Math.Round(days.Average(d => d.Totals.Calories), 1),
                Math.Round(days.Average(d => d.Totals.ProteinG), 1),
                Math.Round(days.Average(d => d.Totals.CarbohydratesG), 1),
                Math.Round(days.Average(d => d.Totals.FatG), 1),
                Math.Round(days.Average(d => d.Totals.FiberG), 1));

        return new NutritionRangeSummaryDto(from, to, days, days.Count, average, await targets.LoadAsync(userId, ct));
    }
}
