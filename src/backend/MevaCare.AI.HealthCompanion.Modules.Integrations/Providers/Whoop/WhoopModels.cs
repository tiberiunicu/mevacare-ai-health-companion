using System.Text.Json;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;

// Wire models of the WHOOP developer API v2 (https://developer.whoop.com), read with snake_case names.
// Only the fields the application uses are declared; unknown fields are ignored. Everything that is optional in the documentation is nullable.

/// <summary>Records stay raw JSON here so that one record in an unexpected shape can be skipped without losing the whole page.</summary>
internal sealed record WhoopPage(List<JsonElement>? Records, string? NextToken);

internal sealed record WhoopProfile(long UserId);

internal sealed record WhoopTokenResponse(string? AccessToken, int? ExpiresIn, string? RefreshToken, string? Scope, string? Error);

internal sealed record WhoopStageSummary(
    long? TotalInBedTimeMilli,
    long? TotalAwakeTimeMilli,
    long? TotalLightSleepTimeMilli,
    long? TotalSlowWaveSleepTimeMilli,
    long? TotalRemSleepTimeMilli,
    int? DisturbanceCount);

internal sealed record WhoopSleepScore(
    WhoopStageSummary? StageSummary,
    decimal? RespiratoryRate,
    decimal? SleepPerformancePercentage,
    decimal? SleepConsistencyPercentage,
    decimal? SleepEfficiencyPercentage);

internal sealed record WhoopSleep(
    string Id, DateTimeOffset Start, DateTimeOffset End, string? TimezoneOffset, bool Nap, string? ScoreState, WhoopSleepScore? Score);

internal sealed record WhoopRecoveryScore(decimal? RecoveryScore, decimal? RestingHeartRate, decimal? HrvRmssdMilli, bool? UserCalibrating);

internal sealed record WhoopRecovery(long CycleId, string? SleepId, string? ScoreState, WhoopRecoveryScore? Score);

internal sealed record WhoopCycleScore(decimal? Strain, decimal? Kilojoule, int? AverageHeartRate, int? MaxHeartRate);

internal sealed record WhoopCycle(long Id, DateTimeOffset Start, DateTimeOffset? End, string? TimezoneOffset, string? ScoreState, WhoopCycleScore? Score);

internal sealed record WhoopWorkoutScore(decimal? Strain, int? AverageHeartRate, int? MaxHeartRate, decimal? Kilojoule, decimal? DistanceMeter);

internal sealed record WhoopWorkout(
    string Id, DateTimeOffset Start, DateTimeOffset End, string? TimezoneOffset, string? SportName, string? ScoreState, WhoopWorkoutScore? Score);
