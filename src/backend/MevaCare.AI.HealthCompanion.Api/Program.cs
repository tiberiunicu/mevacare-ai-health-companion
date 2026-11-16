using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MevaCare.AI.HealthCompanion.Api;
using MevaCare.AI.HealthCompanion.Api.Hosting;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Infrastructure;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Import;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // preserveStaticLogger: several hosts can start in one process (integration tests); freezing the shared bootstrap logger would make the second one fail.
    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext(), preserveStaticLogger: true);

    var modules = ModuleCatalog.CreateAll();
    var configuration = builder.Configuration;

    builder.Services.AddInfrastructure(configuration, modules.Select(m => m.ModelAssembly));
    foreach (var module in modules)
    {
        module.ConfigureServices(builder.Services, configuration);
    }

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecurityTransformer>());

    builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
    ForwardedHeadersSetup.Configure(builder.Services, configuration);

    var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(o => o.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
    }));

    var authPerMinute = configuration.GetValue("RateLimiting:AuthRequestsPerMinute", 10);
    var chatPerMinute = configuration.GetValue("RateLimiting:ChatRequestsPerMinute", 20);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy(RateLimitPolicies.Chat, http => RateLimitPartition.GetSlidingWindowLimiter(
            http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions { PermitLimit = chatPerMinute, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 }));
    });

    var app = builder.Build();

    // Maintenance command: dotnet run --project <Api> -- import-usda <folder with the extracted SR Legacy CSV files>
    if (args is ["import-usda", var importDirectory])
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        var result = await scope.ServiceProvider.GetRequiredService<UsdaSrLegacyImporter>()
            .ImportAsync(importDirectory, message => Log.Information("{Message}", message), CancellationToken.None);
        Log.Information(
            "USDA import finished: {Imported} foods imported, {Skipped} already present ({Nutrients} nutrient rows, {Portions} portion rows read)",
            result.FoodsImported, result.FoodsSkipped, result.NutrientRows, result.PortionRows);
        return 0;
    }

    // Maintenance command: dotnet run --project <Api> -- import-openfoodfacts <en.openfoodfacts.org.products.csv[.gz]> <countries, comma separated> [--limit N]
    // Packaged products with brand and barcode (Open Food Facts, ODbL licence). Countries are Open Food Facts names, e.g. romania,germany,united-kingdom.
    if (args is ["import-openfoodfacts", var offFile, var offCountries, ..] && (args.Length == 3 || (args.Length == 5 && args[3] == "--limit" && int.TryParse(args[4], out _))))
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        int? offLimit = args.Length == 5 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : null;
        var result = await scope.ServiceProvider.GetRequiredService<OpenFoodFactsImporter>()
            .ImportAsync(offFile, offCountries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), offLimit, message => Log.Information("{Message}", message), CancellationToken.None);
        Log.Information(
            "Open Food Facts import finished: {Imported} products imported, {Skipped} already present, {InCountries} in the selected countries of {Rows} rows ({Malformed} malformed rows)",
            result.Imported, result.SkippedExisting, result.InCountries, result.RowsRead, result.MalformedRows);
        foreach (var (reason, count) in result.Rejected.OrderByDescending(r => r.Value))
        {
            Log.Information("Rejected {Count} products: {Reason}", count, reason);
        }

        return 0;
    }

    // Knowledge maintenance (operator commands; there is deliberately no HTTP endpoint for ingestion):
    //   -- ingest-knowledge <folder with sources.json>      approve and index documents
    //   -- reindex-knowledge [--recreate]                   rebuild the vector index from SQL (after changing the embedding model)
    //   -- retire-knowledge <sourceId>                      stop using a source
    if (args is ["ingest-knowledge", var knowledgeFolder])
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        var report = await scope.ServiceProvider.GetRequiredService<KnowledgeFolderIngester>().IngestFolderAsync(knowledgeFolder, CancellationToken.None);
        foreach (var (file, message) in report)
        {
            Log.Information("{File}: {Message}", file, message);
        }

        return report.Any(r => r.Message.StartsWith("REJECTED", StringComparison.Ordinal)) ? 2 : 0;
    }

    if (args is ["reindex-knowledge", ..] && args.Skip(1).All(a => a == "--recreate"))
    {
        using var scope = app.Services.CreateScope();
        var count = await scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>()
            .ReindexAsync(args.Contains("--recreate"), CancellationToken.None);
        Log.Information("Re-indexed {Chunks} chunks", count);
        return 0;
    }

    if (args is ["retire-knowledge", var sourceId] && Guid.TryParse(sourceId, out var retireId))
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>().RetireAsync(retireId, CancellationToken.None);
        Log.Information("Source {SourceId} retired", retireId);
        return 0;
    }

    if (ForwardedHeadersSetup.IsEnabled(configuration))
    {
        app.UseForwardedHeaders(); // first: everything below (rate limits, HSTS, logging) must see the real client address and scheme
    }

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.UseCors();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    if (configuration.GetValue("Database:MigrateOnStartup", false))
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    foreach (var module in modules)
    {
        module.MapEndpoints(app);
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
