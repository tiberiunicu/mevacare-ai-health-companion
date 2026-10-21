using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
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
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"MevaCareAIHealthCompanion_Test_{Guid.NewGuid():N}";
    private readonly string _connectionString;

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
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        }
        finally
        {
            await base.DisposeAsync();
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
