namespace MevaCare.AI.HealthCompanion.Api.Hosting;

/// <summary>
/// Baseline response hardening. API responses contain health data and are therefore never cacheable
/// by browsers or intermediaries.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            if (http.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(http);
    }
}
