using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OpenAI;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.Embeddings;

/// <summary>Turns text into vectors. The only embedding abstraction the rest of the module sees.</summary>
public interface IEmbeddingService
{
    /// <summary>"provider/model/dimensions": stored with every source so vectors from different models are never mixed.</summary>
    string ModelId { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public sealed class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingOptions options) : IEmbeddingService
{
    public string ModelId { get; } = $"{options.Provider}/{(string.IsNullOrWhiteSpace(options.Model) ? LexicalEmbeddingGenerator.ModelName : options.Model)}/{options.Dimensions}";

    public int Dimensions => options.Dimensions;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var vectors = new List<float[]>(texts.Count);
        var generationOptions = options.Provider == EmbeddingProvider.OpenAI ? new EmbeddingGenerationOptions { Dimensions = options.Dimensions } : null;

        for (var offset = 0; offset < texts.Count; offset += options.BatchSize)
        {
            var batch = texts.Skip(offset).Take(options.BatchSize).ToList();
            var generated = await generator.GenerateAsync(batch, generationOptions, ct);
            if (generated.Count != batch.Count)
            {
                throw new InvalidOperationException($"The embedding provider returned {generated.Count} vectors for {batch.Count} texts.");
            }

            foreach (var embedding in generated)
            {
                var vector = embedding.Vector.ToArray();
                if (vector.Length != options.Dimensions)
                {
                    // A wrong size would silently corrupt the index; fail loudly and tell the operator how to fix the configuration.
                    throw new InvalidOperationException(
                        $"The embedding model returned {vector.Length} dimensions but Knowledge:Embeddings:Dimensions is {options.Dimensions}.");
                }

                vectors.Add(vector);
            }
        }

        return vectors;
    }
}

/// <summary>
/// An offline, deterministic embedding baseline using signed feature hashing of words and word pairs with sub-linear term
/// frequency, L2-normalised. It needs no model and no network. Texts that share words are close; texts that merely share
/// MEANING (synonyms, other languages) are not, which is exactly the gap a real embedding model closes.
/// </summary>
public sealed partial class LexicalEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelName = "hashing-v1";

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "if", "in", "is", "it", "its", "may",
        "me", "my", "of", "on", "or", "should", "that", "the", "their", "there", "these", "this", "to", "was", "what", "when", "which", "who",
        "why", "will", "with", "you", "your", "about", "also", "than", "then", "they", "we", "our", "not", "no", "so", "such",
    };

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v))).ToList());
        return Task.FromResult(result);
    }

    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        var tokens = Tokenize(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var pair = tokens[i] + " " + tokens[i + 1];
            counts[pair] = counts.GetValueOrDefault(pair) + 1;
        }

        foreach (var (feature, count) in counts)
        {
            var hash = Fnv1a(feature);
            var index = (int)(hash % (uint)dimensions);
            var sign = ((hash >> 31) & 1) == 0 ? 1f : -1f;
            var weight = (1f + MathF.Log(count)) * (feature.Contains(' ') ? 0.5f : 1f);
            vector[index] += sign * weight;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    public static List<string> Tokenize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return WordPattern().Matches(builder.ToString().ToLowerInvariant())
            .Select(m => Stem(m.Value))
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToList();
    }

    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            return word[..^3];
        }

        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal))
        {
            return word[..^3] + "y";
        }

        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        return word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) ? word[..^1] : word;
    }

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}

public static class EmbeddingGeneratorFactory
{
    /// <summary>
    /// Provider-specific code lives only here. OpenAI credentials and the Ollama endpoint are shared with the chat configuration
    /// (<c>Ai:OpenAI</c>, <c>Ai:Ollama</c>) so each secret is configured once.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options, IConfiguration configuration)
    {
        switch (options.Provider)
        {
            case EmbeddingProvider.Lexical:
                return new LexicalEmbeddingGenerator(options.Dimensions);

            case EmbeddingProvider.OpenAI:
            {
                var apiKey = configuration["Ai:OpenAI:ApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(options.Model))
                {
                    throw new InvalidOperationException("OpenAI embeddings need Ai:OpenAI:ApiKey and Knowledge:Embeddings:Model.");
                }

                var clientOptions = new OpenAIClientOptions();
                if (!string.IsNullOrWhiteSpace(configuration["Ai:OpenAI:Endpoint"]))
                {
                    clientOptions.Endpoint = new Uri(configuration["Ai:OpenAI:Endpoint"]!);
                }

                return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions).GetEmbeddingClient(options.Model).AsIEmbeddingGenerator();
            }

            case EmbeddingProvider.Ollama:
            {
                var endpoint = configuration["Ai:Ollama:Endpoint"];
                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(options.Model))
                {
                    throw new InvalidOperationException("Ollama embeddings need Ai:Ollama:Endpoint and Knowledge:Embeddings:Model.");
                }

                return new OllamaApiClient(new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(60) }, options.Model);
            }

            default:
                throw new InvalidOperationException($"Unknown embedding provider '{options.Provider}'.");
        }
    }
}
