using System.Globalization;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Auditing;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Knowledge;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Embeddings;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Retrieval;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.AIEvaluationTests;

/// <summary>
/// Measures retrieval quality on a gold set: for each question, does a returned passage come from the expected source and contain the
/// expected phrase (recall@k), how high is the first correct passage (MRR), and do out-of-scope questions correctly return nothing.
/// The synthetic corpus runs everywhere with the offline lexical embeddings; the same harness runs on a real corpus and on a real
/// embedding model when configured (see docs/ai/ai-evaluation.md). Results with different embedding models are not comparable.
/// </summary>
public class RetrievalEvaluationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed record Question(string Q, string Source, string Phrase);

    private sealed record Gold(List<Question> Questions, List<string> Unanswerable);

    private static string DatasetPath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Datasets", .. parts]);

    private static Gold LoadGold(string file) =>
        JsonSerializer.Deserialize<Gold>(File.ReadAllText(DatasetPath(file)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed record Metrics(double RecallAt1, double RecallAt3, double RecallAt5, double Mrr, double SourceHitAt3, int FalsePositives, int Unanswerable, int Questions);

    private static async Task<(KnowledgeRetrievalService Retrieval, AppDbContext Db)> BuildAsync(string corpusFolder)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var knowledge = configuration.GetSection("Knowledge").Get<KnowledgeOptions>() ?? new KnowledgeOptions();

        var modelOptions = new PersistenceModelOptions();
        modelOptions.ModelAssemblies.Add(typeof(KnowledgeModule).Assembly);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Options.Create(modelOptions));

        var embeddings = new EmbeddingService(EmbeddingGeneratorFactory.Create(knowledge.Embeddings, configuration), knowledge.Embeddings);
        IVectorStore store = new InMemoryVectorStore();
        var wrapped = Options.Create(knowledge);
        var ingestion = new DocumentIngestionService(db, embeddings, store, wrapped, new AuditLog(db, TimeProvider.System), TimeProvider.System, NullLogger<DocumentIngestionService>.Instance);

        var report = await new KnowledgeFolderIngester(ingestion).IngestFolderAsync(corpusFolder, default);
        Assert.DoesNotContain(report, r => r.Message.StartsWith("REJECTED", StringComparison.Ordinal));
        return (new KnowledgeRetrievalService(db, embeddings, store, wrapped, NullLogger<KnowledgeRetrievalService>.Instance), db);
    }

    private async Task<Metrics> EvaluateAsync(KnowledgeRetrievalService retrieval, Gold gold)
    {
        int hit1 = 0, hit3 = 0, hit5 = 0, sourceHit3 = 0;
        double reciprocalRank = 0;
        foreach (var question in gold.Questions)
        {
            var passages = await retrieval.RetrieveAsync(question.Q, 5, default);
            var rank = passages
                .Select((p, i) => (p, rank: i + 1))
                .FirstOrDefault(x => x.p.Source.Title == question.Source && x.p.Text.Contains(question.Phrase, StringComparison.OrdinalIgnoreCase)).rank;
            var sourceRank = passages.Select((p, i) => (p, rank: i + 1)).FirstOrDefault(x => x.p.Source.Title == question.Source).rank;

            if (rank == 1) hit1++;
            if (rank is >= 1 and <= 3) hit3++;
            if (rank is >= 1 and <= 5) hit5++;
            if (sourceRank is >= 1 and <= 3) sourceHit3++;
            reciprocalRank += rank > 0 ? 1.0 / rank : 0;

            output.WriteLine($"{(rank > 0 ? $"#{rank}" : "MISS"),-5} {question.Q}  ->  {string.Join(" | ", passages.Take(3).Select(p => $"{p.Source.Title} ({p.Score:0.00})"))}");
        }

        var falsePositives = 0;
        foreach (var question in gold.Unanswerable)
        {
            var passages = await retrieval.RetrieveAsync(question, 5, default);
            if (passages.Count > 0)
            {
                falsePositives++;
            }

            output.WriteLine($"{(passages.Count == 0 ? "none" : "FALSE POSITIVE"),-14} {question}" + (passages.Count > 0 ? $" -> {passages[0].Source.Title} ({passages[0].Score:0.00})" : ""));
        }

        var n = gold.Questions.Count;
        var metrics = new Metrics((double)hit1 / n, (double)hit3 / n, (double)hit5 / n, reciprocalRank / n, (double)sourceHit3 / n, falsePositives, gold.Unanswerable.Count, n);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"recall@1 {metrics.RecallAt1:P0}  recall@3 {metrics.RecallAt3:P0}  recall@5 {metrics.RecallAt5:P0}  MRR {metrics.Mrr:0.00}  source-hit@3 {metrics.SourceHitAt3:P0}  false positives {falsePositives}/{gold.Unanswerable.Count}"));
        return metrics;
    }

    [Fact]
    public async Task Synthetic_corpus_retrieval_meets_the_quality_floor_with_the_offline_baseline()
    {
        var (retrieval, db) = await BuildAsync(DatasetPath("synthetic-knowledge"));
        await using var _ = db;

        var metrics = await EvaluateAsync(retrieval, LoadGold("retrieval-gold-synthetic.json"));

        // Floors for the lexical baseline on this small, clean corpus. It currently misses 1 of 17 questions on a purpose-built synonym gap
        // ("exercise" vs "activity"): that is the limit of matching words instead of meaning. A drop below the floors means chunking,
        // embedding or scoring regressed; a real embedding model is expected to close the gap.
        Assert.True(metrics.RecallAt3 >= 0.90, $"recall@3 {metrics.RecallAt3:P0} < 90%");
        Assert.True(metrics.SourceHitAt3 >= 0.90, $"source-hit@3 {metrics.SourceHitAt3:P0} < 90%");
        Assert.True(metrics.Mrr >= 0.85, $"MRR {metrics.Mrr:0.00} < 0.85");
        Assert.Equal(0, metrics.FalsePositives);
    }

    [Fact]
    public void The_gold_sets_are_well_formed()
    {
        foreach (var file in new[] { "retrieval-gold-synthetic.json", "retrieval-gold-demo-corpus.json" })
        {
            var gold = LoadGold(file);
            Assert.True(gold.Questions.Count >= 10, file);
            Assert.True(gold.Unanswerable.Count >= 3, file);
            Assert.All(gold.Questions, q => Assert.False(string.IsNullOrWhiteSpace(q.Phrase)));
        }
    }

    /// <summary>Real corpus: point MEVACARE_KNOWLEDGE_DIR at a folder with sources.json and the approved sample pages.</summary>
    [CorpusFact]
    public async Task Real_corpus_retrieval_is_measured_and_reported()
    {
        var (retrieval, db) = await BuildAsync(Environment.GetEnvironmentVariable("MEVACARE_KNOWLEDGE_DIR")!);
        await using var _ = db;

        var metrics = await EvaluateAsync(retrieval, LoadGold("retrieval-gold-demo-corpus.json"));

        var floor = double.Parse(Environment.GetEnvironmentVariable("MEVACARE_EVAL_MIN_RECALL_AT3") ?? "0.75", CultureInfo.InvariantCulture);
        Assert.True(metrics.RecallAt3 >= floor, $"recall@3 {metrics.RecallAt3:P0} < {floor:P0}");
    }
}

/// <summary>Skips unless MEVACARE_KNOWLEDGE_DIR points to a prepared corpus folder.</summary>
public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEVACARE_KNOWLEDGE_DIR")))
        {
            Skip = "Set MEVACARE_KNOWLEDGE_DIR to a folder with sources.json and the approved sample pages (see docs/ai/ai-evaluation.md).";
        }
    }
}
