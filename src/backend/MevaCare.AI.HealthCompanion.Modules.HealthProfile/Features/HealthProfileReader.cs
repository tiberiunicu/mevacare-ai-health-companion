using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile.Features;

/// <summary>
/// What the AI assistant may learn about the profile: derived age (never the date of birth), sex, height, activity
/// level and goal TYPES (no target weights or dates). Requires the HealthDataProcessing consent.
/// </summary>
public sealed class HealthProfileReader(AppDbContext db, IConsentService consents, TimeProvider clock) : IHealthProfileReader
{
    public async Task<HealthProfileSummary> GetSummaryAsync(Guid userId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var profile = await db.Set<Domain.HealthProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var goals = await db.Set<HealthGoal>().AsNoTracking().Where(g => g.UserId == userId).Select(g => g.Type).ToListAsync(ct);

        if (profile is null)
        {
            return new HealthProfileSummary(false, null, "Unspecified", null, "Unspecified", goals.Select(g => g.ToString()).ToList());
        }

        int? age = null;
        if (profile.DateOfBirth is { } dob)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            age = today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0);
        }

        return new HealthProfileSummary(
            true, age, profile.Sex.ToString(), profile.HeightCm, profile.ActivityLevel.ToString(), goals.Select(g => g.ToString()).ToList());
    }
}
