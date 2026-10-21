using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Auditing;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile;
using MevaCare.AI.HealthCompanion.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MevaCare.AI.HealthCompanion.UnitTests.Support;

internal static class TestDb
{
    /// <summary>Fresh in-memory context with the real model configurations of the Identity and HealthProfile modules.</summary>
    public static AppDbContext Create(TimeProvider? clock = null)
    {
        var modelOptions = new PersistenceModelOptions();
        modelOptions.ModelAssemblies.Add(typeof(IdentityModule).Assembly);
        modelOptions.ModelAssemblies.Add(typeof(HealthProfileModule).Assembly);
        modelOptions.ModelAssemblies.Add(typeof(MevaCare.AI.HealthCompanion.Modules.Nutrition.NutritionModule).Assembly);
        modelOptions.ModelAssemblies.Add(typeof(MevaCare.AI.HealthCompanion.Modules.Assistant.AssistantModule).Assembly);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AuditableEntitiesInterceptor(clock ?? TimeProvider.System))
            .Options;
        return new AppDbContext(options, Options.Create(modelOptions));
    }

    public static FakeTimeProvider Clock(DateTimeOffset? start = null) =>
        new(start ?? new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
}

internal sealed class StubConsentService(bool granted) : IConsentService
{
    public Task<bool> IsGrantedAsync(Guid userId, ConsentType type, CancellationToken ct) => Task.FromResult(granted);

    public Task EnsureGrantedAsync(Guid userId, ConsentType type, CancellationToken ct) =>
        granted ? Task.CompletedTask : throw new ConsentRequiredException(type.ToString());
}
