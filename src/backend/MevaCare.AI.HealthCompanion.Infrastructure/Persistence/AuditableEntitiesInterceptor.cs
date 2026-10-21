using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Persistence;

/// <summary>Stamps CreatedAtUtc / UpdatedAtUtc on every <see cref="AuditableEntity"/> using the injected clock.</summary>
public sealed class AuditableEntitiesInterceptor(TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(AuditableEntity.CreatedAtUtc)).CurrentValue = now;
                    entry.Property(nameof(AuditableEntity.UpdatedAtUtc)).CurrentValue = now;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(AuditableEntity.CreatedAtUtc)).IsModified = false;
                    entry.Property(nameof(AuditableEntity.UpdatedAtUtc)).CurrentValue = now;
                    break;
            }
        }
    }
}
