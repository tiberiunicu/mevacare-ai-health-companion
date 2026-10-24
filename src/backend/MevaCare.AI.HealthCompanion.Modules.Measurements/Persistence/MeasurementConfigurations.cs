using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Measurements.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MevaCare.AI.HealthCompanion.Modules.Measurements.Persistence;

internal sealed class MeasurementSourceConfiguration : IEntityTypeConfiguration<MeasurementSource>
{
    public void Configure(EntityTypeBuilder<MeasurementSource> builder)
    {
        builder.ToTable("MeasurementSources", "measurements");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Provider).HasMaxLength(30).IsRequired();
        builder.HasIndex(s => new { s.UserId, s.Provider }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HealthMeasurementConfiguration : IEntityTypeConfiguration<HealthMeasurement>
{
    public void Configure(EntityTypeBuilder<HealthMeasurement> builder)
    {
        builder.ToTable("HealthMeasurements", "measurements");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Metric).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(m => m.Value).HasPrecision(18, 4);
        builder.Property(m => m.Unit).HasMaxLength(10).IsRequired();
        builder.Property(m => m.ExternalId).HasMaxLength(80);

        // Prevents duplicate wearable records: importing the same provider record twice updates it instead of adding a row.
        builder.HasIndex(m => new { m.SourceId, m.Metric, m.ExternalId }).IsUnique().HasFilter("[ExternalId] IS NOT NULL");
        builder.HasIndex(m => new { m.UserId, m.Metric, m.LocalDate });
        builder.HasOne<MeasurementSource>().WithMany().HasForeignKey(m => m.SourceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SleepSessionConfiguration : IEntityTypeConfiguration<SleepSession>
{
    public void Configure(EntityTypeBuilder<SleepSession> builder)
    {
        builder.ToTable("SleepSessions", "measurements", t =>
            t.HasCheckConstraint("CK_SleepSessions_Times", "[EndUtc] >= [StartUtc]"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.ExternalId).HasMaxLength(80).IsRequired();
        builder.Property(s => s.PerformancePercent).HasPrecision(7, 3);
        builder.Property(s => s.EfficiencyPercent).HasPrecision(7, 3);
        builder.Property(s => s.ConsistencyPercent).HasPrecision(7, 3);
        builder.Property(s => s.RespiratoryRate).HasPrecision(7, 3);
        builder.Ignore(s => s.AsleepMinutes);
        builder.HasIndex(s => new { s.SourceId, s.ExternalId }).IsUnique();
        builder.HasIndex(s => new { s.UserId, s.LocalDate });
        builder.HasOne<MeasurementSource>().WithMany().HasForeignKey(s => s.SourceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WorkoutConfiguration : IEntityTypeConfiguration<Workout>
{
    public void Configure(EntityTypeBuilder<Workout> builder)
    {
        builder.ToTable("Workouts", "measurements", t =>
            t.HasCheckConstraint("CK_Workouts_Times", "[EndUtc] >= [StartUtc]"));
        builder.HasKey(w => w.Id);
        builder.Property(w => w.ExternalId).HasMaxLength(80).IsRequired();
        builder.Property(w => w.SportName).HasMaxLength(80).IsRequired();
        builder.Property(w => w.Strain).HasPrecision(7, 3);
        builder.Property(w => w.Calories).HasPrecision(10, 2);
        builder.Property(w => w.DistanceMeters).HasPrecision(12, 2);
        builder.HasIndex(w => new { w.SourceId, w.ExternalId }).IsUnique();
        builder.HasIndex(w => new { w.UserId, w.LocalDate });
        builder.HasOne<MeasurementSource>().WithMany().HasForeignKey(w => w.SourceId).OnDelete(DeleteBehavior.Cascade);
    }
}
