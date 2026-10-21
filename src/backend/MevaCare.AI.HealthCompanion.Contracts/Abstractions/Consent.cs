namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

public enum ConsentType
{
    TermsOfService = 1,
    HealthDataProcessing = 2,
    AiProcessing = 3,
    ExternalIntegrations = 4,
}

public interface IConsentService
{
    Task<bool> IsGrantedAsync(Guid userId, ConsentType type, CancellationToken ct);

    /// <summary>Throws <see cref="ConsentRequiredException"/> (HTTP 403) when the consent is not currently granted.</summary>
    Task EnsureGrantedAsync(Guid userId, ConsentType type, CancellationToken ct);
}
