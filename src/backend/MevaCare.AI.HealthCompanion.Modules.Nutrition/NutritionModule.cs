using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Contracts.Validation;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Import;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition;

public sealed class NutritionModule : IModule
{
    public string Name => "Nutrition";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IFoodCatalog, LocalFoodCatalog>();
        services.AddScoped<MealService>();
        services.AddScoped<NutritionTargetService>();
        services.AddScoped<NutritionSummaryService>();
        services.AddScoped<INutritionAssistantApi, NutritionAssistantApi>();
        services.AddScoped<UsdaSrLegacyImporter>();
        services.AddScoped<OpenFoodFactsImporter>();
        services.AddScoped<OnlineFoodService>();
        services.AddOptions<OpenFoodFactsOptions>().Bind(configuration.GetSection(OpenFoodFactsOptions.SectionName)).ValidateDataAnnotations();
        services.AddSingleton<SlidingRateGate>();
        services.AddHttpClient<IOpenFoodFactsClient, OpenFoodFactsClient>((sp, client) =>
        {
            var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenFoodFactsOptions>>().Value;
            client.BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
            // Open Food Facts asks API users to identify the application (and, if possible, a contact).
            var agent = "MevaCare-AI-HealthCompanion/1.0";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(settings.ContactEmail) ? agent : $"{agent} ({settings.ContactEmail.Trim()})");
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds + 5);
        });
        services.AddScoped<IUserDataExporter, NutritionExporter>();
        services.AddValidatorsFromAssemblyContaining<NutritionModule>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/nutrition").WithTags("Nutrition");

        group.MapGet("/foods/search", async (string q, PreparationState? preparation, int? limit, IFoodCatalog catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2 || q.Length > 100)
            {
                throw new BadRequestException("invalid_query", "The search text must have 2 to 100 characters.");
            }

            var result = await catalog.SearchAsync(
                new FoodQuery(q, preparation ?? PreparationState.Unspecified, Math.Clamp(limit ?? 10, 1, 25), IncludeNutrients: true), ct);
            return TypedResults.Ok(result.Matches.Select(m => FoodDtos.ToSearchItem(m.Food, result.Ambiguous, result.PreparationAssumed)).ToList());
        });

        // Packaged products from Open Food Facts that are not in the local database. Search = preview only; add = store one product by barcode.
        group.MapGet("/foods/online/search", async (string q, ICurrentUser user, OnlineFoodService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SearchAsync(user.UserId, q, ct)));

        group.MapPost("/foods/online/add", async (AddOnlineFoodRequest request, ICurrentUser user, OnlineFoodService service, CancellationToken ct) =>
            TypedResults.Ok(await service.AddAsync(user.UserId, request.Barcode, ct)));

        group.MapGet("/summary/daily", async (DateOnly date, ICurrentUser user, NutritionSummaryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetDailyAsync(user.UserId, date, ct)));

        group.MapGet("/summary/range", async (DateOnly from, DateOnly to, ICurrentUser user, NutritionSummaryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetRangeAsync(user.UserId, from, to, ct)));

        group.MapGet("/targets", async (ICurrentUser user, NutritionTargetService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetAsync(user.UserId, ct)));

        group.MapPut("/targets", async (NutritionTargetDto request, ICurrentUser user, NutritionTargetService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SetAsync(user.UserId, request, ct)))
            .WithValidation<NutritionTargetDto>();

        group.MapPost("/meals", async (SaveMealRequest request, ICurrentUser user, MealService service, CancellationToken ct) =>
            {
                var result = await service.CreateAsync(user.UserId, request, ct);
                return result.Created
                    ? Results.Created($"/api/nutrition/meals/{result.Meal.Id}", result.Meal)
                    : Results.Ok(result.Meal);
            })
            .WithValidation<SaveMealRequest>();

        group.MapGet("/meals/{id:guid}", async (Guid id, ICurrentUser user, MealService service, CancellationToken ct) =>
            TypedResults.Ok(await service.LoadAsync(user.UserId, id, ct)));

        group.MapPut("/meals/{id:guid}", async (Guid id, SaveMealRequest request, ICurrentUser user, MealService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(user.UserId, id, request, ct)))
            .WithValidation<SaveMealRequest>();

        group.MapDelete("/meals/{id:guid}", async (Guid id, ICurrentUser user, MealService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(user.UserId, id, ct);
            return TypedResults.NoContent();
        });
    }
}

/// <summary>Adds meals and targets to the GDPR export.</summary>
public sealed class NutritionExporter(AppDbContext db) : IUserDataExporter
{
    public string Section => "nutrition";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct)
    {
        var meals = await db.Set<Meal>().AsNoTracking().Include(m => m.Items).Where(m => m.UserId == userId).OrderBy(m => m.EatenAtUtc).ToListAsync(ct);
        var target = await db.Set<NutritionTarget>().AsNoTracking().SingleOrDefaultAsync(t => t.UserId == userId, ct);
        return new
        {
            targets = target?.ToDto(),
            meals = meals.Select(m => new
            {
                Type = m.Type.ToString(),
                m.EatenAtUtc,
                m.LocalDate,
                m.UtcOffsetMinutes,
                m.Notes,
                Origin = m.Origin.ToString(),
                items = m.Items.Select(i => new
                {
                    i.Name,
                    i.QuantityGrams,
                    Preparation = i.Preparation.ToString(),
                    i.IsEstimated,
                    i.SourceLabel,
                    i.Calories,
                    i.ProteinG,
                    i.CarbohydratesG,
                    i.FatG,
                    i.FiberG,
                }),
            }),
        };
    }
}
