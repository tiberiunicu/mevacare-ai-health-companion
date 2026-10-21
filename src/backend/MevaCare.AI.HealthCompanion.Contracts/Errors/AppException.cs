using Microsoft.AspNetCore.Http;

namespace MevaCare.AI.HealthCompanion.Contracts.Errors;

/// <summary>
/// An expected, user-facing failure. The API maps it to an RFC 9457 problem document.
/// Anything that is NOT an AppException is treated as a bug and reported as a generic 500.
/// </summary>
public abstract class AppException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string message = "The requested resource was not found.")
    : AppException("not_found", message, StatusCodes.Status404NotFound);

public sealed class ConflictException(string code, string message)
    : AppException(code, message, StatusCodes.Status409Conflict);

public sealed class BadRequestException(string code, string message)
    : AppException(code, message, StatusCodes.Status400BadRequest);

public sealed class UnauthorizedException(string message = "Authentication failed.")
    : AppException("unauthorized", message, StatusCodes.Status401Unauthorized);

public sealed class ConsentRequiredException(string consent)
    : AppException("consent_required", $"The '{consent}' consent is required for this operation.", StatusCodes.Status403Forbidden)
{
    public string Consent { get; } = consent;
}

public sealed class RateLimitedException(string message)
    : AppException("rate_limited", message, StatusCodes.Status429TooManyRequests);

public sealed class ServiceUnavailableException(string code, string message)
    : AppException(code, message, StatusCodes.Status503ServiceUnavailable);
