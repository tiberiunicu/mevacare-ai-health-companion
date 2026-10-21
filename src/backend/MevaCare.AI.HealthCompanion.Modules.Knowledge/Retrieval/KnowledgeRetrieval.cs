using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Knowledge;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Domain;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Embeddings;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.Retrieval;

public interface IKnowledgeRetrievalService
{
    Task<IReadOnlyList<KnowledgePassage>> RetrieveAsync(string query, int maxPassages, CancellationToken ct);
}

/// <summary>
/// Semantic retrieval: embed the question, find the nearest chunks in the vector index, then load text and source metadata from SQL,
/// which is the authority. Retired sources and sources embedded with another model are never returned. Passages below the similarity
/// threshold are dropped, so "nothing relevant" is a legitimate result and the assistant can say so instead of guessing.
/// </summary>
public sealed class KnowledgeRetrievalService(
    AppDbContext db,
    IEmbeddingService embeddings,
    IVectorStore vectors,
    IOptions<KnowledgeOptions> options,
    ILogger<KnowledgeRetrievalService> logger) : IKnowledgeRetrievalService, IKnowledgeSearch
{
    public const int MaxQueryCharacters = 500;

    private readonly RetrievalOptions _options = options.Value.Retrieval;

    public async Task<KnowledgeSearchResult> SearchAsync(string query, int maxPassages, CancellationToken ct)
    {
        var indexAvailable = await db.Set<KnowledgeSource>().AnyAsync(s => s.Status == SourceStatus.Active, ct);
        if (!indexAvailable)
        {
            return new KnowledgeSearchResult([], IndexAvailable: false);
        }

        return new KnowledgeSearchResult(await RetrieveAsync(query, maxPassages, ct), IndexAvailable: true);
    }

    public async Task<IReadOnlyList<KnowledgePassage>> RetrieveAsync(string query, int maxPassages, CancellationToken ct)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
        {
            return [];
        }

        if (text.Length > MaxQueryCharacters)
        {
            text = text[..MaxQueryCharacters];
        }

        IReadOnlyList<VectorHit> hits;
        try
        {
            var vector = (await embeddings.EmbedAsync([text], ct))[0];
            hits = await vectors.SearchAsync(vector, _options.TopK * 2, _options.MinScore, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider or index problems: report a stable code; never the query, vectors or provider messages.
            logger.LogWarning("Knowledge retrieval failed: {ExceptionType}", ex.GetType().Name);
            throw new ServiceUnavailableException("knowledge_unavailable", "The medical knowledge index is not available right now.");
        }

        if (hits.Count == 0)
        {
            return [];
        }

        var ids = hits.Select(h => h.Id).ToList();
        var chunks = await db.Set<KnowledgeChunk>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var sourceIds = chunks.Select(c => c.SourceId).Distinct().ToList();
        var sources = await db.Set<KnowledgeSource>().AsNoTracking()
            .Where(s => sourceIds.Contains(s.Id) && s.Status == SourceStatus.Active)
            .ToDictionaryAsync(s => s.Id, ct);

        if (sources.Values.Any(s => s.EmbeddingModel != embeddings.ModelId))
        {
            logger.LogWarning("Knowledge sources were embedded with another model than {Model}; re-index required", embeddings.ModelId);
            throw new ServiceUnavailableException("knowledge_reindex_required", "The medical knowledge index must be rebuilt for the configured embedding model.");
        }

        var byId = chunks.ToDictionary(c => c.Id);
        var perSource = new Dictionary<Guid, int>();
        var passages = new List<KnowledgePassage>();
        foreach (var hit in hits.OrderByDescending(h => h.Score))
        {
            if (!byId.TryGetValue(hit.Id, out var chunk) || !sources.TryGetValue(chunk.SourceId, out var source))
            {
                continue; // retired or unknown: the index is only a hint, SQL decides
            }

            if (perSource.GetValueOrDefault(source.Id) >= _options.MaxPassagesPerSource)
            {
                continue; // keep several sources in the answer instead of one long page
            }

            perSource[source.Id] = perSource.GetValueOrDefault(source.Id) + 1;
            passages.Add(new KnowledgePassage(chunk.Id, chunk.Heading, chunk.Text, Math.Round(hit.Score, 4), source.ToInfo()));
            if (passages.Count >= Math.Clamp(maxPassages, 1, 20))
            {
                break;
            }
        }

        return passages;
    }
}
