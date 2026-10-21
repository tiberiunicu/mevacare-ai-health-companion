namespace MevaCare.AI.HealthCompanion.Contracts.Knowledge;

/// <summary>Identity and licence of an approved source document, kept with every passage so answers can cite it.</summary>
public sealed record KnowledgeSourceInfo(
    Guid Id, string Title, string Publisher, string Url, string License, string? LicenseUrl, DateOnly RetrievedOn, string Language);

public sealed record KnowledgePassage(Guid ChunkId, string? Heading, string Text, double Score, KnowledgeSourceInfo Source);

/// <param name="IndexAvailable">False when no approved source is indexed at all (as opposed to "nothing relevant found").</param>
public sealed record KnowledgeSearchResult(IReadOnlyList<KnowledgePassage> Passages, bool IndexAvailable);

/// <summary>A source shown to the user under an answer. <c>Cited</c> = the answer referenced it with [n]; otherwise it was only consulted.</summary>
public sealed record KnowledgeCitation(
    int Number, Guid SourceId, string Title, string Publisher, string Url, string License, DateOnly RetrievedOn, string Excerpt, bool Cited);

/// <summary>
/// What the AI assistant may use to answer medical-knowledge questions. Results are TRUSTED-SOURCE TEXT chosen by the
/// operator, but still handled as untrusted data by the assistant (documents can contain instructions).
/// </summary>
public interface IKnowledgeSearch
{
    /// <summary>Throws ServiceUnavailableException("knowledge_unavailable") when the embedding provider or vector store fails.</summary>
    Task<KnowledgeSearchResult> SearchAsync(string query, int maxPassages, CancellationToken ct);
}
