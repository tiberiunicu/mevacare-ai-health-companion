using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context of the modular monolith. Modules own their entities and
/// IEntityTypeConfiguration classes; the host tells this context which module assemblies to load
/// (see <see cref="PersistenceModelOptions"/>). Modules query through <c>Set&lt;T&gt;()</c>, so Infrastructure never
/// references a module and no dependency cycle appears.
/// Each module stores its tables in its own SQL schema so the boundaries stay visible in the database.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IOptions<PersistenceModelOptions> modelOptions)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        foreach (var assembly in modelOptions.Value.ModelAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime in the model is UTC; make SQL Server store it as datetime2 (never the legacy datetime).
        // SQL Server returns Kind=Unspecified; tag values as UTC so the API serializes them with a "Z" suffix.
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2").HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }
}

public sealed class PersistenceModelOptions
{
    public List<System.Reflection.Assembly> ModelAssemblies { get; } = [];
}

internal sealed class UtcDateTimeConverter()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
