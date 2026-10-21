using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.Domain;

public enum SourceStatus
{
    Active = 1,

    /// <summary>Superseded by a newer version or withdrawn. Never returned by retrieval; its vectors are deleted.</summary>
    Retired = 2,
}

/// <summary>
/// An approved document: who published it, where it came from, under which licence, who approved it and when.
/// This is the system of record. The vector index is derived from it and can be rebuilt at any time.
/// </summary>
public sealed class KnowledgeSource : AuditableEntity
{
    private readonly List<KnowledgeChunk> _chunks = [];

    private KnowledgeSource() { }

    public KnowledgeSource(
        string title, string publisher, string url, string license, string? licenseUrl, string language, DateOnly retrievedOn,
        string approvedBy, DateTime approvedAtUtc, string contentHash, int version)
    {
        Title = title;
        Publisher = publisher;
        Url = url;
        License = license;
        LicenseUrl = licenseUrl;
        Language = language;
        RetrievedOn = retrievedOn;
        ApprovedBy = approvedBy;
        ApprovedAtUtc = approvedAtUtc;
        ContentHash = contentHash;
        Version = version;
        Status = SourceStatus.Active;
        EmbeddingModel = "";
    }

    public string Title { get; private set; } = null!;
    public string Publisher { get; private set; } = null!;
    public string Url { get; private set; } = null!;
    public string License { get; private set; } = null!;
    public string? LicenseUrl { get; private set; }
    public string Language { get; private set; } = null!;
    public DateOnly RetrievedOn { get; private set; }
    public string ApprovedBy { get; private set; } = null!;
    public DateTime ApprovedAtUtc { get; private set; }

    /// <summary>SHA-256 of the extracted text. Re-ingesting identical content is a no-op; changed content becomes a new version.</summary>
    public string ContentHash { get; private set; } = null!;

    /// <summary>1, 2, 3... per URL.</summary>
    public int Version { get; private set; }

    public SourceStatus Status { get; private set; }

    /// <summary>Identity of the embedding model that produced the stored vectors ("provider/model/dimensions").</summary>
    public string EmbeddingModel { get; private set; } = null!;

    public int ChunkCount => _chunks.Count;
    public IReadOnlyCollection<KnowledgeChunk> Chunks => _chunks;

    public KnowledgeChunk AddChunk(int ordinal, string? heading, string text)
    {
        var chunk = new KnowledgeChunk(Id, ordinal, heading, text);
        _chunks.Add(chunk);
        return chunk;
    }

    public void MarkEmbedded(string embeddingModel) => EmbeddingModel = embeddingModel;

    public void Retire() => Status = SourceStatus.Retired;

    public KnowledgeSourceInfo ToInfo() => new(Id, Title, Publisher, Url, License, LicenseUrl, RetrievedOn, Language);
}

public sealed class KnowledgeChunk : Entity
{
    private KnowledgeChunk() { }

    internal KnowledgeChunk(Guid sourceId, int ordinal, string? heading, string text)
    {
        SourceId = sourceId;
        Ordinal = ordinal;
        Heading = heading;
        Text = text;
    }

    public Guid SourceId { get; private set; }

    /// <summary>Position inside the source (0-based); neighbouring chunks can be fetched for context.</summary>
    public int Ordinal { get; private set; }

    /// <summary>Heading path such as "Vitamin D > Sources", prepended to the text when embedding.</summary>
    public string? Heading { get; private set; }

    public string Text { get; private set; } = null!;
}

internal sealed class KnowledgeSourceConfiguration : IEntityTypeConfiguration<KnowledgeSource>
{
    public void Configure(EntityTypeBuilder<KnowledgeSource> builder)
    {
        builder.ToTable("Sources", "knowledge");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Title).HasMaxLength(300).IsRequired();
        builder.Property(s => s.Publisher).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Url).HasMaxLength(500).IsRequired();
        builder.Property(s => s.License).HasMaxLength(100).IsRequired();
        builder.Property(s => s.LicenseUrl).HasMaxLength(500);
        builder.Property(s => s.Language).HasMaxLength(8).IsRequired();
        builder.Property(s => s.ApprovedBy).HasMaxLength(100).IsRequired();
        builder.Property(s => s.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.EmbeddingModel).HasMaxLength(150).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Ignore(s => s.ChunkCount);
        builder.HasIndex(s => new { s.Url, s.ContentHash }).IsUnique();
        builder.HasIndex(s => s.Status);
        builder.HasMany(s => s.Chunks).WithOne().HasForeignKey(c => c.SourceId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Chunks).HasField("_chunks").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("Chunks", "knowledge");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Heading).HasMaxLength(500);
        builder.Property(c => c.Text).IsRequired();
        builder.HasIndex(c => new { c.SourceId, c.Ordinal }).IsUnique();
    }
}
