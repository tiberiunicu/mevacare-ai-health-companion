using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Consents;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth;

public sealed class AuthService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    ITokenService tokens,
    IAuditLog audit,
    TimeProvider clock,
    IOptions<JwtOptions> jwtOptions,
    IOptions<AuthOptions> authOptions)
{
    // Verified for unknown e-mail addresses so that "unknown user" and "wrong password" cost the same time.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString("N"));

    private static readonly UnauthorizedException InvalidCredentials =
        new("Invalid e-mail or password, or the account is temporarily locked.");

    public async Task<AuthSession> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var normalized = User.Normalize(request.Email);
        if (await db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            throw new ConflictException("email_in_use", "An account with this e-mail address already exists.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = User.Register(request.Email, request.DisplayName, hasher.HashPassword(null!, request.Password));
        db.Add(user);

        db.Add(new UserConsent(user.Id, ConsentType.TermsOfService, ConsentPolicy.CurrentVersion, now));
        if (request.HealthDataConsent)
        {
            db.Add(new UserConsent(user.Id, ConsentType.HealthDataProcessing, ConsentPolicy.CurrentVersion, now));
        }

        if (request.AiProcessingConsent)
        {
            db.Add(new UserConsent(user.Id, ConsentType.AiProcessing, ConsentPolicy.CurrentVersion, now));
        }

        audit.Record("auth.registered", user.Id);

        var session = IssueSession(user, Guid.CreateVersion7(), now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Lost the race against a concurrent registration with the same e-mail address (unique index).
            throw new ConflictException("email_in_use", "An account with this e-mail address already exists.");
        }

        return session;
    }

    public async Task<AuthSession> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var options = authOptions.Value;
        var normalized = User.Normalize(request.Email);
        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.ExternalLogins)
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null)
        {
            hasher.VerifyHashedPassword(null!, DummyHash, request.Password);
            audit.Record("auth.login_failed", null, "unknown_account");
            await db.SaveChangesAsync(ct);
            throw InvalidCredentials;
        }

        if (!user.IsActive || user.IsLockedOut(now))
        {
            audit.Record("auth.login_rejected", user.Id, user.IsActive ? "locked_out" : "inactive");
            await db.SaveChangesAsync(ct);
            throw InvalidCredentials;
        }

        // An account created with Google has no password: verify against the dummy hash so it costs the same time and looks the same.
        PasswordVerificationResult verification;
        if (user.PasswordHash is { } storedHash)
        {
            verification = hasher.VerifyHashedPassword(user, storedHash, request.Password);
        }
        else
        {
            hasher.VerifyHashedPassword(null!, DummyHash, request.Password);
            verification = PasswordVerificationResult.Failed;
        }

        if (verification == PasswordVerificationResult.Failed)
        {
            user.RegisterFailedLogin(now, options.MaxFailedLogins, TimeSpan.FromMinutes(options.LockoutMinutes));
            audit.Record(user.IsLockedOut(now) ? "auth.locked_out" : "auth.login_failed", user.Id);
            await db.SaveChangesAsync(ct);
            throw InvalidCredentials;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.UpdatePasswordHash(hasher.HashPassword(user, request.Password));
        }

        user.RegisterSuccessfulLogin(now);
        audit.Record("auth.login_succeeded", user.Id);
        var session = IssueSession(user, Guid.CreateVersion7(), now);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<AuthSession> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new UnauthorizedException("No refresh token.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashRefreshToken(refreshToken);
        var stored = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedException("Invalid refresh token.");

        if (stored.RevokedAtUtc is { } revokedAt)
        {
            if (now - revokedAt <= TimeSpan.FromSeconds(authOptions.Value.RefreshReuseGraceSeconds))
            {
                throw new UnauthorizedException("Invalid refresh token."); // concurrent refresh race, not theft
            }

            // A rotated token was presented again long after rotation: assume theft and kill every token of this sign-in.
            var family = await db.Set<RefreshToken>().Where(t => t.FamilyId == stored.FamilyId).ToListAsync(ct);
            family.ForEach(t => t.Revoke(now));
            audit.Record("auth.refresh_reuse_detected", stored.UserId);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException("Invalid refresh token.");
        }

        if (!stored.IsActive(now))
        {
            throw new UnauthorizedException("Refresh token expired.");
        }

        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.ExternalLogins)
            .SingleOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Invalid refresh token.");
        }

        stored.Revoke(now);
        var session = IssueSession(user, stored.FamilyId, now);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        var hash = tokens.HashRefreshToken(refreshToken);
        var stored = await db.Set<RefreshToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var family = await db.Set<RefreshToken>().Where(t => t.FamilyId == stored.FamilyId).ToListAsync(ct);
        family.ForEach(t => t.Revoke(now));
        audit.Record("auth.logout", stored.UserId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<User>().AsNoTracking().Include(u => u.Roles).Include(u => u.ExternalLogins)
            .SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException();
        return user.ToDto();
    }

    private AuthSession IssueSession(User user, Guid familyId, DateTime now)
    {
        var access = tokens.CreateAccessToken(user);
        var (raw, hash) = tokens.CreateRefreshToken();
        var refreshExpires = now.AddDays(jwtOptions.Value.RefreshTokenDays);
        db.Add(new RefreshToken(user.Id, familyId, hash, now, refreshExpires));
        return new AuthSession(new AuthResponse(access.Value, access.ExpiresAtUtc, user.ToDto()), raw, refreshExpires);
    }
}
