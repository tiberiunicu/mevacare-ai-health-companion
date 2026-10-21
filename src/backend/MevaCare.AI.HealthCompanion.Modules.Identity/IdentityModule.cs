using System.Text;
using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Validation;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Account;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Consents;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MevaCare.AI.HealthCompanion.Modules.Identity;

public sealed class IdentityModule : IModule
{
    private const string RefreshCookieName = "mc_refresh";
    private const string RefreshCookiePath = "/api/auth";

    public string Name => "Identity";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddOptions<PasswordHasherOptions>().Configure<IOptions<AuthOptions>>((hasher, auth) =>
        {
            hasher.IterationCount = auth.Value.PasswordHashIterations;
        });
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<ConsentService>();
        services.AddScoped<IConsentService>(sp => sp.GetRequiredService<ConsentService>());
        services.AddScoped<AccountService>();
        services.AddValidatorsFromAssemblyContaining<IdentityModule>(includeInternalTypes: true);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build());
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/register", async (RegisterRequest request, AuthService service, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                var session = await service.RegisterAsync(request, ct);
                WriteRefreshCookie(http, session, options.Value);
                return TypedResults.Created("/api/auth/me", session.Response);
            })
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth).WithValidation<RegisterRequest>();

        auth.MapPost("/login", async (LoginRequest request, AuthService service, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                var session = await service.LoginAsync(request, ct);
                WriteRefreshCookie(http, session, options.Value);
                return TypedResults.Ok(session.Response);
            })
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth).WithValidation<LoginRequest>();

        auth.MapPost("/refresh", async (AuthService service, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                try
                {
                    var session = await service.RefreshAsync(http.Request.Cookies[RefreshCookieName], ct);
                    WriteRefreshCookie(http, session, options.Value);
                    return TypedResults.Ok(session.Response);
                }
                catch (UnauthorizedException)
                {
                    DeleteRefreshCookie(http, options.Value);
                    throw;
                }
            })
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/logout", async (AuthService service, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                await service.LogoutAsync(http.Request.Cookies[RefreshCookieName], ct);
                DeleteRefreshCookie(http, options.Value);
                return TypedResults.NoContent();
            })
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapGet("/me", async (ICurrentUser user, AuthService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetCurrentUserAsync(user.UserId, ct)));

        var consents = app.MapGroup("/api/consents").WithTags("Consents");

        consents.MapGet("/", async (ICurrentUser user, ConsentService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(user.UserId, ct)));

        consents.MapPut("/{type}", async (string type, SetConsentRequest request, ICurrentUser user, ConsentService service, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ConsentType>(type, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new BadRequestException("unknown_consent", $"Unknown consent type '{type}'.");
            }

            return TypedResults.Ok(await service.SetAsync(user.UserId, parsed, request.Granted, ct));
        });

        var account = app.MapGroup("/api/account").WithTags("Account");

        account.MapGet("/export", async (ICurrentUser user, AccountService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ExportAsync(user.UserId, ct)));

        account.MapPost("/delete", async (DeleteAccountRequest request, ICurrentUser user, AccountService service, HttpContext http, IOptions<AuthOptions> options, CancellationToken ct) =>
            {
                await service.DeleteAsync(user.UserId, request.Password, ct);
                DeleteRefreshCookie(http, options.Value);
                return TypedResults.NoContent();
            })
            .WithValidation<DeleteAccountRequest>();
    }

    private static void WriteRefreshCookie(HttpContext http, AuthSession session, AuthOptions options) =>
        http.Response.Cookies.Append(RefreshCookieName, session.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RefreshCookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = new DateTimeOffset(DateTime.SpecifyKind(session.RefreshTokenExpiresAtUtc, DateTimeKind.Utc)),
        });

    private static void DeleteRefreshCookie(HttpContext http, AuthOptions options) =>
        http.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RefreshCookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
        });

    /// <summary>Configures JWT validation lazily from <see cref="JwtOptions"/> so overrides (tests, env vars) apply.</summary>
    private sealed class ConfigureJwtBearer(IOptions<JwtOptions> jwt) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(string? name, JwtBearerOptions options)
        {
            if (name != JwtBearerDefaults.AuthenticationScheme)
            {
                return;
            }

            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = false; // symmetric key, no metadata endpoint; TLS is enforced by the host
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Value.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Value.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.SigningKey)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "sub",
                RoleClaimType = "role",
            };
        }

        public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);
    }
}
