using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Consents;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Account;

public sealed record DeleteAccountRequest(string Password);

public sealed class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator() => RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
}

public sealed class AccountService(
    AppDbContext db,
    IPasswordHasher<User> hasher,
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
    public async Task DeleteAsync(Guid userId, string password, CancellationToken ct)
    {
        var user = await db.Set<User>().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException();

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            audit.Record("account.delete_rejected", userId, "wrong_password");
            await db.SaveChangesAsync(ct);
            throw new BadRequestException("invalid_password", "The password is incorrect.");
        }

        audit.Record("account.deleted", userId);
        await db.SaveChangesAsync(ct);
        await db.Set<User>().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
    }
}
