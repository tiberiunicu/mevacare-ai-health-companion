using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.VectorStore;

public sealed record VectorPoint(Guid Id, float[] Vector, Guid SourceId);

public sealed record VectorHit(Guid Id, double Score, Guid SourceId);

/// <summary>
/// Stores vectors and finds the nearest ones. Deliberately small: no text, no business data. The index only holds the id of a chunk
/// and the id of its source; the text, licence and metadata live in SQL, so the index can be dropped and rebuilt at any time.
/// </summary>
public interface IVectorStore
{
    /// <summary>Creates the collection if needed; throws when it exists with a different vector size.</summary>
    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);

    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    /// <summary>Nearest points by cosine similarity, best first, with similarity at least <paramref name="minScore"/>.</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int limit, double minScore, CancellationToken ct);

    Task DeleteBySourceAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Deletes the whole collection (used before a full re-index).</summary>
    Task DropCollectionAsync(CancellationToken ct);

    Task<bool> IsAvailableAsync(CancellationToken ct);
}

/// <summary>Brute-force cosine search in memory. For tests and quick local experiments; nothing is persisted.</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<Guid, VectorPoint> _points = new();
    private int _dimensions;

    public Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        if (_dimensions != 0 && _dimensions != dimensions && !_points.IsEmpty)
        {
            throw new InvalidOperationException($"The collection holds {_dimensions}-dimensional vectors, not {dimensions}.");
        }

        _dimensions = dimensions;
        return Task.CompletedTask;
    }

    public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        foreach (var point in points)
        {
            _points[point.Id] = point;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int limit, double minScore, CancellationToken ct)
    {
        IReadOnlyList<VectorHit> hits = _points.Values
            .Select(p => new VectorHit(p.Id, Cosine(vector, p.Vector), p.SourceId))
            .Where(h => h.Score >= minScore)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Id)
            .Take(limit)
            .ToList();
        return Task.FromResult(hits);
    }

    public Task DeleteBySourceAsync(Guid sourceId, CancellationToken ct)
    {
        foreach (var key in _points.Where(p => p.Value.SourceId == sourceId).Select(p => p.Key).ToList())
        {
            _points.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task DropCollectionAsync(CancellationToken ct)
    {
        _points.Clear();
        _dimensions = 0;
        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public int Count => _points.Count;

    internal static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>
/// Qdrant over its REST API (collections, points and the universal query endpoint). Cosine distance. Only ids are stored in the payload.
/// Errors surface as HttpRequestException with the status, never with the query vector or text.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _http;
    private readonly string _collection;

    public QdrantVectorStore(HttpClient http, VectorStoreOptions options)
    {
        _http = http;
        _collection = options.Collection;
        if (!string.IsNullOrWhiteSpace(options.ApiKey) && !_http.DefaultRequestHeaders.Contains("api-key"))
        {
            _http.DefaultRequestHeaders.Add("api-key", options.ApiKey);
        }
    }

    private string Path(string suffix = "") => $"collections/{Uri.EscapeDataString(_collection)}{suffix}";

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        var existing = await _http.GetAsync(Path(), ct);
        if (existing.IsSuccessStatusCode)
        {
            var info = await existing.Content.ReadFromJsonAsync<JsonNode>(ct);
            var size = info?["result"]?["config"]?["params"]?["vectors"]?["size"]?.GetValue<int>();
            if (size is not null && size != dimensions)
            {
                throw new InvalidOperationException(
                    $"Qdrant collection '{_collection}' has {size}-dimensional vectors but the embedding model produces {dimensions}. Re-index with --recreate.");
            }

            return;
        }

        var create = await _http.PutAsJsonAsync(Path(), new { vectors = new { size = dimensions, distance = "Cosine" } }, ct);
        await EnsureSuccess(create, "create collection");

        var index = await _http.PutAsJsonAsync(Path("/index?wait=true"), new { field_name = "sourceId", field_schema = "keyword" }, ct);
        await EnsureSuccess(index, "create payload index");
    }

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        foreach (var batch in points.Chunk(64))
        {
            var body = new
            {
                points = batch.Select(p => new { id = p.Id.ToString(), vector = p.Vector, payload = new { sourceId = p.SourceId.ToString() } }),
            };
            var response = await _http.PutAsJsonAsync(Path("/points?wait=true"), body, ct);
            await EnsureSuccess(response, "upsert points");
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int limit, double minScore, CancellationToken ct)
    {
        var body = new { query = vector, limit, with_payload = true, score_threshold = minScore };
        var response = await _http.PostAsJsonAsync(Path("/points/query"), body, ct);
        await EnsureSuccess(response, "query");

        var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
        var hits = new List<VectorHit>();
        foreach (var point in json?["result"]?["points"]?.AsArray() ?? [])
        {
            var id = Guid.Parse(point!["id"]!.GetValue<string>());
            var score = point["score"]!.GetValue<double>();
            var sourceId = Guid.Parse(point["payload"]?["sourceId"]?.GetValue<string>() ?? Guid.Empty.ToString());
            hits.Add(new VectorHit(id, score, sourceId));
        }

        return hits;
    }

    public async Task DeleteBySourceAsync(Guid sourceId, CancellationToken ct)
    {
        var body = new { filter = new { must = new[] { new { key = "sourceId", match = new { value = sourceId.ToString() } } } } };
        var response = await _http.PostAsJsonAsync(Path("/points/delete?wait=true"), body, ct);
        await EnsureSuccess(response, "delete points");
    }

    public async Task DropCollectionAsync(CancellationToken ct)
    {
        var response = await _http.DeleteAsync(Path(), ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            await EnsureSuccess(response, "drop collection");
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("collections", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string action)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Qdrant {action} failed with HTTP {(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}");
        }
    }
}
