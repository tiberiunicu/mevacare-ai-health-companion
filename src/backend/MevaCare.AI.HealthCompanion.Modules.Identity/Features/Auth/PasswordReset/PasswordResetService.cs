using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.PasswordReset;

/// <param name="Language">"en" or "ro": the language of the e-mail (the one the person uses in the app).</param>
public sealed record ForgotPasswordRequest(string Email, string? Language = null);

public sealed record ResetPasswordRequest(string Token, string NewPassword, string? Language = null);

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Language).MaximumLength(10);
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Language).MaximumLength(10);
        RuleFor(x => x.NewPassword).SetValidator(new PasswordValidator());
    }
}

/// <summary>
/// "I forgot my password". The rules that matter:
/// <list type="bullet">
/// <item>The request always looks the same from outside (no hint whether the address has an account), and the e-mail is queued, not sent
/// in the request, so the response time does not tell either.</item>
/// <item>The link holds a random one-time token valid for <see cref="AuthOptions.PasswordResetMinutes"/>; only its hash is stored; a newer link
/// cancels older ones; at most one e-mail per account every two minutes.</item>
/// <item>Using the link sets the password, ends every session of the account (all refresh tokens), lifts a lockout and
/// sends a "your password was changed" notice. It does not sign anyone in: the person signs in with the new password.</item>
/// <item>The link is built from the configured public address, never from the request.</item>
/// </list>
/// An account created with Google has no password; resetting gives it one (the e-mail link proves the same thing Google did).
/// </summary>
public sealed class PasswordResetService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    ITokenService tokens,
    IEmailQueue email,
    IAuditLog audit,
    TimeProvider clock,
    IOptions<AuthOptions> authOptions)
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(2);

    private static readonly BadRequestException InvalidLink =
        new("invalid_reset_token", "This link is invalid or has expired. Ask for a new one.");

    public async Task RequestAsync(ForgotPasswordRequest request, CancellationToken ct)
    {
        if (!email.IsConfigured)
        {
            return; // the app tells the browser (GET /api/auth/providers) that reset is off; a direct call changes nothing
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var normalized = User.Normalize(request.Email);
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null || !user.IsActive)
        {
            return;
        }

        var tooSoon = await db.Set<PasswordResetToken>().AnyAsync(t => t.UserId == user.Id && t.CreatedAtUtc > now - MinimumGap, ct);
        if (tooSoon)
        {
            audit.Record("auth.password_reset_throttled", user.Id);
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var older in await db.Set<PasswordResetToken>().Where(t => t.UserId == user.Id && t.UsedAtUtc == null).ToListAsync(ct))
        {
            older.MarkUsed(now);
        }

        var options = authOptions.Value;
        var (raw, hash) = tokens.CreateRefreshToken(); // 32 random bytes and their SHA-256: the same recipe as refresh tokens
        db.Add(new PasswordResetToken(user.Id, hash, now, now.AddMinutes(options.PasswordResetMinutes)));
        audit.Record("auth.password_reset_requested", user.Id);
        await db.SaveChangesAsync(ct);

        // The token goes in the URL fragment: browsers do not send it to servers, proxies, logs or the Referer header.
        var link = $"{options.PublicBaseUrl.TrimEnd('/')}/reset-password#token={raw}";
        email.Enqueue(PasswordResetEmails.Request(user.Email, user.DisplayName, link, options.PasswordResetMinutes, request.Language));
    }

    public async Task ResetAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = tokens.HashRefreshToken(request.Token);
        var token = await db.Set<PasswordResetToken>().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(now))
        {
            throw InvalidLink;
        }

        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Id == token.UserId, ct);
        if (user is null || !user.IsActive)
        {
            throw InvalidLink;
        }

        if (string.Equals(request.NewPassword, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new BadRequestException("weak_password", "The password must not be the same as the e-mail address.");
        }

        // Everything below is saved in one transaction, and the token's row version makes "use it" atomic.
        token.MarkUsed(now);
        user.SetPasswordAfterReset(hasher.HashPassword(user, request.NewPassword));
        foreach (var session in await db.Set<RefreshToken>().Where(t => t.UserId == user.Id && t.RevokedAtUtc == null).ToListAsync(ct))
        {
            session.Revoke(now);
        }

        foreach (var other in await db.Set<PasswordResetToken>().Where(t => t.UserId == user.Id && t.UsedAtUtc == null && t.Id != token.Id).ToListAsync(ct))
        {
            other.MarkUsed(now);
        }

        audit.Record("auth.password_reset_completed", user.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw InvalidLink; // the same link was used at the same moment by another request
        }

        email.Enqueue(PasswordResetEmails.Changed(user.Email, user.DisplayName, request.Language));
    }
}
