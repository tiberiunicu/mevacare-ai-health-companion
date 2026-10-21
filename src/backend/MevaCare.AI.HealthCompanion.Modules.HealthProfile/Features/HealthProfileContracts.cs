using FluentValidation;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.HealthProfile.Features;

public sealed record HealthProfileDto(
    bool Exists, DateOnly? DateOfBirth, BiologicalSex Sex, decimal? HeightCm, ActivityLevel ActivityLevel, string TimeZoneId);

public sealed record UpdateHealthProfileRequest(
    DateOnly? DateOfBirth, BiologicalSex Sex, decimal? HeightCm, ActivityLevel ActivityLevel, string TimeZoneId);

public sealed record HealthGoalDto(Guid Id, GoalType Type, decimal? TargetWeightKg, DateOnly? TargetDate, DateTime CreatedAtUtc);

public sealed record CreateHealthGoalRequest(GoalType Type, decimal? TargetWeightKg, DateOnly? TargetDate);

public sealed record HealthPreferencesDto(string Language, UnitSystem UnitSystem, ThemePreference Theme);

public sealed record UpdateHealthPreferencesRequest(string Language, UnitSystem UnitSystem, ThemePreference Theme);

public sealed class UpdateHealthProfileRequestValidator : AbstractValidator<UpdateHealthProfileRequest>
{
    public const int MinAgeYears = 16;
    public const int MaxAgeYears = 120;

    public UpdateHealthProfileRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.DateOfBirth).Must(dob =>
            {
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                return dob is null || (dob.Value <= today.AddYears(-MinAgeYears) && dob.Value >= today.AddYears(-MaxAgeYears));
            })
            .WithMessage($"The date of birth must correspond to an age between {MinAgeYears} and {MaxAgeYears} years.");
        RuleFor(x => x.Sex).IsInEnum();
        RuleFor(x => x.ActivityLevel).IsInEnum();
        RuleFor(x => x.HeightCm).InclusiveBetween(50m, 260m).When(x => x.HeightCm is not null);
        RuleFor(x => x.TimeZoneId).NotEmpty().MaximumLength(64).Must(BeKnownTimeZone)
            .WithMessage("Unknown time zone.");
    }

    private static bool BeKnownTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public sealed class CreateHealthGoalRequestValidator : AbstractValidator<CreateHealthGoalRequest>
{
    public CreateHealthGoalRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.TargetWeightKg).InclusiveBetween(20m, 500m).When(x => x.TargetWeightKg is not null);
        RuleFor(x => x.TargetWeightKg).NotNull()
            .When(x => x.Type is GoalType.WeightLoss or GoalType.WeightGain)
            .WithMessage("A target weight is required for weight goals.");
        RuleFor(x => x.TargetDate).Must(d => d is null || d.Value >= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            .WithMessage("The target date cannot be in the past.");
    }
}

public sealed class UpdateHealthPreferencesRequestValidator : AbstractValidator<UpdateHealthPreferencesRequest>
{
    public static readonly string[] SupportedLanguages = ["en", "ro"];

    public UpdateHealthPreferencesRequestValidator()
    {
        RuleFor(x => x.Language).Must(l => SupportedLanguages.Contains(l)).WithMessage("Unsupported language.");
        RuleFor(x => x.UnitSystem).IsInEnum();
        RuleFor(x => x.Theme).IsInEnum();
    }
}
