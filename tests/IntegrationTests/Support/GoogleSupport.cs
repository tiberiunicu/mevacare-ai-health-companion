using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.Google;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>
/// Stand-in for the verification of a Google ID token: the "token" is a plain string that carries the claims, so a test can sign in as any
/// Google identity. NOT Google: the real signature, issuer, audience and expiry checks are tested separately in
/// <c>GoogleIdTokenValidatorTests</c> (unit tests, with a locally generated key); only a real browser sign-in proves Google's side.
/// </summary>
public sealed class FakeGoogleIdTokenValidator : IGoogleIdTokenValidator
{
    private const string Prefix = "fake-google-token|";

    /// <summary>A token for a Google identity. Every test should use a fresh subject, because one identity can belong to one account only.</summary>
    public static string Token(string subject, string email, bool emailVerified = true, string? name = "Synthetic Google User") =>
        $"{Prefix}{subject}|{email}|{emailVerified}|{name}";

    public static string NewSubject() => "g" + Guid.NewGuid().ToString("N");

    public static string NewEmail() => $"google-{Guid.NewGuid():N}@example.test";

    public Task<GoogleIdentity> ValidateAsync(string idToken, CancellationToken ct)
    {
        if (!idToken.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedException("The Google sign-in could not be verified.");
        }

        var parts = idToken[Prefix.Length..].Split('|');
        return Task.FromResult(new GoogleIdentity(parts[0], parts[1], bool.Parse(parts[2]), string.IsNullOrEmpty(parts[3]) ? null : parts[3]));
    }
}
