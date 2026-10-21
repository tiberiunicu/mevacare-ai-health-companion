using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Knowledge;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Domain;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Embeddings;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Retrieval;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.VectorStore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge;

public sealed record KnowledgeSourceDto(
    Guid Id, string Title, string Publisher, string Url, string License, string? LicenseUrl, string Language, DateOnly RetrievedOn, int Version, int ChunkCount);

/// <summary>
/// Trusted medical knowledge for retrieval-augmented answers: approved documents → text → chunks → embeddings → vector index →
/// retrieval with citations. Relational data (sources, chunk text) and the vector index are separate; ingestion is an operator action (CLI),
/// never an HTTP endpoint, and nothing is downloaded automatically.
/// </summary>
public sealed class KnowledgeModule : IModule
{
    public string Name => "Knowledge";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KnowledgeOptions>().Bind(configuration.GetSection(KnowledgeOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        // Created lazily: a missing embedding configuration only disables knowledge search (503 knowledge_unavailable), not the application.
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            EmbeddingGeneratorFactory.Create(sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value.Embeddings, sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IEmbeddingService>(sp => new EmbeddingService(
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value.Embeddings));

        services.AddHttpClient("qdrant", (sp, client) =>
        {
            var url = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value.VectorStore.Url;
            client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<IVectorStore>(sp =>
        {
            var store = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value.VectorStore;
            return store.Provider == VectorStoreProvider.InMemory
                ? new InMemoryVectorStore()
                : new QdrantVectorStore(sp.GetRequiredService<IHttpClientFactory>().CreateClient("qdrant"), store);
        });

        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
        services.AddScoped<KnowledgeFolderIngester>();
        services.AddScoped<KnowledgeRetrievalService>();
        services.AddScoped<IKnowledgeRetrievalService>(sp => sp.GetRequiredService<KnowledgeRetrievalService>());
        if (configuration.GetValue(KnowledgeOptions.SectionName + ":Enabled", true))
        {
            // Registering the search is what makes the assistant offer the SearchMedicalKnowledge tool.
            services.AddScoped<IKnowledgeSearch>(sp => sp.GetRequiredService<KnowledgeRetrievalService>());
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // The list of approved sources is public information inside the app: users can see what answers are based on.
        app.MapGet("/api/knowledge/sources", async (AppDbContext db, CancellationToken ct) =>
            {
                var sources = await db.Set<KnowledgeSource>().AsNoTracking().Include(s => s.Chunks)
                    .Where(s => s.Status == SourceStatus.Active).OrderBy(s => s.Publisher).ThenBy(s => s.Title).ToListAsync(ct);
                return TypedResults.Ok(sources.Select(s => new KnowledgeSourceDto(
                    s.Id, s.Title, s.Publisher, s.Url, s.License, s.LicenseUrl, s.Language, s.RetrievedOn, s.Version, s.ChunkCount)).ToList());
            })
            .WithTags("Knowledge");

        // Operational status for operators; contains no secrets and no user data.
        app.MapGet("/health/knowledge", async (AppDbContext db, IVectorStore store, IOptions<KnowledgeOptions> options, IServiceProvider services, CancellationToken ct) =>
            {
                var active = await db.Set<KnowledgeSource>().CountAsync(s => s.Status == SourceStatus.Active, ct);
                string? model = null;
                try
                {
                    model = services.GetRequiredService<IEmbeddingService>().ModelId;
                }
                catch (Exception)
                {
                    // embedding provider not configured
                }

                var available = await store.IsAvailableAsync(ct);
                return Results.Json(new
                {
                    enabled = options.Value.Enabled,
                    vectorStore = available ? "available" : "unavailable",
                    vectorStoreProvider = options.Value.VectorStore.Provider.ToString(),
                    embeddingModel = model ?? "not configured",
                    activeSources = active,
                }, statusCode: available ? 200 : 503);
            })
            .AllowAnonymous()
            .WithTags("Knowledge");
    }
}
