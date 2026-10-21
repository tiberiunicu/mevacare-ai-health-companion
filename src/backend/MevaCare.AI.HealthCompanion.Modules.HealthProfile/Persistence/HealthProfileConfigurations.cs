using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile.Persistence;

internal sealed class HealthProfileConfiguration : IEntityTypeConfiguration<Domain.HealthProfile>
{
    public void Configure(EntityTypeBuilder<Domain.HealthProfile> builder)
    {
        builder.ToTable("HealthProfiles", "health_profile", t =>
        {
            t.HasCheckConstraint("CK_HealthProfiles_HeightCm", "[HeightCm] IS NULL OR ([HeightCm] >= 50 AND [HeightCm] <= 260)");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Sex).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.ActivityLevel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.HeightCm).HasPrecision(5, 1);
        builder.Property(p => p.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.HasIndex(p => p.UserId).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HealthGoalConfiguration : IEntityTypeConfiguration<HealthGoal>
{
    public void Configure(EntityTypeBuilder<HealthGoal> builder)
    {
        builder.ToTable("HealthGoals", "health_profile", t =>
        {
            t.HasCheckConstraint("CK_HealthGoals_TargetWeightKg", "[TargetWeightKg] IS NULL OR ([TargetWeightKg] >= 20 AND [TargetWeightKg] <= 500)");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(g => g.TargetWeightKg).HasPrecision(5, 1);
        builder.HasIndex(g => g.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HealthPreferencesConfiguration : IEntityTypeConfiguration<HealthPreferences>
{
    public void Configure(EntityTypeBuilder<HealthPreferences> builder)
    {
        builder.ToTable("HealthPreferences", "health_profile");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Language).HasMaxLength(8).IsRequired();
        builder.Property(p => p.UnitSystem).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Theme).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(p => p.UserId).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
