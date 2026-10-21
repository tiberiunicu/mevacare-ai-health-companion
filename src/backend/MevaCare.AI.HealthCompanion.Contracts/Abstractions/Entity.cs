namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>Base class for persisted entities. Ids are time-ordered GUIDs (v7) to keep clustered indexes compact.</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
}

/// <summary>
/// Entity with audit timestamps. Both values are UTC and are populated by the persistence layer
/// (see AuditableEntitiesInterceptor), never by callers.
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
}
