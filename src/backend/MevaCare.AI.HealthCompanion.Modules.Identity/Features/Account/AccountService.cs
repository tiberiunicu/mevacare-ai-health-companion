using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Consents;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Account;

/// <summary>Re-authentication for deleting the account: the password, or, for an account created with Google (no password), a fresh Google ID token.</summary>
public sealed record DeleteAccountRequest(string? Password = null, string? GoogleIdToken = null);

public sealed class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator()
    {
        RuleFor(x => x.Password).MaximumLength(256);
        RuleFor(x => x.GoogleIdToken).MaximumLength(8192);
        RuleFor(x => x).Must(x => !string.IsNullOrEmpty(x.Password) || !string.IsNullOrEmpty(x.GoogleIdToken))
            .WithName("Password").WithMessage("Confirm with your password or with Google.");
    }
}

public sealed class AccountService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
    Auth.Google.GoogleAuthService googleAuth,
    ConsentService consents,
    IEnumerable<IUserDataExporter> exporters,
    IAuditLog audit,
    TimeProvider clock)
{
    /// <summary>GDPR art. 15 / 20: a machine-readable copy of everything the application stores about the user.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> ExportAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException();

        var export = new Dictionary<string, object?>
        {
            ["exportedAtUtc"] = clock.GetUtcNow().UtcDateTime,
            ["account"] = new { user.Email, user.DisplayName, user.CreatedAtUtc, user.LastLoginAtUtc },
            ["consents"] = await consents.ListAsync(userId, ct),
        };

        foreach (var exporter in exporters)
        {
            export[exporter.Section] = await exporter.ExportAsync(userId, ct);
        }

        audit.Record("account.exported", userId);
        await db.SaveChangesAsync(ct);
        return export;
    }

    /// <summary>
    /// GDPR art. 17: permanently deletes the account and, through cascading foreign keys, all personal data
    /// of every module. The audit trail keeps only the pseudonymous user id. Re-authentication is required.
    /// </summary>
    public async Task DeleteAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException();

        // An account with a password is confirmed with the password; a Google-only account with a fresh Google sign-in of the same identity.
        var confirmed = user.PasswordHash is { } storedHash
            ? !string.IsNullOrEmpty(request.Password)
              && hasher.VerifyHashedPassword(user, storedHash, request.Password) != PasswordVerificationResult.Failed
            : !string.IsNullOrEmpty(request.GoogleIdToken)
              && await googleAuth.ConfirmsAccountAsync(userId, request.GoogleIdToken, ct);

        if (!confirmed)
        {
            audit.Record("account.delete_rejected", userId, user.HasPassword ? "wrong_password" : "google_not_confirmed");
            await db.SaveChangesAsync(ct);
            throw new BadRequestException("invalid_password",
                user.HasPassword ? "The password is incorrect." : "Google could not confirm that this is your account.");
        }

        audit.Record("account.deleted", userId);
        await db.SaveChangesAsync(ct);
        await db.Set<User>().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
    }
}
