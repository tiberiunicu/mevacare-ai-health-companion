using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.Google;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>
/// Boots the real API against a throw-away SQL Server database that is created through the EF Core migrations
/// (so the migrations themselves are exercised) and dropped afterwards.
/// Connection source, in order: env var MEVACARE_TEST_SQL, then the API's user-secrets connection string
/// (database name replaced). Only synthetic data is used.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"MevaCareAIHealthCompanion_Test_{Guid.NewGuid():N}";
    private readonly string _connectionString;

    /// <summary>Deterministic stand-in for the LLM provider: no test depends on a paid or non-deterministic model.</summary>
    public ScriptedChatClient Chat { get; } = new();

    /// <summary>Wraps the real embedding service so a test can simulate an outage of the embedding provider.</summary>
    public FlakyEmbeddingService Embeddings { get; } = new();

    /// <summary>Scripted stand-in for the WHOOP service; see <see cref="FakeWhoopHandler"/>.</summary>
    public FakeWhoopHandler Whoop { get; } = new();

    /// <summary>Scripted stand-in for Open Food Facts (the online food lookup); see <see cref="FakeOpenFoodFactsHandler"/>.</summary>
    public FakeOpenFoodFactsHandler OpenFoodFacts { get; } = new();

    /// <summary>Derived factories can change the WHOOP configuration (for example leave it unconfigured).</summary>
    protected virtual bool WhoopConfigured => true;

    protected virtual int ManualSyncIntervalSeconds => 0;

    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "mevacare-test-keys-" + Guid.NewGuid().ToString("N"));

    public ApiFactory() =>
        _connectionString = new SqlConnectionStringBuilder(BaseConnectionString()) { InitialCatalog = _databaseName }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:SigningKey", "integration-tests-only-signing-key-0123456789");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:RefreshCookieSecure", "false");
        builder.UseSetting("Auth:PasswordHashIterations", "100000");
        builder.UseSetting("Auth:MaxFailedLogins", "5");
        builder.UseSetting("Auth:RefreshReuseGraceSeconds", "0");
        builder.UseSetting("RateLimiting:AuthRequestsPerMinute", "100000");
        builder.UseSetting("RateLimiting:ChatRequestsPerMinute", "100000");
        builder.UseSetting("Knowledge:VectorStore:Provider", "InMemory");
        builder.UseSetting("Knowledge:Embeddings:Provider", "Lexical");
        builder.UseSetting("Knowledge:Embeddings:Dimensions", "1024");
        builder.UseSetting("Integrations:Sync:BackgroundEnabled", "false");
        builder.UseSetting("Integrations:Sync:MinManualIntervalSeconds", ManualSyncIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Integrations:DataProtectionKeysPath", _keysPath);
        builder.UseSetting("Integrations:FrontendBaseUrl", "http://localhost:5173");
        builder.UseSetting("Integrations:Whoop:ClientId", WhoopConfigured ? "test-client-id" : "");
        builder.UseSetting("Integrations:Whoop:ClientSecret", WhoopConfigured ? "test-client-secret" : "");
        builder.UseSetting("Integrations:Whoop:RedirectUri", "http://localhost:5080/api/integrations/whoop/callback");
        builder.UseSetting("Auth:Google:ClientId", "test-client.apps.googleusercontent.com");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleIdTokenValidator>();
            services.AddSingleton<IGoogleIdTokenValidator, FakeGoogleIdTokenValidator>();
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(Chat);
            services.Decorate_EmbeddingService(Embeddings);
            services.AddHttpClient<WhoopProvider>().ConfigurePrimaryHttpMessageHandler(() => Whoop);
            services.AddHttpClient<IOpenFoodFactsClient, OpenFoodFactsClient>().ConfigurePrimaryHttpMessageHandler(() => OpenFoodFacts);
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Hook for derived factories to clean up external resources while the host is still running.</summary>
    protected virtual Task BeforeDisposeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await BeforeDisposeAsync();
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        }
        finally
        {
            await base.DisposeAsync();
            try
            {
                if (Directory.Exists(_keysPath))
                {
                    Directory.Delete(_keysPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // best effort: the folder only holds throw-away test keys
            }
        }
    }

    public HttpClient CreateApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    private static string BaseConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MEVACARE_TEST_SQL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var secrets = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        return secrets.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "No SQL Server connection for integration tests. Set MEVACARE_TEST_SQL or configure the API user-secret 'ConnectionStrings:Default' (see README.md).");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>A signed-in synthetic user with a ready-to-use HTTP client.</summary>
public sealed class TestUser
{
    public required HttpClient Client { get; init; }
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required Guid Id { get; init; }
    public required string RefreshCookie { get; set; }
    public required string AccessToken { get; set; }

    public static async Task<TestUser> RegisterAsync(
        ApiFactory factory, bool healthConsent = true, bool aiConsent = false)
    {
        var client = factory.CreateApiClient();
        var email = $"user-{Guid.NewGuid():N}@example.test";
        const string password = "synthetic-passphrase-123";

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            displayName = "Synthetic User",
            acceptTerms = true,
            healthDataConsent = healthConsent,
            aiProcessingConsent = aiConsent,
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return new TestUser
        {
            Client = client,
            Email = email,
            Password = password,
            Id = body.GetProperty("user").GetProperty("id").GetGuid(),
            AccessToken = token,
            RefreshCookie = ExtractRefreshCookie(response)!,
        };
    }

    public static string? ExtractRefreshCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("mc_refresh=", StringComparison.Ordinal))
            : null;
}
