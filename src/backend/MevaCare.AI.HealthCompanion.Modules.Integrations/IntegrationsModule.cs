using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Features;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations;

/// <summary>
/// Connected wearable/health accounts: OAuth connection, protected token storage, synchronization jobs.
/// Providers implement <see cref="IWearableProvider"/>; the data they return is normalized and handed to the Measurements module
/// through <c>IMeasurementIngest</c>, so no domain entity depends on a vendor.
/// </summary>
public sealed class IntegrationsModule : IModule
{
    public string Name => "Integrations";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IntegrationsOptions>(configuration.GetSection(IntegrationsOptions.Section));

        var protection = services.AddDataProtection().SetApplicationName("MevaCare.AI.HealthCompanion");
        var keysPath = configuration[$"{IntegrationsOptions.Section}:{nameof(IntegrationsOptions.DataProtectionKeysPath)}"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        services.AddSingleton<ITokenProtector, DataProtectionTokenProtector>();
        services.AddSingleton<SyncLocks>();
        services.AddHttpClient<WhoopProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddTransient<IWearableProvider>(sp => sp.GetRequiredService<WhoopProvider>());
        services.AddScoped<ConnectionService>();
        services.AddScoped<SyncService>();
        services.AddHostedService<SyncBackgroundService>();
        services.AddScoped<IUserDataExporter, IntegrationsExporter>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/integrations").WithTags("Integrations");

        group.MapGet("/", async (ICurrentUser user, ConnectionService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(user.UserId, ct)));

        group.MapPost("/{provider}/connect", async (string provider, ICurrentUser user, ConnectionService service, CancellationToken ct) =>
            TypedResults.Ok(await service.StartConnectAsync(user.UserId, provider, ct)));

        // The provider redirects the browser here: no bearer token, the single-use state identifies the user. Always answers with a redirect to the SPA.
        group.MapGet("/{provider}/callback", async (
                string provider, string? code, string? state, string? error, ConnectionService service, IOptions<IntegrationsOptions> options, CancellationToken ct) =>
            {
                var result = await service.CompleteAsync(provider, code, state, error, ct);
                var target = options.Value.FrontendBaseUrl.TrimEnd('/') + "/devices?"
                    + (result.Succeeded ? $"connected={Uri.EscapeDataString(result.Provider!)}" : $"error={Uri.EscapeDataString(result.ErrorCode!)}");
                return Results.Redirect(target);
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Auth);

        group.MapPost("/{provider}/sync", async (string provider, ICurrentUser user, SyncService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SyncAsync(user.UserId, provider, SyncTrigger.Manual, ct)));

        group.MapGet("/{provider}/sync-jobs", async (string provider, int? limit, ICurrentUser user, SyncService service, CancellationToken ct) =>
            TypedResults.Ok(await service.RecentJobsAsync(user.UserId, provider, limit ?? 10, ct)));

        group.MapDelete("/{provider}", async (string provider, bool? deleteData, ICurrentUser user, ConnectionService service, CancellationToken ct) =>
        {
            await service.DisconnectAsync(user.UserId, provider, deleteData ?? false, ct);
            return TypedResults.NoContent();
        });
    }
}

/// <summary>Adds the connections (never the tokens) to the GDPR export.</summary>
public sealed class IntegrationsExporter(AppDbContext db) : IUserDataExporter
{
    public string Section => "integrations";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct)
    {
        var accounts = await db.Set<ConnectedAccount>().AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.Provider).ToListAsync(ct);
        return new
        {
            connectedAccounts = accounts.Select(a => new
            {
                a.Provider,
                Status = a.Status.ToString(),
                a.Scopes,
                a.ConnectedAtUtc,
                a.LastSyncAtUtc,
                a.LastSyncStatus,
            }),
        };
    }
}
