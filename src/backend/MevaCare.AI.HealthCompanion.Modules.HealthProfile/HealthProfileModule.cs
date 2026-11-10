using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Assistant;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Contracts.Validation;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile;

public sealed class HealthProfileModule : IModule
{
    public string Name => "HealthProfile";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<HealthProfileService>();
        services.AddScoped<IUserDataExporter, HealthProfileExporter>();
        services.AddScoped<IHealthProfileReader, HealthProfileReader>();
        services.AddScoped<IProfileAssistantApi, ProfileAssistantApi>();
        services.AddValidatorsFromAssemblyContaining<HealthProfileModule>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var profile = app.MapGroup("/api/health-profile").WithTags("Health profile");
        profile.MapGet("/", async (ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetProfileAsync(user.UserId, ct)));
        profile.MapPut("/", async (UpdateHealthProfileRequest request, ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpsertProfileAsync(user.UserId, request, ct)))
            .WithValidation<UpdateHealthProfileRequest>();

        var goals = app.MapGroup("/api/health-goals").WithTags("Health profile");
        goals.MapGet("/", async (ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListGoalsAsync(user.UserId, ct)));
        goals.MapPost("/", async (CreateHealthGoalRequest request, ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
            {
                var goal = await service.AddGoalAsync(user.UserId, request, ct);
                return TypedResults.Created($"/api/health-goals/{goal.Id}", goal);
            })
            .WithValidation<CreateHealthGoalRequest>();
        goals.MapDelete("/{id:guid}", async (Guid id, ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
        {
            await service.DeleteGoalAsync(user.UserId, id, ct);
            return TypedResults.NoContent();
        });

        var prefs = app.MapGroup("/api/health-preferences").WithTags("Health profile");
        prefs.MapGet("/", async (ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetPreferencesAsync(user.UserId, ct)));
        prefs.MapPut("/", async (UpdateHealthPreferencesRequest request, ICurrentUser user, HealthProfileService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpsertPreferencesAsync(user.UserId, request, ct)))
            .WithValidation<UpdateHealthPreferencesRequest>();
    }
}
