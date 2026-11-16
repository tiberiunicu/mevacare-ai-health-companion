using System.Security.Claims;
using System.Security.Cryptography;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.Google;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MevaCare.AI.HealthCompanion.UnitTests.Identity;

/// <summary>
/// The checks the API makes on a Google ID token, with tokens signed by a locally generated RSA key standing in for Google's key.
/// This proves the rules (signature, issuer, audience, expiry, algorithm, claims); it cannot prove what Google actually sends.
/// </summary>
public sealed class GoogleIdTokenValidatorTests : IDisposable
{
    private const string ClientId = "123-abc.apps.googleusercontent.com";

    private readonly RSA _googleKey = RSA.Create(2048);
    private readonly RSA _attackerKey = RSA.Create(2048);

    public void Dispose()
    {
        _googleKey.Dispose();
        _attackerKey.Dispose();
    }

    private sealed class FixedKeys(params SecurityKey[] keys) : IGoogleSigningKeys
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken ct) => Task.FromResult<IReadOnlyCollection<SecurityKey>>(keys);
    }

    private sealed class BrokenKeys : IGoogleSigningKeys
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken ct) => throw new HttpRequestException("offline");
    }

    private GoogleIdTokenValidator Validator(IGoogleSigningKeys? keys = null, string clientId = ClientId) =>
        new(keys ?? new FixedKeys(new RsaSecurityKey(_googleKey) { KeyId = "k1" }),
            Options.Create(new GoogleAuthOptions { ClientId = clientId }),
            NullLogger<GoogleIdTokenValidator>.Instance);

    private string Token(
        RSA? key = null,
        string issuer = "https://accounts.google.com",
        string audience = ClientId,
        DateTime? expires = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        Dictionary<string, object>? claims = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = "110169484474386276334",
            ["email"] = "ana@example.test",
            ["email_verified"] = true,
            ["name"] = "Ana Pop",
        };
        foreach (var (name, value) in claims ?? [])
        {
            payload[name] = value;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = payload,
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? _googleKey) { KeyId = "k1" }, algorithm),
        });
    }

    [Fact]
    public async Task A_valid_token_yields_the_identity()
    {
        var identity = await Validator().ValidateAsync(Token(), default);

        Assert.Equal("110169484474386276334", identity.Subject);
        Assert.Equal("ana@example.test", identity.Email);
        Assert.True(identity.EmailVerified);
        Assert.Equal("Ana Pop", identity.Name);
    }

    [Fact]
    public async Task The_issuer_may_be_written_with_or_without_the_scheme()
    {
        var identity = await Validator().ValidateAsync(Token(issuer: "accounts.google.com"), default);

        Assert.Equal("ana@example.test", identity.Email);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://accounts.google.com.evil.example")]
    public async Task Another_issuer_is_rejected(string issuer) =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(Token(issuer: issuer), default));

    [Fact]
    public async Task A_token_issued_for_another_application_is_rejected() =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(Token(audience: "999-other.apps.googleusercontent.com"), default));

    [Fact]
    public async Task An_expired_token_is_rejected() =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(Token(expires: DateTime.UtcNow.AddMinutes(-10)), default));

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected() =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(Token(key: _attackerKey), default));

    [Fact]
    public async Task A_token_with_a_tampered_payload_is_rejected()
    {
        var parts = Token().Split('.');
        var forged = System.Text.Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("ana@example.test", "victim@example.test");
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(forged)}.{parts[2]}";

        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(tampered, default));
    }

    [Fact]
    public async Task Only_rs256_is_accepted()
    {
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(Token(algorithm: SecurityAlgorithms.RsaSha512), default));

        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://accounts.google.com",
            Audience = ClientId,
            Claims = new Dictionary<string, object> { ["sub"] = "1", ["email"] = "ana@example.test", ["email_verified"] = true },
            Expires = DateTime.UtcNow.AddMinutes(30),
        });
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(unsigned, default));
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task Garbage_is_rejected(string token) =>
        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(token, default));

    [Fact]
    public async Task An_unverified_email_is_reported_as_unverified_and_the_caller_decides()
    {
        var identity = await Validator().ValidateAsync(Token(claims: new() { ["email_verified"] = false }), default);

        Assert.False(identity.EmailVerified);
    }

    [Fact]
    public async Task The_verified_flag_is_also_read_when_google_sends_it_as_text()
    {
        var identity = await Validator().ValidateAsync(Token(claims: new() { ["email_verified"] = "true" }), default);

        Assert.True(identity.EmailVerified);
    }

    [Fact]
    public async Task A_missing_email_or_subject_is_rejected()
    {
        var noEmail = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://accounts.google.com",
            Audience = ClientId,
            Subject = new ClaimsIdentity([new Claim("sub", "1")]),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_googleKey) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });

        await Assert.ThrowsAsync<UnauthorizedException>(() => Validator().ValidateAsync(noEmail, default));
    }

    [Fact]
    public async Task When_google_cannot_be_reached_the_answer_is_service_unavailable_not_unauthorized() =>
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Validator(new BrokenKeys()).ValidateAsync(Token(), default));

    [Fact]
    public async Task Without_a_configured_client_id_the_feature_is_off() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Validator(clientId: "").ValidateAsync(Token(), default));
}
