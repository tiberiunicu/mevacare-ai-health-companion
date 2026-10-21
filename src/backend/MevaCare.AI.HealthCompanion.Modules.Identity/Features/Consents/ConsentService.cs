using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Consents;

/// <summary>Version of the privacy policy / consent texts the user agreed to. Bump when the texts change materially.</summary>
public static class ConsentPolicy
{
    public const string CurrentVersion = "2026-10";
}

public sealed record ConsentDto(
    string Type, bool Granted, bool Required, string? PolicyVersion, DateTime? GrantedAtUtc, DateTime? RevokedAtUtc);

public sealed record SetConsentRequest(bool Granted);

public sealed class ConsentService(AppDbContext db, IAuditLog audit, TimeProvider clock) : IConsentService
{
    public async Task<bool> IsGrantedAsync(Guid userId, ConsentType type, CancellationToken ct) =>
        await db.Set<UserConsent>().AsNoTracking()
            .AnyAsync(c => c.UserId == userId && c.Type == type && c.RevokedAtUtc == null, ct);

    public async Task EnsureGrantedAsync(Guid userId, ConsentType type, CancellationToken ct)
    {
        if (!await IsGrantedAsync(userId, type, ct))
        {
            throw new ConsentRequiredException(type.ToString());
        }
    }

    public async Task<IReadOnlyList<ConsentDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var stored = await db.Set<UserConsent>().AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct);
        return Enum.GetValues<ConsentType>()
            .Select(type =>
            {
                var consent = stored.SingleOrDefault(c => c.Type == type);
                return new ConsentDto(
                    type.ToString(),
                    consent is { IsGranted: true },
                    type == ConsentType.TermsOfService,
                    consent?.PolicyVersion,
                    consent?.GrantedAtUtc,
                    consent?.RevokedAtUtc);
            })
            .ToList();
    }

    public async Task<ConsentDto> SetAsync(Guid userId, ConsentType type, bool granted, CancellationToken ct)
    {
        if (type == ConsentType.TermsOfService && !granted)
        {
            throw new BadRequestException(
                "terms_cannot_be_revoked", "The Terms of Service cannot be withdrawn while the account exists. Delete the account instead.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var consent = await db.Set<UserConsent>().SingleOrDefaultAsync(c => c.UserId == userId && c.Type == type, ct);

        if (granted)
        {
            if (consent is null)
            {
                db.Add(new UserConsent(userId, type, ConsentPolicy.CurrentVersion, now));
            }
            else if (!consent.IsGranted || consent.PolicyVersion != ConsentPolicy.CurrentVersion)
            {
                consent.Grant(ConsentPolicy.CurrentVersion, now);
            }
        }
        else if (consent is { IsGranted: true })
        {
            consent.Revoke(now);
        }

        audit.Record(granted ? "consent.granted" : "consent.revoked", userId, $"{type}@{ConsentPolicy.CurrentVersion}");
        await db.SaveChangesAsync(ct);

        return (await ListAsync(userId, ct)).Single(c => c.Type == type.ToString());
    }
}
