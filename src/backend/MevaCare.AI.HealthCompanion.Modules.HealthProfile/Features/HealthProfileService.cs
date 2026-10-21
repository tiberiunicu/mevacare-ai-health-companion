using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile.Features;

/// <summary>
/// Application service for the health profile, goals and preferences. Every method takes the user id of the
/// authenticated caller (from <see cref="ICurrentUser"/>) and filters on it: there is no way to address another
/// user's data. Health data requires the HealthDataProcessing consent; display preferences do not.
/// </summary>
public sealed class HealthProfileService(AppDbContext db, IConsentService consents, IAuditLog audit)
{
    public const int MaxGoals = 5;

    public async Task<HealthProfileDto> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var profile = await db.Set<Domain.HealthProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        return profile is null
            ? new HealthProfileDto(false, null, BiologicalSex.Unspecified, null, ActivityLevel.Unspecified, "UTC")
            : ToDto(profile);
    }

    public async Task<HealthProfileDto> UpsertProfileAsync(Guid userId, UpdateHealthProfileRequest request, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var profile = await db.Set<Domain.HealthProfile>().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            profile = new Domain.HealthProfile(userId);
            db.Add(profile);
        }

        profile.Update(request.DateOfBirth, request.Sex, request.HeightCm, request.ActivityLevel, request.TimeZoneId);
        audit.Record("health_profile.updated", userId);
        await db.SaveChangesAsync(ct);
        return ToDto(profile);
    }

    public async Task<IReadOnlyList<HealthGoalDto>> ListGoalsAsync(Guid userId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var goals = await db.Set<HealthGoal>().AsNoTracking().Where(g => g.UserId == userId)
            .OrderBy(g => g.CreatedAtUtc).ToListAsync(ct);
        return goals.Select(ToDto).ToList();
    }

    public async Task<HealthGoalDto> AddGoalAsync(Guid userId, CreateHealthGoalRequest request, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        if (await db.Set<HealthGoal>().CountAsync(g => g.UserId == userId, ct) >= MaxGoals)
        {
            throw new ConflictException("goal_limit_reached", $"You can have at most {MaxGoals} goals.");
        }

        var goal = new HealthGoal(userId, request.Type, request.TargetWeightKg, request.TargetDate);
        db.Add(goal);
        audit.Record("health_goal.created", userId);
        await db.SaveChangesAsync(ct);
        return ToDto(goal);
    }

    public async Task DeleteGoalAsync(Guid userId, Guid goalId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        // The user id is part of the predicate: a foreign goal id behaves exactly like a missing one (404).
        var goal = await db.Set<HealthGoal>().SingleOrDefaultAsync(g => g.Id == goalId && g.UserId == userId, ct)
            ?? throw new NotFoundException();
        db.Remove(goal);
        audit.Record("health_goal.deleted", userId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<HealthPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken ct)
    {
        var prefs = await db.Set<HealthPreferences>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        return prefs is null ? new HealthPreferencesDto("en", UnitSystem.Metric, ThemePreference.System) : ToDto(prefs);
    }

    public async Task<HealthPreferencesDto> UpsertPreferencesAsync(Guid userId, UpdateHealthPreferencesRequest request, CancellationToken ct)
    {
        var prefs = await db.Set<HealthPreferences>().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            prefs = new HealthPreferences(userId);
            db.Add(prefs);
        }

        prefs.Update(request.Language, request.UnitSystem, request.Theme);
        await db.SaveChangesAsync(ct);
        return ToDto(prefs);
    }

    private static HealthProfileDto ToDto(Domain.HealthProfile p) =>
        new(true, p.DateOfBirth, p.Sex, p.HeightCm, p.ActivityLevel, p.TimeZoneId);

    private static HealthGoalDto ToDto(HealthGoal g) => new(g.Id, g.Type, g.TargetWeightKg, g.TargetDate, g.CreatedAtUtc);

    private static HealthPreferencesDto ToDto(HealthPreferences p) => new(p.Language, p.UnitSystem, p.Theme);
}

/// <summary>Adds the health profile, goals and preferences to the GDPR export.</summary>
public sealed class HealthProfileExporter(AppDbContext db) : IUserDataExporter
{
    public string Section => "healthProfile";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken ct)
    {
        var profile = await db.Set<Domain.HealthProfile>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);
        var goals = await db.Set<HealthGoal>().AsNoTracking().Where(g => g.UserId == userId).ToListAsync(ct);
        var prefs = await db.Set<HealthPreferences>().AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct);

        return new
        {
            profile = profile is null ? null : new { profile.DateOfBirth, profile.Sex, profile.HeightCm, profile.ActivityLevel, profile.TimeZoneId },
            goals = goals.Select(g => new { g.Type, g.TargetWeightKg, g.TargetDate, g.CreatedAtUtc }),
            preferences = prefs is null ? null : new { prefs.Language, prefs.UnitSystem, prefs.Theme },
        };
    }
}
