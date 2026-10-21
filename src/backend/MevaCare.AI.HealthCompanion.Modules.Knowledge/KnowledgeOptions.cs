using System.ComponentModel.DataAnnotations;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge;

public enum EmbeddingProvider
{
    /// <summary>
    /// Offline feature-hashing baseline (no model, no network). It matches words, not meaning, so it is a development and
    /// test default, not a recommendation for production.
    /// </summary>
    Lexical = 0,
    OpenAI = 1,
    Ollama = 2,
}

public enum VectorStoreProvider
{
    Qdrant = 1,

    /// <summary>Non-persistent brute-force store for tests and quick local experiments.</summary>
    InMemory = 2,
}

public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>Registers the SearchMedicalKnowledge tool and the knowledge endpoints.</summary>
    public bool Enabled { get; init; } = true;

    public EmbeddingOptions Embeddings { get; init; } = new();
    public VectorStoreOptions VectorStore { get; init; } = new();
    public ChunkingOptions Chunking { get; init; } = new();
    public RetrievalOptions Retrieval { get; init; } = new();

    /// <summary>Only documents under one of these licences can be ingested. Extending the list is a deliberate, reviewed change.</summary>
    public List<string> AllowedLicenses { get; init; } = ["Public Domain (US Government work)", "CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0"];
}

public sealed class EmbeddingOptions
{
    public EmbeddingProvider Provider { get; init; } = EmbeddingProvider.Lexical;

    /// <summary>Embedding model name (OpenAI: for example a text-embedding model; Ollama: a pulled embedding model). Not used by Lexical.</summary>
    public string Model { get; init; } = "";

    /// <summary>Vector size. Lexical: any (default 1024; fewer dimensions mean more hash collisions and noisier scores). OpenAI: optional (models that support it can shorten vectors). Ollama: must match the model.</summary>
    [Range(8, 8192)] public int Dimensions { get; init; } = 1024;

    [Range(1, 256)] public int BatchSize { get; init; } = 32;
}

public sealed class VectorStoreOptions
{
    public VectorStoreProvider Provider { get; init; } = VectorStoreProvider.Qdrant;
    public string Url { get; init; } = "http://localhost:6333";

    /// <summary>Qdrant API key, if the server requires one. Keep it in user-secrets or an environment variable.</summary>
    public string ApiKey { get; init; } = "";

    public string Collection { get; init; } = "medical_knowledge";
}

public sealed class ChunkingOptions
{
    /// <summary>Target size of a chunk in characters (about 4 characters per token).</summary>
    [Range(200, 6000)] public int TargetCharacters { get; init; } = 900;

    /// <summary>No chunk is longer than this (except a single sentence that is itself longer).</summary>
    [Range(300, 8000)] public int MaxCharacters { get; init; } = 1400;

    /// <summary>Characters carried over from the end of the previous chunk, in whole sentences, to keep context across boundaries.</summary>
    [Range(0, 600)] public int OverlapCharacters { get; init; } = 150;
}

public sealed class RetrievalOptions
{
    [Range(1, 50)] public int TopK { get; init; } = 8;

    /// <summary>Cosine similarity below this is "not relevant". Depends on the embedding model: calibrate with the evaluation.</summary>
    [Range(0, 1)] public double MinScore { get; init; } = 0.15;

    [Range(1, 10)] public int MaxPassagesPerSource { get; init; } = 3;
}
