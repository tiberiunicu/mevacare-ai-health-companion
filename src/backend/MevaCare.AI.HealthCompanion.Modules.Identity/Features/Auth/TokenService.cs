using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth;

public sealed record AccessToken(string Value, DateTime ExpiresAtUtc);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);

    /// <summary>Creates a new opaque refresh token. Returns the raw value (sent to the client once) and its hash (stored).</summary>
    (string Raw, string Hash) CreateRefreshToken();

    string HashRefreshToken(string raw);
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        // Minimal claims: no e-mail, no name. The client fetches those from /api/auth/me.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(user.Roles.Select(r => new Claim("role", r.Role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), expires);
    }

    public (string Raw, string Hash) CreateRefreshToken()
    {
        var raw = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (raw, HashRefreshToken(raw));
    }

    public string HashRefreshToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
