using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Auditing;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Features;
using MevaCare.AI.HealthCompanion.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using HealthProfileEntity = MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain.HealthProfile;

namespace MevaCare.AI.HealthCompanion.UnitTests.Profile;

public class HealthProfileValidatorTests
{
    private readonly UpdateHealthProfileRequestValidator _profile = new(TestDb.Clock());
    private readonly CreateHealthGoalRequestValidator _goal = new(TestDb.Clock());

    private static UpdateHealthProfileRequest ValidProfile() =>
        new(new DateOnly(1985, 4, 12), BiologicalSex.Female, 170m, ActivityLevel.LightlyActive, "Europe/Bucharest");

    [Fact]
    public void A_complete_profile_is_valid() => Assert.True(_profile.Validate(ValidProfile()).IsValid);

    [Theory]
    [InlineData(10)]
    [InlineData(49.9)]
    [InlineData(261)]
    public void Implausible_heights_are_rejected(double height) =>
        Assert.False(_profile.Validate(ValidProfile() with { HeightCm = (decimal)height }).IsValid);

    [Fact]
    public void Date_of_birth_in_the_future_is_rejected() =>
        Assert.False(_profile.Validate(ValidProfile() with { DateOfBirth = new DateOnly(2027, 1, 1) }).IsValid);

    [Fact]
    public void Users_below_the_minimum_age_are_rejected() =>
        Assert.False(_profile.Validate(ValidProfile() with { DateOfBirth = new DateOnly(2015, 1, 1) }).IsValid);

    [Fact]
    public void Unknown_time_zones_are_rejected() =>
        Assert.False(_profile.Validate(ValidProfile() with { TimeZoneId = "Mars/Olympus_Mons" }).IsValid);

    [Fact]
    public void Undefined_enum_values_are_rejected() =>
        Assert.False(_profile.Validate(ValidProfile() with { Sex = (BiologicalSex)42 }).IsValid);

    [Fact]
    public void Weight_goals_require_a_target_weight()
    {
        Assert.False(_goal.Validate(new CreateHealthGoalRequest(GoalType.WeightLoss, null, null)).IsValid);
        Assert.True(_goal.Validate(new CreateHealthGoalRequest(GoalType.WeightLoss, 72m, null)).IsValid);
        Assert.True(_goal.Validate(new CreateHealthGoalRequest(GoalType.ImproveSleep, null, null)).IsValid);
    }

    [Fact]
    public void Goal_target_date_cannot_be_in_the_past() =>
        Assert.False(_goal.Validate(new CreateHealthGoalRequest(GoalType.WeightLoss, 72m, new DateOnly(2026, 1, 1))).IsValid);

    [Fact]
    public void Only_supported_languages_are_accepted()
    {
        var validator = new UpdateHealthPreferencesRequestValidator();
        Assert.True(validator.Validate(new UpdateHealthPreferencesRequest("ro", UnitSystem.Metric, ThemePreference.Dark)).IsValid);
        Assert.False(validator.Validate(new UpdateHealthPreferencesRequest("xx", UnitSystem.Metric, ThemePreference.Dark)).IsValid);
    }
}

public class HealthProfileServiceTests
{
    private static HealthProfileService CreateService(AppDbContext db, bool consent = true) =>
        new(db, new StubConsentService(consent), new AuditLog(db, TimeProvider.System));

    private static readonly UpdateHealthProfileRequest Update =
        new(new DateOnly(1985, 4, 12), BiologicalSex.Male, 180m, ActivityLevel.ModeratelyActive, "UTC");

    [Fact]
    public async Task Reading_the_profile_requires_health_data_consent()
    {
        await using var db = TestDb.Create();
        var service = CreateService(db, consent: false);

        var ex = await Assert.ThrowsAsync<ConsentRequiredException>(() => service.GetProfileAsync(Guid.NewGuid(), default));
        Assert.Equal("consent_required", ex.Code);
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task Preferences_do_not_require_health_data_consent()
    {
        await using var db = TestDb.Create();
        var service = CreateService(db, consent: false);

        var prefs = await service.GetPreferencesAsync(Guid.NewGuid(), default);

        Assert.Equal("en", prefs.Language);
    }

    [Fact]
    public async Task A_missing_profile_is_reported_as_not_existing_instead_of_being_invented()
    {
        await using var db = TestDb.Create();

        var profile = await CreateService(db).GetProfileAsync(Guid.NewGuid(), default);

        Assert.False(profile.Exists);
        Assert.Null(profile.HeightCm);
        Assert.Empty(db.Set<HealthProfileEntity>());
    }

    [Fact]
    public async Task Upserting_twice_keeps_a_single_profile_row()
    {
        await using var db = TestDb.Create();
        var service = CreateService(db);
        var userId = Guid.NewGuid();

        await service.UpsertProfileAsync(userId, Update, default);
        await service.UpsertProfileAsync(userId, Update with { HeightCm = 181m }, default);

        var rows = await db.Set<HealthProfileEntity>().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(181m, rows[0].HeightCm);
    }

    [Fact]
    public async Task Users_only_see_their_own_data()
    {
        await using var db = TestDb.Create();
        var service = CreateService(db);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await service.UpsertProfileAsync(alice, Update, default);

        var bobsView = await service.GetProfileAsync(bob, default);

        Assert.False(bobsView.Exists);
    }

    [Fact]
    public async Task Deleting_another_users_goal_behaves_like_a_missing_goal()
    {
        await using var db = TestDb.Create();
        var service = CreateService(db);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var goal = await service.AddGoalAsync(alice, new CreateHealthGoalRequest(GoalType.ImproveSleep, null, null), default);

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteGoalAsync(bob, goal.Id, default));

        Assert.Single(await service.ListGoalsAsync(alice, default));
    }

    [Fact]
    public async Task The_number_of_goals_is_limited()
    {
        await using var db = TestDb.Create();
        var service = CreateService(db);
        var userId = Guid.NewGuid();
        for (var i = 0; i < HealthProfileService.MaxGoals; i++)
        {
            await service.AddGoalAsync(userId, new CreateHealthGoalRequest(GoalType.GeneralWellness, null, null), default);
        }

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.AddGoalAsync(userId, new CreateHealthGoalRequest(GoalType.GeneralWellness, null, null), default));
        Assert.Equal("goal_limit_reached", ex.Code);
    }

    [Fact]
    public async Task Changes_are_written_to_the_audit_log_without_personal_details()
    {
        await using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await CreateService(db).UpsertProfileAsync(userId, Update, default);

        var audit = await db.Set<AuditEvent>().SingleAsync();
        Assert.Equal("health_profile.updated", audit.Action);
        Assert.Equal(userId, audit.UserId);
        Assert.Null(audit.Details);
    }

    [Fact]
    public async Task Audit_timestamps_are_stamped_by_the_persistence_layer()
    {
        var clock = TestDb.Clock();
        await using var db = TestDb.Create(clock);

        await CreateService(db).UpsertProfileAsync(Guid.NewGuid(), Update, default);

        var created = (await db.Set<HealthProfileEntity>().SingleAsync()).CreatedAtUtc;
        Assert.Equal(clock.GetUtcNow().UtcDateTime, created);
    }
}
