using MevaCare.AI.HealthCompanion.Modules.Knowledge.Embeddings;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>Decorates the real embedding service; <see cref="Fail"/> simulates a provider outage.</summary>
public sealed class FlakyEmbeddingService : IEmbeddingService
{
    private IEmbeddingService? _inner;

    public bool Fail { get; set; }

    internal void Attach(IEmbeddingService inner) => _inner = inner;

    public string ModelId => _inner!.ModelId;

    public int Dimensions => _inner!.Dimensions;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Fail ? throw new HttpRequestException("simulated embedding outage sk-secret-embed") : _inner!.EmbedAsync(texts, ct);
}

public static class KnowledgeTestExtensions
{
    internal static void Decorate_EmbeddingService(this IServiceCollection services, FlakyEmbeddingService flaky)
    {
        var original = services.Single(d => d.ServiceType == typeof(IEmbeddingService));
        services.Remove(original);
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            flaky.Attach((IEmbeddingService)original.ImplementationFactory!(sp));
            return flaky;
        });
    }
}

/// <summary>Synthetic approved documents. They describe invented things and are never medical information.</summary>
public static class KnowledgeSeed
{
    public const string SleepUrl = "https://example.test/veltrin-sleep";
    public const string IronUrl = "https://example.test/pelnor-iron";

    public const string SleepDocument = """
        # The Veltrin Sleep Guide

        ## Sleep duration

        Adults in the Veltrin study were advised to sleep seven to nine hours each night. Teenagers needed eight to ten hours. Regular bedtimes matter more than a single long night of sleep at the weekend.

        ## Evening habits

        Avoid caffeine after 14:00, because it can stay in the body for many hours. Heavy meals within two hours of bedtime delayed sleep onset in the study.
        """;

    public const string IronDocument = """
        # Pelnor Iron Facts

        ## Food sources

        Red meat, lentils, spinach and fortified cereals contain iron. Vitamin C helps absorb iron from plant foods, so adding citrus fruit or peppers to a lentil meal is helpful.

        ## Tea and coffee

        Tea and coffee with meals can reduce iron absorption. The Pelnor facts suggest drinking them between meals instead.
        """;

    public static async Task<Guid> IngestAsync(IServiceProvider services, string url, string title, string markdown, string license = "CC0-1.0")
    {
        using var scope = services.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>();
        var meta = new SourceMetadata(title, "Synthetic Test Publisher", url, license, null, "en", new DateOnly(2026, 1, 1), "integration-test");
        return (await ingestion.IngestAsync(new IngestRequest(meta, markdown, "text/markdown"), default)).SourceId;
    }

    public static async Task EnsureAsync(IServiceProvider services)
    {
        await IngestAsync(services, SleepUrl, "The Veltrin Sleep Guide", SleepDocument);
        await IngestAsync(services, IronUrl, "Pelnor Iron Facts", IronDocument);
    }
}
