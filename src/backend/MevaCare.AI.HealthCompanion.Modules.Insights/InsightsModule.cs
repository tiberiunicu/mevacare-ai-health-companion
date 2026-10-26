using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Insights;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Modules.Insights.Analysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.Modules.Insights;

/// <summary>
/// Health intelligence: trends, period comparisons and combined insights, computed on request from the caller's stored data.
/// No tables and no model: the module reads through the Measurements and Nutrition contracts (which check consent and filter by user)
/// and applies the rules in <see cref="InsightEngine"/>. Nothing is persisted, so an insight can never be stale or contradict its source.
/// </summary>
public sealed class InsightsModule : IModule
{
    public string Name => "Insights";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IInsightsAssistantApi, InsightsService>();

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/insights").WithTags("Insights")
            .MapGet("/", async (int? days, DateOnly? to, ICurrentUser user, IInsightsAssistantApi insights, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await insights.GetInsightsAsync(user.UserId, days ?? 30, to ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct)));
    }
}

public sealed class InsightsService(IMeasurementsAssistantApi measurements, INutritionAssistantApi nutrition) : IInsightsAssistantApi
{
    public const int MinDays = 7;
    public const int MaxDays = 90;

    public async Task<HealthInsightsDto> GetInsightsAsync(Guid userId, int days, DateOnly to, CancellationToken ct)
    {
        if (days is < MinDays or > MaxDays)
        {
            throw new BadRequestException("invalid_period", $"The period must be {MinDays} to {MaxDays} days.");
        }

        // The current period and the one of the same length before it, in one read per source.
        var from = to.AddDays(-(2 * days - 1));
        var weights = await measurements.GetWeightHistoryAsync(userId, from, to, ct);
        var sleep = await measurements.GetSleepRecoveryAsync(userId, from, to, ct);
        var meals = await nutrition.GetRangeSummaryAsync(userId, from, to, ct);

        var input = new InsightInput(
            to,
            days,
            weights.Points.Select(p => (p.LocalDate, p.Kilograms)).ToList(),
            meals.Days.Select(d => new NutritionDay(d.Date, d.Totals.Calories, d.Totals.ProteinG, d.Totals.IncompleteData)).ToList(),
            meals.Targets,
            sleep.Days);
        return InsightEngine.Analyse(input);
    }
}
