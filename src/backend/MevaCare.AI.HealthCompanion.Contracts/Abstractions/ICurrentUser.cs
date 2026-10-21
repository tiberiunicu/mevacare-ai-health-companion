namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>
/// The authenticated caller, resolved from the validated access token on the server.
/// This is the ONLY source of a user id for application code: it must never come from a request body,
/// a route parameter or, in the AI module, from model output.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The caller's id. Throws <see cref="UnauthorizedAccessException"/> when not authenticated.</summary>
    Guid UserId { get; }
}
