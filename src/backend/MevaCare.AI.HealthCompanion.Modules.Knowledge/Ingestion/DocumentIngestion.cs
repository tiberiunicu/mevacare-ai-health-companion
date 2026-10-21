using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Domain;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.Embeddings;
using MevaCare.AI.HealthCompanion.Modules.Knowledge.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;

/// <summary>Who published a document, under which licence, and who approved it. Required for every document: no anonymous content gets in.</summary>
public sealed record SourceMetadata(
    string Title, string Publisher, string Url, string License, string? LicenseUrl, string Language, DateOnly RetrievedOn, string ApprovedBy);

public sealed record IngestRequest(SourceMetadata Metadata, string Content, string MediaType);

public enum IngestOutcome
{
    Ingested = 1,

    /// <summary>The same content from the same URL is already indexed.</summary>
    Unchanged = 2,
}

public sealed record IngestResult(IngestOutcome Outcome, Guid SourceId, int Version, int ChunksIndexed, IReadOnlyList<string> Notes);

/// <summary>An ingestion request that was refused. The message says why and is safe to show to the operator.</summary>
public sealed class IngestionException(string message) : Exception(message);

public interface IDocumentIngestionService
{
    Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct);

    Task RetireAsync(Guid sourceId, CancellationToken ct);

    /// <summary>Rebuilds the vector index from the chunks stored in SQL (after changing the embedding model). Returns the number of chunks indexed.</summary>
    Task<int> ReindexAsync(bool recreateCollection, CancellationToken ct);
}

/// <summary>Cheap pattern screen for text that tries to instruct an AI model. Defence in depth, not a guarantee.</summary>
public static partial class InjectionHeuristics
{
    public static bool LooksLikeInstructionToAModel(string text) => Patterns().IsMatch(text);

