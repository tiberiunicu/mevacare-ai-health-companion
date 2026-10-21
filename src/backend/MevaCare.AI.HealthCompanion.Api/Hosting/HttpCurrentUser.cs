using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Api.Hosting;

/// <summary>Resolves the caller from the validated JWT ('sub' claim). Nothing else may supply a user id.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public bool IsAuthenticated => TryGetUserId(out _);

    public Guid UserId => TryGetUserId(out var id)
        ? id
        : throw new UnauthorizedAccessException("The request is not authenticated.");

    private bool TryGetUserId(out Guid id)
    {
        var principal = accessor.HttpContext?.User;
        var sub = principal?.Identity?.IsAuthenticated == true ? principal.FindFirst("sub")?.Value : null;
        return Guid.TryParse(sub, out id);
    }
}
