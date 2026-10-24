using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Contracts.Validation;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Measurements.Domain;
using MevaCare.AI.HealthCompanion.Modules.Measurements.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.Modules.Measurements;

/// <summary>
/// Normalized health measurements with provenance: manual weight entries and data imported by integrations
/// (sleep sessions, recovery, strain, workouts). Knows nothing about any particular provider.
/// </summary>
public sealed class MeasurementsModule : IModule
{
    public string Name => "Measurements";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<MeasurementIngestService>();
        services.AddScoped<IMeasurementIngest>(sp => sp.GetRequiredService<MeasurementIngestService>());
        services.AddScoped<MeasurementQueryService>();
        services.AddScoped<IMeasurementsAssistantApi>(sp => sp.GetRequiredService<MeasurementQueryService>());
        services.AddScoped<IUserDataExporter, MeasurementsExporter>();
        services.AddValidatorsFromAssemblyContaining<MeasurementsModule>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/measurements").WithTags("Measurements");

        group.MapPost("/weight", async (AddWeightRequest request, ICurrentUser user, MeasurementQueryService service, CancellationToken ct) =>
            {
                var point = await service.AddWeightAsync(user.UserId, request, ct);
                return Results.Created($"/api/measurements/weight/{point.Id}", point);
            })
            .WithValidation<AddWeightRequest>();

        group.MapGet("/weight", async (DateOnly from, DateOnly to, ICurrentUser user, MeasurementQueryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetWeightHistoryAsync(user.UserId, from, to, ct)));

        group.MapDelete("/weight/{id:guid}", async (Guid id, ICurrentUser user, MeasurementQueryService service, CancellationToken ct) =>
        {
            await service.DeleteManualWeightAsync(user.UserId, id, ct);
            return TypedResults.NoContent();
        });

        group.MapGet("/sleep-recovery", async (DateOnly from, DateOnly to, ICurrentUser user, MeasurementQueryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetSleepRecoveryAsync(user.UserId, from, to, ct)));

        group.MapGet("/activity", async (DateOnly from, DateOnly to, ICurrentUser user, MeasurementQueryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetActivityAsync(user.UserId, from, to, ct)));
    }
}

/// <summary>Adds measurements and their provenance to the GDPR export.</summary>
public sealed class MeasurementsExporter(AppDbContext db) : IUserDataExporter
{
    public string Section => "measurements";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct)
    {
        var sources = await db.Set<MeasurementSource>().AsNoTracking().Where(s => s.UserId == userId).ToDictionaryAsync(s => s.Id, s => s.Provider, ct);
        var measurements = await db.Set<HealthMeasurement>().AsNoTracking().Where(m => m.UserId == userId).OrderBy(m => m.MeasuredAtUtc).ToListAsync(ct);
        var sleeps = await db.Set<SleepSession>().AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.StartUtc).ToListAsync(ct);
        var workouts = await db.Set<Workout>().AsNoTracking().Where(w => w.UserId == userId).OrderBy(w => w.StartUtc).ToListAsync(ct);
        return new
        {
            measurements = measurements.Select(m => new
            {
                Provider = sources.GetValueOrDefault(m.SourceId),
                Metric = m.Metric.ToString(),
                m.Value,
                m.Unit,
                m.MeasuredAtUtc,
                m.UtcOffsetMinutes,
                m.LocalDate,
            }),
            sleepSessions = sleeps.Select(s => new
            {
                Provider = sources.GetValueOrDefault(s.SourceId),
                s.StartUtc,
                s.EndUtc,
                s.UtcOffsetMinutes,
                s.LocalDate,
                s.IsNap,
                s.IsScored,
                s.TimeInBedMinutes,
                s.AwakeMinutes,
                s.LightMinutes,
                s.DeepMinutes,
                s.RemMinutes,
                s.PerformancePercent,
                s.EfficiencyPercent,
                s.ConsistencyPercent,
                s.RespiratoryRate,
                s.DisturbanceCount,
            }),
            workouts = workouts.Select(w => new
            {
                Provider = sources.GetValueOrDefault(w.SourceId),
                w.SportName,
                w.StartUtc,
                w.EndUtc,
                w.UtcOffsetMinutes,
                w.IsScored,
                w.Strain,
                w.AverageHeartRate,
                w.MaxHeartRate,
                w.Calories,
                w.DistanceMeters,
            }),
        };
    }
}
