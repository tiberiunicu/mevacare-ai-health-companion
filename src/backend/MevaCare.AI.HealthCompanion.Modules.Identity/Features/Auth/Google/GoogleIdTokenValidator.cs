using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.Google;

/// <summary>What Google vouches for about a person, after the ID token was verified.</summary>
public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? Name);

public interface IGoogleIdTokenValidator
{
    /// <summary>Verifies a Google ID token and returns the identity in it. Throws <see cref="UnauthorizedException"/> for anything invalid.</summary>
    Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken ct);
}

/// <summary>Source of Google's current signing keys. A seam so the validation can be tested with a locally generated key.</summary>
public interface IGoogleSigningKeys
{
    Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken ct);
}

/// <summary>Reads (and caches, with refresh) the keys from Google's OpenID Connect discovery document.</summary>
public sealed class GoogleSigningKeys : IGoogleSigningKeys
{
    public const string HttpClientName = "google-discovery";

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _manager;

    public GoogleSigningKeys(IHttpClientFactory httpClients, IOptions<GoogleAuthOptions> options) =>
        _manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Value.DiscoveryUrl,
            new OpenIdConnectConfigurationRetriever(),
            new FactoryDocumentRetriever(httpClients));

    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken ct) =>
        (await _manager.GetConfigurationAsync(ct)).SigningKeys.ToArray();

    /// <summary>Takes a fresh client from the factory for every download, so connection and DNS rotation keep working in a singleton.</summary>
    private sealed class FactoryDocumentRetriever(IHttpClientFactory httpClients) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            using var response = await httpClients.CreateClient(HttpClientName).GetAsync(address, cancel);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancel);
        }
    }
}

public sealed class GoogleIdTokenValidator(
    IGoogleSigningKeys keys,
    IOptions<GoogleAuthOptions> options,
    ILogger<GoogleIdTokenValidator> logger) : IGoogleIdTokenValidator
{
    private static readonly string[] Issuers = ["https://accounts.google.com", "accounts.google.com"];

    public async Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            throw new NotFoundException("Sign-in with Google is not configured.");
        }

        IReadOnlyCollection<SecurityKey> signingKeys;
        try
        {
            signingKeys = await keys.GetAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load Google's signing keys.");
            throw new ServiceUnavailableException("google_unavailable", "Google could not be reached to verify the sign-in. Try again in a moment.");
        }

        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = Issuers,
            ValidateAudience = true,
            ValidAudience = options.Value.ClientId.Trim(),
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            // Google signs ID tokens with RS256; anything else (notably "none" or an HMAC with a public key) is rejected.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        });

        if (!result.IsValid)
        {
            logger.LogInformation("A Google ID token was rejected: {Reason}", result.Exception?.GetType().Name);
            throw new UnauthorizedException("The Google sign-in could not be verified.");
        }

        var token = (JsonWebToken)result.SecurityToken;
        var subject = Claim(token, "sub");
        var email = Claim(token, "email");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            throw new UnauthorizedException("The Google sign-in did not include an e-mail address.");
        }

        // Google sends email_verified as a JSON boolean (older tokens used the string "true"); accept only an explicit true.
        var verified = string.Equals(Claim(token, "email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        return new GoogleIdentity(subject, email, verified, Claim(token, "name"));
    }

    private static string? Claim(JsonWebToken token, string type) =>
        token.TryGetPayloadValue<object>(type, out var value) ? value?.ToString() : null;
}
