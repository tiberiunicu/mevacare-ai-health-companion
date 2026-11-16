using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Consents;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.Google;

/// <summary>The Google ID token plus, for a first sign-in that creates an account, the same consent choices as the registration form.</summary>
public sealed record GoogleSignInRequest(
    string IdToken,
    bool AcceptTerms = false,
    bool HealthDataConsent = false,
    bool AiProcessingConsent = false);

public sealed record GoogleLinkRequest(string IdToken);

public sealed record AuthProvidersResponse(GoogleProviderInfo? Google);

public sealed record GoogleProviderInfo(string ClientId);

public sealed class GoogleSignInRequestValidator : AbstractValidator<GoogleSignInRequest>
{
    public GoogleSignInRequestValidator() => RuleFor(x => x.IdToken).NotEmpty().MaximumLength(8192);
}

public sealed class GoogleLinkRequestValidator : AbstractValidator<GoogleLinkRequest>
{
    public GoogleLinkRequestValidator() => RuleFor(x => x.IdToken).NotEmpty().MaximumLength(8192);
}

/// <summary>
/// Sign-in with Google. Account-matching rules (the part that matters for security):
/// <list type="bullet">
/// <item>A Google identity (its <c>sub</c>) already linked to an account signs in to that account.</item>
/// <item>An account that was created with a password is <b>never</b> taken over by e-mail match: registration does not verify e-mail
/// addresses, so someone could have registered a stranger's address first. The owner signs in with the password and links Google in the settings.</item>
/// <item>An unknown person creates a new account, but only after accepting the Terms (same consents as the registration form).</item>
/// <item>The e-mail address must be verified by Google.</item>
/// </list>
/// </summary>
public sealed class GoogleAuthService(
    AppDbContext db,
    IGoogleIdTokenValidator google,
    ITokenService tokens,
    IAuditLog audit,
    TimeProvider clock,
    IOptions<JwtOptions> jwtOptions,
    IOptions<GoogleAuthOptions> googleOptions)
{
    private static readonly UnauthorizedException InvalidSignIn = new("This account cannot sign in.");

    public AuthProvidersResponse Providers() =>
        new(googleOptions.Value.Enabled ? new GoogleProviderInfo(googleOptions.Value.ClientId.Trim()) : null);

    public async Task<(AuthSession Session, bool Created)> SignInAsync(GoogleSignInRequest request, CancellationToken ct)
    {
        var identity = await VerifiedIdentityAsync(request.IdToken, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.ExternalLogins)
            .SingleOrDefaultAsync(u => u.ExternalLogins.Any(l => l.Provider == ExternalProviders.Google && l.Subject == identity.Subject), ct);

        if (user is not null)
        {
            if (!user.IsActive)
            {
                audit.Record("auth.login_rejected", user.Id, "inactive");
                await db.SaveChangesAsync(ct);
                throw InvalidSignIn;
            }

            user.RegisterExternalSignIn(now);
            audit.Record("auth.google_login_succeeded", user.Id);
            var existing = IssueSession(user, now);
            await db.SaveChangesAsync(ct);
            return (existing, false);
        }

        var normalized = User.Normalize(identity.Email);
        if (await db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            audit.Record("auth.google_link_required", null, "email_matches_unlinked_account");
            await db.SaveChangesAsync(ct);
            throw new ConflictException("google_account_not_linked",
                "An account with this e-mail address already exists. Sign in with your password, then connect Google in the settings.");
        }

        if (!request.AcceptTerms)
        {
            throw new ConflictException("registration_required",
                "No account exists for this Google identity yet. Accept the Terms of Service and Privacy Policy to create one.");
        }

        var created = User.RegisterExternal(identity.Email, DisplayNameFor(identity), ExternalProviders.Google, identity.Subject, now);
        db.Add(created);
        db.Add(new UserConsent(created.Id, ConsentType.TermsOfService, ConsentPolicy.CurrentVersion, now));
        if (request.HealthDataConsent)
        {
            db.Add(new UserConsent(created.Id, ConsentType.HealthDataProcessing, ConsentPolicy.CurrentVersion, now));
        }

        if (request.AiProcessingConsent)
        {
            db.Add(new UserConsent(created.Id, ConsentType.AiProcessing, ConsentPolicy.CurrentVersion, now));
        }

        audit.Record("auth.registered_google", created.Id);
        var session = IssueSession(created, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Lost a race against a concurrent sign-in of the same person (unique indexes on e-mail and on the Google identity).
            throw new ConflictException("email_in_use", "An account with this e-mail address already exists.");
        }

        return (session, true);
    }

    /// <summary>Connects a Google identity to the signed-in account.</summary>
    public async Task LinkAsync(Guid userId, string idToken, CancellationToken ct)
    {
        var identity = await VerifiedIdentityAsync(idToken, ct);
        var user = await db.Set<User>().Include(u => u.ExternalLogins).SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException();

        if (user.ExternalLogins.Any(l => l.Provider == ExternalProviders.Google))
        {
            throw new ConflictException("google_already_linked", "A Google account is already connected to this account.");
        }

        if (await db.Set<ExternalLogin>().AnyAsync(l => l.Provider == ExternalProviders.Google && l.Subject == identity.Subject, ct))
        {
            throw new ConflictException("google_in_use", "This Google account is already connected to another account.");
        }

        db.Add(new ExternalLogin(user.Id, ExternalProviders.Google, identity.Subject, identity.Email, clock.GetUtcNow().UtcDateTime));
        audit.Record("auth.google_linked", user.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            throw new ConflictException("google_in_use", "This Google account is already connected to another account.");
        }
    }

    /// <summary>Disconnects Google. Refused when the account has no password: the person would be locked out.</summary>
    public async Task UnlinkAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<User>().Include(u => u.ExternalLogins).SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException();

        var link = user.ExternalLogins.FirstOrDefault(l => l.Provider == ExternalProviders.Google)
            ?? throw new NotFoundException("No Google account is connected.");

        if (!user.HasPassword)
        {
            throw new BadRequestException("password_required",
                "This account has no password, so Google is the only way to sign in. It cannot be disconnected.");
        }

        db.Remove(link);
        audit.Record("auth.google_unlinked", user.Id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Proves the person behind the signed-in account controls its Google identity (used to confirm deleting a password-less account).</summary>
    internal async Task<bool> ConfirmsAccountAsync(Guid userId, string idToken, CancellationToken ct)
    {
        var identity = await VerifiedIdentityAsync(idToken, ct);
        return await db.Set<ExternalLogin>().AnyAsync(l =>
            l.UserId == userId && l.Provider == ExternalProviders.Google && l.Subject == identity.Subject, ct);
    }

    private async Task<GoogleIdentity> VerifiedIdentityAsync(string idToken, CancellationToken ct)
    {
        var identity = await google.ValidateAsync(idToken, ct);
        if (!identity.EmailVerified)
        {
            throw new BadRequestException("google_email_not_verified", "Google has not verified the e-mail address of this account.");
        }

        return identity;
    }

    private static string DisplayNameFor(GoogleIdentity identity)
    {
        var name = identity.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var at = identity.Email.IndexOf('@');
            name = at > 0 ? identity.Email[..at] : identity.Email;
        }

        return name.Length <= 100 ? name : name[..100];
    }

    private AuthSession IssueSession(User user, DateTime now)
    {
        var access = tokens.CreateAccessToken(user);
        var (raw, hash) = tokens.CreateRefreshToken();
        var refreshExpires = now.AddDays(jwtOptions.Value.RefreshTokenDays);
        db.Add(new RefreshToken(user.Id, Guid.CreateVersion7(), hash, now, refreshExpires));
        return new AuthSession(new AuthResponse(access.Value, access.ExpiresAtUtc, user.ToDto()), raw, refreshExpires);
    }
}
