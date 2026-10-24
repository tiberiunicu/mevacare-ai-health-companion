using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Persistence;

internal sealed class ConnectedAccountConfiguration : IEntityTypeConfiguration<ConnectedAccount>
{
    public void Configure(EntityTypeBuilder<ConnectedAccount> builder)
    {
        builder.ToTable("ConnectedAccounts", "integrations");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Provider).HasMaxLength(30).IsRequired();
        builder.Property(a => a.ExternalUserId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(a => a.Scopes).HasMaxLength(500).IsRequired();
        builder.Property(a => a.AccessTokenProtected).HasMaxLength(4000).IsRequired();
        builder.Property(a => a.RefreshTokenProtected).HasMaxLength(4000).IsRequired();
        builder.Property(a => a.LastSyncStatus).HasMaxLength(60);
        builder.HasIndex(a => new { a.UserId, a.Provider }).IsUnique();
        builder.HasIndex(a => new { a.Provider, a.ExternalUserId }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SyncJobConfiguration : IEntityTypeConfiguration<SyncJob>
{
    public void Configure(EntityTypeBuilder<SyncJob> builder)
    {
        builder.ToTable("SyncJobs", "integrations");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Trigger).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.ErrorCode).HasMaxLength(60);
        builder.HasIndex(j => new { j.ConnectedAccountId, j.StartedAtUtc });
        builder.HasOne<ConnectedAccount>().WithMany().HasForeignKey(j => j.ConnectedAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OAuthStateConfiguration : IEntityTypeConfiguration<OAuthState>
{
    public void Configure(EntityTypeBuilder<OAuthState> builder)
    {
        builder.ToTable("OAuthStates", "integrations");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Provider).HasMaxLength(30).IsRequired();
        builder.Property(s => s.StateHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(s => s.StateHash).IsUnique();
        builder.HasIndex(s => s.ExpiresAtUtc);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
