using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", "identity");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(512);
        builder.Ignore(u => u.HasPassword);
        builder.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        builder.HasIndex(u => u.NormalizedEmail).IsUnique();

        builder.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.Roles).HasField("_roles").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(u => u.ExternalLogins).WithOne().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.ExternalLogins).HasField("_externalLogins").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.ToTable("ExternalLogins", "identity");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Provider).HasMaxLength(32).IsRequired();
        builder.Property(l => l.Subject).HasMaxLength(255).IsRequired();
        builder.Property(l => l.Email).HasMaxLength(256);

        // One provider identity belongs to exactly one account, and one account has at most one identity per provider.
        builder.HasIndex(l => new { l.Provider, l.Subject }).IsUnique();
        builder.HasIndex(l => new { l.UserId, l.Provider }).IsUnique();
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles", "identity");
        builder.HasKey(r => new { r.UserId, r.Role });
        builder.Property(r => r.Role).HasMaxLength(32).IsRequired();
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", "identity");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetTokens", "identity");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.CreatedAtUtc });
        builder.Property<byte[]>("RowVersion").IsRowVersion();
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserConsentConfiguration : IEntityTypeConfiguration<UserConsent>
{
    public void Configure(EntityTypeBuilder<UserConsent> builder)
    {
        builder.ToTable("UserConsents", "identity");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(c => c.PolicyVersion).HasMaxLength(32).IsRequired();
        builder.HasIndex(c => new { c.UserId, c.Type }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
