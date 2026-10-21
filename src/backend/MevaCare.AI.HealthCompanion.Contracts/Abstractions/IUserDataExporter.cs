namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>
/// Implemented by every module that stores personal data so that the GDPR export (right of access / portability)
/// automatically includes it. Deletion is handled by cascading foreign keys to the Users table.
/// </summary>
public interface IUserDataExporter
{
    /// <summary>Name of the section in the export document, e.g. "healthProfile".</summary>
    string Section { get; }

    Task<object?> ExportAsync(Guid userId, CancellationToken ct);
}
