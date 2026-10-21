using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;

public enum BiologicalSex { Unspecified = 0, Female = 1, Male = 2, Other = 3 }

public enum ActivityLevel { Unspecified = 0, Sedentary = 1, LightlyActive = 2, ModeratelyActive = 3, VeryActive = 4, ExtraActive = 5 }

public enum GoalType { WeightLoss = 1, WeightGain = 2, MaintainWeight = 3, MuscleGain = 4, ImproveSleep = 5, GeneralWellness = 6 }

public enum UnitSystem { Metric = 1, Imperial = 2 }

public enum ThemePreference { System = 0, Light = 1, Dark = 2 }

/// <summary>Personal health attributes of one user (at most one profile per user). Requires the HealthDataProcessing consent.</summary>
public sealed class HealthProfile : AuditableEntity
{
    private HealthProfile() { }

    public HealthProfile(Guid userId) => UserId = userId;

    public Guid UserId { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public BiologicalSex Sex { get; private set; } = BiologicalSex.Unspecified;
    public decimal? HeightCm { get; private set; }
    public ActivityLevel ActivityLevel { get; private set; } = ActivityLevel.Unspecified;

    /// <summary>IANA or Windows time-zone id; "today" and "last night" are evaluated in this zone.</summary>
    public string TimeZoneId { get; private set; } = "UTC";

    public void Update(DateOnly? dateOfBirth, BiologicalSex sex, decimal? heightCm, ActivityLevel activityLevel, string timeZoneId)
    {
        DateOfBirth = dateOfBirth;
        Sex = sex;
        HeightCm = heightCm;
        ActivityLevel = activityLevel;
        TimeZoneId = timeZoneId;
    }
}

/// <summary>A wellness goal. Goals are user-defined targets, not medical prescriptions.</summary>
public sealed class HealthGoal : AuditableEntity
{
    private HealthGoal() { }

    public HealthGoal(Guid userId, GoalType type, decimal? targetWeightKg, DateOnly? targetDate)
    {
        UserId = userId;
        Type = type;
        TargetWeightKg = targetWeightKg;
        TargetDate = targetDate;
    }

    public Guid UserId { get; private set; }
    public GoalType Type { get; private set; }
    public decimal? TargetWeightKg { get; private set; }
    public DateOnly? TargetDate { get; private set; }
}

/// <summary>Display preferences. Contains no health data, so it does not require health-data consent.</summary>
public sealed class HealthPreferences : AuditableEntity
{
    private HealthPreferences() { }

    public HealthPreferences(Guid userId) => UserId = userId;

    public Guid UserId { get; private set; }
    public string Language { get; private set; } = "en";
    public UnitSystem UnitSystem { get; private set; } = UnitSystem.Metric;
    public ThemePreference Theme { get; private set; } = ThemePreference.System;

    public void Update(string language, UnitSystem unitSystem, ThemePreference theme)
    {
        Language = language;
        UnitSystem = unitSystem;
        Theme = theme;
    }
}
