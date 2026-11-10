using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Assistant;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile.Features;

/// <summary>
/// Lets the assistant change two profile facts, height and activity level, after the user confirmed. Date of birth, sex and time zone
/// are never touched. The same limits as the profile page apply.
/// </summary>
public sealed class ProfileAssistantApi(AppDbContext db, IConsentService consents, IAuditLog audit) : IProfileAssistantApi
{
    public const decimal MinHeightCm = 50m;
    public const decimal MaxHeightCm = 260m;

    public async Task<ProfileBasicsDto> GetBasicsAsync(Guid userId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var profile = await db.Set<Domain.HealthProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        return new ProfileBasicsDto(profile?.HeightCm, (profile?.ActivityLevel ?? ActivityLevel.Unspecified).ToString());
    }

    public async Task<ProfileBasicsDto> SetBasicsAsync(Guid userId, decimal? heightCm, string? activityLevel, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);

        if (heightCm is < MinHeightCm or > MaxHeightCm)
        {
            throw new BadRequestException("invalid_height", $"The height must be between {MinHeightCm} and {MaxHeightCm} cm.");
        }

        ActivityLevel? level = null;
        if (!string.IsNullOrWhiteSpace(activityLevel))
        {
            if (!Enum.TryParse<ActivityLevel>(activityLevel.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new BadRequestException("invalid_activity_level", "The activity level is not one of the known values.");
            }

            level = parsed;
        }

        var profile = await db.Set<Domain.HealthProfile>().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            profile = new Domain.HealthProfile(userId);
            db.Add(profile);
        }

        profile.Update(profile.DateOfBirth, profile.Sex, heightCm ?? profile.HeightCm, level ?? profile.ActivityLevel, profile.TimeZoneId);
        audit.Record("health_profile.updated", userId, "assistant");
        await db.SaveChangesAsync(ct);
        return new ProfileBasicsDto(profile.HeightCm, profile.ActivityLevel.ToString());
    }
}