    [GeneratedRegex(
        @"ignore (all |any |the )?(previous|prior|above|earlier) (instructions|rules|prompts?|messages)"
        + @"|disregard (all |any |the )?(previous|prior|above|system|earlier)"
        + @"|\byou are now\b"
        + @"|\bsystem prompt\b"
        + @"|(reveal|print|show|repeat) (your|the) (instructions|prompt|rules)"
        + @"|do not (tell|inform) the user"
        + @"|<\s*/?\s*(system|assistant)\s*>"
        + @"|\[/?INST\]"
        + @"|^\s*(system|assistant)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Patterns();
}

public sealed class DocumentIngestionService(
    AppDbContext db,
    IEmbeddingService embeddings,
    IVectorStore vectors,
    IOptions<KnowledgeOptions> options,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<DocumentIngestionService> logger) : IDocumentIngestionService
{
    private const int MaxContentBytes = 2 * 1024 * 1024;
    private const int MinTextCharacters = 200;

    private readonly KnowledgeOptions _options = options.Value;

    public async Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct)
    {
        var meta = Validate(request);
        var notes = new List<string>();

        var document = DocumentExtractors.For(request.MediaType).Extract(request.Content);
        var text = document.PlainText;
        if (text.Length < MinTextCharacters)
        {
            throw new IngestionException($"Only {text.Length} characters of text could be extracted (minimum {MinTextCharacters}); the document is empty or in an unsupported layout.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var existing = await db.Set<KnowledgeSource>().SingleOrDefaultAsync(s => s.Url == meta.Url && s.ContentHash == hash, ct);
        if (existing is { Status: SourceStatus.Active } active && active.EmbeddingModel == embeddings.ModelId)
        {
            return new IngestResult(IngestOutcome.Unchanged, active.Id, active.Version, 0, ["The same content is already indexed."]);
        }

        if (existing is not null)
        {
            // Identical content that was retired (or embedded with another model): replace it instead of keeping two rows.
            await vectors.DeleteBySourceAsync(existing.Id, ct);
            db.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        var drafts = new Chunker(_options.Chunking).Chunk(document).ToList();
        var accepted = new List<ChunkDraft>();
        foreach (var draft in drafts)
        {
            if (InjectionHeuristics.LooksLikeInstructionToAModel(draft.Text) || (draft.Heading is not null && InjectionHeuristics.LooksLikeInstructionToAModel(draft.Heading)))
            {
                notes.Add($"Chunk {draft.Ordinal} was quarantined: it contains text that looks like an instruction to an AI model. Review the source.");
                continue;
            }

            accepted.Add(draft);
        }

        if (accepted.Count == 0)
        {
            throw new IngestionException("No usable text remained after screening; nothing was indexed.");
        }

        var texts = accepted.Select(c => EmbeddingText(c.Heading, c.Text)).ToList();
        var vectorsForChunks = await embeddings.EmbedAsync(texts, ct);

        var version = (await db.Set<KnowledgeSource>().Where(s => s.Url == meta.Url).MaxAsync(s => (int?)s.Version, ct) ?? 0) + 1;
        var source = new KnowledgeSource(
            meta.Title, meta.Publisher, meta.Url, meta.License, meta.LicenseUrl, meta.Language, meta.RetrievedOn, meta.ApprovedBy,
            clock.GetUtcNow().UtcDateTime, hash, version);
        var points = new List<VectorPoint>();
        for (var i = 0; i < accepted.Count; i++)
        {
            var chunk = source.AddChunk(accepted[i].Ordinal, accepted[i].Heading, accepted[i].Text);
            points.Add(new VectorPoint(chunk.Id, vectorsForChunks[i], source.Id));
        }

        source.MarkEmbedded(embeddings.ModelId);

        await vectors.EnsureCollectionAsync(embeddings.Dimensions, ct);
        await vectors.UpsertAsync(points, ct);
        try
        {
            db.Add(source);
            audit.Record("knowledge.source_ingested", null, $"{meta.Publisher} v{version}, {accepted.Count} chunks");
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Never leave vectors behind that SQL does not know about.
            await vectors.DeleteBySourceAsync(source.Id, CancellationToken.None);
            throw;
        }

        // The new version is searchable; now retire older versions of the same URL.
        var older = await db.Set<KnowledgeSource>().Where(s => s.Url == meta.Url && s.Id != source.Id && s.Status == SourceStatus.Active).ToListAsync(ct);
        foreach (var old in older)
        {
            old.Retire();
            await vectors.DeleteBySourceAsync(old.Id, ct);
            notes.Add($"Version {old.Version} of this source was retired.");
        }

        if (older.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Ingested knowledge source {Publisher} v{Version}: {Chunks} chunks", meta.Publisher, version, accepted.Count);
        return new IngestResult(IngestOutcome.Ingested, source.Id, version, accepted.Count, notes);
    }

    public async Task RetireAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.Set<KnowledgeSource>().SingleOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new IngestionException("Source not found.");
        source.Retire();
        audit.Record("knowledge.source_retired", null, source.Url);
        await vectors.DeleteBySourceAsync(source.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ReindexAsync(bool recreateCollection, CancellationToken ct)
    {
        if (recreateCollection)
        {
            await vectors.DropCollectionAsync(ct);
        }

        await vectors.EnsureCollectionAsync(embeddings.Dimensions, ct);
        var total = 0;
        var sources = await db.Set<KnowledgeSource>().Include(s => s.Chunks).Where(s => s.Status == SourceStatus.Active).ToListAsync(ct);
        foreach (var source in sources)
        {
            var chunks = source.Chunks.OrderBy(c => c.Ordinal).ToList();
            var vectorsForChunks = await embeddings.EmbedAsync(chunks.Select(c => EmbeddingText(c.Heading, c.Text)).ToList(), ct);
            await vectors.DeleteBySourceAsync(source.Id, ct);
            await vectors.UpsertAsync(chunks.Select((c, i) => new VectorPoint(c.Id, vectorsForChunks[i], source.Id)).ToList(), ct);
            source.MarkEmbedded(embeddings.ModelId);
            total += chunks.Count;
        }

        audit.Record("knowledge.reindexed", null, $"{sources.Count} sources, {total} chunks, {embeddings.ModelId}");
        await db.SaveChangesAsync(ct);
        return total;
    }

    /// <summary>The text that is embedded: the heading gives short chunks the context of their section.</summary>
    internal static string EmbeddingText(string? heading, string text) => heading is null ? text : $"{heading}\n{text}";

    private SourceMetadata Validate(IngestRequest request)
    {
        var m = request.Metadata;
        string Required(string value, string name, int max)
        {
            var v = value?.Trim() ?? "";
            return v.Length is > 0 && v.Length <= max ? v : throw new IngestionException($"'{name}' is required (at most {max} characters).");
        }

        var title = Required(m.Title, "title", 300);
        var publisher = Required(m.Publisher, "publisher", 200);
        var approvedBy = Required(m.ApprovedBy, "approvedBy", 100);
        var license = Required(m.License, "license", 100);
        var url = Required(m.Url, "url", 500);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new IngestionException("'url' must be an absolute http(s) URL that identifies the source (it is shown to users as the citation link).");
        }

        var allowed = _options.AllowedLicenses.FirstOrDefault(l => string.Equals(l, license, StringComparison.OrdinalIgnoreCase))
            ?? throw new IngestionException($"The licence '{license}' is not on the allow-list ({string.Join(", ", _options.AllowedLicenses)}).");

        string? licenseUrl = null;
        if (!string.IsNullOrWhiteSpace(m.LicenseUrl))
        {
            licenseUrl = m.LicenseUrl.Trim();
            if (!Uri.TryCreate(licenseUrl, UriKind.Absolute, out var l) || l.Scheme is not ("http" or "https") || licenseUrl.Length > 500)
            {
                throw new IngestionException("'licenseUrl' must be an absolute http(s) URL.");
            }
        }

        if (!Regex.IsMatch(m.Language ?? "", "^[a-z]{2}$"))
        {
            throw new IngestionException("'language' must be a two-letter code such as 'en'.");
        }

        if (m.RetrievedOn > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
        {
            throw new IngestionException("'retrievedOn' cannot be in the future.");
        }

        if (!DocumentExtractors.SupportedMediaTypes.Contains(request.MediaType))
        {
            throw new IngestionException($"Media type '{request.MediaType}' is not supported (text/html, text/markdown, text/plain).");
        }

        if (Encoding.UTF8.GetByteCount(request.Content) > MaxContentBytes)
        {
            throw new IngestionException("The document is larger than 2 MB.");
        }

        return new SourceMetadata(title, publisher, uri.AbsoluteUri, allowed, licenseUrl, m.Language!, m.RetrievedOn, approvedBy);
    }
}

/// <summary>Ingests a folder that contains a <c>sources.json</c> manifest next to the approved document files.</summary>
public sealed class KnowledgeFolderIngester(IDocumentIngestionService ingestion)
{
    private sealed record ManifestEntry(
        string File, string Title, string Publisher, string Url, string License, string? LicenseUrl, string Language, string RetrievedOn,
        string ApprovedBy, string? MediaType);

    public async Task<IReadOnlyList<(string File, string Message)>> IngestFolderAsync(string folder, CancellationToken ct)
    {
        var manifestPath = Path.Combine(folder, "sources.json");
        if (!File.Exists(manifestPath))
        {
            throw new IngestionException($"'sources.json' not found in '{folder}'.");
        }

        var entries = JsonSerializer.Deserialize<List<ManifestEntry>>(
            await File.ReadAllTextAsync(manifestPath, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new IngestionException("sources.json is empty.");

        var root = Path.GetFullPath(folder);
        var report = new List<(string, string)>();
        foreach (var entry in entries)
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, entry.File ?? ""));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IngestionException("The file must be inside the manifest folder.");
                }

                if (!DateOnly.TryParseExact(entry.RetrievedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var retrieved))
                {
                    throw new IngestionException("'retrievedOn' must be yyyy-MM-dd.");
                }

                var mediaType = entry.MediaType ?? DocumentExtractors.MediaTypeFromExtension(path);
                var meta = new SourceMetadata(entry.Title, entry.Publisher, entry.Url, entry.License, entry.LicenseUrl, entry.Language, retrieved, entry.ApprovedBy);
                var result = await ingestion.IngestAsync(new IngestRequest(meta, await File.ReadAllTextAsync(path, ct), mediaType), ct);
                report.Add((entry.File!, $"{result.Outcome}: version {result.Version}, {result.ChunksIndexed} chunks" + (result.Notes.Count > 0 ? " | " + string.Join(" ", result.Notes) : "")));
            }
            catch (Exception ex) when (ex is IngestionException or NotSupportedException or FileNotFoundException or IOException)
            {
                report.Add((entry.File ?? "?", "REJECTED: " + ex.Message));
            }
        }

        return report;
    }
}
