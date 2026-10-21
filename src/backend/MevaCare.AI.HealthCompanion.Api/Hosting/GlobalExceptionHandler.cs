using MevaCare.AI.HealthCompanion.Contracts.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MevaCare.AI.HealthCompanion.Api.Hosting;

/// <summary>
/// Single place that turns exceptions into RFC 9457 problem documents.
/// Expected failures (<see cref="AppException"/>) keep their message; anything else is a bug and is reported
/// as a generic 500 with a trace id, so internals (SQL, stack traces, health data) never reach the client.
/// </summary>
internal sealed class GlobalExceptionHandler(IProblemDetailsService problems, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
        {
            http.Response.StatusCode = 499; // client closed the request
            return true;
        }

        var problem = exception switch
        {
            AppException app => Describe(app.StatusCode, app.Message, app.Code),
            UnauthorizedAccessException => Describe(StatusCodes.Status401Unauthorized, "Authentication is required.", "unauthorized"),
            BadHttpRequestException => Describe(StatusCodes.Status400BadRequest, "The request body is malformed or has an invalid value.", "bad_request"),
            _ => Describe(StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "internal_error"),
        };

        if (problem.Status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
        }
        else
        {
            logger.LogInformation("Request rejected with {Status} {Code}", problem.Status, problem.Extensions["code"]);
        }

        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = null });
    }

    private static ProblemDetails Describe(int status, string detail, string code)
    {
        var problem = new ProblemDetails { Status = status, Title = ReasonPhrase(status), Detail = detail };
        problem.Extensions["code"] = code;
        return problem;
    }

    private static string ReasonPhrase(int status) => status switch
    {
        400 => "Bad request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        429 => "Too many requests",
        503 => "Service unavailable",
        _ => "Server error",
    };
}
