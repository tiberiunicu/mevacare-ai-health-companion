using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Auditing;

/// <summary>
/// Append-only security audit record. UserId is deliberately NOT a foreign key: the trail must survive
/// account deletion (it keeps only a pseudonymous id, never an e-mail address or health data).
/// </summary>
public sealed class AuditEvent : Entity
{
    private AuditEvent() { }

    public AuditEvent(string action, Guid? userId, string? details, DateTime occurredAtUtc)
    {
        Action = action;
        UserId = userId;
        Details = details;
        OccurredAtUtc = occurredAtUtc;
    }

    public string Action { get; private set; } = null!;
    public Guid? UserId { get; private set; }
    public string? Details { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents", "core");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Details).HasMaxLength(500);
        builder.HasIndex(e => new { e.UserId, e.OccurredAtUtc });
        builder.HasIndex(e => e.OccurredAtUtc);
    }
}

public sealed class AuditLog(Persistence.AppDbContext db, TimeProvider clock) : IAuditLog
{
    public void Record(string action, Guid? userId, string? details = null) =>
        db.Add(new AuditEvent(action, userId, details, clock.GetUtcNow().UtcDateTime));
}
