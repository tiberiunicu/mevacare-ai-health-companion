using MevaCare.AI.HealthCompanion.Contracts.Knowledge;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using MevaCare.AI.HealthCompanion.Modules.Knowledge;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.VectorStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>Runs only when MEVACARE_QDRANT_URL points to a reachable Qdrant (for example docker compose up qdrant).</summary>
public sealed class QdrantFactAttribute : FactAttribute
{
    public const string Variable = "MEVACARE_QDRANT_URL";

    public QdrantFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} (for example http://localhost:6333) to run the Qdrant integration tests.";
        }
    }

    public static string Url => Environment.GetEnvironmentVariable(Variable)!;
}

public class QdrantVectorStoreTests : IAsyncLifetime
{
    private readonly string _collection = "mevacare_test_" + Guid.NewGuid().ToString("N");
    private readonly HttpClient _http = new() { BaseAddress = new Uri((Environment.GetEnvironmentVariable(QdrantFactAttribute.Variable) ?? "http://localhost:6333").TrimEnd('/') + "/") };
    private QdrantVectorStore Store => new(_http, new VectorStoreOptions { Collection = _collection });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(QdrantFactAttribute.Variable)))
        {
            await Store.DropCollectionAsync(default);
        }

        _http.Dispose();
    }

    [QdrantFact]
    public async Task Availability_creation_and_idempotent_ensure_work()
    {
        Assert.True(await Store.IsAvailableAsync(default));

        await Store.EnsureCollectionAsync(4, default);
        await Store.EnsureCollectionAsync(4, default); // second call is a no-op
    }

    [QdrantFact]
    public async Task A_collection_with_another_vector_size_is_refused_with_a_clear_message()
    {
        await Store.EnsureCollectionAsync(4, default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Store.EnsureCollectionAsync(8, default));

        Assert.Contains("--recreate", ex.Message, StringComparison.Ordinal);
    }

    [QdrantFact]
    public async Task Search_returns_the_nearest_points_best_first_above_the_threshold_with_their_source_ids()
    {
        var store = Store;
        await store.EnsureCollectionAsync(3, default);
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var near = new VectorPoint(Guid.NewGuid(), [1, 0.1f, 0], sourceA);
        var mid = new VectorPoint(Guid.NewGuid(), [0.5f, 1, 0], sourceB);
        var opposite = new VectorPoint(Guid.NewGuid(), [-1, 0, 0], sourceB);
        await store.UpsertAsync([mid, near, opposite], default);

        var hits = await store.SearchAsync([1, 0, 0], 10, 0.1, default);

        Assert.Equal([near.Id, mid.Id], hits.Select(h => h.Id));
        Assert.Equal(sourceA, hits[0].SourceId);
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.Single(await store.SearchAsync([1, 0, 0], 1, 0, default));
    }

    [QdrantFact]
    public async Task Deleting_by_source_removes_only_that_sources_points_and_upserting_the_same_id_replaces_it()
    {
        var store = Store;
        await store.EnsureCollectionAsync(2, default);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var id = Guid.NewGuid();
        await store.UpsertAsync([new VectorPoint(id, [1, 0], a), new VectorPoint(Guid.NewGuid(), [1, 0.2f], b)], default);
        await store.UpsertAsync([new VectorPoint(id, [0, 1], a)], default);

        Assert.Equal(2, (await store.SearchAsync([1, 1], 10, -1, default)).Count);
        await store.DeleteBySourceAsync(a, default);

        var remaining = await store.SearchAsync([1, 0], 10, -1, default);
        Assert.Single(remaining);
        Assert.Equal(b, remaining[0].SourceId);
    }

    [QdrantFact]
    public async Task Dropping_the_collection_and_searching_a_missing_one_behave_predictably()
    {
        var store = Store;
        await store.EnsureCollectionAsync(2, default);

        await store.DropCollectionAsync(default);
        await store.DropCollectionAsync(default); // already gone: not an error

        await Assert.ThrowsAsync<HttpRequestException>(() => store.SearchAsync([1, 0], 1, 0, default));
    }

    [QdrantFact]
    public async Task Errors_do_not_contain_the_query_vector()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Store.SearchAsync([0.123456f, 0.654321f], 1, 0, default));

        Assert.DoesNotContain("0.123456", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>The whole pipeline (ingest → embed → real Qdrant → retrieve) through the application's own DI.</summary>
public class QdrantPipelineTests : IClassFixture<QdrantPipelineTests.QdrantApiFactory>
{
    private readonly QdrantApiFactory _factory;

    public QdrantPipelineTests(QdrantApiFactory factory) => _factory = factory;

    public sealed class QdrantApiFactory : ApiFactory
    {
        public string Collection { get; } = "mevacare_pipeline_" + Guid.NewGuid().ToString("N");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            var url = Environment.GetEnvironmentVariable(QdrantFactAttribute.Variable);
            if (!string.IsNullOrWhiteSpace(url))
            {
                builder.UseSetting("Knowledge:VectorStore:Provider", "Qdrant");
                builder.UseSetting("Knowledge:VectorStore:Url", url);
                builder.UseSetting("Knowledge:VectorStore:Collection", Collection);
            }
        }

        protected override async Task BeforeDisposeAsync()
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(QdrantFactAttribute.Variable)))
            {
                await Services.GetRequiredService<IVectorStore>().DropCollectionAsync(default);
            }
        }
    }

    [QdrantFact]
    public async Task Documents_ingested_through_the_application_are_found_by_semantic_search_in_real_qdrant()
    {
        await KnowledgeSeed.EnsureAsync(_factory.Services);
        using var scope = _factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<IKnowledgeSearch>();

        var result = await search.SearchAsync("how long should adults sleep each night", 3, default);

        Assert.True(result.IndexAvailable);
        Assert.Equal("The Veltrin Sleep Guide", result.Passages[0].Source.Title);
        Assert.Contains("seven to nine hours", result.Passages[0].Text, StringComparison.Ordinal);
        Assert.Empty((await search.SearchAsync("capital of France", 3, default)).Passages);
    }
}
